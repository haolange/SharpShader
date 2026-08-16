using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public readonly struct ShaderArrayExtent : IEquatable<ShaderArrayExtent>
    {
        public ShaderArrayExtentKind Kind { get; }
        public uint Value { get; }

        private ShaderArrayExtent(ShaderArrayExtentKind kind, uint value)
        {
            Kind = kind;
            Value = value;
        }

        public static ShaderArrayExtent Bounded(uint count)
        {
            if (count == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "A bounded array extent must be greater than zero.");
            }

            return new ShaderArrayExtent(ShaderArrayExtentKind.Bounded, count);
        }

        public static ShaderArrayExtent Runtime()
        {
            return new ShaderArrayExtent(ShaderArrayExtentKind.Runtime, 0);
        }

        public static ShaderArrayExtent SpecializationConstant(uint constantId)
        {
            return new ShaderArrayExtent(ShaderArrayExtentKind.SpecializationConstant, constantId);
        }

        public uint BoundedCount => Kind == ShaderArrayExtentKind.Bounded
            ? Value
            : throw new InvalidOperationException("Only bounded array extents have a fixed count.");

        public uint SpecializationConstantId => Kind == ShaderArrayExtentKind.SpecializationConstant
            ? Value
            : throw new InvalidOperationException("This array extent is not specialization-constant sized.");

        public bool Equals(ShaderArrayExtent other) => Kind == other.Kind && Value == other.Value;
        public override bool Equals(object? obj) => obj is ShaderArrayExtent other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Kind, Value);
        public static bool operator ==(ShaderArrayExtent left, ShaderArrayExtent right) => left.Equals(right);
        public static bool operator !=(ShaderArrayExtent left, ShaderArrayExtent right) => !left.Equals(right);
    }
}
