using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public readonly struct MetalDirectBindingMapping : IEquatable<MetalDirectBindingMapping>
    {
        public ShaderBindingKey LogicalBinding { get; }
        public uint BindingTable { get; }
        public ShaderPhysicalBindingNamespace Namespace { get; }
        public uint Index { get; }

        public MetalDirectBindingMapping(
            ShaderBindingKey logicalBinding,
            uint bindingTable,
            ShaderPhysicalBindingNamespace bindingNamespace,
            uint index)
        {
            ShaderBackendLayoutValidation.ValidateMetalNamespace(bindingNamespace, nameof(bindingNamespace));

            LogicalBinding = logicalBinding;
            BindingTable = bindingTable;
            Namespace = bindingNamespace;
            Index = index;
        }

        public bool Equals(MetalDirectBindingMapping other)
        {
            return LogicalBinding == other.LogicalBinding
                && BindingTable == other.BindingTable
                && Namespace == other.Namespace
                && Index == other.Index;
        }

        public override bool Equals(object? obj) => obj is MetalDirectBindingMapping other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(LogicalBinding, BindingTable, Namespace, Index);
        public static bool operator ==(MetalDirectBindingMapping left, MetalDirectBindingMapping right) => left.Equals(right);
        public static bool operator !=(MetalDirectBindingMapping left, MetalDirectBindingMapping right) => !left.Equals(right);
    }
}
