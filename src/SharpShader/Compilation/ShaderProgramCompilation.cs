using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.Compilation
{

    public sealed class ShaderProgramCompilation
    {
        private readonly ReadOnlyCollection<ShaderProgramArtifact> m_Artifacts;

        public string CacheKey { get; }
        public ShaderInterfaceManifest Manifest { get; }
        public IReadOnlyList<ShaderProgramArtifact> Artifacts => m_Artifacts;

        internal ShaderProgramCompilation(
            string cacheKey,
            ShaderInterfaceManifest manifest,
            IEnumerable<ShaderProgramArtifact> artifacts)
        {
            if (string.IsNullOrWhiteSpace(cacheKey))
            {
                throw new ArgumentException("Compilation cache key must not be empty.", nameof(cacheKey));
            }

            ArgumentNullException.ThrowIfNull(manifest);
            ArgumentNullException.ThrowIfNull(artifacts);
            ShaderProgramArtifact[] copy = new List<ShaderProgramArtifact>(artifacts).ToArray();
            Array.Sort(copy, ShaderProgramModelValidation.CompareArtifacts);

            CacheKey = cacheKey;
            Manifest = manifest;
            m_Artifacts = Array.AsReadOnly(copy);
        }

        public ShaderProgramArtifact GetArtifact(
            string variantKey,
            string entryPoint,
            ShaderExecutionStage stage,
            ShaderArtifactKind artifactKind)
        {
            _ = ShaderStageMaskUtility.FromStage(stage);
            foreach (ShaderProgramArtifact artifact in m_Artifacts)
            {
                if (artifact.Stage == stage
                    && artifact.Identity.ArtifactKind == artifactKind
                    && string.Equals(artifact.VariantKey, variantKey, StringComparison.Ordinal)
                    && string.Equals(artifact.EntryPoint, entryPoint, StringComparison.Ordinal))
                {
                    return artifact;
                }
            }

            throw new KeyNotFoundException(
                $"No {artifactKind} artifact exists for variant {variantKey}, "
                + $"entry {entryPoint} ({stage}).");
        }

        public ShaderProgramArtifact GetArtifact(
            string variantKey,
            string entryPoint,
            ShaderArtifactKind artifactKind)
        {
            ShaderProgramArtifact? match = null;
            foreach (ShaderProgramArtifact artifact in m_Artifacts)
            {
                if (artifact.Identity.ArtifactKind == artifactKind
                    && string.Equals(artifact.VariantKey, variantKey, StringComparison.Ordinal)
                    && string.Equals(artifact.EntryPoint, entryPoint, StringComparison.Ordinal))
                {
                    if (match is not null)
                    {
                        throw new InvalidOperationException(
                            $"Artifact lookup for {variantKey}/{entryPoint}/{artifactKind} "
                            + "is ambiguous across shader stages. "
                            + "Use the stage-qualified overload.");
                    }

                    match = artifact;
                }
            }

            if (match is not null)
            {
                return match;
            }

            throw new KeyNotFoundException(
                $"No {artifactKind} artifact exists for variant {variantKey}, entry {entryPoint}.");
        }
    }
}
