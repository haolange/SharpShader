using System;

namespace SharpShader.Compilation
{

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
