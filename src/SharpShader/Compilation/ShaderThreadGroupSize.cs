using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public readonly struct ShaderThreadGroupSize : IEquatable<ShaderThreadGroupSize>
    {
        public ShaderThreadGroupDimension X { get; }
        public ShaderThreadGroupDimension Y { get; }
        public ShaderThreadGroupDimension Z { get; }

        public ShaderThreadGroupSize(
            ShaderThreadGroupDimension x,
            ShaderThreadGroupDimension y,
            ShaderThreadGroupDimension z)
        {
            ValidateDimension(x, nameof(x));
            ValidateDimension(y, nameof(y));
            ValidateDimension(z, nameof(z));
            X = x;
            Y = y;
            Z = z;
        }

        public static ShaderThreadGroupSize Fixed(uint x, uint y, uint z)
        {
            return new ShaderThreadGroupSize(
                ShaderThreadGroupDimension.Fixed(x),
                ShaderThreadGroupDimension.Fixed(y),
                ShaderThreadGroupDimension.Fixed(z));
        }

        private static void ValidateDimension(ShaderThreadGroupDimension dimension, string parameterName)
        {
            if (!Enum.IsDefined(dimension.Kind)
                || (dimension.Kind == ShaderThreadGroupDimensionKind.Fixed && dimension.Value == 0)
                || dimension.DefaultValue == 0)
            {
                throw new ArgumentException("Thread-group dimension is not valid.", parameterName);
            }
        }

        public bool Equals(ShaderThreadGroupSize other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object? obj) => obj is ShaderThreadGroupSize other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public static bool operator ==(ShaderThreadGroupSize left, ShaderThreadGroupSize right) => left.Equals(right);
        public static bool operator !=(ShaderThreadGroupSize left, ShaderThreadGroupSize right) => !left.Equals(right);
    }
}
