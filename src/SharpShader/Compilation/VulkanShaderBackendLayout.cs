using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public sealed class VulkanShaderBackendLayout : IEquatable<VulkanShaderBackendLayout>
    {
        private readonly ReadOnlyCollection<VulkanShaderBindingMapping> m_Bindings;

        public IReadOnlyList<VulkanShaderBindingMapping> Bindings => m_Bindings;

        public VulkanShaderBackendLayout(IEnumerable<VulkanShaderBindingMapping> bindings)
        {
            VulkanShaderBindingMapping[] copy = ShaderBackendLayoutValidation.MaterializeAndSort(
                bindings,
                static mapping => mapping.LogicalBinding,
                nameof(bindings));

            HashSet<ShaderBindingKey> logicalBindings = new();
            HashSet<(uint Set, uint Binding)> physicalBindings = new();
            foreach (VulkanShaderBindingMapping mapping in copy)
            {
                if (!logicalBindings.Add(mapping.LogicalBinding))
                {
                    throw new ArgumentException($"Vulkan layout contains duplicate logical binding {mapping.LogicalBinding}.", nameof(bindings));
                }

                if (!physicalBindings.Add((mapping.DescriptorSet, mapping.Binding)))
                {
                    throw new ArgumentException(
                        $"Vulkan layout contains duplicate physical binding set={mapping.DescriptorSet}, binding={mapping.Binding}.",
                        nameof(bindings));
                }
            }

            m_Bindings = Array.AsReadOnly(copy);
        }

        public bool Equals(VulkanShaderBackendLayout? other)
        {
            return other is not null && ShaderBackendLayoutValidation.SequenceEqual(m_Bindings, other.m_Bindings);
        }

        public override bool Equals(object? obj) => Equals(obj as VulkanShaderBackendLayout);
        public override int GetHashCode() => ShaderBackendLayoutValidation.GetSequenceHashCode(m_Bindings);
    }
}
