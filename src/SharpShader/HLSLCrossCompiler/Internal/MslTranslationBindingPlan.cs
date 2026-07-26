using System;
using System.Collections.Generic;
using SharpShader.Compilation;
using SharpShader.Compilation.Internal;

namespace SharpShader.HLSLCrossCompiler.Internal
{
    internal enum MslTranslationBindingMode
    {
        Direct,
        ReferenceBuffer,
    }

    internal readonly struct MslTranslationResourceBinding
    {
        public ShaderBindingKey LogicalBinding { get; }
        public uint DescriptorSet { get; }
        public uint Binding { get; }
        public VulkanDescriptorKind DescriptorKind { get; }
        public ShaderPhysicalBindingNamespace MetalNamespace { get; }
        public uint MetalIndex { get; }
        public uint ResourceCount { get; }
        public bool IsPrivateAttachment { get; }

        public MslTranslationResourceBinding(
            ShaderBindingKey logicalBinding,
            uint descriptorSet,
            uint binding,
            VulkanDescriptorKind descriptorKind,
            ShaderPhysicalBindingNamespace metalNamespace,
            uint metalIndex,
            uint resourceCount,
            bool isPrivateAttachment = false)
        {
            LogicalBinding = logicalBinding;
            DescriptorSet = descriptorSet;
            Binding = binding;
            DescriptorKind = descriptorKind;
            MetalNamespace = metalNamespace;
            MetalIndex = metalIndex;
            ResourceCount = resourceCount;
            IsPrivateAttachment = isPrivateAttachment;
        }
    }

    internal readonly struct MslTranslationArgumentBufferBinding
    {
        public uint DescriptorSet { get; }
        public uint MetalBufferIndex { get; }

        public MslTranslationArgumentBufferBinding(
            uint descriptorSet,
            uint metalBufferIndex)
        {
            DescriptorSet = descriptorSet;
            MetalBufferIndex = metalBufferIndex;
        }
    }

    internal readonly struct MslTranslationShaderOutput
    {
        public uint Location { get; }
        public uint LogicalAttachmentId { get; }
        public uint ComponentCount { get; }
        public ShaderAttachmentNumericClass NumericClass { get; }

        public MslTranslationShaderOutput(
            uint location,
            uint logicalAttachmentId,
            uint componentCount,
            ShaderAttachmentNumericClass numericClass)
        {
            Location = location;
            LogicalAttachmentId = logicalAttachmentId;
            ComponentCount = componentCount;
            NumericClass = numericClass;
        }
    }

    internal sealed class MslTranslationBindingPlan
    {
        private readonly MslTranslationResourceBinding[] m_Resources;
        private readonly MslTranslationArgumentBufferBinding[] m_ArgumentBuffers;
        private readonly MslTranslationShaderOutput[] m_ShaderOutputs;

        public string EntryPoint { get; }
        public ShaderExecutionStage Stage { get; }
        public MslTranslationBindingMode Mode { get; }
        public IReadOnlyList<MslTranslationResourceBinding> Resources => m_Resources;
        public IReadOnlyList<MslTranslationArgumentBufferBinding> ArgumentBuffers => m_ArgumentBuffers;
        public IReadOnlyList<MslTranslationShaderOutput> ShaderOutputs => m_ShaderOutputs;
        public uint PrivateAttachmentDescriptorSet { get; }
        public bool HasPrivateAttachments { get; }
        public bool UsesFramebufferFetch { get; }
        public bool UsesRasterOrderGroups { get; }
        public ShaderDepthExport DepthExport { get; }
        public ShaderStencilExport StencilExport { get; }

        private MslTranslationBindingPlan(
            string entryPoint,
            ShaderExecutionStage stage,
            MslTranslationBindingMode mode,
            MslTranslationResourceBinding[] resources,
            MslTranslationArgumentBufferBinding[] argumentBuffers,
            MslTranslationShaderOutput[] shaderOutputs,
            uint privateAttachmentDescriptorSet,
            bool hasPrivateAttachments,
            bool usesFramebufferFetch,
            bool usesRasterOrderGroups,
            ShaderDepthExport depthExport,
            ShaderStencilExport stencilExport)
        {
            EntryPoint = entryPoint;
            Stage = stage;
            Mode = mode;
            m_Resources = resources;
            m_ArgumentBuffers = argumentBuffers;
            m_ShaderOutputs = shaderOutputs;
            PrivateAttachmentDescriptorSet = privateAttachmentDescriptorSet;
            HasPrivateAttachments = hasPrivateAttachments;
            UsesFramebufferFetch = usesFramebufferFetch;
            UsesRasterOrderGroups = usesRasterOrderGroups;
            DepthExport = depthExport;
            StencilExport = stencilExport;
        }

        public static MslTranslationBindingPlan Create(
            string entryPoint,
            ShaderExecutionStage stage,
            IReadOnlyList<VulkanShaderBindingMapping> vulkanBindings,
            MetalShaderBackendLayout metalLayout,
            ShaderAttachmentInterface attachmentInterface,
            ShaderEntryPointReflection postRemapSpirvReflection,
            uint privateAttachmentDescriptorSet,
            uint privateMetalTextureBase)
        {
            if (string.IsNullOrWhiteSpace(entryPoint))
            {
                throw Failure("A planned MSL translation requires a non-empty entry-point name.");
            }

            if (!Enum.IsDefined(stage))
            {
                throw Failure($"MSL translation stage {stage} is not defined.");
            }

            ArgumentNullException.ThrowIfNull(vulkanBindings);
            ArgumentNullException.ThrowIfNull(metalLayout);
            ArgumentNullException.ThrowIfNull(attachmentInterface);
            ArgumentNullException.ThrowIfNull(postRemapSpirvReflection);
            if (!string.Equals(
                    attachmentInterface.EntryPoint,
                    entryPoint,
                    StringComparison.Ordinal)
                || attachmentInterface.Stage != stage
                || !string.Equals(
                    postRemapSpirvReflection.Name,
                    entryPoint,
                    StringComparison.Ordinal)
                || postRemapSpirvReflection.Stage != stage)
            {
                throw Failure(
                    "MSL attachment contract, reflection, and translation entry identity do not match.");
            }

            bool hasDirectBindings = metalLayout.DirectBindings.Count != 0;
            bool hasReferenceBindings = metalLayout.ReferenceBufferBindings.Count != 0;
            if (hasDirectBindings && hasReferenceBindings)
            {
                throw Failure(
                    "A planned MSL translation cannot mix direct and reference-buffer bindings.");
            }

            MslTranslationBindingMode mode = hasReferenceBindings
                ? MslTranslationBindingMode.ReferenceBuffer
                : MslTranslationBindingMode.Direct;
            int metalBindingCount = hasReferenceBindings
                ? metalLayout.ReferenceBufferBindings.Count
                : metalLayout.DirectBindings.Count;
            if (vulkanBindings.Count != metalBindingCount)
            {
                throw Failure(
                    $"Vulkan and Metal plans contain different binding counts ({vulkanBindings.Count} and {metalBindingCount}).");
            }

            Dictionary<ShaderBindingKey, VulkanShaderBindingMapping> vulkanByLogicalBinding =
                new(vulkanBindings.Count);
            HashSet<(uint Set, uint Binding)> physicalVulkanBindings = new();
            for (int index = 0; index < vulkanBindings.Count; ++index)
            {
                VulkanShaderBindingMapping mapping = vulkanBindings[index];
                if (!vulkanByLogicalBinding.TryAdd(mapping.LogicalBinding, mapping))
                {
                    throw Failure(
                        $"Vulkan translation plan contains duplicate logical binding {mapping.LogicalBinding}.");
                }

                if (!physicalVulkanBindings.Add((mapping.DescriptorSet, mapping.Binding)))
                {
                    throw Failure(
                        $"Vulkan translation plan contains duplicate physical binding set={mapping.DescriptorSet}, binding={mapping.Binding}.");
                }
            }

            List<MslTranslationResourceBinding> resources = new(metalBindingCount);
            List<MslTranslationArgumentBufferBinding> argumentBuffers = new();
            List<MslTranslationShaderOutput> shaderOutputs = new();
            bool usesFramebufferFetch = false;
            bool usesRasterOrderGroups = false;
            if (mode == MslTranslationBindingMode.Direct)
            {
                AddDirectBindings(
                    metalLayout,
                    vulkanByLogicalBinding,
                    resources);
            }
            else
            {
                AddReferenceBindings(
                    metalLayout,
                    vulkanByLogicalBinding,
                    resources,
                    argumentBuffers);
            }

            if (vulkanByLogicalBinding.Count != 0)
            {
                foreach (ShaderBindingKey extra in vulkanByLogicalBinding.Keys)
                {
                    throw Failure(
                        $"Vulkan translation plan contains logical binding {extra} with no Metal mapping.");
                }
            }

            AddAttachmentBindings(
                attachmentInterface,
                postRemapSpirvReflection,
                privateAttachmentDescriptorSet,
                privateMetalTextureBase,
                physicalVulkanBindings,
                resources,
                shaderOutputs,
                out usesFramebufferFetch,
                out usesRasterOrderGroups);

            resources.Sort(static (left, right) =>
            {
                int descriptorSet = left.DescriptorSet.CompareTo(right.DescriptorSet);
                return descriptorSet != 0
                    ? descriptorSet
                    : left.Binding.CompareTo(right.Binding);
            });
            argumentBuffers.Sort(static (left, right) =>
                left.DescriptorSet.CompareTo(right.DescriptorSet));
            shaderOutputs.Sort(static (left, right) =>
                left.Location.CompareTo(right.Location));

            return new MslTranslationBindingPlan(
                entryPoint,
                stage,
                mode,
                resources.ToArray(),
                argumentBuffers.ToArray(),
                shaderOutputs.ToArray(),
                privateAttachmentDescriptorSet,
                resources.Exists(static resource => resource.IsPrivateAttachment),
                usesFramebufferFetch,
                usesRasterOrderGroups,
                attachmentInterface.Phase?.DepthExport
                    ?? ShaderDepthExport.None,
                attachmentInterface.Phase?.StencilExport
                    ?? ShaderStencilExport.None);
        }

        private static void AddAttachmentBindings(
            ShaderAttachmentInterface attachmentInterface,
            ShaderEntryPointReflection reflection,
            uint privateDescriptorSet,
            uint privateMetalTextureBase,
            HashSet<(uint Set, uint Binding)> physicalVulkanBindings,
            List<MslTranslationResourceBinding> resources,
            List<MslTranslationShaderOutput> shaderOutputs,
            out bool usesFramebufferFetch,
            out bool usesRasterOrderGroups)
        {
            usesFramebufferFetch = false;
            usesRasterOrderGroups = false;
            ShaderAttachmentPhase? phase = attachmentInterface.Phase;
            if (phase is null)
            {
                return;
            }

            Dictionary<(uint Location, uint Index), ShaderStageIoReflection> reflectedOutputs =
                new();
            foreach (ShaderStageIoReflection output in reflection.StageOutputs)
            {
                if (output.BuiltIn == ShaderStageIoBuiltIn.None
                    && output.Location.HasValue
                    && !reflectedOutputs.TryAdd(
                        (output.Location.Value, output.Index),
                        output))
                {
                    throw Failure(
                        $"SPIR-V has duplicate color outputs at location/index "
                        + $"{output.Location.Value}/{output.Index}.");
                }
            }

            Dictionary<uint, uint> primaryOutputs = new();
            HashSet<uint> outputLocations = new();
            HashSet<uint> localInputIndices = new();
            HashSet<uint> rasterOrderedLogicalIds = new();
            foreach (ShaderAttachmentDeclaration attachment in phase.Attachments)
            {
                if (attachment.OutputLocation.HasValue
                    && attachment.OutputIndex == 0)
                {
                    uint location = attachment.OutputLocation.Value;
                    if (!primaryOutputs.TryAdd(
                            location,
                            attachment.LogicalAttachmentId))
                    {
                        throw Failure(
                            $"MSL output location {location} has more than one primary "
                            + "logical attachment.");
                    }

                    if (!reflectedOutputs.TryGetValue(
                            (location, 0),
                            out ShaderStageIoReflection? reflectedOutput))
                    {
                        throw Failure(
                            $"SPIR-V is missing primary output location {location} "
                            + "required by the Metal attachment strategy.");
                    }

                    shaderOutputs.Add(new MslTranslationShaderOutput(
                        location,
                        attachment.LogicalAttachmentId,
                        reflectedOutput.ComponentCount,
                        attachment.NumericClass));
                    outputLocations.Add(location);
                }

                if (attachment.Ordering == ShaderAttachmentOrdering.RasterOrdered)
                {
                    if (!rasterOrderedLogicalIds.Add(attachment.LogicalAttachmentId))
                    {
                        continue;
                    }

                    uint binding = checked(
                        SpirvBindingRemapper.RasterOrderedBindingBase
                        + attachment.LogicalAttachmentId);
                    if (!physicalVulkanBindings.Add((privateDescriptorSet, binding)))
                    {
                        throw Failure(
                            $"Private raster-ordered attachment collides at Vulkan "
                            + $"set={privateDescriptorSet}, binding={binding}.");
                    }

                    uint metalTextureIndex = checked(
                        privateMetalTextureBase
                        + attachment.LogicalAttachmentId);
                    resources.Add(new MslTranslationResourceBinding(
                        new ShaderBindingKey(
                            ShaderAttachmentDeclaration.ReservedAttachmentBindingTable,
                            attachment.LogicalAttachmentId,
                            ShaderBindingClass.UnorderedAccess),
                        privateDescriptorSet,
                        binding,
                        VulkanDescriptorKind.StorageImage,
                        ShaderPhysicalBindingNamespace.Texture,
                        metalTextureIndex,
                        resourceCount: 1,
                        isPrivateAttachment: true));
                    usesRasterOrderGroups = true;
                    continue;
                }

                if (!attachment.InputIndex.HasValue
                    || !localInputIndices.Add(attachment.InputIndex.Value))
                {
                    continue;
                }

                uint inputIndex = attachment.InputIndex.Value;

                if (!physicalVulkanBindings.Add((privateDescriptorSet, inputIndex)))
                {
                    throw Failure(
                        $"Private input attachment collides at Vulkan set="
                        + $"{privateDescriptorSet}, binding={inputIndex}.");
                }

                resources.Add(new MslTranslationResourceBinding(
                    new ShaderBindingKey(
                        ShaderAttachmentDeclaration.ReservedAttachmentBindingTable,
                        inputIndex,
                        ShaderBindingClass.ShaderResource),
                    privateDescriptorSet,
                    inputIndex,
                    VulkanDescriptorKind.InputAttachment,
                    ShaderPhysicalBindingNamespace.Texture,
                    attachment.LogicalAttachmentId,
                    resourceCount: 1,
                    isPrivateAttachment: true));
                usesFramebufferFetch = true;
            }

            foreach ((uint location, uint logicalAttachmentId) in primaryOutputs)
            {
                if (!outputLocations.Contains(location))
                {
                    throw Failure(
                        $"Metal output mapping for logical attachment "
                        + $"{logicalAttachmentId} at location {location} was not frozen.");
                }
            }
        }

        private static void AddDirectBindings(
            MetalShaderBackendLayout metalLayout,
            Dictionary<ShaderBindingKey, VulkanShaderBindingMapping> vulkanByLogicalBinding,
            List<MslTranslationResourceBinding> resources)
        {
            foreach (MetalDirectBindingMapping metal in metalLayout.DirectBindings)
            {
                if (metal.BindingTable != MetalShaderBackendLayout.RootBindingTable)
                {
                    throw Failure(
                        $"Metal direct binding {metal.LogicalBinding} targets unsupported binding table {metal.BindingTable}.");
                }

                VulkanShaderBindingMapping vulkan = TakeVulkanBinding(
                    vulkanByLogicalBinding,
                    metal.LogicalBinding);
                ValidateNamespace(
                    vulkan,
                    metal.Namespace);
                resources.Add(new MslTranslationResourceBinding(
                    metal.LogicalBinding,
                    vulkan.DescriptorSet,
                    vulkan.Binding,
                    vulkan.DescriptorKind,
                    metal.Namespace,
                    metal.Index,
                    1));
            }
        }

        private static void AddReferenceBindings(
            MetalShaderBackendLayout metalLayout,
            Dictionary<ShaderBindingKey, VulkanShaderBindingMapping> vulkanByLogicalBinding,
            List<MslTranslationResourceBinding> resources,
            List<MslTranslationArgumentBufferBinding> argumentBuffers)
        {
            Dictionary<uint, uint> bufferByDescriptorSet = new();
            Dictionary<uint, uint> descriptorSetByBuffer = new();
            foreach (MetalReferenceBufferBindingMapping metal in metalLayout.ReferenceBufferBindings)
            {
                if (metal.BindingTable != MetalShaderBackendLayout.RootBindingTable)
                {
                    throw Failure(
                        $"Metal reference binding {metal.LogicalBinding} targets unsupported binding table {metal.BindingTable}.");
                }

                VulkanShaderBindingMapping vulkan = TakeVulkanBinding(
                    vulkanByLogicalBinding,
                    metal.LogicalBinding);
                ValidateNamespace(
                    vulkan,
                    metal.ResourceNamespace);

                ulong resourceIdValue =
                    metal.ByteOffset / MetalReferenceBufferBindingMapping.ReferenceByteSize;
                if (resourceIdValue > uint.MaxValue)
                {
                    throw Failure(
                        $"Metal reference binding {metal.LogicalBinding} has resource id {resourceIdValue}, which exceeds UInt32.");
                }

                uint resourceId = checked((uint)resourceIdValue);
                if (resourceId > uint.MaxValue - (metal.ReferenceCount - 1))
                {
                    throw Failure(
                        $"Metal reference binding {metal.LogicalBinding} spans resource ids beyond UInt32.");
                }

                resources.Add(new MslTranslationResourceBinding(
                    metal.LogicalBinding,
                    vulkan.DescriptorSet,
                    vulkan.Binding,
                    vulkan.DescriptorKind,
                    metal.ResourceNamespace,
                    resourceId,
                    metal.ReferenceCount));

                if (bufferByDescriptorSet.TryGetValue(
                        vulkan.DescriptorSet,
                        out uint existingBuffer))
                {
                    if (existingBuffer != metal.ReferenceBufferIndex)
                    {
                        throw Failure(
                            $"Vulkan descriptor set {vulkan.DescriptorSet} is split across Metal reference buffers {existingBuffer} and {metal.ReferenceBufferIndex}.");
                    }
                }
                else
                {
                    bufferByDescriptorSet.Add(
                        vulkan.DescriptorSet,
                        metal.ReferenceBufferIndex);
                }

                if (descriptorSetByBuffer.TryGetValue(
                        metal.ReferenceBufferIndex,
                        out uint existingDescriptorSet))
                {
                    if (existingDescriptorSet != vulkan.DescriptorSet)
                    {
                        throw Failure(
                            $"Metal reference buffer {metal.ReferenceBufferIndex} is shared by Vulkan descriptor sets {existingDescriptorSet} and {vulkan.DescriptorSet}.");
                    }
                }
                else
                {
                    descriptorSetByBuffer.Add(
                        metal.ReferenceBufferIndex,
                        vulkan.DescriptorSet);
                }
            }

            foreach (KeyValuePair<uint, uint> mapping in bufferByDescriptorSet)
            {
                argumentBuffers.Add(new MslTranslationArgumentBufferBinding(
                    mapping.Key,
                    mapping.Value));
            }
        }

        private static VulkanShaderBindingMapping TakeVulkanBinding(
            Dictionary<ShaderBindingKey, VulkanShaderBindingMapping> mappings,
            ShaderBindingKey logicalBinding)
        {
            if (!mappings.Remove(
                    logicalBinding,
                    out VulkanShaderBindingMapping mapping))
            {
                throw Failure(
                    $"Metal translation plan contains logical binding {logicalBinding} with no Vulkan mapping.");
            }

            return mapping;
        }

        private static void ValidateNamespace(
            VulkanShaderBindingMapping vulkan,
            ShaderPhysicalBindingNamespace metalNamespace)
        {
            ShaderPhysicalBindingNamespace expected = vulkan.DescriptorKind switch
            {
                VulkanDescriptorKind.Sampler => ShaderPhysicalBindingNamespace.Sampler,
                VulkanDescriptorKind.SampledImage
                    or VulkanDescriptorKind.StorageImage
                    or VulkanDescriptorKind.InputAttachment
                    or VulkanDescriptorKind.UniformTexelBuffer
                    or VulkanDescriptorKind.StorageTexelBuffer => ShaderPhysicalBindingNamespace.Texture,
                VulkanDescriptorKind.UniformBuffer
                    or VulkanDescriptorKind.StorageBuffer
                    or VulkanDescriptorKind.AccelerationStructure => ShaderPhysicalBindingNamespace.Buffer,
                _ => throw Failure(
                    $"Vulkan descriptor kind {vulkan.DescriptorKind} is not supported by the Metal translation path."),
            };

            if (metalNamespace != expected)
            {
                throw Failure(
                    $"Binding {vulkan.LogicalBinding} maps Vulkan {vulkan.DescriptorKind} to Metal {metalNamespace}, but {expected} is required.");
            }
        }

        private static ShaderCompilerException Failure(string message)
        {
            return new ShaderCompilerException(
                ShaderCompilerErrorCode.MslTranslateFailed,
                message);
        }
    }
}
