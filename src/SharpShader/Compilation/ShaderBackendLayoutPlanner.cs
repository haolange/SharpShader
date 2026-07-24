using System;
using System.Collections.Generic;
using SharpShader.Compilation.Internal;

namespace SharpShader.Compilation
{
    public static class ShaderBackendLayoutPlanner
    {
        public static ShaderBackendLayouts Plan(
            ShaderInterfaceLayout logicalLayout,
            IReadOnlyDictionary<ShaderBindingKey, uint>? metalArrayCapacities = null)
        {
            ArgumentNullException.ThrowIfNull(logicalLayout);
            ValidateCapacities(logicalLayout, metalArrayCapacities);

            return new ShaderBackendLayouts(
                logicalLayout.Signature,
                PlanDx12(logicalLayout),
                PlanVulkan(logicalLayout),
                PlanMetal(logicalLayout, metalArrayCapacities));
        }

        private static Dx12ShaderBackendLayout PlanDx12(ShaderInterfaceLayout layout)
        {
            Dx12ShaderBindingMapping[] mappings =
                new Dx12ShaderBindingMapping[layout.Bindings.Count];
            for (int index = 0; index < layout.Bindings.Count; ++index)
            {
                ShaderBindingKey key = layout.Bindings[index].Key;
                mappings[index] = new Dx12ShaderBindingMapping(
                    key,
                    key.Table,
                    key.Slot,
                    key.Type);
            }

            return new Dx12ShaderBackendLayout(mappings);
        }

        private static VulkanShaderBackendLayout PlanVulkan(ShaderInterfaceLayout layout)
        {
            VulkanShaderBindingMapping[] mappings =
                new VulkanShaderBindingMapping[layout.Bindings.Count];
            uint currentLogicalTable = 0;
            uint descriptorSet = 0;
            uint bindingIndex = 0;
            bool hasCurrentTable = false;

            for (int index = 0; index < layout.Bindings.Count; ++index)
            {
                ShaderLogicalBinding logicalBinding = layout.Bindings[index];
                if (!hasCurrentTable || logicalBinding.Key.Table != currentLogicalTable)
                {
                    if (hasCurrentTable)
                    {
                        descriptorSet = checked(descriptorSet + 1);
                    }

                    currentLogicalTable = logicalBinding.Key.Table;
                    bindingIndex = 0;
                    hasCurrentTable = true;
                }

                mappings[index] = new VulkanShaderBindingMapping(
                    logicalBinding.Key,
                    descriptorSet,
                    bindingIndex,
                    ShaderBackendLayoutSemantics.GetVulkanDescriptorKind(logicalBinding));
                bindingIndex = checked(bindingIndex + 1);
            }

            return new VulkanShaderBackendLayout(mappings);
        }

        private static MetalShaderBackendLayout PlanMetal(
            ShaderInterfaceLayout layout,
            IReadOnlyDictionary<ShaderBindingKey, uint>? arrayCapacities)
        {
            if (CanUseMetalDirectBindings(layout))
            {
                return PlanMetalDirect(layout);
            }

            return PlanMetalReferenceBuffers(layout, arrayCapacities);
        }

        private static bool CanUseMetalDirectBindings(ShaderInterfaceLayout layout)
        {
            if (layout.Bindings.Count == 0)
            {
                return true;
            }

            uint table = layout.Bindings[0].Key.Table;
            foreach (ShaderLogicalBinding binding in layout.Bindings)
            {
                if (binding.Key.Table != table || binding.Shape.Array.IsArray)
                {
                    return false;
                }
            }

            return true;
        }

        private static MetalShaderBackendLayout PlanMetalDirect(ShaderInterfaceLayout layout)
        {
            MetalDirectBindingMapping[] mappings =
                new MetalDirectBindingMapping[layout.Bindings.Count];
            uint bufferIndex = 0;
            uint textureIndex = 0;
            uint samplerIndex = 0;

            for (int index = 0; index < layout.Bindings.Count; ++index)
            {
                ShaderLogicalBinding binding = layout.Bindings[index];
                ShaderPhysicalBindingNamespace bindingNamespace =
                    ShaderBackendLayoutSemantics.GetMetalNamespace(binding);
                uint physicalIndex = bindingNamespace switch
                {
                    ShaderPhysicalBindingNamespace.Buffer => bufferIndex,
                    ShaderPhysicalBindingNamespace.Texture => textureIndex,
                    ShaderPhysicalBindingNamespace.Sampler => samplerIndex,
                    _ => throw new InvalidOperationException(
                        $"Unsupported Metal namespace {bindingNamespace}."),
                };

                mappings[index] = new MetalDirectBindingMapping(
                    binding.Key,
                    MetalShaderBackendLayout.RootArgumentTable,
                    bindingNamespace,
                    physicalIndex);

                switch (bindingNamespace)
                {
                    case ShaderPhysicalBindingNamespace.Buffer:
                        bufferIndex = checked(bufferIndex + 1);
                        break;
                    case ShaderPhysicalBindingNamespace.Texture:
                        textureIndex = checked(textureIndex + 1);
                        break;
                    case ShaderPhysicalBindingNamespace.Sampler:
                        samplerIndex = checked(samplerIndex + 1);
                        break;
                }
            }

            return new MetalShaderBackendLayout(mappings);
        }

        private static MetalShaderBackendLayout PlanMetalReferenceBuffers(
            ShaderInterfaceLayout layout,
            IReadOnlyDictionary<ShaderBindingKey, uint>? arrayCapacities)
        {
            MetalReferenceBufferBindingMapping[] mappings =
                new MetalReferenceBufferBindingMapping[layout.Bindings.Count];
            uint currentLogicalTable = 0;
            uint referenceBufferIndex = 0;
            ulong byteOffset = 0;
            bool hasCurrentTable = false;

            for (int index = 0; index < layout.Bindings.Count; ++index)
            {
                ShaderLogicalBinding binding = layout.Bindings[index];
                if (!hasCurrentTable || binding.Key.Table != currentLogicalTable)
                {
                    if (hasCurrentTable)
                    {
                        referenceBufferIndex = checked(referenceBufferIndex + 1);
                    }

                    currentLogicalTable = binding.Key.Table;
                    byteOffset = 0;
                    hasCurrentTable = true;
                }

                uint referenceCount = ResolveReferenceCount(binding, arrayCapacities);
                mappings[index] = new MetalReferenceBufferBindingMapping(
                    binding.Key,
                    MetalShaderBackendLayout.RootArgumentTable,
                    ShaderBackendLayoutSemantics.GetMetalNamespace(binding),
                    referenceBufferIndex,
                    byteOffset,
                    referenceCount);
                byteOffset = checked(
                    byteOffset
                    + checked((ulong)referenceCount * MetalReferenceBufferBindingMapping.ReferenceByteSize));
            }

            return new MetalShaderBackendLayout(referenceBufferBindings: mappings);
        }

        private static uint ResolveReferenceCount(
            ShaderLogicalBinding binding,
            IReadOnlyDictionary<ShaderBindingKey, uint>? arrayCapacities)
        {
            uint? boundedCount = binding.Shape.Array.BoundedElementCount;
            if (boundedCount.HasValue)
            {
                return boundedCount.Value;
            }

            if (arrayCapacities is null
                || !arrayCapacities.TryGetValue(binding.Key, out uint capacity)
                || capacity == 0)
            {
                throw new ArgumentException(
                    $"Metal reference binding {binding.Key} requires a positive flattened capacity for its runtime or specialization-sized array.",
                    nameof(arrayCapacities));
            }

            return capacity;
        }

        private static void ValidateCapacities(
            ShaderInterfaceLayout layout,
            IReadOnlyDictionary<ShaderBindingKey, uint>? arrayCapacities)
        {
            if (arrayCapacities is null)
            {
                return;
            }

            Dictionary<ShaderBindingKey, ShaderLogicalBinding> bindingsByKey = new();
            foreach (ShaderLogicalBinding binding in layout.Bindings)
            {
                bindingsByKey.Add(binding.Key, binding);
            }

            foreach (KeyValuePair<ShaderBindingKey, uint> capacity in arrayCapacities)
            {
                if (!bindingsByKey.TryGetValue(capacity.Key, out ShaderLogicalBinding? binding))
                {
                    throw new ArgumentException(
                        $"Metal array capacity references unknown logical binding {capacity.Key}.",
                        nameof(arrayCapacities));
                }

                if (binding.Shape.Array.BoundedElementCount.HasValue)
                {
                    throw new ArgumentException(
                        $"Metal array capacity for {capacity.Key} is invalid because the binding has a statically bounded shape.",
                        nameof(arrayCapacities));
                }

                if (capacity.Value == 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(arrayCapacities),
                        $"Metal array capacity for {capacity.Key} must be positive.");
                }
            }
        }
    }
}
