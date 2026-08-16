using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public sealed class ShaderValueMember : IEquatable<ShaderValueMember>
    {
        public string Name { get; }
        public uint ByteOffset { get; }
        public uint ByteSize { get; }
        public ShaderValueLayout Value { get; }

        public ShaderValueMember(string name, uint byteOffset, uint byteSize, ShaderValueLayout value)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Value member name must not be empty.", nameof(name));
            }

            if (byteSize == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(byteSize), "Value member byte size must be greater than zero.");
            }

            _ = checked(byteOffset + byteSize);
            ArgumentNullException.ThrowIfNull(value);

            Name = name;
            ByteOffset = byteOffset;
            ByteSize = byteSize;
            Value = value;
        }

        public bool Equals(ShaderValueMember? other)
        {
            return other is not null
                && string.Equals(Name, other.Name, StringComparison.Ordinal)
                && ByteOffset == other.ByteOffset
                && ByteSize == other.ByteSize
                && Value.Equals(other.Value);
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderValueMember);
        public override int GetHashCode() => HashCode.Combine(Name, ByteOffset, ByteSize, Value);
    }
}
