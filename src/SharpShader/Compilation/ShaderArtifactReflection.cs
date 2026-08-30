using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public sealed class ShaderArtifactReflection : IEquatable<ShaderArtifactReflection>
    {
        public const uint CurrentSchemaVersion = 3;

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
