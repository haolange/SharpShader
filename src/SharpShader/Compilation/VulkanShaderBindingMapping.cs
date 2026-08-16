using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public readonly struct VulkanShaderBindingMapping : IEquatable<VulkanShaderBindingMapping>
    {
        public ShaderBindingKey LogicalBinding { get; }
        public uint DescriptorSet { get; }
        public uint Binding { get; }
        public VulkanDescriptorKind DescriptorKind { get; }

        public VulkanShaderBindingMapping(
            ShaderBindingKey logicalBinding,
            uint descriptorSet,
            uint binding,
            VulkanDescriptorKind descriptorKind)
        {
            if (!Enum.IsDefined(descriptorKind))
            {
                throw new ArgumentOutOfRangeException(nameof(descriptorKind), descriptorKind, "Vulkan descriptor kind is not defined.");
            }

            LogicalBinding = logicalBinding;
            DescriptorSet = descriptorSet;
            Binding = binding;
            DescriptorKind = descriptorKind;
        }

        public bool Equals(VulkanShaderBindingMapping other)
        {
            return LogicalBinding == other.LogicalBinding
                && DescriptorSet == other.DescriptorSet
                && Binding == other.Binding
                && DescriptorKind == other.DescriptorKind;
        }

        public override bool Equals(object? obj) => obj is VulkanShaderBindingMapping other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(LogicalBinding, DescriptorSet, Binding, DescriptorKind);
        public static bool operator ==(VulkanShaderBindingMapping left, VulkanShaderBindingMapping right) => left.Equals(right);
        public static bool operator !=(VulkanShaderBindingMapping left, VulkanShaderBindingMapping right) => !left.Equals(right);
    }
}
