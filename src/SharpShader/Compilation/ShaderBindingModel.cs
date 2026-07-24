using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{
    public enum ShaderBindingClass
    {
        ShaderResource,
        Sampler,
        ConstantBuffer,
        UnorderedAccess,
    }

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

    public enum ShaderArrayExtentKind
    {
        Bounded,
        Runtime,
        SpecializationConstant,
    }

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

    public sealed class ShaderArrayShape : IEquatable<ShaderArrayShape>
    {
        private readonly ReadOnlyCollection<ShaderArrayExtent> m_Extents;

        public static ShaderArrayShape Scalar { get; } = new ShaderArrayShape(Array.Empty<ShaderArrayExtent>());

        public IReadOnlyList<ShaderArrayExtent> Extents => m_Extents;
        public bool IsArray => m_Extents.Count != 0;
        public bool HasRuntimeExtent { get; }
        public bool HasSpecializationConstantExtent { get; }
        public uint? BoundedElementCount { get; }

        public ShaderArrayShape(IEnumerable<ShaderArrayExtent> extents)
        {
            ArgumentNullException.ThrowIfNull(extents);

            ShaderArrayExtent[] copy = extents is ShaderArrayExtent[] array
                ? (ShaderArrayExtent[])array.Clone()
                : new List<ShaderArrayExtent>(extents).ToArray();

            bool hasRuntime = false;
            bool hasSpecializationConstant = false;
            uint boundedCount = 1;
            for (int index = 0; index < copy.Length; ++index)
            {
                ShaderArrayExtent extent = copy[index];
                if (!Enum.IsDefined(extent.Kind))
                {
                    throw new ArgumentException($"Array extent {index} has an undefined kind.", nameof(extents));
                }

                switch (extent.Kind)
                {
                    case ShaderArrayExtentKind.Bounded:
                        if (extent.Value == 0)
                        {
                            throw new ArgumentException($"Array extent {index} has a zero bounded count.", nameof(extents));
                        }

                        boundedCount = checked(boundedCount * extent.Value);
                        break;
                    case ShaderArrayExtentKind.Runtime:
                        if (hasRuntime || index != copy.Length - 1)
                        {
                            throw new ArgumentException("A runtime extent may appear at most once and must be the final array dimension.", nameof(extents));
                        }

                        hasRuntime = true;
                        break;
                    case ShaderArrayExtentKind.SpecializationConstant:
                        hasSpecializationConstant = true;
                        break;
                    default:
                        throw new ArgumentException($"Array extent {index} has an unsupported kind.", nameof(extents));
                }
            }

            m_Extents = Array.AsReadOnly(copy);
            HasRuntimeExtent = hasRuntime;
            HasSpecializationConstantExtent = hasSpecializationConstant;
            BoundedElementCount = hasRuntime || hasSpecializationConstant ? null : boundedCount;
        }

        public bool Equals(ShaderArrayShape? other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            if (other is null || m_Extents.Count != other.m_Extents.Count)
            {
                return false;
            }

            for (int index = 0; index < m_Extents.Count; ++index)
            {
                if (m_Extents[index] != other.m_Extents[index])
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderArrayShape);

        public override int GetHashCode()
        {
            HashCode hash = new HashCode();
            foreach (ShaderArrayExtent extent in m_Extents)
            {
                hash.Add(extent);
            }

            return hash.ToHashCode();
        }
    }

    public enum ShaderResourceKind
    {
        ConstantBuffer,
        Texture,
        Sampler,
        TypedBuffer,
        StructuredBuffer,
        StorageBuffer,
        ByteAddressBuffer,
        AccelerationStructure,
        InputAttachment,
        AtomicCounter,
        ShaderRecordBuffer,
        FeedbackTexture,
    }

    public enum ShaderResourceDimension
    {
        Unknown,
        Buffer,
        Texture1D,
        Texture1DArray,
        Texture2D,
        Texture2DArray,
        Texture2DMultisampled,
        Texture2DMultisampledArray,
        Texture3D,
        TextureCube,
        TextureCubeArray,
    }

    public enum ShaderResourceAccess
    {
        ReadOnly,
        WriteOnly,
        ReadWrite,
    }

    public enum ShaderSamplerKind
    {
        Unknown,
        Regular,
        Comparison,
    }

    public enum ShaderCounterKind
    {
        None,
        Counter,
        Append,
        Consume,
    }

    public sealed class ShaderResourceShape : IEquatable<ShaderResourceShape>
    {
        public ShaderResourceKind Kind { get; }
        public ShaderResourceDimension Dimension { get; }
        public ShaderResourceAccess Access { get; }
        public ShaderArrayShape Array { get; }
        public uint? StructureStride { get; }
        public ShaderSamplerKind? SamplerKind { get; }
        public ShaderCounterKind CounterKind { get; }

        public ShaderResourceShape(
            ShaderResourceKind kind,
            ShaderResourceDimension dimension,
            ShaderResourceAccess access,
            ShaderArrayShape? array = null,
            uint? structureStride = null,
            ShaderSamplerKind? samplerKind = null,
            ShaderCounterKind counterKind = ShaderCounterKind.None)
        {
            if (!Enum.IsDefined(kind))
            {
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Resource kind is not defined.");
            }

            if (!Enum.IsDefined(dimension))
            {
                throw new ArgumentOutOfRangeException(nameof(dimension), dimension, "Resource dimension is not defined.");
            }

            if (!Enum.IsDefined(access))
            {
                throw new ArgumentOutOfRangeException(nameof(access), access, "Resource access is not defined.");
            }

            if (samplerKind.HasValue && !Enum.IsDefined(samplerKind.Value))
            {
                throw new ArgumentOutOfRangeException(nameof(samplerKind), samplerKind, "Sampler kind is not defined.");
            }

            if (!Enum.IsDefined(counterKind))
            {
                throw new ArgumentOutOfRangeException(nameof(counterKind), counterKind, "Counter kind is not defined.");
            }

            ValidateShape(kind, dimension, access, structureStride, samplerKind, counterKind);

            Kind = kind;
            Dimension = dimension;
            Access = access;
            Array = array ?? ShaderArrayShape.Scalar;
            StructureStride = structureStride;
            SamplerKind = samplerKind;
            CounterKind = counterKind;
        }

        internal void ValidateBindingClass(ShaderBindingClass bindingClass)
        {
            ShaderBindingClass expected = Kind switch
            {
                ShaderResourceKind.ConstantBuffer => ShaderBindingClass.ConstantBuffer,
                ShaderResourceKind.Sampler => ShaderBindingClass.Sampler,
                _ when Access == ShaderResourceAccess.ReadOnly => ShaderBindingClass.ShaderResource,
                _ => ShaderBindingClass.UnorderedAccess,
            };

            if (bindingClass != expected)
            {
                throw new ArgumentException(
                    $"Resource kind {Kind} with {Access} access requires binding class {expected}, not {bindingClass}.",
                    nameof(bindingClass));
            }
        }

        private static void ValidateShape(
            ShaderResourceKind kind,
            ShaderResourceDimension dimension,
            ShaderResourceAccess access,
            uint? structureStride,
            ShaderSamplerKind? samplerKind,
            ShaderCounterKind counterKind)
        {
            bool isTextureDimension = dimension is not ShaderResourceDimension.Unknown and not ShaderResourceDimension.Buffer;

            if (kind == ShaderResourceKind.Sampler)
            {
                if (dimension != ShaderResourceDimension.Unknown || access != ShaderResourceAccess.ReadOnly || !samplerKind.HasValue)
                {
                    throw new ArgumentException("Sampler resources require unknown dimension, read-only access, and sampler metadata.");
                }
            }
            else if (samplerKind.HasValue)
            {
                throw new ArgumentException("Sampler metadata is only valid for sampler resources.", nameof(samplerKind));
            }

            if (kind == ShaderResourceKind.ConstantBuffer
                && (dimension != ShaderResourceDimension.Buffer || access != ShaderResourceAccess.ReadOnly))
            {
                throw new ArgumentException("Constant buffers require buffer dimension and read-only access.");
            }

            if (kind is ShaderResourceKind.TypedBuffer or ShaderResourceKind.StructuredBuffer or ShaderResourceKind.StorageBuffer
                or ShaderResourceKind.ByteAddressBuffer or ShaderResourceKind.AtomicCounter or ShaderResourceKind.ShaderRecordBuffer)
            {
                if (dimension != ShaderResourceDimension.Buffer)
                {
                    throw new ArgumentException($"{kind} requires buffer dimension.", nameof(dimension));
                }
            }

            if (kind is ShaderResourceKind.Texture or ShaderResourceKind.InputAttachment or ShaderResourceKind.FeedbackTexture)
            {
                if (!isTextureDimension)
                {
                    throw new ArgumentException($"{kind} requires a texture dimension.", nameof(dimension));
                }
            }

            if (kind == ShaderResourceKind.AccelerationStructure
                && (dimension != ShaderResourceDimension.Unknown || access != ShaderResourceAccess.ReadOnly))
            {
                throw new ArgumentException("Acceleration structures require unknown dimension and read-only access.");
            }

            if (kind == ShaderResourceKind.InputAttachment && access != ShaderResourceAccess.ReadOnly)
            {
                throw new ArgumentException("Input attachments require read-only access.", nameof(access));
            }

            if (kind == ShaderResourceKind.AtomicCounter && access != ShaderResourceAccess.ReadWrite)
            {
                throw new ArgumentException("Atomic counters require read-write access.", nameof(access));
            }

            if (kind == ShaderResourceKind.FeedbackTexture && access == ShaderResourceAccess.ReadOnly)
            {
                throw new ArgumentException("Feedback textures require writable access.", nameof(access));
            }

            if (kind == ShaderResourceKind.ShaderRecordBuffer && access != ShaderResourceAccess.ReadOnly)
            {
                throw new ArgumentException("Shader-record buffers require read-only access.", nameof(access));
            }

            if (kind is ShaderResourceKind.StructuredBuffer or ShaderResourceKind.StorageBuffer)
            {
                if (!structureStride.HasValue || structureStride.Value == 0)
                {
                    throw new ArgumentException($"{kind} requires a positive structure stride.", nameof(structureStride));
                }
            }
            else if (structureStride.HasValue)
            {
                throw new ArgumentException("Structure stride is only valid for structured buffers.", nameof(structureStride));
            }

            if (counterKind != ShaderCounterKind.None
                && (kind != ShaderResourceKind.StructuredBuffer || access == ShaderResourceAccess.ReadOnly))
            {
                throw new ArgumentException("Counter metadata is only valid for writable structured buffers.", nameof(counterKind));
            }
        }

        public bool Equals(ShaderResourceShape? other)
        {
            return other is not null
                && Kind == other.Kind
                && Dimension == other.Dimension
                && Access == other.Access
                && Array.Equals(other.Array)
                && StructureStride == other.StructureStride
                && SamplerKind == other.SamplerKind
                && CounterKind == other.CounterKind;
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderResourceShape);
        public override int GetHashCode() => HashCode.Combine(Kind, Dimension, Access, Array, StructureStride, SamplerKind, CounterKind);
    }

    public enum ShaderBindingProvenance
    {
        Unknown,
        ExplicitSource,
        CompilerAssigned,
    }

    public sealed class ShaderLogicalBinding : IEquatable<ShaderLogicalBinding>
    {
        private readonly ReadOnlyCollection<string> m_Aliases;

        public ShaderBindingKey Key { get; }
        public string CanonicalName { get; }
        public IReadOnlyList<string> Aliases => m_Aliases;
        public ShaderResourceShape Shape { get; }
        public ShaderStageMask StageMask { get; }
        public ShaderBindingProvenance Provenance { get; }
        public ShaderConstantBufferLayout? ConstantBufferLayout { get; }

        public ShaderLogicalBinding(
            ShaderBindingKey key,
            string canonicalName,
            IEnumerable<string>? aliases,
            ShaderResourceShape shape,
            ShaderStageMask stageMask,
            ShaderConstantBufferLayout? constantBufferLayout = null,
            ShaderBindingProvenance provenance = ShaderBindingProvenance.Unknown)
        {
            if (string.IsNullOrWhiteSpace(canonicalName))
            {
                throw new ArgumentException("Logical binding canonical name must not be empty.", nameof(canonicalName));
            }

            ArgumentNullException.ThrowIfNull(shape);
            shape.ValidateBindingClass(key.Type);
            ShaderStageMaskUtility.Validate(stageMask);
            uint? boundedCount = shape.Array.BoundedElementCount;
            if (boundedCount.HasValue)
            {
                _ = checked(key.Slot + boundedCount.Value - 1);
            }

            if (!Enum.IsDefined(provenance))
            {
                throw new ArgumentOutOfRangeException(nameof(provenance), provenance, "Binding provenance is not defined.");
            }

            if (shape.Kind != ShaderResourceKind.ConstantBuffer && constantBufferLayout is not null)
            {
                throw new ArgumentException(
                    "Constant-buffer layout is only valid for a constant-buffer resource.",
                    nameof(constantBufferLayout));
            }

            string[] aliasCopy = aliases is null
                ? System.Array.Empty<string>()
                : new List<string>(aliases).ToArray();
            System.Array.Sort(aliasCopy, StringComparer.Ordinal);

            string? previous = null;
            foreach (string alias in aliasCopy)
            {
                if (string.IsNullOrWhiteSpace(alias))
                {
                    throw new ArgumentException("Logical binding aliases must not contain empty names.", nameof(aliases));
                }

                if (string.Equals(alias, canonicalName, StringComparison.Ordinal)
                    || string.Equals(alias, previous, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Logical binding alias {alias} is duplicate or equals the canonical name.",
                        nameof(aliases));
                }

                previous = alias;
            }

            Key = key;
            CanonicalName = canonicalName;
            Shape = shape;
            StageMask = stageMask;
            Provenance = provenance;
            ConstantBufferLayout = constantBufferLayout;
            m_Aliases = System.Array.AsReadOnly(aliasCopy);
        }

        public bool Equals(ShaderLogicalBinding? other)
        {
            if (other is null
                || Key != other.Key
                || !string.Equals(CanonicalName, other.CanonicalName, StringComparison.Ordinal)
                || !Shape.Equals(other.Shape)
                || StageMask != other.StageMask
                || Provenance != other.Provenance
                || !Equals(ConstantBufferLayout, other.ConstantBufferLayout)
                || m_Aliases.Count != other.m_Aliases.Count)
            {
                return false;
            }

            for (int index = 0; index < m_Aliases.Count; ++index)
            {
                if (!string.Equals(m_Aliases[index], other.m_Aliases[index], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderLogicalBinding);

        public override int GetHashCode()
        {
            HashCode hash = new HashCode();
            hash.Add(Key);
            hash.Add(CanonicalName, StringComparer.Ordinal);
            hash.Add(Shape);
            hash.Add(StageMask);
            hash.Add(Provenance);
            hash.Add(ConstantBufferLayout);
            foreach (string alias in m_Aliases)
            {
                hash.Add(alias, StringComparer.Ordinal);
            }

            return hash.ToHashCode();
        }
    }

    public enum ShaderExecutionStage
    {
        Vertex,
        Hull,
        Domain,
        Geometry,
        Pixel,
        Compute,
        Amplification,
        Mesh,
        RayGeneration,
        Intersection,
        AnyHit,
        ClosestHit,
        Miss,
        Callable,
        Node,
    }

    [Flags]
    public enum ShaderStageMask : ulong
    {
        None = 0,
        Vertex = 1UL << 0,
        Hull = 1UL << 1,
        Domain = 1UL << 2,
        Geometry = 1UL << 3,
        Pixel = 1UL << 4,
        Compute = 1UL << 5,
        Amplification = 1UL << 6,
        Mesh = 1UL << 7,
        RayGeneration = 1UL << 8,
        Intersection = 1UL << 9,
        AnyHit = 1UL << 10,
        ClosestHit = 1UL << 11,
        Miss = 1UL << 12,
        Callable = 1UL << 13,
        Node = 1UL << 14,
        All = (1UL << 15) - 1,
    }

    public static class ShaderStageMaskUtility
    {
        public static ShaderStageMask FromStage(ShaderExecutionStage stage)
        {
            return stage switch
            {
                ShaderExecutionStage.Vertex => ShaderStageMask.Vertex,
                ShaderExecutionStage.Hull => ShaderStageMask.Hull,
                ShaderExecutionStage.Domain => ShaderStageMask.Domain,
                ShaderExecutionStage.Geometry => ShaderStageMask.Geometry,
                ShaderExecutionStage.Pixel => ShaderStageMask.Pixel,
                ShaderExecutionStage.Compute => ShaderStageMask.Compute,
                ShaderExecutionStage.Amplification => ShaderStageMask.Amplification,
                ShaderExecutionStage.Mesh => ShaderStageMask.Mesh,
                ShaderExecutionStage.RayGeneration => ShaderStageMask.RayGeneration,
                ShaderExecutionStage.Intersection => ShaderStageMask.Intersection,
                ShaderExecutionStage.AnyHit => ShaderStageMask.AnyHit,
                ShaderExecutionStage.ClosestHit => ShaderStageMask.ClosestHit,
                ShaderExecutionStage.Miss => ShaderStageMask.Miss,
                ShaderExecutionStage.Callable => ShaderStageMask.Callable,
                ShaderExecutionStage.Node => ShaderStageMask.Node,
                _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Shader stage is not defined."),
            };
        }

        public static ShaderStageMask Union(IEnumerable<ShaderExecutionStage> stages)
        {
            ArgumentNullException.ThrowIfNull(stages);

            ShaderStageMask result = ShaderStageMask.None;
            foreach (ShaderExecutionStage stage in stages)
            {
                result |= FromStage(stage);
            }

            return result;
        }

        public static void Validate(ShaderStageMask stages, bool allowNone = false)
        {
            if ((stages & ~ShaderStageMask.All) != 0 || (!allowNone && stages == ShaderStageMask.None))
            {
                throw new ArgumentOutOfRangeException(nameof(stages), stages, "Shader stage mask is empty or contains undefined bits.");
            }
        }
    }
}
