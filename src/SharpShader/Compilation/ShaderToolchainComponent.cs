using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.Compilation.Internal;

namespace SharpShader.Compilation
{
    public sealed class ShaderToolchainComponent : IEquatable<ShaderToolchainComponent>
    {
        public string Name { get; }
        public string Version { get; }
        public string? ContentDigest { get; }

        public ShaderToolchainComponent(string name, string version, string? contentDigest = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Toolchain component name must not be empty.", nameof(name));
            }

            if (string.IsNullOrWhiteSpace(version))
            {
                throw new ArgumentException("Toolchain component version must not be empty.", nameof(version));
            }

            if (contentDigest is not null)
            {
                ShaderManifestValidation.ValidateSha256(contentDigest, nameof(contentDigest));
            }

            Name = name;
            Version = version;
            ContentDigest = contentDigest;
        }

        public bool Equals(ShaderToolchainComponent? other)
        {
            return other is not null
                && string.Equals(Name, other.Name, StringComparison.Ordinal)
                && string.Equals(Version, other.Version, StringComparison.Ordinal)
                && string.Equals(ContentDigest, other.ContentDigest, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderToolchainComponent);
        public override int GetHashCode() => HashCode.Combine(Name, Version, ContentDigest);
    }
}
