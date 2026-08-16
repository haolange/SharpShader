using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.Compilation.Internal;

namespace SharpShader.Compilation
{

    public sealed class ShaderArtifactIdentity : IEquatable<ShaderArtifactIdentity>
    {
        public ShaderArtifactKind ArtifactKind { get; }
        public string ContentDigest { get; }
        public ulong ByteLength { get; }
        public string? ArtifactName { get; }

        public ShaderArtifactIdentity(
            ShaderArtifactKind artifactKind,
            string contentDigest,
            ulong byteLength,
            string? artifactName = null)
        {
            if (!Enum.IsDefined(artifactKind))
            {
                throw new ArgumentOutOfRangeException(nameof(artifactKind), artifactKind, "Shader artifact kind is not defined.");
            }

            ShaderManifestValidation.ValidateSha256(contentDigest, nameof(contentDigest));
            if (byteLength == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(byteLength), "Shader artifacts must not be empty.");
            }

            if (artifactName is not null && string.IsNullOrWhiteSpace(artifactName))
            {
                throw new ArgumentException("Artifact name must be null or non-empty.", nameof(artifactName));
            }

            ArtifactKind = artifactKind;
            ContentDigest = contentDigest;
            ByteLength = byteLength;
            ArtifactName = artifactName;
        }

        public bool Equals(ShaderArtifactIdentity? other)
        {
            return other is not null
                && ArtifactKind == other.ArtifactKind
                && string.Equals(ContentDigest, other.ContentDigest, StringComparison.Ordinal)
                && ByteLength == other.ByteLength
                && string.Equals(ArtifactName, other.ArtifactName, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderArtifactIdentity);
        public override int GetHashCode() => HashCode.Combine(ArtifactKind, ContentDigest, ByteLength, ArtifactName);
    }
}
