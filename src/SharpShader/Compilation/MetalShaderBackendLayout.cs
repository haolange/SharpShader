using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

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
}
