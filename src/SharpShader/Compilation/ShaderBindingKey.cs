using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public readonly struct ShaderBindingKey : IEquatable<ShaderBindingKey>
    {
        public uint Table { get; }
        public uint Slot { get; }
        public ShaderBindingClass Type { get; }

        public ShaderBindingKey(uint table, uint slot, ShaderBindingClass type)
        {
            if (!Enum.IsDefined(type))
            {
                throw new ArgumentOutOfRangeException(nameof(type), type, "Binding class is not defined.");
            }

            Table = table;
            Slot = slot;
            Type = type;
        }

        public bool Equals(ShaderBindingKey other)
        {
            return Table == other.Table && Slot == other.Slot && Type == other.Type;
        }

        public override bool Equals(object? obj)
        {
            return obj is ShaderBindingKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Table, Slot, Type);
        }

        public static bool operator ==(ShaderBindingKey left, ShaderBindingKey right) => left.Equals(right);
        public static bool operator !=(ShaderBindingKey left, ShaderBindingKey right) => !left.Equals(right);

        public override string ToString()
        {
            return $"table={Table}, slot={Slot}, type={Type}";
        }
    }
}
