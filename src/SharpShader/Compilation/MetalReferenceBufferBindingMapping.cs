using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public readonly struct MetalReferenceBufferBindingMapping : IEquatable<MetalReferenceBufferBindingMapping>
    {
        public const uint ReferenceByteSize = 8;

        public ShaderBindingKey LogicalBinding { get; }
        public uint BindingTable { get; }
        public ShaderPhysicalBindingNamespace ResourceNamespace { get; }
        public uint ReferenceBufferIndex { get; }
        public ulong ByteOffset { get; }
        public uint ReferenceCount { get; }

        public MetalReferenceBufferBindingMapping(
            ShaderBindingKey logicalBinding,
            uint bindingTable,
            ShaderPhysicalBindingNamespace resourceNamespace,
            uint referenceBufferIndex,
            ulong byteOffset,
            uint referenceCount)
        {
            ShaderBackendLayoutValidation.ValidateMetalNamespace(resourceNamespace, nameof(resourceNamespace));
            if ((byteOffset % ReferenceByteSize) != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(byteOffset),
                    "Metal reference-buffer offsets must be aligned to the fixed 8-byte reference size.");
            }

            if (referenceCount == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(referenceCount), "Metal reference buffers require at least one reference.");
            }

            _ = checked(byteOffset + checked((ulong)referenceCount * ReferenceByteSize));

            LogicalBinding = logicalBinding;
            BindingTable = bindingTable;
            ResourceNamespace = resourceNamespace;
            ReferenceBufferIndex = referenceBufferIndex;
            ByteOffset = byteOffset;
            ReferenceCount = referenceCount;
        }

        internal ulong EndByteOffset => checked(ByteOffset + checked((ulong)ReferenceCount * ReferenceByteSize));

        public bool Equals(MetalReferenceBufferBindingMapping other)
        {
            return LogicalBinding == other.LogicalBinding
                && BindingTable == other.BindingTable
                && ResourceNamespace == other.ResourceNamespace
                && ReferenceBufferIndex == other.ReferenceBufferIndex
                && ByteOffset == other.ByteOffset
                && ReferenceCount == other.ReferenceCount;
        }

        public override bool Equals(object? obj) => obj is MetalReferenceBufferBindingMapping other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(LogicalBinding, BindingTable, ResourceNamespace, ReferenceBufferIndex, ByteOffset, ReferenceCount);
        public static bool operator ==(MetalReferenceBufferBindingMapping left, MetalReferenceBufferBindingMapping right) => left.Equals(right);
        public static bool operator !=(MetalReferenceBufferBindingMapping left, MetalReferenceBufferBindingMapping right) => !left.Equals(right);
    }
}
