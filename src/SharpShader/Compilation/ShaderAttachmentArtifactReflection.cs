using System;

namespace SharpShader.Compilation
{
    public enum ShaderStageIoDirection : byte
    {
        Input,
        Output,
    }

    public enum ShaderStageIoBuiltIn : byte
    {
        None,
        Position,
        Color,
        Depth,
        DepthGreaterEqual,
        DepthLessEqual,
        StencilReference,
        PrimitiveId,
        RenderTargetArrayIndex,
        ViewportArrayIndex,
        SampleIndex,
        Coverage,
        ClipDistance,
        CullDistance,
        VertexId,
        InstanceId,
        FrontFace,
        TessellationFactor,
        InsideTessellationFactor,
        Barycentrics,
        ShadingRate,
        CullPrimitive,
        InnerCoverage,
        PointSize,
        PointCoordinate,
        SamplePosition,
        Layer,
        TessellationCoordinate,
        InvocationId,
    }

    [Flags]
    public enum ShaderAttachmentArtifactRequirement : byte
    {
        None = 0,
        RasterOrderedViews = 1 << 0,
        StencilReferenceExport = 1 << 1,
        FramebufferLocalRead = 1 << 2,
        OrderedPixelFragmentInterlock = 1 << 3,
        UnorderedFragmentInterlock = 1 << 4,
        SampleOrderedFragmentInterlock = 1 << 5,
        ShadingRateOrderedFragmentInterlock = 1 << 6,
    }

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

    public sealed class ShaderInputAttachmentReflection :
        IEquatable<ShaderInputAttachmentReflection>
    {
        public string Name { get; }
        public uint InputAttachmentIndex { get; }
        public uint? Location { get; }
        public uint? Component { get; }
        public ShaderAttachmentNumericClass NumericClass { get; }
        public ShaderAttachmentSampleMode SampleMode { get; }
        public ShaderPhysicalBindingLocation PhysicalLocation { get; }

        public ShaderInputAttachmentReflection(
            string name,
            uint inputAttachmentIndex,
            uint? location,
            uint? component,
            ShaderAttachmentNumericClass numericClass,
            ShaderAttachmentSampleMode sampleMode,
            ShaderPhysicalBindingLocation physicalLocation)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException(
                    "Input attachment name must not be empty.",
                    nameof(name));
            }

            if (component > 3)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(component),
                    component,
                    "Input attachment component must be in [0, 3].");
            }

            if (!Enum.IsDefined(numericClass)
                || numericClass == ShaderAttachmentNumericClass.DepthStencil)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(numericClass),
                    numericClass,
                    "Input attachment requires a color numeric class.");
            }

            if (!Enum.IsDefined(sampleMode))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sampleMode),
                    sampleMode,
                    "Input attachment sample mode is not defined.");
            }

            if (physicalLocation.Backend != ShaderBackendKind.Vulkan
                || physicalLocation.Namespace != ShaderPhysicalBindingNamespace.Unified)
            {
                throw new ArgumentException(
                    "SPIR-V input attachments require a Vulkan unified physical binding.",
                    nameof(physicalLocation));
            }

            Name = name;
            InputAttachmentIndex = inputAttachmentIndex;
            Location = location;
            Component = component;
            NumericClass = numericClass;
            SampleMode = sampleMode;
            PhysicalLocation = physicalLocation;
        }

        public bool Equals(ShaderInputAttachmentReflection? other)
        {
            return other is not null
                && string.Equals(Name, other.Name, StringComparison.Ordinal)
                && InputAttachmentIndex == other.InputAttachmentIndex
                && Location == other.Location
                && Component == other.Component
                && NumericClass == other.NumericClass
                && SampleMode == other.SampleMode
                && PhysicalLocation == other.PhysicalLocation;
        }

        public override bool Equals(object? obj) =>
            Equals(obj as ShaderInputAttachmentReflection);

        public override int GetHashCode()
        {
            return HashCode.Combine(
                Name,
                InputAttachmentIndex,
                Location,
                Component,
                NumericClass,
                SampleMode,
                PhysicalLocation);
        }
    }
}
