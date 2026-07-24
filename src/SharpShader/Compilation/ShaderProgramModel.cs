using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.Compilation
{
    [Flags]
    public enum ShaderProgramTarget
    {
        None = 0,
        DirectX12 = 1 << 0,
        Vulkan = 1 << 1,
        MetalMsl = 1 << 2,
        All = DirectX12 | Vulkan | MetalMsl,
    }

    public sealed class ShaderProgramEntry
    {
        public string Name { get; }
        public ShaderExecutionStage Stage { get; }

        public ShaderProgramEntry(string name, ShaderExecutionStage stage)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Shader program entry name must not be empty.", nameof(name));
            }

            _ = ShaderStageMaskUtility.FromStage(stage);
            Name = name;
            Stage = stage;
        }
    }

    public sealed class ShaderProgramVariant
    {
        private readonly ReadOnlyCollection<ShaderDefine> m_Defines;

        public static ShaderProgramVariant Default { get; } = new("default");

        public string Key { get; }
        public IReadOnlyList<ShaderDefine> Defines => m_Defines;

        public ShaderProgramVariant(string key, IEnumerable<ShaderDefine>? defines = null)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Shader program variant key must not be empty.", nameof(key));
            }

            ShaderDefine[] copy = defines is null
                ? Array.Empty<ShaderDefine>()
                : new List<ShaderDefine>(defines).ToArray();
            Array.Sort(copy, ShaderProgramModelValidation.CompareDefines);
            ShaderProgramModelValidation.ValidateDefines(copy, nameof(defines));

            Key = key;
            m_Defines = Array.AsReadOnly(copy);
        }
    }

    public sealed class ShaderProgramCompileRequest
    {
        private readonly ReadOnlyCollection<ShaderProgramEntry> m_Entries;
        private readonly ReadOnlyCollection<ShaderProgramVariant> m_Variants;
        private readonly ReadOnlyCollection<ShaderDefine> m_GlobalDefines;
        private readonly ReadOnlyCollection<string> m_IncludeDirectories;
        private readonly ReadOnlyDictionary<ShaderBindingKey, uint> m_MetalArrayCapacities;

        public string Source { get; }
        public string SourceName { get; }
        public IReadOnlyList<ShaderProgramEntry> Entries => m_Entries;
        public IReadOnlyList<ShaderProgramVariant> Variants => m_Variants;
        public IReadOnlyList<ShaderDefine> GlobalDefines => m_GlobalDefines;
        public IReadOnlyList<string> IncludeDirectories => m_IncludeDirectories;
        public IReadOnlyDictionary<ShaderBindingKey, uint> MetalArrayCapacities => m_MetalArrayCapacities;
        public ShaderProgramTarget Targets { get; }
        public ShaderModelVersion ShaderModel { get; }
        public SpirvCompileOptions SpirvOptions { get; }
        public MslCompileOptions MslOptions { get; }
        public bool Enable16BitTypes { get; }
        public bool EnableDebugInfo { get; }
        public bool DisableOptimizations { get; }
        public int OptimizationLevel { get; }
        public bool SkipValidation { get; }
        public bool TreatWarningsAsErrors { get; }

        public ShaderProgramCompileRequest(
            string source,
            string sourceName,
            IEnumerable<ShaderProgramEntry> entries,
            IEnumerable<ShaderProgramVariant> variants,
            ShaderProgramTarget targets,
            ShaderModelVersion shaderModel,
            IEnumerable<ShaderDefine>? globalDefines = null,
            IEnumerable<string>? includeDirectories = null,
            SpirvCompileOptions? spirvOptions = null,
            MslCompileOptions? mslOptions = null,
            IReadOnlyDictionary<ShaderBindingKey, uint>? metalArrayCapacities = null,
            bool enable16BitTypes = true,
            bool enableDebugInfo = false,
            bool disableOptimizations = false,
            int optimizationLevel = 3,
            bool skipValidation = false,
            bool treatWarningsAsErrors = false)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(entries);
            ArgumentNullException.ThrowIfNull(variants);

            ShaderProgramEntry[] entryCopy = new List<ShaderProgramEntry>(entries).ToArray();
            Array.Sort(entryCopy, ShaderProgramModelValidation.CompareEntries);
            ShaderProgramModelValidation.ValidateEntries(entryCopy);

            ShaderProgramVariant[] variantCopy = new List<ShaderProgramVariant>(variants).ToArray();
            Array.Sort(variantCopy, static (left, right) =>
                string.CompareOrdinal(left?.Key, right?.Key));
            ShaderProgramModelValidation.ValidateVariants(variantCopy);

            ShaderDefine[] globalDefineCopy = globalDefines is null
                ? Array.Empty<ShaderDefine>()
                : new List<ShaderDefine>(globalDefines).ToArray();
            Array.Sort(globalDefineCopy, ShaderProgramModelValidation.CompareDefines);
            ShaderProgramModelValidation.ValidateDefines(globalDefineCopy, nameof(globalDefines));

            string[] includeCopy = includeDirectories is null
                ? Array.Empty<string>()
                : new List<string>(includeDirectories).ToArray();
            Dictionary<ShaderBindingKey, uint> capacityCopy = metalArrayCapacities is null
                ? new Dictionary<ShaderBindingKey, uint>()
                : new Dictionary<ShaderBindingKey, uint>(metalArrayCapacities);

            Source = source;
            SourceName = sourceName;
            m_Entries = Array.AsReadOnly(entryCopy);
            m_Variants = Array.AsReadOnly(variantCopy);
            m_GlobalDefines = Array.AsReadOnly(globalDefineCopy);
            m_IncludeDirectories = Array.AsReadOnly(includeCopy);
            m_MetalArrayCapacities =
                new ReadOnlyDictionary<ShaderBindingKey, uint>(capacityCopy);
            Targets = targets;
            ShaderModel = shaderModel;
            SpirvCompileOptions requestedSpirv =
                spirvOptions ?? SpirvCompileOptions.Default;
            ArgumentNullException.ThrowIfNull(requestedSpirv.BindingShifts);
            ArgumentNullException.ThrowIfNull(requestedSpirv.AdditionalArguments);
            SpirvOptions = new SpirvCompileOptions
            {
                UseDxLayout = requestedSpirv.UseDxLayout,
                UseGlLayout = requestedSpirv.UseGlLayout,
                UseScalarLayout = requestedSpirv.UseScalarLayout,
                InvertY = requestedSpirv.InvertY,
                BindingShifts = Array.AsReadOnly(new List<SpirvBindingShift>(
                    requestedSpirv.BindingShifts).ToArray()),
                TargetEnvironment = requestedSpirv.TargetEnvironment,
                AdditionalArguments = Array.AsReadOnly(new List<string>(
                    requestedSpirv.AdditionalArguments).ToArray()),
            };
            MslCompileOptions requestedMsl = mslOptions ?? MslCompileOptions.Default;
            MslOptions = requestedMsl with
            {
                Platform = requestedMsl.Platform,
                MslVersion = requestedMsl.MslVersion,
            };
            Enable16BitTypes = enable16BitTypes;
            EnableDebugInfo = enableDebugInfo;
            DisableOptimizations = disableOptimizations;
            OptimizationLevel = optimizationLevel;
            SkipValidation = skipValidation;
            TreatWarningsAsErrors = treatWarningsAsErrors;
        }
    }

    public sealed class ShaderProgramCacheLimits
    {
        public static ShaderProgramCacheLimits Default { get; } = new();

        public long MaximumSourceBytes { get; }
        public int MaximumIncludeFileCount { get; }
        public long MaximumIncludeFileBytes { get; }
        public long MaximumIncludeTotalBytes { get; }
        public long MaximumCachePackageBytes { get; }
        public int MaximumMemoryCacheEntries { get; }
        public long MaximumMemoryCacheBytes { get; }
        public int MaximumPersistentCacheEntries { get; }
        public long MaximumPersistentCacheBytes { get; }
        public int MaximumQuarantineEntries { get; }
        public long MaximumQuarantineBytes { get; }

        public ShaderProgramCacheLimits(
            long maximumSourceBytes = 64L * 1024 * 1024,
            int maximumIncludeFileCount = 32768,
            long maximumIncludeFileBytes = 64L * 1024 * 1024,
            long maximumIncludeTotalBytes = 1024L * 1024 * 1024,
            long maximumCachePackageBytes = 1024L * 1024 * 1024,
            int maximumMemoryCacheEntries = 256,
            long maximumMemoryCacheBytes = 512L * 1024 * 1024,
            int maximumPersistentCacheEntries = 4096,
            long maximumPersistentCacheBytes = 8L * 1024 * 1024 * 1024,
            int maximumQuarantineEntries = 64,
            long maximumQuarantineBytes = 512L * 1024 * 1024)
        {
            if (maximumSourceBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumSourceBytes));
            }

            if (maximumIncludeFileCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumIncludeFileCount));
            }

            if (maximumIncludeFileBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumIncludeFileBytes));
            }

            if (maximumIncludeTotalBytes < maximumIncludeFileBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumIncludeTotalBytes),
                    "The include total-byte limit must be at least the per-file limit.");
            }

            if (maximumCachePackageBytes <= 0
                || maximumCachePackageBytes > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumCachePackageBytes));
            }

            if (maximumMemoryCacheEntries <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumMemoryCacheEntries));
            }

            if (maximumMemoryCacheBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumMemoryCacheBytes));
            }
            if (maximumPersistentCacheEntries <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumPersistentCacheEntries));
            }

            if (maximumPersistentCacheBytes < maximumCachePackageBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumPersistentCacheBytes),
                    "The persistent-cache byte limit must be at least the per-package limit.");
            }

            if (maximumQuarantineEntries < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumQuarantineEntries));
            }

            if (maximumQuarantineBytes < 0
                || maximumQuarantineBytes > maximumPersistentCacheBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumQuarantineBytes),
                    "The quarantine byte limit must be non-negative and no greater than the persistent-cache limit.");
            }

            MaximumSourceBytes = maximumSourceBytes;
            MaximumIncludeFileCount = maximumIncludeFileCount;
            MaximumIncludeFileBytes = maximumIncludeFileBytes;
            MaximumIncludeTotalBytes = maximumIncludeTotalBytes;
            MaximumCachePackageBytes = maximumCachePackageBytes;
            MaximumMemoryCacheEntries = maximumMemoryCacheEntries;
            MaximumMemoryCacheBytes = maximumMemoryCacheBytes;
            MaximumPersistentCacheEntries = maximumPersistentCacheEntries;
            MaximumPersistentCacheBytes = maximumPersistentCacheBytes;
            MaximumQuarantineEntries = maximumQuarantineEntries;
            MaximumQuarantineBytes = maximumQuarantineBytes;
        }
    }

    public sealed class ShaderProgramCompilerOptions
    {
        public string? PersistentCacheDirectory { get; }
        public ShaderProgramCacheLimits CacheLimits { get; }

        public ShaderProgramCompilerOptions(
            string? persistentCacheDirectory = null,
            ShaderProgramCacheLimits? cacheLimits = null)
        {
            if (persistentCacheDirectory is not null
                && string.IsNullOrWhiteSpace(persistentCacheDirectory))
            {
                throw new ArgumentException(
                    "A persistent cache directory must be null or non-empty.",
                    nameof(persistentCacheDirectory));
            }

            PersistentCacheDirectory = persistentCacheDirectory is null
                ? null
                : System.IO.Path.GetFullPath(persistentCacheDirectory);
            CacheLimits = cacheLimits ?? ShaderProgramCacheLimits.Default;
        }
    }

    public sealed class ShaderProgramArtifact
    {
        private readonly byte[] m_Content;

        public string VariantKey { get; }
        public string EntryPoint { get; }
        public ShaderExecutionStage Stage { get; }
        public ShaderArtifactIdentity Identity { get; }
        public ReadOnlyMemory<byte> Content => new((byte[])m_Content.Clone());
        public string? Text { get; }
        public void CopyContentTo(System.IO.Stream destination)
        {
            ArgumentNullException.ThrowIfNull(destination);
            byte[] copy = (byte[])m_Content.Clone();
            destination.Write(copy, 0, copy.Length);
        }


        internal ShaderProgramArtifact(
            string variantKey,
            string entryPoint,
            ShaderExecutionStage stage,
            ShaderArtifactIdentity identity,
            ReadOnlySpan<byte> content,
            string? text)
        {
            if (string.IsNullOrWhiteSpace(variantKey))
            {
                throw new ArgumentException("Artifact variant key must not be empty.", nameof(variantKey));
            }

            if (string.IsNullOrWhiteSpace(entryPoint))
            {
                throw new ArgumentException("Artifact entry point must not be empty.", nameof(entryPoint));
            }

            ArgumentNullException.ThrowIfNull(identity);
            _ = ShaderStageMaskUtility.FromStage(stage);
            if ((ulong)content.Length != identity.ByteLength)
            {
                throw new ArgumentException(
                    "Artifact content length does not match its identity.",
                    nameof(content));
            }

            VariantKey = variantKey;
            EntryPoint = entryPoint;
            Stage = stage;
            Identity = identity;
            m_Content = content.ToArray();
            Text = text;
        }

        internal byte[] CopyContent()
        {
            return (byte[])m_Content.Clone();
        }
    }

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

    internal static class ShaderProgramModelValidation
    {
        internal static int CompareDefines(ShaderDefine left, ShaderDefine right)
        {
            int name = string.CompareOrdinal(left.Name, right.Name);
            return name != 0 ? name : string.CompareOrdinal(left.Value, right.Value);
        }

        internal static int CompareEntries(ShaderProgramEntry? left, ShaderProgramEntry? right)
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

        internal static int CompareArtifacts(
            ShaderProgramArtifact? left,
            ShaderProgramArtifact? right)
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

            int variant = string.CompareOrdinal(left.VariantKey, right.VariantKey);
            if (variant != 0)
            {
                return variant;
            }

            int stage = left.Stage.CompareTo(right.Stage);
            if (stage != 0)
            {
                return stage;
            }

            int entry = string.CompareOrdinal(left.EntryPoint, right.EntryPoint);
            return entry != 0
                ? entry
                : left.Identity.ArtifactKind.CompareTo(right.Identity.ArtifactKind);
        }

        internal static void ValidateDefines(ShaderDefine[] defines, string parameterName)
        {
            for (int index = 0; index < defines.Length; ++index)
            {
                ShaderDefine define = defines[index];
                if (string.IsNullOrWhiteSpace(define.Name))
                {
                    throw new ArgumentException(
                        "Shader defines must not contain empty names.",
                        parameterName);
                }

                if (index > 0
                    && string.Equals(defines[index - 1].Name, define.Name, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Shader define {define.Name} is duplicated.",
                        parameterName);
                }
            }
        }

        internal static void ValidateEntries(ShaderProgramEntry[] entries)
        {
            if (entries.Length == 0)
            {
                throw new ArgumentException(
                    "Shader program compilation requires at least one entry point.",
                    nameof(entries));
            }

            for (int index = 0; index < entries.Length; ++index)
            {
                ArgumentNullException.ThrowIfNull(entries[index]);
                if (index > 0
                    && entries[index - 1].Stage == entries[index].Stage
                    && string.Equals(entries[index - 1].Name, entries[index].Name, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Shader program entry {entries[index].Name} ({entries[index].Stage}) is duplicated.",
                        nameof(entries));
                }
            }
        }

        internal static void ValidateVariants(ShaderProgramVariant[] variants)
        {
            if (variants.Length == 0)
            {
                throw new ArgumentException(
                    "Shader program compilation requires at least one variant.",
                    nameof(variants));
            }

            for (int index = 0; index < variants.Length; ++index)
            {
                ArgumentNullException.ThrowIfNull(variants[index]);
                if (index > 0
                    && string.Equals(variants[index - 1].Key, variants[index].Key, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Shader program variant {variants[index].Key} is duplicated.",
                        nameof(variants));
                }
            }
        }
    }
}
