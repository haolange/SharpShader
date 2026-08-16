using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.Compilation.Internal;

namespace SharpShader.Compilation
{

    public sealed class ShaderInterfaceVariant : IEquatable<ShaderInterfaceVariant>
    {
        private readonly ReadOnlyCollection<string> m_Defines;
        private readonly ReadOnlyCollection<ShaderInterfaceEntry> m_Entries;

        public string Key { get; }
        public IReadOnlyList<string> Defines => m_Defines;
        public IReadOnlyList<ShaderInterfaceEntry> Entries => m_Entries;

        public ShaderInterfaceVariant(
            string key,
            IEnumerable<string>? defines,
            IEnumerable<ShaderInterfaceEntry> entries)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Shader variant key must not be empty.", nameof(key));
            }

            string[] defineCopy = defines is null ? Array.Empty<string>() : new List<string>(defines).ToArray();
            Array.Sort(defineCopy, StringComparer.Ordinal);
            for (int index = 0; index < defineCopy.Length; ++index)
            {
                if (string.IsNullOrWhiteSpace(defineCopy[index]))
                {
                    throw new ArgumentException("Shader variant defines must not contain empty values.", nameof(defines));
                }

                if (index > 0 && string.Equals(defineCopy[index - 1], defineCopy[index], StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Shader variant contains duplicate define {defineCopy[index]}.", nameof(defines));
                }
            }

            ArgumentNullException.ThrowIfNull(entries);
            ShaderInterfaceEntry[] entryCopy = new List<ShaderInterfaceEntry>(entries).ToArray();
            Array.Sort(entryCopy, CompareEntries);
            if (entryCopy.Length == 0)
            {
                throw new ArgumentException("Shader variants require at least one entry.", nameof(entries));
            }

            for (int index = 0; index < entryCopy.Length; ++index)
            {
                ArgumentNullException.ThrowIfNull(entryCopy[index]);
                if (index > 0
                    && entryCopy[index - 1].Stage == entryCopy[index].Stage
                    && string.Equals(entryCopy[index - 1].Name, entryCopy[index].Name, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Shader variant contains duplicate entry {entryCopy[index].Name} ({entryCopy[index].Stage}).",
                        nameof(entries));
                }

                if (!string.Equals(
                        entryCopy[index].AttachmentInterface.VariantKey,
                        key,
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "The attachment interface variant key must match its manifest variant.",
                        nameof(entries));
                }
            }

            Key = key;
            m_Defines = Array.AsReadOnly(defineCopy);
            m_Entries = Array.AsReadOnly(entryCopy);
        }

        public bool Equals(ShaderInterfaceVariant? other)
        {
            return other is not null
                && string.Equals(Key, other.Key, StringComparison.Ordinal)
                && ShaderManifestValidation.SequenceEqual(m_Defines, other.m_Defines, StringComparer.Ordinal)
                && ShaderManifestValidation.SequenceEqual(m_Entries, other.m_Entries);
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderInterfaceVariant);
        public override int GetHashCode() => HashCode.Combine(Key, ShaderManifestValidation.GetSequenceHashCode(m_Defines, StringComparer.Ordinal), ShaderManifestValidation.GetSequenceHashCode(m_Entries));

        private static int CompareEntries(ShaderInterfaceEntry? left, ShaderInterfaceEntry? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            int stage = left.Stage.CompareTo(right.Stage);
            return stage != 0 ? stage : string.CompareOrdinal(left.Name, right.Name);
        }
    }
}
