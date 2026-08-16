using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SharpShader.HLSLCrossCompiler;
using SharpShader.HLSLCrossCompiler.Internal;
using Silk.NET.SPIRV;
using Silk.NET.SPIRV.Cross;
using CrossResult = Silk.NET.SPIRV.Cross.Result;
using NativeEntryPoint = Silk.NET.SPIRV.Cross.EntryPoint;
using NativeResource = Silk.NET.SPIRV.Cross.ReflectedResource;
using NativeSpecializationConstant = Silk.NET.SPIRV.Cross.SpecializationConstant;

namespace SharpShader.Compilation.Internal
{
    internal static unsafe partial class SpirvArtifactReflector
    {

        private static ShaderStageIoReflection[] ReflectStageIo(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            Compiler* compiler,
            Resources* resources,
            ResourceType resourceType,
            ShaderStageIoDirection direction)
        {
            NativeResource* nativeResources = null;
            nuint countValue = 0;
            ThrowIfFailed(
                request,
                cross,
                context,
                cross.ResourcesGetResourceListForType(
                    resources,
                    resourceType,
                    &nativeResources,
                    &countValue),
                $"Failed to enumerate SPIR-V {resourceType} variables.");
            int count = ToManagedCount(
                request,
                countValue,
                $"SPIR-V {resourceType} variable count");
            ShaderStageIoReflection[] reflected =
                new ShaderStageIoReflection[count];
            for (int index = 0; index < count; ++index)
            {
                NativeResource resource = nativeResources[index];
                string name = ReadResourceName(cross, compiler, resource);
                bool hasBuiltIn = cross.CompilerHasDecoration(
                    compiler,
                    resource.Id,
                    Decoration.BuiltIn) != 0;
                ShaderStageIoBuiltIn builtIn = hasBuiltIn
                    ? MapStageIoBuiltIn(
                        request,
                        name,
                        (BuiltIn)cross.CompilerGetDecoration(
                            compiler,
                            resource.Id,
                            Decoration.BuiltIn))
                    : ShaderStageIoBuiltIn.None;
                bool hasLocation = cross.CompilerHasDecoration(
                    compiler,
                    resource.Id,
                    Decoration.Location) != 0;
                if (!hasBuiltIn && !hasLocation)
                {
                    throw ReflectionFailure(
                        request,
                        $"SPIR-V stage I/O {name} has neither a Location nor a BuiltIn decoration.");
                }

                uint? location = hasLocation
                    ? cross.CompilerGetDecoration(
                        compiler,
                        resource.Id,
                        Decoration.Location)
                    : null;
                uint outputIndex = cross.CompilerHasDecoration(
                    compiler,
                    resource.Id,
                    Decoration.Index) != 0
                        ? cross.CompilerGetDecoration(
                            compiler,
                            resource.Id,
                            Decoration.Index)
                        : 0;
                uint component = cross.CompilerHasDecoration(
                    compiler,
                    resource.Id,
                    Decoration.Component) != 0
                        ? cross.CompilerGetDecoration(
                            compiler,
                            resource.Id,
                            Decoration.Component)
                        : 0;
                CrossType* type = RequireTypeHandle(
                    request,
                    cross,
                    compiler,
                    resource.TypeId,
                    $"SPIR-V stage I/O {name}");
                uint componentCount = cross.TypeGetVectorSize(type);
                if (componentCount == 0)
                {
                    componentCount = 1;
                }

                reflected[index] = new ShaderStageIoReflection(
                    name,
                    direction,
                    location,
                    outputIndex,
                    component,
                    builtIn,
                    MapAttachmentNumericClass(
                        request,
                        cross.TypeGetBasetype(type),
                        $"SPIR-V stage I/O {name}"),
                    componentCount);
            }

            BuiltinResourceType builtInResourceType = direction
                == ShaderStageIoDirection.Input
                    ? BuiltinResourceType.StageInput
                    : BuiltinResourceType.StageOutput;
            ShaderStageIoReflection[] builtIns = ReflectBuiltInStageIo(
                request,
                cross,
                context,
                compiler,
                resources,
                builtInResourceType,
                direction);
            if (builtIns.Length == 0)
            {
                return reflected;
            }

            ShaderStageIoReflection[] combined =
                new ShaderStageIoReflection[reflected.Length + builtIns.Length];
            Array.Copy(reflected, combined, reflected.Length);
            Array.Copy(builtIns, 0, combined, reflected.Length, builtIns.Length);
            return combined;
        }

        private static ShaderStageIoReflection[] ReflectBuiltInStageIo(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            Compiler* compiler,
            Resources* resources,
            BuiltinResourceType resourceType,
            ShaderStageIoDirection direction)
        {
            ReflectedBuiltinResource* nativeResources = null;
            nuint countValue = 0;
            ThrowIfFailed(
                request,
                cross,
                context,
                cross.ResourcesGetBuiltinResourceListForType(
                    resources,
                    resourceType,
                    &nativeResources,
                    &countValue),
                $"Failed to enumerate SPIR-V {resourceType} built-in variables.");
            int count = ToManagedCount(
                request,
                countValue,
                $"SPIR-V {resourceType} built-in variable count");
            ShaderStageIoReflection[] reflected =
                new ShaderStageIoReflection[count];
            for (int index = 0; index < count; ++index)
            {
                ReflectedBuiltinResource resource = nativeResources[index];
                string name = resource.Resource.Name is null
                    || resource.Resource.Name[0] == 0
                        ? $"BuiltIn.{resource.Builtin}"
                        : ReadRequiredName(
                            request,
                            resource.Resource.Name,
                            $"SPIR-V {resourceType} built-in {resource.Builtin}");
                CrossType* type = RequireTypeHandle(
                    request,
                    cross,
                    compiler,
                    resource.ValueTypeId,
                    $"SPIR-V built-in stage I/O {name}");
                uint componentCount = cross.TypeGetVectorSize(type);
                if (componentCount == 0)
                {
                    componentCount = 1;
                }

                reflected[index] = new ShaderStageIoReflection(
                    name,
                    direction,
                    location: null,
                    index: 0,
                    component: 0,
                    MapStageIoBuiltIn(request, name, resource.Builtin),
                    MapAttachmentNumericClass(
                        request,
                        cross.TypeGetBasetype(type),
                        $"SPIR-V built-in stage I/O {name}"),
                    componentCount);
            }

            return reflected;
        }

        private static ShaderStageIoReflection[] NormalizeDepthExportMode(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            Compiler* compiler,
            string entryPointName,
            ShaderStageIoReflection[] stageOutputs)
        {
            bool hasFragDepth = false;
            foreach (ShaderStageIoReflection output in stageOutputs)
            {
                hasFragDepth |= output.BuiltIn == ShaderStageIoBuiltIn.Depth;
            }

            ExecutionMode* modes = null;
            nuint modeCountValue = 0;
            ThrowIfFailed(
                request,
                cross,
                context,
                cross.CompilerGetExecutionModes(
                    compiler,
                    &modes,
                    &modeCountValue),
                $"Failed to enumerate depth execution modes for SPIR-V entry "
                + $"point {entryPointName}.");
            int modeCount = ToManagedCount(
                request,
                modeCountValue,
                $"SPIR-V entry point {entryPointName} depth execution-mode count");
            bool depthReplacing = false;
            bool depthGreater = false;
            bool depthLess = false;
            bool depthUnchanged = false;
            for (int index = 0; index < modeCount; ++index)
            {
                depthReplacing |= modes[index] == ExecutionMode.DepthReplacing;
                depthGreater |= modes[index] == ExecutionMode.DepthGreater;
                depthLess |= modes[index] == ExecutionMode.DepthLess;
                depthUnchanged |= modes[index] == ExecutionMode.DepthUnchanged;
            }

            if (depthGreater && depthLess)
            {
                throw ReflectionFailure(
                    request,
                    $"SPIR-V entry point {entryPointName} declares conflicting "
                    + "DepthGreater and DepthLess execution modes.");
            }

            bool hasDepthMode = depthReplacing
                || depthGreater
                || depthLess
                || depthUnchanged;
            if (hasFragDepth != depthReplacing
                || (!hasFragDepth && hasDepthMode)
                || depthUnchanged)
            {
                throw ReflectionFailure(
                    request,
                    $"SPIR-V entry point {entryPointName} has inconsistent FragDepth "
                    + $"and depth execution modes (FragDepth={hasFragDepth}, "
                    + $"DepthReplacing={depthReplacing}, DepthGreater={depthGreater}, "
                    + $"DepthLess={depthLess}, DepthUnchanged={depthUnchanged}).");
            }

            if (!hasFragDepth || (!depthGreater && !depthLess))
            {
                return stageOutputs;
            }

            ShaderStageIoBuiltIn normalized = depthGreater
                ? ShaderStageIoBuiltIn.DepthGreaterEqual
                : ShaderStageIoBuiltIn.DepthLessEqual;
            ShaderStageIoReflection[] result =
                new ShaderStageIoReflection[stageOutputs.Length];
            for (int index = 0; index < stageOutputs.Length; ++index)
            {
                ShaderStageIoReflection output = stageOutputs[index];
                result[index] = output.BuiltIn == ShaderStageIoBuiltIn.Depth
                    ? new ShaderStageIoReflection(
                        output.Name,
                        output.Direction,
                        output.Location,
                        output.Index,
                        output.Component,
                        normalized,
                        output.NumericClass,
                        output.ComponentCount)
                    : output;
            }

            return result;
        }

        private static ShaderInputAttachmentReflection[] ReflectInputAttachments(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            Compiler* compiler,
            Resources* resources)
        {
            NativeResource* nativeResources = null;
            nuint countValue = 0;
            ThrowIfFailed(
                request,
                cross,
                context,
                cross.ResourcesGetResourceListForType(
                    resources,
                    ResourceType.SubpassInput,
                    &nativeResources,
                    &countValue),
                "Failed to enumerate SPIR-V input attachments.");
            int count = ToManagedCount(
                request,
                countValue,
                "SPIR-V input attachment count");
            ShaderInputAttachmentReflection[] reflected =
                new ShaderInputAttachmentReflection[count];
            for (int index = 0; index < count; ++index)
            {
                NativeResource resource = nativeResources[index];
                string name = ReadResourceName(cross, compiler, resource);
                if (cross.CompilerHasDecoration(
                        compiler,
                        resource.Id,
                        Decoration.DescriptorSet) == 0
                    || cross.CompilerHasDecoration(
                        compiler,
                        resource.Id,
                        Decoration.Binding) == 0)
                {
                    throw ReflectionFailure(
                        request,
                        $"SPIR-V input attachment {name} requires direct DescriptorSet "
                        + "and Binding decorations for native RenderPass2 lowering.");
                }

                uint descriptorSet = cross.CompilerGetDecoration(
                    compiler,
                    resource.Id,
                    Decoration.DescriptorSet);
                uint binding = cross.CompilerGetDecoration(
                    compiler,
                    resource.Id,
                    Decoration.Binding);
                if (cross.CompilerHasDecoration(
                        compiler,
                        resource.Id,
                        Decoration.InputAttachmentIndex) == 0)
                {
                    throw ReflectionFailure(
                        request,
                        $"SPIR-V input attachment {name} has no InputAttachmentIndex decoration.");
                }

                uint inputIndex = cross.CompilerGetDecoration(
                    compiler,
                    resource.Id,
                    Decoration.InputAttachmentIndex);
                uint? location = cross.CompilerHasDecoration(
                    compiler,
                    resource.Id,
                    Decoration.Location) != 0
                        ? cross.CompilerGetDecoration(
                            compiler,
                            resource.Id,
                            Decoration.Location)
                        : null;
                uint? component = cross.CompilerHasDecoration(
                    compiler,
                    resource.Id,
                    Decoration.Component) != 0
                        ? cross.CompilerGetDecoration(
                            compiler,
                            resource.Id,
                            Decoration.Component)
                        : null;
                CrossType* imageType = RequireTypeHandle(
                    request,
                    cross,
                    compiler,
                    resource.TypeId,
                    $"SPIR-V input attachment {name}");
                uint sampledTypeId = cross.TypeGetImageSampledType(imageType);
                CrossType* sampledType = RequireTypeHandle(
                    request,
                    cross,
                    compiler,
                    sampledTypeId,
                    $"SPIR-V input attachment {name} sampled type");

                reflected[index] = new ShaderInputAttachmentReflection(
                    name,
                    inputIndex,
                    location,
                    component,
                    MapAttachmentNumericClass(
                        request,
                        cross.TypeGetBasetype(sampledType),
                        $"SPIR-V input attachment {name}"),
                    cross.TypeGetImageMultisampled(imageType) != 0
                        ? ShaderAttachmentSampleMode.Multisampled
                        : ShaderAttachmentSampleMode.SingleSample,
                    new ShaderPhysicalBindingLocation(
                        ShaderBackendKind.Vulkan,
                        descriptorSet,
                        binding,
                        ShaderPhysicalBindingNamespace.Unified));
            }

            return reflected;
        }

        private static ShaderAttachmentArtifactRequirement ReflectAttachmentRequirements(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            Compiler* compiler,
            string entryPointName,
            IReadOnlyList<ShaderStageIoReflection> stageOutputs,
            IReadOnlyList<ShaderInputAttachmentReflection> inputAttachments)
        {
            ShaderAttachmentArtifactRequirement requirements =
                inputAttachments.Count == 0
                    ? ShaderAttachmentArtifactRequirement.None
                    : ShaderAttachmentArtifactRequirement.FramebufferLocalRead;
            foreach (ShaderStageIoReflection output in stageOutputs)
            {
                if (output.BuiltIn == ShaderStageIoBuiltIn.StencilReference)
                {
                    requirements |=
                        ShaderAttachmentArtifactRequirement.StencilReferenceExport;
                }
            }

            ExecutionMode* modes = null;
            nuint modeCountValue = 0;
            ThrowIfFailed(
                request,
                cross,
                context,
                cross.CompilerGetExecutionModes(
                    compiler,
                    &modes,
                    &modeCountValue),
                $"Failed to enumerate attachment execution modes for SPIR-V "
                + $"entry point {entryPointName}.");
            int modeCount = ToManagedCount(
                request,
                modeCountValue,
                $"SPIR-V entry point {entryPointName} attachment execution-mode count");
            for (int index = 0; index < modeCount; ++index)
            {
                if (modes[index] == ExecutionMode.PixelInterlockOrderedExt)
                {
                    requirements |= ShaderAttachmentArtifactRequirement.OrderedPixelFragmentInterlock;
                }
                else if (modes[index] == ExecutionMode.SampleInterlockOrderedExt)
                {
                    requirements |=
                        ShaderAttachmentArtifactRequirement.SampleOrderedFragmentInterlock;
                }
                else if (modes[index] ==
                         ExecutionMode.ShadingRateInterlockOrderedExt)
                {
                    requirements |=
                        ShaderAttachmentArtifactRequirement.ShadingRateOrderedFragmentInterlock;
                }
                else if (modes[index] is ExecutionMode.PixelInterlockUnorderedExt
                    or ExecutionMode.SampleInterlockUnorderedExt
                    or ExecutionMode.ShadingRateInterlockUnorderedExt)
                {
                    requirements |=
                        ShaderAttachmentArtifactRequirement.UnorderedFragmentInterlock;
                }
            }

            return requirements;
        }

        private static ShaderAttachmentNumericClass MapAttachmentNumericClass(
            ShaderCompileRequest request,
            Basetype baseType,
            string path)
        {
            return baseType switch
            {
                Basetype.FP16 or Basetype.FP32 or Basetype.FP64 =>
                    ShaderAttachmentNumericClass.FloatingPoint,
                Basetype.Int8
                    or Basetype.Int16
                    or Basetype.Int32
                    or Basetype.Int64 =>
                        ShaderAttachmentNumericClass.SignedInteger,
                Basetype.Uint8
                    or Basetype.Uint16
                    or Basetype.Uint32
                    or Basetype.Uint64 =>
                        ShaderAttachmentNumericClass.UnsignedInteger,
                _ => throw ReflectionFailure(
                    request,
                    $"{path} has unsupported numeric base type {baseType}."),
            };
        }

        private static ShaderStageIoBuiltIn MapStageIoBuiltIn(
            ShaderCompileRequest request,
            string name,
            BuiltIn builtIn)
        {
            return builtIn switch
            {
                BuiltIn.Position or BuiltIn.FragCoord =>
                    ShaderStageIoBuiltIn.Position,
                BuiltIn.PointSize => ShaderStageIoBuiltIn.PointSize,
                BuiltIn.ClipDistance => ShaderStageIoBuiltIn.ClipDistance,
                BuiltIn.CullDistance => ShaderStageIoBuiltIn.CullDistance,
                BuiltIn.VertexId or BuiltIn.VertexIndex =>
                    ShaderStageIoBuiltIn.VertexId,
                BuiltIn.InstanceId or BuiltIn.InstanceIndex =>
                    ShaderStageIoBuiltIn.InstanceId,
                BuiltIn.PrimitiveId => ShaderStageIoBuiltIn.PrimitiveId,
                BuiltIn.InvocationId => ShaderStageIoBuiltIn.InvocationId,
                BuiltIn.Layer => ShaderStageIoBuiltIn.Layer,
                BuiltIn.ViewportIndex =>
                    ShaderStageIoBuiltIn.ViewportArrayIndex,
                BuiltIn.TessLevelOuter =>
                    ShaderStageIoBuiltIn.TessellationFactor,
                BuiltIn.TessLevelInner =>
                    ShaderStageIoBuiltIn.InsideTessellationFactor,
                BuiltIn.TessCoord =>
                    ShaderStageIoBuiltIn.TessellationCoordinate,
                BuiltIn.PointCoord => ShaderStageIoBuiltIn.PointCoordinate,
                BuiltIn.FrontFacing => ShaderStageIoBuiltIn.FrontFace,
                BuiltIn.SampleId => ShaderStageIoBuiltIn.SampleIndex,
                BuiltIn.SamplePosition =>
                    ShaderStageIoBuiltIn.SamplePosition,
                BuiltIn.SampleMask => ShaderStageIoBuiltIn.Coverage,
                BuiltIn.FragDepth => ShaderStageIoBuiltIn.Depth,
                BuiltIn.PrimitiveShadingRateKhr or BuiltIn.ShadingRateKhr =>
                    ShaderStageIoBuiltIn.ShadingRate,
                BuiltIn.FragStencilRefExt =>
                    ShaderStageIoBuiltIn.StencilReference,
                BuiltIn.FullyCoveredExt =>
                    ShaderStageIoBuiltIn.InnerCoverage,
                BuiltIn.BaryCoordKhr
                    or BuiltIn.BaryCoordNV
                    or BuiltIn.BaryCoordNoPerspKhr
                    or BuiltIn.BaryCoordNoPerspNV
                    or BuiltIn.BaryCoordNoPerspAmd
                    or BuiltIn.BaryCoordNoPerspCentroidAmd
                    or BuiltIn.BaryCoordNoPerspSampleAmd
                    or BuiltIn.BaryCoordSmoothAmd
                    or BuiltIn.BaryCoordSmoothCentroidAmd
                    or BuiltIn.BaryCoordSmoothSampleAmd
                    or BuiltIn.BaryCoordPullModelAmd =>
                        ShaderStageIoBuiltIn.Barycentrics,
                BuiltIn.CullPrimitiveExt =>
                    ShaderStageIoBuiltIn.CullPrimitive,
                _ => throw ReflectionFailure(
                    request,
                    $"SPIR-V stage I/O {name} has unsupported BuiltIn {builtIn}."),
            };
        }

        private static IReadOnlyDictionary<uint, uint> ReflectSpecializationConstants(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            Compiler* compiler)
        {
            NativeSpecializationConstant* constants = null;
            nuint countValue = 0;
            ThrowIfFailed(
                request,
                cross,
                context,
                cross.CompilerGetSpecializationConstants(
                    compiler,
                    &constants,
                    &countValue),
                "Failed to enumerate SPIR-V specialization constants.");

            int count = ToManagedCount(
                request,
                countValue,
                "SPIR-V specialization-constant count");
            Dictionary<uint, uint> result = new Dictionary<uint, uint>(count);
            for (int index = 0; index < count; ++index)
            {
                NativeSpecializationConstant constant = constants[index];
                if (constant.Id == 0)
                {
                    throw ReflectionFailure(
                        request,
                        $"SPIR-V specialization constant {index} has an invalid zero result ID.");
                }

                if (!result.TryAdd(constant.Id, constant.ConstantId))
                {
                    throw ReflectionFailure(
                        request,
                        $"SPIR-V specialization constant result ID {constant.Id} is duplicated.");
                }
            }

            return result;
        }

        private static void ValidateUnsupportedResources(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            Resources* resources,
            string entryPointName)
        {
            foreach (ResourceType resourceType in s_UnsupportedResourceTypes)
            {
                NativeResource* nativeResources = null;
                nuint countValue = 0;
                ThrowIfFailed(
                    request,
                    cross,
                    context,
                    cross.ResourcesGetResourceListForType(
                        resources,
                        resourceType,
                        &nativeResources,
                        &countValue),
                    $"Failed to inspect SPIR-V {resourceType} resources.");

                if (countValue != 0)
                {
                    throw ReflectionFailure(
                        request,
                        $"SPIR-V entry point {entryPointName} contains unsupported resource class {resourceType}.");
                }
            }
        }
}
}
