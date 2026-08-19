using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.Compilation;
using Rhi = global::SharpGPU;

namespace SharpShader.SharpGPU
{
    public readonly struct SharpGpuBindingLocation : IEquatable<SharpGpuBindingLocation>
    {
        public ShaderBindingKey LogicalBinding { get; }
        public uint BindingTableIndex { get; }
        public uint Slot { get; }
        public uint Count { get; }
        public Rhi.ERHIBindType BindType { get; }
        public Rhi.ERHIShaderStageMask ShaderStages { get; }
        public int ElementIndex { get; }

        internal SharpGpuBindingLocation(
            ShaderBindingKey logicalBinding,
            uint bindingTableIndex,
            uint slot,
            uint count,
            Rhi.ERHIBindType bindType,
            Rhi.ERHIShaderStageMask shaderStages,
            int elementIndex)
        {
            LogicalBinding = logicalBinding;
            BindingTableIndex = bindingTableIndex;
            Slot = slot;
            Count = count;
            BindType = bindType;
            ShaderStages = shaderStages;
            ElementIndex = elementIndex;
        }

        public bool Equals(SharpGpuBindingLocation other)
        {
            return LogicalBinding == other.LogicalBinding
                && BindingTableIndex == other.BindingTableIndex
                && Slot == other.Slot
                && Count == other.Count
                && BindType == other.BindType
                && ShaderStages == other.ShaderStages
                && ElementIndex == other.ElementIndex;
        }

        public override bool Equals(object? obj)
        {
            return obj is SharpGpuBindingLocation other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(
                LogicalBinding,
                BindingTableIndex,
                Slot,
                Count,
                BindType,
                ShaderStages,
                ElementIndex);
        }

        public static bool operator ==(SharpGpuBindingLocation left, SharpGpuBindingLocation right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(SharpGpuBindingLocation left, SharpGpuBindingLocation right)
        {
            return !left.Equals(right);
        }
    }
}
