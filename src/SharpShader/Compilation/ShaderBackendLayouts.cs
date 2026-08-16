using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public sealed class ShaderBackendLayouts : IEquatable<ShaderBackendLayouts>
    {
        public ShaderLayoutSignature LogicalLayoutSignature { get; }
        public Dx12ShaderBackendLayout? Dx12 { get; }
        public VulkanShaderBackendLayout? Vulkan { get; }
        public MetalShaderBackendLayout? Metal { get; }

        public ShaderBackendLayouts(
            ShaderLayoutSignature logicalLayoutSignature,
            Dx12ShaderBackendLayout? dx12 = null,
            VulkanShaderBackendLayout? vulkan = null,
            MetalShaderBackendLayout? metal = null)
        {
            if (dx12 is null && vulkan is null && metal is null)
            {
                throw new ArgumentException("At least one backend layout must be provided.");
            }

            LogicalLayoutSignature = logicalLayoutSignature;
            Dx12 = dx12;
            Vulkan = vulkan;
            Metal = metal;
        }

        public bool Equals(ShaderBackendLayouts? other)
        {
            return other is not null
                && LogicalLayoutSignature == other.LogicalLayoutSignature
                && Equals(Dx12, other.Dx12)
                && Equals(Vulkan, other.Vulkan)
                && Equals(Metal, other.Metal);
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderBackendLayouts);
        public override int GetHashCode() => HashCode.Combine(LogicalLayoutSignature, Dx12, Vulkan, Metal);
    }
}
