using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.Compilation.Internal;

namespace SharpShader.Compilation
{

    public sealed class ShaderInterfaceEntry : IEquatable<ShaderInterfaceEntry>
    {
        private readonly ReadOnlyCollection<ShaderArtifactIdentity> m_Artifacts;

        public string Name { get; }
        public ShaderExecutionStage Stage { get; }
        public ShaderLayoutSignature LogicalLayoutSignature { get; }
        public ShaderAttachmentInterface AttachmentInterface { get; }
        public IReadOnlyList<ShaderArtifactIdentity> Artifacts => m_Artifacts;

        public ShaderInterfaceEntry(
            string name,
            ShaderExecutionStage stage,
            ShaderLayoutSignature logicalLayoutSignature,
            ShaderAttachmentInterface attachmentInterface,
            IEnumerable<ShaderArtifactIdentity> artifacts)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Shader interface entry name must not be empty.", nameof(name));
            }

            _ = ShaderStageMaskUtility.FromStage(stage);
            ArgumentNullException.ThrowIfNull(attachmentInterface);
            if (!string.Equals(
                    attachmentInterface.EntryPoint,
                    name,
                    StringComparison.Ordinal)
                || attachmentInterface.Stage != stage)
            {
                throw new ArgumentException(
                    "The attachment interface identity must match the manifest entry.",
                    nameof(attachmentInterface));
            }

            ArgumentNullException.ThrowIfNull(artifacts);
            ShaderArtifactIdentity[] copy = new List<ShaderArtifactIdentity>(artifacts).ToArray();
            Array.Sort(copy, CompareArtifacts);
            if (copy.Length == 0)
            {
                throw new ArgumentException("Shader interface entries require at least one artifact.", nameof(artifacts));
            }

            ShaderArtifactKind? previousKind = null;
            foreach (ShaderArtifactIdentity artifact in copy)
            {
                ArgumentNullException.ThrowIfNull(artifact);
                if (previousKind == artifact.ArtifactKind)
                {
                    throw new ArgumentException(
                        $"Entry {name} contains more than one {artifact.ArtifactKind} artifact.",
                        nameof(artifacts));
                }

                previousKind = artifact.ArtifactKind;
            }

            Name = name;
            Stage = stage;
            LogicalLayoutSignature = logicalLayoutSignature;
            AttachmentInterface = attachmentInterface;
            m_Artifacts = Array.AsReadOnly(copy);
        }

        public bool Equals(ShaderInterfaceEntry? other)
        {
            return other is not null
                && string.Equals(Name, other.Name, StringComparison.Ordinal)
                && Stage == other.Stage
                && LogicalLayoutSignature == other.LogicalLayoutSignature
                && AttachmentInterface.Equals(other.AttachmentInterface)
                && ShaderManifestValidation.SequenceEqual(m_Artifacts, other.m_Artifacts);
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderInterfaceEntry);
        public override int GetHashCode()
        {
            return HashCode.Combine(
                Name,
                Stage,
                LogicalLayoutSignature,
                AttachmentInterface,
                ShaderManifestValidation.GetSequenceHashCode(m_Artifacts));
        }

        private static int CompareArtifacts(ShaderArtifactIdentity? left, ShaderArtifactIdentity? right)
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

            int kind = left.ArtifactKind.CompareTo(right.ArtifactKind);
            if (kind != 0)
            {
                return kind;
            }

            int digest = string.CompareOrdinal(left.ContentDigest, right.ContentDigest);
            return digest != 0 ? digest : string.CompareOrdinal(left.ArtifactName, right.ArtifactName);
        }
    }
}
