using System;
using System.Collections.Generic;
using SharpShader.Compilation;

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

        public MslTranslationResourceBinding(
            ShaderBindingKey logicalBinding,
            uint descriptorSet,
            uint binding,
            VulkanDescriptorKind descriptorKind,
            ShaderPhysicalBindingNamespace metalNamespace,
            uint metalIndex,
            uint resourceCount)
        {
            LogicalBinding = logicalBinding;
            DescriptorSet = descriptorSet;
            Binding = binding;
            DescriptorKind = descriptorKind;
            MetalNamespace = metalNamespace;
            MetalIndex = metalIndex;
            ResourceCount = resourceCount;
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

    internal sealed class MslTranslationBindingPlan
    {
        private readonly MslTranslationResourceBinding[] m_Resources;
        private readonly MslTranslationArgumentBufferBinding[] m_ArgumentBuffers;

        public string EntryPoint { get; }
        public ShaderExecutionStage Stage { get; }
        public MslTranslationBindingMode Mode { get; }
        public IReadOnlyList<MslTranslationResourceBinding> Resources => m_Resources;
        public IReadOnlyList<MslTranslationArgumentBufferBinding> ArgumentBuffers => m_ArgumentBuffers;

        private MslTranslationBindingPlan(
            string entryPoint,
            ShaderExecutionStage stage,
            MslTranslationBindingMode mode,
            MslTranslationResourceBinding[] resources,
            MslTranslationArgumentBufferBinding[] argumentBuffers)
        {
            EntryPoint = entryPoint;
            Stage = stage;
            Mode = mode;
            m_Resources = resources;
            m_ArgumentBuffers = argumentBuffers;
        }

        public static MslTranslationBindingPlan Create(
            string entryPoint,
            ShaderExecutionStage stage,
            IReadOnlyList<VulkanShaderBindingMapping> vulkanBindings,
            MetalShaderBackendLayout metalLayout)
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

            resources.Sort(static (left, right) =>
            {
                int descriptorSet = left.DescriptorSet.CompareTo(right.DescriptorSet);
                return descriptorSet != 0
                    ? descriptorSet
                    : left.Binding.CompareTo(right.Binding);
            });
            argumentBuffers.Sort(static (left, right) =>
                left.DescriptorSet.CompareTo(right.DescriptorSet));

            return new MslTranslationBindingPlan(
                entryPoint,
                stage,
                mode,
                resources.ToArray(),
                argumentBuffers.ToArray());
        }

        private static void AddDirectBindings(
            MetalShaderBackendLayout metalLayout,
            Dictionary<ShaderBindingKey, VulkanShaderBindingMapping> vulkanByLogicalBinding,
            List<MslTranslationResourceBinding> resources)
        {
            foreach (MetalDirectBindingMapping metal in metalLayout.DirectBindings)
            {
                if (metal.ArgumentTable != MetalShaderBackendLayout.RootArgumentTable)
                {
                    throw Failure(
                        $"Metal direct binding {metal.LogicalBinding} targets unsupported argument table {metal.ArgumentTable}.");
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
                if (metal.ArgumentTable != MetalShaderBackendLayout.RootArgumentTable)
                {
                    throw Failure(
                        $"Metal reference binding {metal.LogicalBinding} targets unsupported argument table {metal.ArgumentTable}.");
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
