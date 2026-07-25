using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SharpShader.Compilation;
using Silk.NET.SPIRV;
using Silk.NET.SPIRV.Cross;
using CrossResult = Silk.NET.SPIRV.Cross.Result;
using NativeEntryPoint = Silk.NET.SPIRV.Cross.EntryPoint;
using NativeResource = Silk.NET.SPIRV.Cross.ReflectedResource;

namespace SharpShader.HLSLCrossCompiler.Internal
{
    internal static unsafe class SpirvToMslBindingApplier
    {
        private const uint MslArgumentBufferBinding = ~(3u);

        private static readonly ResourceType[] s_DescriptorResourceTypes =
        {
            ResourceType.UniformBuffer,
            ResourceType.StorageBuffer,
            ResourceType.SubpassInput,
            ResourceType.StorageImage,
            ResourceType.AtomicCounter,
            ResourceType.SeparateImage,
            ResourceType.SeparateSamplers,
            ResourceType.AccelerationStructure,
            ResourceType.ShaderRecordBuffer,
        };

        private static readonly ResourceType[] s_UnsupportedResourceTypes =
        {
            ResourceType.SampledImage,
            ResourceType.PushConstant,
        };

        public static ExecutionModel SelectEntryPoint(
            Cross cross,
            Context* context,
            Compiler* compiler,
            MslTranslationBindingPlan plan)
        {
            if (!SupportsMslSourceTranslation(plan.Stage))
            {
                throw Failure(
                    $"Planned SPIRV-Cross MSL source translation does not support stage {plan.Stage}. "
                    + "Use a backend-specific Metal library compilation path.");
            }

            NativeEntryPoint* entryPoints = null;
            nuint entryPointCountValue = 0;
            ThrowIfFailed(
                cross.CompilerGetEntryPoints(
                    compiler,
                    &entryPoints,
                    &entryPointCountValue),
                cross,
                context,
                "Failed to enumerate SPIR-V entry points for planned MSL translation.");

            int entryPointCount = ToManagedCount(
                entryPointCountValue,
                "SPIR-V entry-point count");
            ExecutionModel? selectedModel = null;
            List<string> available = new(entryPointCount);
            for (int index = 0; index < entryPointCount; ++index)
            {
                string name = ReadName(
                    entryPoints[index].Name,
                    $"entry point {index}");
                ExecutionModel model = entryPoints[index].ExecutionModel;
                available.Add($"{name}:{model}");
                if (!string.Equals(
                        name,
                        plan.EntryPoint,
                        StringComparison.Ordinal)
                    || !MatchesStage(model, plan.Stage))
                {
                    continue;
                }

                if (selectedModel.HasValue)
                {
                    throw Failure(
                        $"SPIR-V contains more than one entry point named {plan.EntryPoint} for stage {plan.Stage}.");
                }

                selectedModel = model;
            }

            if (!selectedModel.HasValue)
            {
                string suffix = available.Count == 0
                    ? "The module contains no entry points."
                    : $"Available entries: {string.Join(", ", available)}.";
                throw Failure(
                    $"SPIR-V entry point {plan.EntryPoint} with stage {plan.Stage} was not found. {suffix}");
            }

            ThrowIfFailed(
                cross.CompilerSetEntryPoint(
                    compiler,
                    plan.EntryPoint,
                    selectedModel.Value),
                cross,
                context,
                $"Failed to select SPIR-V entry point {plan.EntryPoint} for MSL translation.");
            return selectedModel.Value;
        }

        public static void Apply(
            Cross cross,
            Context* context,
            Compiler* compiler,
            ExecutionModel executionModel,
            MslTranslationBindingPlan plan)
        {
            Dictionary<(uint Set, uint Binding), MslTranslationResourceBinding>
                plannedByPhysicalBinding = new(plan.Resources.Count);
            if (plan.HasPrivateAttachments
                && plan.Mode == MslTranslationBindingMode.ReferenceBuffer)
            {
                ThrowIfFailed(
                    cross.CompilerMslAddDiscreteDescriptorSet(
                        compiler,
                        plan.PrivateAttachmentDescriptorSet),
                    cross,
                    context,
                    $"Failed to keep private attachment descriptor set "
                    + $"{plan.PrivateAttachmentDescriptorSet} outside Metal argument buffers.");
            }

            foreach (MslTranslationResourceBinding planned in plan.Resources)
            {
                if (!plannedByPhysicalBinding.TryAdd(
                        (planned.DescriptorSet, planned.Binding),
                        planned))
                {
                    throw Failure(
                        $"Planned MSL translation contains duplicate Vulkan binding set={planned.DescriptorSet}, binding={planned.Binding}.");
                }
            }

            Set* activeVariables = null;
            ThrowIfFailed(
                cross.CompilerGetActiveInterfaceVariables(
                    compiler,
                    &activeVariables),
                cross,
                context,
                $"Failed to enumerate active variables for SPIR-V entry point {plan.EntryPoint}.");

            Resources* resources = null;
            ThrowIfFailed(
                cross.CompilerCreateShaderResourcesForActiveVariables(
                    compiler,
                    &resources,
                    activeVariables),
                cross,
                context,
                $"Failed to enumerate active resources for SPIR-V entry point {plan.EntryPoint}.");

            RejectUnsupportedResources(
                cross,
                context,
                resources,
                plan.EntryPoint);

            HashSet<(uint Set, uint Binding)> adoptedBindings = new();
            foreach (ResourceType resourceType in s_DescriptorResourceTypes)
            {
                NativeResource* nativeResources = null;
                nuint resourceCountValue = 0;
                ThrowIfFailed(
                    cross.ResourcesGetResourceListForType(
                        resources,
                        resourceType,
                        &nativeResources,
                        &resourceCountValue),
                    cross,
                    context,
                    $"Failed to enumerate SPIR-V {resourceType} resources.");

                int resourceCount = ToManagedCount(
                    resourceCountValue,
                    $"SPIR-V {resourceType} resource count");
                for (int index = 0; index < resourceCount; ++index)
                {
                    NativeResource resource = nativeResources[index];
                    ApplyResourceBinding(
                        cross,
                        context,
                        compiler,
                        executionModel,
                        plan,
                        resourceType,
                        resource,
                        plannedByPhysicalBinding,
                        adoptedBindings);
                }
            }

            if (adoptedBindings.Count != plannedByPhysicalBinding.Count)
            {
                foreach (KeyValuePair<
                    (uint Set, uint Binding),
                    MslTranslationResourceBinding> planned in plannedByPhysicalBinding)
                {
                    if (!adoptedBindings.Contains(planned.Key))
                    {
                        throw Failure(
                            $"Planned binding {planned.Value.LogicalBinding} at Vulkan set={planned.Key.Set}, binding={planned.Key.Binding} is not active in entry point {plan.EntryPoint}.");
                    }
                }
            }

            if (plan.Mode == MslTranslationBindingMode.ReferenceBuffer)
            {
                foreach (MslTranslationArgumentBufferBinding argumentBuffer in plan.ArgumentBuffers)
                {
                    MslResourceBinding2 nativeBinding = default;
                    cross.MslResourceBindingInit2(&nativeBinding);
                    nativeBinding.Stage = executionModel;
                    nativeBinding.DescSet = argumentBuffer.DescriptorSet;
                    nativeBinding.Binding = MslArgumentBufferBinding;
                    nativeBinding.Count = 1;
                    nativeBinding.MslBuffer = argumentBuffer.MetalBufferIndex;
                    ThrowIfFailed(
                        cross.CompilerMslAddResourceBinding2(
                            compiler,
                            &nativeBinding),
                        cross,
                        context,
                        $"Failed to map Vulkan descriptor set {argumentBuffer.DescriptorSet} to Metal reference buffer {argumentBuffer.MetalBufferIndex}.");
                }
            }

            foreach (MslTranslationShaderOutput output in plan.ShaderOutputs)
            {
                MslShaderInterfaceVar2 nativeOutput = default;
                cross.MslShaderInterfaceVarInit2(&nativeOutput);
                nativeOutput.Location = output.Location;
                nativeOutput.Format = MslShaderVariableFormat.ShaderVariableFormatOther;
                nativeOutput.Builtin = BuiltIn.Max;
                nativeOutput.Vecsize = output.ComponentCount;
                nativeOutput.Rate = MslShaderVariableRate.PerVertex;
                ThrowIfFailed(
                    cross.CompilerMslAddShaderOutput2(compiler, &nativeOutput),
                    cross,
                    context,
                    $"Failed to register Metal color output location "
                    + $"{output.Location} for logical attachment "
                    + $"{output.LogicalAttachmentId}.");
                ThrowIfFailed(
                    cross.CompilerMslSetFragmentOutputComponents(
                        compiler,
                        output.Location,
                        output.ComponentCount),
                    cross,
                    context,
                    $"Failed to freeze Metal fragment output component count at "
                    + $"location {output.Location}.");
            }
        }

        public static void ValidateAdopted(
            Cross cross,
            Compiler* compiler,
            ExecutionModel executionModel,
            MslTranslationBindingPlan plan)
        {
            foreach (MslTranslationResourceBinding resource in plan.Resources)
            {
                if (cross.CompilerMslIsResourceUsed(
                        compiler,
                        executionModel,
                        resource.DescriptorSet,
                        resource.Binding) != 0)
                {
                    continue;
                }

                throw Failure(
                    $"SPIRV-Cross did not adopt planned binding {resource.LogicalBinding} at Vulkan set={resource.DescriptorSet}, binding={resource.Binding} for entry point {plan.EntryPoint}.");
            }
            foreach (MslTranslationShaderOutput output in plan.ShaderOutputs)
            {
                if (cross.CompilerMslIsShaderOutputUsed(
                        compiler,
                        output.Location) == 0)
                {
                    throw Failure(
                        $"SPIRV-Cross did not adopt planned Metal color output "
                        + $"location {output.Location} for logical attachment "
                        + $"{output.LogicalAttachmentId} in entry point "
                        + $"{plan.EntryPoint}.");
                }
            }

        }

        private static void ApplyResourceBinding(
            Cross cross,
            Context* context,
            Compiler* compiler,
            ExecutionModel executionModel,
            MslTranslationBindingPlan plan,
            ResourceType resourceType,
            NativeResource resource,
            Dictionary<(uint Set, uint Binding), MslTranslationResourceBinding>
                plannedByPhysicalBinding,
            HashSet<(uint Set, uint Binding)> adoptedBindings)
        {
            string name = ReadResourceName(
                cross,
                compiler,
                resource);
            if (cross.CompilerHasDecoration(
                    compiler,
                    resource.Id,
                    Decoration.DescriptorSet) == 0)
            {
                throw Failure(
                    $"SPIR-V resource {name} has no DescriptorSet decoration.");
            }

            if (cross.CompilerHasDecoration(
                    compiler,
                    resource.Id,
                    Decoration.Binding) == 0)
            {
                throw Failure(
                    $"SPIR-V resource {name} has no Binding decoration.");
            }

            uint descriptorSet = cross.CompilerGetDecoration(
                compiler,
                resource.Id,
                Decoration.DescriptorSet);
            uint binding = cross.CompilerGetDecoration(
                compiler,
                resource.Id,
                Decoration.Binding);
            if (!plannedByPhysicalBinding.TryGetValue(
                    (descriptorSet, binding),
                    out MslTranslationResourceBinding planned))
            {
                throw Failure(
                    $"SPIR-V resource {name} at Vulkan set={descriptorSet}, binding={binding} has no planned Metal mapping.");
            }

            if (!adoptedBindings.Add((descriptorSet, binding)))
            {
                throw Failure(
                    $"SPIR-V entry point {plan.EntryPoint} contains more than one descriptor at Vulkan set={descriptorSet}, binding={binding}.");
            }

            CrossType* type = cross.CompilerGetTypeHandle(
                compiler,
                resource.TypeId);
            if (type == null)
            {
                throw Failure(
                    $"SPIR-V resource {name} has no type handle.");
            }

            VulkanDescriptorKind actualDescriptorKind =
                GetDescriptorKind(
                    cross,
                    resourceType,
                    type);
            if (planned.DescriptorKind != actualDescriptorKind)
            {
                throw Failure(
                    $"SPIR-V resource {name} at set={descriptorSet}, binding={binding} is {actualDescriptorKind}, but the Vulkan plan requires {planned.DescriptorKind}.");
            }

            ShaderPhysicalBindingNamespace actualMetalNamespace =
                GetMetalNamespace(actualDescriptorKind);
            if (planned.MetalNamespace != actualMetalNamespace)
            {
                throw Failure(
                    $"SPIR-V resource {name} at set={descriptorSet}, binding={binding} requires Metal {actualMetalNamespace}, but the Metal plan uses {planned.MetalNamespace}.");
            }

            ValidateResourceCount(
                cross,
                type,
                planned,
                name);
            MslResourceBinding2 nativeBinding = default;
            cross.MslResourceBindingInit2(&nativeBinding);
            nativeBinding.Stage = executionModel;
            nativeBinding.DescSet = descriptorSet;
            nativeBinding.Binding = binding;
            nativeBinding.Count = planned.ResourceCount;
            SetMetalIndex(
                ref nativeBinding,
                planned.MetalNamespace,
                planned.MetalIndex);
            ThrowIfFailed(
                cross.CompilerMslAddResourceBinding2(
                    compiler,
                    &nativeBinding),
                cross,
                context,
                $"Failed to map SPIR-V resource {name} at set={descriptorSet}, binding={binding} to Metal.");
        }

        private static void RejectUnsupportedResources(
            Cross cross,
            Context* context,
            Resources* resources,
            string entryPoint)
        {
            foreach (ResourceType resourceType in s_UnsupportedResourceTypes)
            {
                NativeResource* nativeResources = null;
                nuint countValue = 0;
                ThrowIfFailed(
                    cross.ResourcesGetResourceListForType(
                        resources,
                        resourceType,
                        &nativeResources,
                        &countValue),
                    cross,
                    context,
                    $"Failed to inspect SPIR-V {resourceType} resources.");
                if (countValue != 0)
                {
                    throw Failure(
                        $"SPIR-V entry point {entryPoint} contains unsupported resource class {resourceType}.");
                }
            }
        }

        private static VulkanDescriptorKind GetDescriptorKind(
            Cross cross,
            ResourceType resourceType,
            CrossType* type)
        {
            return resourceType switch
            {
                ResourceType.UniformBuffer => VulkanDescriptorKind.UniformBuffer,
                ResourceType.StorageBuffer
                    or ResourceType.AtomicCounter
                    or ResourceType.ShaderRecordBuffer => VulkanDescriptorKind.StorageBuffer,
                ResourceType.SubpassInput => VulkanDescriptorKind.InputAttachment,
                ResourceType.StorageImage => cross.TypeGetImageDimension(type) == Dim.DimBuffer
                    ? VulkanDescriptorKind.StorageTexelBuffer
                    : VulkanDescriptorKind.StorageImage,
                ResourceType.SeparateImage => cross.TypeGetImageDimension(type) == Dim.DimBuffer
                    ? VulkanDescriptorKind.UniformTexelBuffer
                    : VulkanDescriptorKind.SampledImage,
                ResourceType.SeparateSamplers => VulkanDescriptorKind.Sampler,
                ResourceType.AccelerationStructure => VulkanDescriptorKind.AccelerationStructure,
                _ => throw Failure(
                    $"SPIR-V resource class {resourceType} has no supported Vulkan descriptor mapping."),
            };
        }

        private static ShaderPhysicalBindingNamespace GetMetalNamespace(
            VulkanDescriptorKind descriptorKind)
        {
            return descriptorKind switch
            {
                VulkanDescriptorKind.Sampler =>
                    ShaderPhysicalBindingNamespace.Sampler,
                VulkanDescriptorKind.SampledImage
                    or VulkanDescriptorKind.StorageImage
                    or VulkanDescriptorKind.InputAttachment
                    or VulkanDescriptorKind.UniformTexelBuffer
                    or VulkanDescriptorKind.StorageTexelBuffer =>
                        ShaderPhysicalBindingNamespace.Texture,
                VulkanDescriptorKind.UniformBuffer
                    or VulkanDescriptorKind.StorageBuffer
                    or VulkanDescriptorKind.AccelerationStructure =>
                        ShaderPhysicalBindingNamespace.Buffer,
                _ => throw Failure(
                    $"Vulkan descriptor kind {descriptorKind} has no supported Metal namespace."),
            };
        }

        private static void ValidateResourceCount(
            Cross cross,
            CrossType* type,
            MslTranslationResourceBinding planned,
            string name)
        {
            nuint dimensionCountValue =
                cross.TypeGetNumArrayDimensions(type);
            int dimensionCount = ToManagedCount(
                dimensionCountValue,
                $"SPIR-V resource {name} array dimension count");
            if (dimensionCount == 0)
            {
                if (planned.ResourceCount != 1)
                {
                    throw Failure(
                        $"Scalar SPIR-V resource {name} is mapped with Metal count {planned.ResourceCount}.");
                }

                return;
            }

            uint literalElementCount = 1;
            bool hasDynamicDimension = false;
            for (int dimensionIndex = 0; dimensionIndex < dimensionCount; ++dimensionIndex)
            {
                uint dimension = cross.TypeGetArrayDimension(
                    type,
                    (uint)dimensionIndex);
                if (cross.TypeArrayDimensionIsLiteral(
                        type,
                        (uint)dimensionIndex) == 0
                    || dimension == 0)
                {
                    hasDynamicDimension = true;
                    continue;
                }

                if (literalElementCount > uint.MaxValue / dimension)
                {
                    throw Failure(
                        $"SPIR-V resource {name} array element count exceeds UInt32.");
                }

                literalElementCount *= dimension;
            }

            if (!hasDynamicDimension)
            {
                if (planned.ResourceCount != literalElementCount)
                {
                    throw Failure(
                        $"SPIR-V resource {name} has {literalElementCount} array elements, but the Metal plan reserves {planned.ResourceCount}.");
                }

                return;
            }

            if (planned.ResourceCount < literalElementCount
                || (planned.ResourceCount % literalElementCount) != 0)
            {
                throw Failure(
                    $"Dynamic SPIR-V resource {name} requires a Metal capacity that is a positive multiple of its {literalElementCount} literal elements, but {planned.ResourceCount} was provided.");
            }
        }

        private static void SetMetalIndex(
            ref MslResourceBinding2 binding,
            ShaderPhysicalBindingNamespace bindingNamespace,
            uint index)
        {
            switch (bindingNamespace)
            {
                case ShaderPhysicalBindingNamespace.Buffer:
                    binding.MslBuffer = index;
                    break;
                case ShaderPhysicalBindingNamespace.Texture:
                    binding.MslTexture = index;
                    break;
                case ShaderPhysicalBindingNamespace.Sampler:
                    binding.MslSampler = index;
                    break;
                default:
                    throw Failure(
                        $"Metal namespace {bindingNamespace} is unsupported by SPIRV-Cross.");
            }
        }

        private static bool MatchesStage(
            ExecutionModel model,
            ShaderExecutionStage stage)
        {
            return stage switch
            {
                ShaderExecutionStage.Vertex =>
                    model == ExecutionModel.Vertex,
                ShaderExecutionStage.Hull =>
                    model == ExecutionModel.TessellationControl,
                ShaderExecutionStage.Domain =>
                    model == ExecutionModel.TessellationEvaluation,
                ShaderExecutionStage.Geometry =>
                    model == ExecutionModel.Geometry,
                ShaderExecutionStage.Pixel =>
                    model == ExecutionModel.Fragment,
                ShaderExecutionStage.Compute =>
                    model == ExecutionModel.GLCompute,
                ShaderExecutionStage.Amplification =>
                    model == ExecutionModel.TaskNV
                    || model == ExecutionModel.TaskExt,
                ShaderExecutionStage.Mesh =>
                    model == ExecutionModel.MeshNV
                    || model == ExecutionModel.MeshExt,
                _ => false,
            };
        }

        private static bool SupportsMslSourceTranslation(
            ShaderExecutionStage stage)
        {
            return stage is ShaderExecutionStage.Vertex
                or ShaderExecutionStage.Hull
                or ShaderExecutionStage.Domain
                or ShaderExecutionStage.Pixel
                or ShaderExecutionStage.Compute
                or ShaderExecutionStage.Amplification
                or ShaderExecutionStage.Mesh;
        }

        private static string ReadResourceName(
            Cross cross,
            Compiler* compiler,
            NativeResource resource)
        {
            string? name = Marshal.PtrToStringUTF8(
                (IntPtr)resource.Name);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            name = cross.CompilerGetNameS(
                compiler,
                resource.Id);
            return string.IsNullOrWhiteSpace(name)
                ? $"resource_{resource.Id}"
                : name;
        }

        private static string ReadName(
            byte* pointer,
            string description)
        {
            string? name = Marshal.PtrToStringUTF8(
                (IntPtr)pointer);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw Failure(
                    $"SPIR-V {description} has no UTF-8 name.");
            }

            return name;
        }

        private static int ToManagedCount(
            nuint value,
            string description)
        {
            if (value > int.MaxValue)
            {
                throw Failure(
                    $"{description} {value} exceeds the managed collection limit {int.MaxValue}.");
            }

            return checked((int)value);
        }

        private static void ThrowIfFailed(
            CrossResult result,
            Cross cross,
            Context* context,
            string message)
        {
            if (result == CrossResult.Success)
            {
                return;
            }

            string detail = context == null
                ? string.Empty
                : (cross.ContextGetLastErrorStringS(context) ?? string.Empty);
            throw Failure(
                string.IsNullOrWhiteSpace(detail)
                    ? message
                    : $"{message} {detail}",
                detail);
        }

        private static ShaderCompilerException Failure(
            string message,
            string diagnostics = "")
        {
            return new ShaderCompilerException(
                ShaderCompilerErrorCode.MslTranslateFailed,
                message,
                diagnostics);
        }
    }
}
