using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{
    public enum VulkanDescriptorKind
    {
        Sampler,
        SampledImage,
        StorageImage,
        UniformBuffer,
        StorageBuffer,
        UniformTexelBuffer,
        StorageTexelBuffer,
        AccelerationStructure,
        InputAttachment,
    }

    public readonly struct Dx12ShaderBindingMapping : IEquatable<Dx12ShaderBindingMapping>
    {
        public ShaderBindingKey LogicalBinding { get; }
        public uint RegisterSpace { get; }
        public uint ShaderRegister { get; }
        public ShaderBindingClass RegisterClass { get; }

        public Dx12ShaderBindingMapping(
            ShaderBindingKey logicalBinding,
            uint registerSpace,
            uint shaderRegister,
            ShaderBindingClass registerClass)
        {
            if (!Enum.IsDefined(registerClass))
            {
                throw new ArgumentOutOfRangeException(nameof(registerClass), registerClass, "DX12 register class is not defined.");
            }

            if (logicalBinding.Type != registerClass)
            {
                throw new ArgumentException(
                    $"DX12 register class {registerClass} does not match logical binding class {logicalBinding.Type}.",
                    nameof(registerClass));
            }

            if (logicalBinding.Table != registerSpace || logicalBinding.Slot != shaderRegister)
            {
                throw new ArgumentException(
                    "DX12 mappings must preserve the logical table/slot as register space/register.");
            }

            LogicalBinding = logicalBinding;
            RegisterSpace = registerSpace;
            ShaderRegister = shaderRegister;
            RegisterClass = registerClass;
        }

        public bool Equals(Dx12ShaderBindingMapping other)
        {
            return LogicalBinding == other.LogicalBinding
                && RegisterSpace == other.RegisterSpace
                && ShaderRegister == other.ShaderRegister
                && RegisterClass == other.RegisterClass;
        }

        public override bool Equals(object? obj) => obj is Dx12ShaderBindingMapping other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(LogicalBinding, RegisterSpace, ShaderRegister, RegisterClass);
        public static bool operator ==(Dx12ShaderBindingMapping left, Dx12ShaderBindingMapping right) => left.Equals(right);
        public static bool operator !=(Dx12ShaderBindingMapping left, Dx12ShaderBindingMapping right) => !left.Equals(right);
    }

    public readonly struct VulkanShaderBindingMapping : IEquatable<VulkanShaderBindingMapping>
    {
        public ShaderBindingKey LogicalBinding { get; }
        public uint DescriptorSet { get; }
        public uint Binding { get; }
        public VulkanDescriptorKind DescriptorKind { get; }

        public VulkanShaderBindingMapping(
            ShaderBindingKey logicalBinding,
            uint descriptorSet,
            uint binding,
            VulkanDescriptorKind descriptorKind)
        {
            if (!Enum.IsDefined(descriptorKind))
            {
                throw new ArgumentOutOfRangeException(nameof(descriptorKind), descriptorKind, "Vulkan descriptor kind is not defined.");
            }

            LogicalBinding = logicalBinding;
            DescriptorSet = descriptorSet;
            Binding = binding;
            DescriptorKind = descriptorKind;
        }

        public bool Equals(VulkanShaderBindingMapping other)
        {
            return LogicalBinding == other.LogicalBinding
                && DescriptorSet == other.DescriptorSet
                && Binding == other.Binding
                && DescriptorKind == other.DescriptorKind;
        }

        public override bool Equals(object? obj) => obj is VulkanShaderBindingMapping other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(LogicalBinding, DescriptorSet, Binding, DescriptorKind);
        public static bool operator ==(VulkanShaderBindingMapping left, VulkanShaderBindingMapping right) => left.Equals(right);
        public static bool operator !=(VulkanShaderBindingMapping left, VulkanShaderBindingMapping right) => !left.Equals(right);
    }

    public readonly struct MetalDirectBindingMapping : IEquatable<MetalDirectBindingMapping>
    {
        public ShaderBindingKey LogicalBinding { get; }
        public uint BindingTable { get; }
        public ShaderPhysicalBindingNamespace Namespace { get; }
        public uint Index { get; }

        public MetalDirectBindingMapping(
            ShaderBindingKey logicalBinding,
            uint bindingTable,
            ShaderPhysicalBindingNamespace bindingNamespace,
            uint index)
        {
            ShaderBackendLayoutValidation.ValidateMetalNamespace(bindingNamespace, nameof(bindingNamespace));

            LogicalBinding = logicalBinding;
            BindingTable = bindingTable;
            Namespace = bindingNamespace;
            Index = index;
        }

        public bool Equals(MetalDirectBindingMapping other)
        {
            return LogicalBinding == other.LogicalBinding
                && BindingTable == other.BindingTable
                && Namespace == other.Namespace
                && Index == other.Index;
        }

        public override bool Equals(object? obj) => obj is MetalDirectBindingMapping other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(LogicalBinding, BindingTable, Namespace, Index);
        public static bool operator ==(MetalDirectBindingMapping left, MetalDirectBindingMapping right) => left.Equals(right);
        public static bool operator !=(MetalDirectBindingMapping left, MetalDirectBindingMapping right) => !left.Equals(right);
    }

    public readonly struct MetalReferenceBufferBindingMapping : IEquatable<MetalReferenceBufferBindingMapping>
    {
        public const uint ReferenceByteSize = 8;

        public ShaderBindingKey LogicalBinding { get; }
        public uint BindingTable { get; }
        public ShaderPhysicalBindingNamespace ResourceNamespace { get; }
        public uint ReferenceBufferIndex { get; }
        public ulong ByteOffset { get; }
        public uint ReferenceCount { get; }

        public MetalReferenceBufferBindingMapping(
            ShaderBindingKey logicalBinding,
            uint bindingTable,
            ShaderPhysicalBindingNamespace resourceNamespace,
            uint referenceBufferIndex,
            ulong byteOffset,
            uint referenceCount)
        {
            ShaderBackendLayoutValidation.ValidateMetalNamespace(resourceNamespace, nameof(resourceNamespace));
            if ((byteOffset % ReferenceByteSize) != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(byteOffset),
                    "Metal reference-buffer offsets must be aligned to the fixed 8-byte reference size.");
            }

            if (referenceCount == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(referenceCount), "Metal reference buffers require at least one reference.");
            }

            _ = checked(byteOffset + checked((ulong)referenceCount * ReferenceByteSize));

            LogicalBinding = logicalBinding;
            BindingTable = bindingTable;
            ResourceNamespace = resourceNamespace;
            ReferenceBufferIndex = referenceBufferIndex;
            ByteOffset = byteOffset;
            ReferenceCount = referenceCount;
        }

        internal ulong EndByteOffset => checked(ByteOffset + checked((ulong)ReferenceCount * ReferenceByteSize));

        public bool Equals(MetalReferenceBufferBindingMapping other)
        {
            return LogicalBinding == other.LogicalBinding
                && BindingTable == other.BindingTable
                && ResourceNamespace == other.ResourceNamespace
                && ReferenceBufferIndex == other.ReferenceBufferIndex
                && ByteOffset == other.ByteOffset
                && ReferenceCount == other.ReferenceCount;
        }

        public override bool Equals(object? obj) => obj is MetalReferenceBufferBindingMapping other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(LogicalBinding, BindingTable, ResourceNamespace, ReferenceBufferIndex, ByteOffset, ReferenceCount);
        public static bool operator ==(MetalReferenceBufferBindingMapping left, MetalReferenceBufferBindingMapping right) => left.Equals(right);
        public static bool operator !=(MetalReferenceBufferBindingMapping left, MetalReferenceBufferBindingMapping right) => !left.Equals(right);
    }

    public sealed class Dx12ShaderBackendLayout : IEquatable<Dx12ShaderBackendLayout>
    {
        private readonly ReadOnlyCollection<Dx12ShaderBindingMapping> m_Bindings;

        public IReadOnlyList<Dx12ShaderBindingMapping> Bindings => m_Bindings;

        public Dx12ShaderBackendLayout(IEnumerable<Dx12ShaderBindingMapping> bindings)
        {
            Dx12ShaderBindingMapping[] copy = ShaderBackendLayoutValidation.MaterializeAndSort(
                bindings,
                static mapping => mapping.LogicalBinding,
                nameof(bindings));

            HashSet<ShaderBindingKey> logicalBindings = new();
            HashSet<(uint Space, uint Register, ShaderBindingClass Class)> physicalBindings = new();
            foreach (Dx12ShaderBindingMapping mapping in copy)
            {
                if (!logicalBindings.Add(mapping.LogicalBinding))
                {
                    throw new ArgumentException($"DX12 layout contains duplicate logical binding {mapping.LogicalBinding}.", nameof(bindings));
                }

                if (!physicalBindings.Add((mapping.RegisterSpace, mapping.ShaderRegister, mapping.RegisterClass)))
                {
                    throw new ArgumentException(
                        $"DX12 layout contains duplicate physical register {mapping.RegisterClass}{mapping.ShaderRegister}, space{mapping.RegisterSpace}.",
                        nameof(bindings));
                }
            }

            m_Bindings = Array.AsReadOnly(copy);
        }

        public bool Equals(Dx12ShaderBackendLayout? other)
        {
            return other is not null && ShaderBackendLayoutValidation.SequenceEqual(m_Bindings, other.m_Bindings);
        }

        public override bool Equals(object? obj) => Equals(obj as Dx12ShaderBackendLayout);
        public override int GetHashCode() => ShaderBackendLayoutValidation.GetSequenceHashCode(m_Bindings);
    }

    public sealed class VulkanShaderBackendLayout : IEquatable<VulkanShaderBackendLayout>
    {
        private readonly ReadOnlyCollection<VulkanShaderBindingMapping> m_Bindings;

        public IReadOnlyList<VulkanShaderBindingMapping> Bindings => m_Bindings;

        public VulkanShaderBackendLayout(IEnumerable<VulkanShaderBindingMapping> bindings)
        {
            VulkanShaderBindingMapping[] copy = ShaderBackendLayoutValidation.MaterializeAndSort(
                bindings,
                static mapping => mapping.LogicalBinding,
                nameof(bindings));

            HashSet<ShaderBindingKey> logicalBindings = new();
            HashSet<(uint Set, uint Binding)> physicalBindings = new();
            foreach (VulkanShaderBindingMapping mapping in copy)
            {
                if (!logicalBindings.Add(mapping.LogicalBinding))
                {
                    throw new ArgumentException($"Vulkan layout contains duplicate logical binding {mapping.LogicalBinding}.", nameof(bindings));
                }

                if (!physicalBindings.Add((mapping.DescriptorSet, mapping.Binding)))
                {
                    throw new ArgumentException(
                        $"Vulkan layout contains duplicate physical binding set={mapping.DescriptorSet}, binding={mapping.Binding}.",
                        nameof(bindings));
                }
            }

            m_Bindings = Array.AsReadOnly(copy);
        }

        public bool Equals(VulkanShaderBackendLayout? other)
        {
            return other is not null && ShaderBackendLayoutValidation.SequenceEqual(m_Bindings, other.m_Bindings);
        }

        public override bool Equals(object? obj) => Equals(obj as VulkanShaderBackendLayout);
        public override int GetHashCode() => ShaderBackendLayoutValidation.GetSequenceHashCode(m_Bindings);
    }

    public sealed class MetalShaderBackendLayout : IEquatable<MetalShaderBackendLayout>
    {
        public const uint RootBindingTable = 0;

        private readonly ReadOnlyCollection<MetalDirectBindingMapping> m_DirectBindings;
        private readonly ReadOnlyCollection<MetalReferenceBufferBindingMapping> m_ReferenceBufferBindings;

        public IReadOnlyList<MetalDirectBindingMapping> DirectBindings => m_DirectBindings;
        public IReadOnlyList<MetalReferenceBufferBindingMapping> ReferenceBufferBindings => m_ReferenceBufferBindings;

        public MetalShaderBackendLayout(
            IEnumerable<MetalDirectBindingMapping>? directBindings = null,
            IEnumerable<MetalReferenceBufferBindingMapping>? referenceBufferBindings = null)
        {
            MetalDirectBindingMapping[] directCopy = ShaderBackendLayoutValidation.MaterializeAndSort(
                directBindings ?? Array.Empty<MetalDirectBindingMapping>(),
                static mapping => mapping.LogicalBinding,
                nameof(directBindings));
            MetalReferenceBufferBindingMapping[] referenceCopy = ShaderBackendLayoutValidation.MaterializeAndSort(
                referenceBufferBindings ?? Array.Empty<MetalReferenceBufferBindingMapping>(),
                static mapping => mapping.LogicalBinding,
                nameof(referenceBufferBindings));

            HashSet<ShaderBindingKey> logicalBindings = new();
            HashSet<(uint Table, ShaderPhysicalBindingNamespace Namespace, uint Index)> directLocations = new();
            foreach (MetalDirectBindingMapping mapping in directCopy)
            {
                if (!logicalBindings.Add(mapping.LogicalBinding))
                {
                    throw new ArgumentException($"Metal layout contains duplicate logical binding {mapping.LogicalBinding}.", nameof(directBindings));
                }

                if (!directLocations.Add((mapping.BindingTable, mapping.Namespace, mapping.Index)))
                {
                    throw new ArgumentException(
                        $"Metal layout contains duplicate direct {mapping.Namespace} index {mapping.Index} in binding table {mapping.BindingTable}.",
                        nameof(directBindings));
                }
            }

            Dictionary<(uint Table, uint BufferIndex), List<MetalReferenceBufferBindingMapping>> referenceBuffers = new();
            foreach (MetalReferenceBufferBindingMapping mapping in referenceCopy)
            {
                if (!logicalBindings.Add(mapping.LogicalBinding))
                {
                    throw new ArgumentException($"Metal layout contains duplicate logical binding {mapping.LogicalBinding}.", nameof(referenceBufferBindings));
                }

                (uint Table, uint BufferIndex) bufferKey = (mapping.BindingTable, mapping.ReferenceBufferIndex);
                if (directLocations.Contains((mapping.BindingTable, ShaderPhysicalBindingNamespace.Buffer, mapping.ReferenceBufferIndex)))
                {
                    throw new ArgumentException(
                        $"Metal reference buffer index {mapping.ReferenceBufferIndex} collides with a direct buffer in binding table {mapping.BindingTable}.",
                        nameof(referenceBufferBindings));
                }

                if (!referenceBuffers.TryGetValue(bufferKey, out List<MetalReferenceBufferBindingMapping>? ranges))
                {
                    ranges = new List<MetalReferenceBufferBindingMapping>();
                    referenceBuffers.Add(bufferKey, ranges);
                }

                foreach (MetalReferenceBufferBindingMapping existing in ranges)
                {
                    if (mapping.ByteOffset < existing.EndByteOffset && existing.ByteOffset < mapping.EndByteOffset)
                    {
                        throw new ArgumentException(
                            $"Metal reference-buffer ranges overlap in binding table {mapping.BindingTable}, buffer {mapping.ReferenceBufferIndex}.",
                            nameof(referenceBufferBindings));
                    }
                }

                ranges.Add(mapping);
            }

            m_DirectBindings = Array.AsReadOnly(directCopy);
            m_ReferenceBufferBindings = Array.AsReadOnly(referenceCopy);
        }

        public bool Equals(MetalShaderBackendLayout? other)
        {
            return other is not null
                && ShaderBackendLayoutValidation.SequenceEqual(m_DirectBindings, other.m_DirectBindings)
                && ShaderBackendLayoutValidation.SequenceEqual(m_ReferenceBufferBindings, other.m_ReferenceBufferBindings);
        }

        public override bool Equals(object? obj) => Equals(obj as MetalShaderBackendLayout);

        public override int GetHashCode()
        {
            return HashCode.Combine(
                ShaderBackendLayoutValidation.GetSequenceHashCode(m_DirectBindings),
                ShaderBackendLayoutValidation.GetSequenceHashCode(m_ReferenceBufferBindings));
        }
    }

    public sealed class ShaderBackendLayouts : IEquatable<ShaderBackendLayouts>
    {
        public ShaderLayoutSignature LogicalLayoutSignature { get; }
        public Dx12ShaderBackendLayout? Dx12 { get; }
        public VulkanShaderBackendLayout? Vulkan { get; }
        public MetalShaderBackendLayout? Metal { get; }

        public ShaderBackendLayouts(
            ShaderLayoutSignature logicalLayoutSignature,
            Dx12ShaderBackendLayout? dx12 = null,
            VulkanShaderBackendLayout? vulkan = null,
            MetalShaderBackendLayout? metal = null)
        {
            if (dx12 is null && vulkan is null && metal is null)
            {
                throw new ArgumentException("At least one backend layout must be provided.");
            }

            LogicalLayoutSignature = logicalLayoutSignature;
            Dx12 = dx12;
            Vulkan = vulkan;
            Metal = metal;
        }

        public bool Equals(ShaderBackendLayouts? other)
        {
            return other is not null
                && LogicalLayoutSignature == other.LogicalLayoutSignature
                && Equals(Dx12, other.Dx12)
                && Equals(Vulkan, other.Vulkan)
                && Equals(Metal, other.Metal);
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderBackendLayouts);
        public override int GetHashCode() => HashCode.Combine(LogicalLayoutSignature, Dx12, Vulkan, Metal);
    }

    internal static class ShaderBackendLayoutValidation
    {
        public static T[] MaterializeAndSort<T>(
            IEnumerable<T> values,
            Func<T, ShaderBindingKey> keySelector,
            string parameterName)
        {
            ArgumentNullException.ThrowIfNull(values, parameterName);
            T[] copy = new List<T>(values).ToArray();
            Array.Sort(copy, (left, right) => CompareKeys(keySelector(left), keySelector(right)));
            return copy;
        }

        public static int CompareKeys(ShaderBindingKey left, ShaderBindingKey right)
        {
            int table = left.Table.CompareTo(right.Table);
            if (table != 0)
            {
                return table;
            }

            int slot = left.Slot.CompareTo(right.Slot);
            return slot != 0 ? slot : left.Type.CompareTo(right.Type);
        }

        public static void ValidateMetalNamespace(ShaderPhysicalBindingNamespace bindingNamespace, string parameterName)
        {
            if (bindingNamespace is not ShaderPhysicalBindingNamespace.Buffer
                and not ShaderPhysicalBindingNamespace.Texture
                and not ShaderPhysicalBindingNamespace.Sampler)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    bindingNamespace,
                    "Metal bindings must use the buffer, texture, or sampler namespace.");
            }
        }

        public static bool SequenceEqual<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
            where T : IEquatable<T>
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (int index = 0; index < left.Count; ++index)
            {
                if (!left[index].Equals(right[index]))
                {
                    return false;
                }
            }

            return true;
        }

        public static int GetSequenceHashCode<T>(IReadOnlyList<T> values)
        {
            HashCode hash = new();
            foreach (T value in values)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }
}
