using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public readonly struct ShaderThreadGroupDimension : IEquatable<ShaderThreadGroupDimension>
    {
        public ShaderThreadGroupDimensionKind Kind { get; }
        public uint Value { get; }
        public uint? DefaultValue { get; }

        private ShaderThreadGroupDimension(ShaderThreadGroupDimensionKind kind, uint value, uint? defaultValue)
        {
            Kind = kind;
            Value = value;
            DefaultValue = defaultValue;
        }

        public static ShaderThreadGroupDimension Fixed(uint count)
        {
            if (count == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "A fixed thread-group dimension must be greater than zero.");
            }

            return new ShaderThreadGroupDimension(ShaderThreadGroupDimensionKind.Fixed, count, null);
        }

        public static ShaderThreadGroupDimension SpecializationConstant(uint constantId, uint? defaultValue = null)
        {
            if (defaultValue == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(defaultValue), "A thread-group default value must be greater than zero.");
            }

            return new ShaderThreadGroupDimension(ShaderThreadGroupDimensionKind.SpecializationConstant, constantId, defaultValue);
        }

        public uint FixedCount => Kind == ShaderThreadGroupDimensionKind.Fixed
            ? Value
            : throw new InvalidOperationException("This thread-group dimension is specialization-constant sized.");

        public uint SpecializationConstantId => Kind == ShaderThreadGroupDimensionKind.SpecializationConstant
            ? Value
            : throw new InvalidOperationException("This thread-group dimension is fixed.");

        public bool Equals(ShaderThreadGroupDimension other)
        {
            return Kind == other.Kind && Value == other.Value && DefaultValue == other.DefaultValue;
        }

        public override bool Equals(object? obj) => obj is ShaderThreadGroupDimension other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Kind, Value, DefaultValue);
        public static bool operator ==(ShaderThreadGroupDimension left, ShaderThreadGroupDimension right) => left.Equals(right);
        public static bool operator !=(ShaderThreadGroupDimension left, ShaderThreadGroupDimension right) => !left.Equals(right);
    }
}
