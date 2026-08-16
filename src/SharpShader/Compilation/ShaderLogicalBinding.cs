using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

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
}
