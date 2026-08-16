using System;

namespace SharpShader.Compilation
{

    public sealed class ShaderStageIoReflection : IEquatable<ShaderStageIoReflection>
    {
        public string Name { get; }
        public ShaderStageIoDirection Direction { get; }
        public uint? Location { get; }
        public uint Index { get; }
        public uint Component { get; }
        public ShaderStageIoBuiltIn BuiltIn { get; }
        public ShaderAttachmentNumericClass NumericClass { get; }
        public uint ComponentCount { get; }

        public ShaderStageIoReflection(
            string name,
            ShaderStageIoDirection direction,
            uint? location,
            uint index,
            uint component,
            ShaderStageIoBuiltIn builtIn,
            ShaderAttachmentNumericClass numericClass,
            uint componentCount)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException(
                    "Stage I/O name must not be empty.",
                    nameof(name));
            }

            if (!Enum.IsDefined(direction))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(direction),
                    direction,
                    "Stage I/O direction is not defined.");
            }

            if (!Enum.IsDefined(builtIn))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(builtIn),
                    builtIn,
                    "Stage I/O builtin is not defined.");
            }

            if (!Enum.IsDefined(numericClass)
                || numericClass == ShaderAttachmentNumericClass.DepthStencil)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(numericClass),
                    numericClass,
                    "Stage I/O requires a color numeric class.");
            }

            if (component > 3)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(component),
                    component,
                    "Stage I/O component must be in [0, 3].");
            }

            if (componentCount is < 1 or > 4)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(componentCount),
                    componentCount,
                    "Stage I/O component count must be in [1, 4].");
            }

            if (component + componentCount > 4)
            {
                throw new ArgumentException(
                    "Stage I/O component range exceeds a four-component location.");
            }

            if (builtIn == ShaderStageIoBuiltIn.None && !location.HasValue)
            {
                throw new ArgumentException(
                    "Non-builtin stage I/O requires a location.",
                    nameof(location));
            }

            Name = name;
            Direction = direction;
            Location = location;
            Index = index;
            Component = component;
            BuiltIn = builtIn;
            NumericClass = numericClass;
            ComponentCount = componentCount;
        }

        public bool Equals(ShaderStageIoReflection? other)
        {
            return other is not null
                && string.Equals(Name, other.Name, StringComparison.Ordinal)
                && Direction == other.Direction
                && Location == other.Location
                && Index == other.Index
                && Component == other.Component
                && BuiltIn == other.BuiltIn
                && NumericClass == other.NumericClass
                && ComponentCount == other.ComponentCount;
        }

        public override bool Equals(object? obj) =>
            Equals(obj as ShaderStageIoReflection);

        public override int GetHashCode()
        {
            return HashCode.Combine(
                Name,
                Direction,
                Location,
                Index,
                Component,
                BuiltIn,
                NumericClass,
                ComponentCount);
        }
    }
}
