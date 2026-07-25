using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{
    public enum ShaderArtifactKind
    {
        Dxil,
        SpirV,
        MslSource,
        MetalLibrary,
    }

    public enum ShaderBackendKind
    {
        DirectX12,
        Vulkan,
        Metal,
    }

    public enum ShaderPhysicalBindingNamespace
    {
        Unified,
        ShaderResource,
        Sampler,
        ConstantBuffer,
        UnorderedAccess,
        Buffer,
        Texture,
    }

    public readonly struct ShaderPhysicalBindingLocation : IEquatable<ShaderPhysicalBindingLocation>
    {
        public ShaderBackendKind Backend { get; }
        public uint Group { get; }
        public uint Binding { get; }
        public ShaderPhysicalBindingNamespace Namespace { get; }

        public ShaderPhysicalBindingLocation(
            ShaderBackendKind backend,
            uint group,
            uint binding,
            ShaderPhysicalBindingNamespace bindingNamespace)
        {
            if (!Enum.IsDefined(backend))
            {
                throw new ArgumentOutOfRangeException(nameof(backend), backend, "Shader backend is not defined.");
            }

            if (!Enum.IsDefined(bindingNamespace))
            {
                throw new ArgumentOutOfRangeException(nameof(bindingNamespace), bindingNamespace, "Physical binding namespace is not defined.");
            }

            bool validNamespace = backend switch
            {
                ShaderBackendKind.DirectX12 => bindingNamespace is ShaderPhysicalBindingNamespace.ShaderResource
                    or ShaderPhysicalBindingNamespace.Sampler
                    or ShaderPhysicalBindingNamespace.ConstantBuffer
                    or ShaderPhysicalBindingNamespace.UnorderedAccess,
                ShaderBackendKind.Vulkan => bindingNamespace == ShaderPhysicalBindingNamespace.Unified,
                ShaderBackendKind.Metal => bindingNamespace is ShaderPhysicalBindingNamespace.Buffer
                    or ShaderPhysicalBindingNamespace.Texture
                    or ShaderPhysicalBindingNamespace.Sampler,
                _ => false,
            };

            if (!validNamespace)
            {
                throw new ArgumentException(
                    $"Binding namespace {bindingNamespace} is not valid for backend {backend}.",
                    nameof(bindingNamespace));
            }

            Backend = backend;
            Group = group;
            Binding = binding;
            Namespace = bindingNamespace;
        }

        public bool Equals(ShaderPhysicalBindingLocation other)
        {
            return Backend == other.Backend
                && Group == other.Group
                && Binding == other.Binding
                && Namespace == other.Namespace;
        }

        public override bool Equals(object? obj) => obj is ShaderPhysicalBindingLocation other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Backend, Group, Binding, Namespace);
        public static bool operator ==(ShaderPhysicalBindingLocation left, ShaderPhysicalBindingLocation right) => left.Equals(right);
        public static bool operator !=(ShaderPhysicalBindingLocation left, ShaderPhysicalBindingLocation right) => !left.Equals(right);
    }

    public enum ShaderThreadGroupDimensionKind
    {
        Fixed,
        SpecializationConstant,
    }

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

    public sealed class ShaderResourceBindingReflection : IEquatable<ShaderResourceBindingReflection>
    {
        public ShaderLogicalBinding LogicalBinding { get; }
        public string Name => LogicalBinding.CanonicalName;
        public ShaderBindingKey Key => LogicalBinding.Key;
        public ShaderResourceShape Shape => LogicalBinding.Shape;
        public ShaderStageMask Stages => LogicalBinding.StageMask;
        public ShaderBindingProvenance Provenance => LogicalBinding.Provenance;
        public ShaderConstantBufferLayout? ConstantBufferLayout => LogicalBinding.ConstantBufferLayout;
        public ShaderPhysicalBindingLocation PhysicalLocation { get; }

        public ShaderResourceBindingReflection(
            ShaderLogicalBinding logicalBinding,
            ShaderPhysicalBindingLocation physicalLocation)
        {
            ArgumentNullException.ThrowIfNull(logicalBinding);
            ValidatePhysicalNamespace(logicalBinding.Key, physicalLocation);
            uint? boundedCount = logicalBinding.Shape.Array.BoundedElementCount;
            if (boundedCount.HasValue)
            {
                _ = checked(physicalLocation.Binding + boundedCount.Value - 1);
            }

            LogicalBinding = logicalBinding;
            PhysicalLocation = physicalLocation;
        }

        public ShaderResourceBindingReflection(
            string name,
            ShaderBindingKey key,
            ShaderResourceShape shape,
            ShaderStageMask stages,
            ShaderPhysicalBindingLocation physicalLocation)
            : this(
                new ShaderLogicalBinding(
                    key,
                    name,
                    null,
                    shape,
                    stages,
                    null,
                    ShaderBindingProvenance.Unknown),
                physicalLocation)
        {
        }

        private static void ValidatePhysicalNamespace(ShaderBindingKey key, ShaderPhysicalBindingLocation location)
        {
            ShaderPhysicalBindingNamespace? expected = location.Backend == ShaderBackendKind.DirectX12
                ? key.Type switch
                {
                    ShaderBindingClass.ShaderResource => ShaderPhysicalBindingNamespace.ShaderResource,
                    ShaderBindingClass.Sampler => ShaderPhysicalBindingNamespace.Sampler,
                    ShaderBindingClass.ConstantBuffer => ShaderPhysicalBindingNamespace.ConstantBuffer,
                    ShaderBindingClass.UnorderedAccess => ShaderPhysicalBindingNamespace.UnorderedAccess,
                    _ => null,
                }
                : null;

            if (expected.HasValue && location.Namespace != expected.Value)
            {
                throw new ArgumentException(
                    $"DX12 physical namespace {location.Namespace} does not match logical binding class {key.Type}.",
                    nameof(location));
            }
        }

        public bool Equals(ShaderResourceBindingReflection? other)
        {
            return other is not null
                && LogicalBinding.Equals(other.LogicalBinding)
                && PhysicalLocation == other.PhysicalLocation;
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderResourceBindingReflection);
        public override int GetHashCode() => HashCode.Combine(LogicalBinding, PhysicalLocation);
    }

    public sealed class ShaderEntryPointReflection : IEquatable<ShaderEntryPointReflection>
    {
        private readonly ReadOnlyCollection<ShaderResourceBindingReflection> m_Resources;
        private readonly ReadOnlyCollection<ShaderStageIoReflection> m_StageInputs;
        private readonly ReadOnlyCollection<ShaderStageIoReflection> m_StageOutputs;
        private readonly ReadOnlyCollection<ShaderInputAttachmentReflection> m_InputAttachments;

        public string Name { get; }
        public ShaderExecutionStage Stage { get; }
        public ShaderThreadGroupSize? ThreadGroupSize { get; }
        public IReadOnlyList<ShaderResourceBindingReflection> Resources => m_Resources;
        public IReadOnlyList<ShaderStageIoReflection> StageInputs => m_StageInputs;
        public IReadOnlyList<ShaderStageIoReflection> StageOutputs => m_StageOutputs;
        public IReadOnlyList<ShaderInputAttachmentReflection> InputAttachments =>
            m_InputAttachments;
        public ShaderAttachmentArtifactRequirement AttachmentRequirements { get; }

        public ShaderEntryPointReflection(
            string name,
            ShaderExecutionStage stage,
            IEnumerable<ShaderResourceBindingReflection>? resources = null,
            ShaderThreadGroupSize? threadGroupSize = null,
            IEnumerable<ShaderStageIoReflection>? stageInputs = null,
            IEnumerable<ShaderStageIoReflection>? stageOutputs = null,
            IEnumerable<ShaderInputAttachmentReflection>? inputAttachments = null,
            ShaderAttachmentArtifactRequirement attachmentRequirements =
                ShaderAttachmentArtifactRequirement.None)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Entry-point name must not be empty.", nameof(name));
            }

            ShaderStageMask expectedStage = ShaderStageMaskUtility.FromStage(stage);
            bool supportsThreadGroup = stage is ShaderExecutionStage.Compute
                or ShaderExecutionStage.Amplification
                or ShaderExecutionStage.Mesh
                or ShaderExecutionStage.Node;
            if (threadGroupSize.HasValue && !supportsThreadGroup)
            {
                throw new ArgumentException(
                    $"Thread-group size is not valid for entry-point stage {stage}.",
                    nameof(threadGroupSize));
            }

            ShaderResourceBindingReflection[] copy = resources is null
                ? Array.Empty<ShaderResourceBindingReflection>()
                : new List<ShaderResourceBindingReflection>(resources).ToArray();

            HashSet<ShaderBindingKey> keys = new HashSet<ShaderBindingKey>();
            foreach (ShaderResourceBindingReflection resource in copy)
            {
                ArgumentNullException.ThrowIfNull(resource);
                if ((resource.Stages & expectedStage) == 0)
                {
                    throw new ArgumentException(
                        $"Resource {resource.Name} is not visible to entry-point stage {stage}.",
                        nameof(resources));
                }

                if (!keys.Add(resource.Key))
                {
                    throw new ArgumentException($"Entry point contains duplicate logical binding {resource.Key}.", nameof(resources));
                }
            }

            Array.Sort(copy, CompareResources);
            ShaderStageIoReflection[] inputCopy = MaterializeStageIo(
                stageInputs,
                ShaderStageIoDirection.Input,
                nameof(stageInputs));
            ShaderStageIoReflection[] outputCopy = MaterializeStageIo(
                stageOutputs,
                ShaderStageIoDirection.Output,
                nameof(stageOutputs));
            ShaderInputAttachmentReflection[] inputAttachmentCopy =
                inputAttachments is null
                    ? Array.Empty<ShaderInputAttachmentReflection>()
                    : new List<ShaderInputAttachmentReflection>(
                        inputAttachments).ToArray();
            Array.Sort(
                inputAttachmentCopy,
                static (left, right) =>
                {
                    int index = left.InputAttachmentIndex.CompareTo(
                        right.InputAttachmentIndex);
                    return index != 0
                        ? index
                        : string.CompareOrdinal(left.Name, right.Name);
                });
            for (int index = 0; index < inputAttachmentCopy.Length; ++index)
            {
                ArgumentNullException.ThrowIfNull(inputAttachmentCopy[index]);
                if (index > 0
                    && inputAttachmentCopy[index - 1].InputAttachmentIndex
                        == inputAttachmentCopy[index].InputAttachmentIndex)
                {
                    throw new ArgumentException(
                        $"Entry point contains duplicate input attachment index "
                        + $"{inputAttachmentCopy[index].InputAttachmentIndex}.",
                        nameof(inputAttachments));
                }
            }

            const ShaderAttachmentArtifactRequirement knownRequirements =
                ShaderAttachmentArtifactRequirement.RasterOrderedViews |
                ShaderAttachmentArtifactRequirement.StencilReferenceExport |
                ShaderAttachmentArtifactRequirement.FramebufferLocalRead |
                ShaderAttachmentArtifactRequirement.OrderedPixelFragmentInterlock |
                ShaderAttachmentArtifactRequirement.UnorderedFragmentInterlock |
                ShaderAttachmentArtifactRequirement.SampleOrderedFragmentInterlock |
                ShaderAttachmentArtifactRequirement.ShadingRateOrderedFragmentInterlock;
            if ((attachmentRequirements & ~knownRequirements) != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(attachmentRequirements),
                    attachmentRequirements,
                    "Attachment artifact requirements contain unknown flags.");
            }

            if (stage != ShaderExecutionStage.Pixel
                && (inputAttachmentCopy.Length != 0
                    || attachmentRequirements
                        != ShaderAttachmentArtifactRequirement.None))
            {
                throw new ArgumentException(
                    "Only pixel shader entries may declare attachment-specific reflection.");
            }

            Name = name;
            Stage = stage;
            ThreadGroupSize = threadGroupSize;
            m_Resources = Array.AsReadOnly(copy);
            m_StageInputs = Array.AsReadOnly(inputCopy);
            m_StageOutputs = Array.AsReadOnly(outputCopy);
            m_InputAttachments = Array.AsReadOnly(inputAttachmentCopy);
            AttachmentRequirements = attachmentRequirements;
        }

        private static ShaderStageIoReflection[] MaterializeStageIo(
            IEnumerable<ShaderStageIoReflection>? values,
            ShaderStageIoDirection expectedDirection,
            string parameterName)
        {
            ShaderStageIoReflection[] copy = values is null
                ? Array.Empty<ShaderStageIoReflection>()
                : new List<ShaderStageIoReflection>(values).ToArray();
            Array.Sort(copy, CompareStageIo);
            for (int index = 0; index < copy.Length; ++index)
            {
                ArgumentNullException.ThrowIfNull(copy[index]);
                if (copy[index].Direction != expectedDirection)
                {
                    throw new ArgumentException(
                        $"Stage I/O {copy[index].Name} has direction "
                        + $"{copy[index].Direction}; expected {expectedDirection}.",
                        parameterName);
                }

                if (index > 0
                    && HaveSameStageIoLocation(copy[index - 1], copy[index]))
                {
                    throw new ArgumentException(
                        "Entry point contains duplicate stage I/O locations.",
                        parameterName);
                }
            }

            return copy;
        }

        private static int CompareResources(
            ShaderResourceBindingReflection left,
            ShaderResourceBindingReflection right)
        {
            int comparison = left.Key.Table.CompareTo(right.Key.Table);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.Key.Slot.CompareTo(right.Key.Slot);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.Key.Type.CompareTo(right.Key.Type);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(left.Name, right.Name);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.PhysicalLocation.Backend.CompareTo(right.PhysicalLocation.Backend);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.PhysicalLocation.Group.CompareTo(right.PhysicalLocation.Group);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.PhysicalLocation.Binding.CompareTo(right.PhysicalLocation.Binding);
            return comparison != 0
                ? comparison
                : left.PhysicalLocation.Namespace.CompareTo(right.PhysicalLocation.Namespace);
        }

        private static int CompareStageIo(
            ShaderStageIoReflection left,
            ShaderStageIoReflection right)
        {
            int builtIn = left.BuiltIn.CompareTo(right.BuiltIn);
            if (builtIn != 0)
            {
                return builtIn;
            }

            int location = Nullable.Compare(left.Location, right.Location);
            if (location != 0)
            {
                return location;
            }

            int index = left.Index.CompareTo(right.Index);
            if (index != 0)
            {
                return index;
            }

            int component = left.Component.CompareTo(right.Component);
            return component != 0
                ? component
                : string.CompareOrdinal(left.Name, right.Name);
        }

        private static bool HaveSameStageIoLocation(
            ShaderStageIoReflection left,
            ShaderStageIoReflection right)
        {
            return left.BuiltIn == right.BuiltIn
                && left.Location == right.Location
                && left.Index == right.Index
                && left.Component == right.Component;
        }

        public bool Equals(ShaderEntryPointReflection? other)
        {
            if (other is null
                || !string.Equals(Name, other.Name, StringComparison.Ordinal)
                || Stage != other.Stage
                || ThreadGroupSize != other.ThreadGroupSize
                || AttachmentRequirements != other.AttachmentRequirements
                || m_Resources.Count != other.m_Resources.Count
                || m_StageInputs.Count != other.m_StageInputs.Count
                || m_StageOutputs.Count != other.m_StageOutputs.Count
                || m_InputAttachments.Count != other.m_InputAttachments.Count)
            {
                return false;
            }

            for (int index = 0; index < m_Resources.Count; ++index)
            {
                if (!m_Resources[index].Equals(other.m_Resources[index]))
                {
                    return false;
                }
            }

            for (int index = 0; index < m_StageInputs.Count; ++index)
            {
                if (!m_StageInputs[index].Equals(other.m_StageInputs[index]))
                {
                    return false;
                }
            }

            for (int index = 0; index < m_StageOutputs.Count; ++index)
            {
                if (!m_StageOutputs[index].Equals(other.m_StageOutputs[index]))
                {
                    return false;
                }
            }

            for (int index = 0; index < m_InputAttachments.Count; ++index)
            {
                if (!m_InputAttachments[index].Equals(other.m_InputAttachments[index]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderEntryPointReflection);

        public override int GetHashCode()
        {
            HashCode hash = new HashCode();
            hash.Add(Name, StringComparer.Ordinal);
            hash.Add(Stage);
            hash.Add(ThreadGroupSize);
            hash.Add(AttachmentRequirements);
            foreach (ShaderResourceBindingReflection resource in m_Resources)
            {
                hash.Add(resource);
            }

            foreach (ShaderStageIoReflection stageInput in m_StageInputs)
            {
                hash.Add(stageInput);
            }

            foreach (ShaderStageIoReflection stageOutput in m_StageOutputs)
            {
                hash.Add(stageOutput);
            }

            foreach (ShaderInputAttachmentReflection inputAttachment in m_InputAttachments)
            {
                hash.Add(inputAttachment);
            }

            return hash.ToHashCode();
        }
    }

    public sealed class ShaderArtifactReflection : IEquatable<ShaderArtifactReflection>
    {
        public const uint CurrentSchemaVersion = 2;

        private readonly ReadOnlyCollection<ShaderEntryPointReflection> m_EntryPoints;

        public uint SchemaVersion { get; }
        public ShaderArtifactKind ArtifactKind { get; }
        public IReadOnlyList<ShaderEntryPointReflection> EntryPoints => m_EntryPoints;

        public ShaderArtifactReflection(
            ShaderArtifactKind artifactKind,
            IEnumerable<ShaderEntryPointReflection> entryPoints,
            uint schemaVersion = CurrentSchemaVersion)
        {
            if (!Enum.IsDefined(artifactKind))
            {
                throw new ArgumentOutOfRangeException(nameof(artifactKind), artifactKind, "Shader artifact kind is not defined.");
            }

            if (schemaVersion != CurrentSchemaVersion)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(schemaVersion),
                    schemaVersion,
                    $"Reflection schema version must be exactly {CurrentSchemaVersion}.");
            }

            ArgumentNullException.ThrowIfNull(entryPoints);
            ShaderEntryPointReflection[] copy = new List<ShaderEntryPointReflection>(entryPoints).ToArray();
            if (copy.Length == 0)
            {
                throw new ArgumentException("Artifact reflection must contain at least one entry point.", nameof(entryPoints));
            }

            ShaderBackendKind expectedBackend = artifactKind switch
            {
                ShaderArtifactKind.Dxil => ShaderBackendKind.DirectX12,
                ShaderArtifactKind.SpirV => ShaderBackendKind.Vulkan,
                ShaderArtifactKind.MslSource or ShaderArtifactKind.MetalLibrary => ShaderBackendKind.Metal,
                _ => throw new ArgumentOutOfRangeException(nameof(artifactKind), artifactKind, "Shader artifact kind is not defined."),
            };

            HashSet<(string Name, ShaderExecutionStage Stage)> identities = new();
            foreach (ShaderEntryPointReflection entryPoint in copy)
            {
                ArgumentNullException.ThrowIfNull(entryPoint);
                if (!identities.Add((entryPoint.Name, entryPoint.Stage)))
                {
                    throw new ArgumentException(
                        $"Artifact contains duplicate entry point {entryPoint.Name} ({entryPoint.Stage}).",
                        nameof(entryPoints));
                }

                foreach (ShaderResourceBindingReflection resource in entryPoint.Resources)
                {
                    if (resource.PhysicalLocation.Backend != expectedBackend)
                    {
                        throw new ArgumentException(
                            $"Artifact kind {artifactKind} cannot contain a {resource.PhysicalLocation.Backend} physical binding for resource {resource.Name}.",
                            nameof(entryPoints));
                    }
                }
            }

            Array.Sort(copy, CompareEntryPoints);
            SchemaVersion = schemaVersion;
            ArtifactKind = artifactKind;
            m_EntryPoints = Array.AsReadOnly(copy);
        }

        private static int CompareEntryPoints(ShaderEntryPointReflection left, ShaderEntryPointReflection right)
        {
            int comparison = left.Stage.CompareTo(right.Stage);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left.Name, right.Name);
        }

        public bool Equals(ShaderArtifactReflection? other)
        {
            if (other is null
                || SchemaVersion != other.SchemaVersion
                || ArtifactKind != other.ArtifactKind
                || m_EntryPoints.Count != other.m_EntryPoints.Count)
            {
                return false;
            }

            for (int index = 0; index < m_EntryPoints.Count; ++index)
            {
                if (!m_EntryPoints[index].Equals(other.m_EntryPoints[index]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderArtifactReflection);

        public override int GetHashCode()
        {
            HashCode hash = new HashCode();
            hash.Add(SchemaVersion);
            hash.Add(ArtifactKind);
            foreach (ShaderEntryPointReflection entryPoint in m_EntryPoints)
            {
                hash.Add(entryPoint);
            }

            return hash.ToHashCode();
        }
    }
}
