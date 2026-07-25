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

    public sealed class ShaderInterfaceLayout : IEquatable<ShaderInterfaceLayout>
    {
        private readonly ReadOnlyCollection<ShaderLogicalBinding> m_Bindings;
        private readonly byte[] m_CanonicalAbi;

        public ShaderLayoutSignature Signature { get; }
        public IReadOnlyList<ShaderLogicalBinding> Bindings => m_Bindings;

        public ShaderInterfaceLayout(IEnumerable<ShaderLogicalBinding> bindings)
            : this(bindings, ShaderLayoutCanonicalWriter.ComputeSha256)
        {
        }

        internal ShaderInterfaceLayout(
            IEnumerable<ShaderLogicalBinding> bindings,
            Func<ReadOnlyMemory<byte>, ShaderLayoutSignature> hashProvider)
        {
            ArgumentNullException.ThrowIfNull(bindings);
            ArgumentNullException.ThrowIfNull(hashProvider);

            ShaderLogicalBinding[] copy = new List<ShaderLogicalBinding>(bindings).ToArray();
            Array.Sort(copy, CompareBindings);

            for (int index = 0; index < copy.Length; ++index)
            {
                ArgumentNullException.ThrowIfNull(copy[index]);
                if (index > 0 && copy[index - 1].Key == copy[index].Key)
                {
                    throw new ArgumentException($"Logical layout contains duplicate binding {copy[index].Key}.", nameof(bindings));
                }
            }

            ValidateRegisterRanges(copy);

            m_Bindings = Array.AsReadOnly(copy);
            m_CanonicalAbi = ShaderLayoutCanonicalWriter.Write(m_Bindings);
            Signature = hashProvider(m_CanonicalAbi);
        }

        public bool Equals(ShaderInterfaceLayout? other)
        {
            return other is not null
                && Signature == other.Signature
                && m_CanonicalAbi.AsSpan().SequenceEqual(other.m_CanonicalAbi)
                && ContentEquals(other);
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderInterfaceLayout);
        public override int GetHashCode() => Signature.GetHashCode();

        internal bool ContentEquals(ShaderInterfaceLayout other)
        {
            if (m_Bindings.Count != other.m_Bindings.Count)
            {
                return false;
            }

            for (int index = 0; index < m_Bindings.Count; ++index)
            {
                if (!m_Bindings[index].Equals(other.m_Bindings[index]))
                {
                    return false;
                }
            }

            return true;
        }

        internal bool AbiEquals(ShaderInterfaceLayout other)
        {
            return Signature == other.Signature
                && m_CanonicalAbi.AsSpan().SequenceEqual(other.m_CanonicalAbi);
        }

        private static int CompareBindings(ShaderLogicalBinding? left, ShaderLogicalBinding? right)
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

            return ShaderBackendLayoutValidation.CompareKeys(left.Key, right.Key);
        }

        private static void ValidateRegisterRanges(ShaderLogicalBinding[] bindings)
        {
            for (int leftIndex = 0; leftIndex < bindings.Length; ++leftIndex)
            {
                ShaderLogicalBinding left = bindings[leftIndex];
                uint leftEnd = GetLastSlot(left);

                for (int rightIndex = leftIndex + 1; rightIndex < bindings.Length; ++rightIndex)
                {
                    ShaderLogicalBinding right = bindings[rightIndex];
                    if (right.Key.Table != left.Key.Table || right.Key.Type != left.Key.Type)
                    {
                        continue;
                    }

                    if ((left.StageMask & right.StageMask) == ShaderStageMask.None)
                    {
                        continue;
                    }

                    uint rightEnd = GetLastSlot(right);
                    if (left.Key.Slot <= rightEnd && right.Key.Slot <= leftEnd)
                    {
                        throw new ArgumentException(
                            $"Logical bindings {left.Key} and {right.Key} overlap for intersecting shader stages.",
                            nameof(bindings));
                    }
                }
            }
        }

        private static uint GetLastSlot(ShaderLogicalBinding binding)
        {
            uint? count = binding.Shape.Array.BoundedElementCount;
            return count.HasValue
                ? checked(binding.Key.Slot + count.Value - 1)
                : uint.MaxValue;
        }
    }

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

    public sealed class ShaderInterfaceManifest : IEquatable<ShaderInterfaceManifest>
    {
        public const uint CurrentSchemaVersion = 2;

        private readonly ReadOnlyCollection<ShaderToolchainComponent> m_ToolchainComponents;
        private readonly ReadOnlyCollection<ShaderInterfaceLayout> m_LogicalLayouts;
        private readonly ReadOnlyCollection<ShaderInterfaceVariant> m_Variants;
        private readonly ReadOnlyCollection<ShaderBackendLayouts> m_BackendLayouts;

        public uint SchemaVersion { get; }
        public string SourceDigest { get; }
        public ShaderProgramTarget Targets { get; }
        public IReadOnlyList<ShaderToolchainComponent> ToolchainComponents => m_ToolchainComponents;
        public IReadOnlyList<ShaderInterfaceLayout> LogicalLayouts => m_LogicalLayouts;
        public IReadOnlyList<ShaderInterfaceVariant> Variants => m_Variants;
        public IReadOnlyList<ShaderBackendLayouts> BackendLayouts => m_BackendLayouts;

        public ShaderInterfaceManifest(
            string sourceDigest,
            IEnumerable<ShaderToolchainComponent> toolchainComponents,
            IEnumerable<ShaderInterfaceLayout> logicalLayouts,
            IEnumerable<ShaderInterfaceVariant> variants,
            IEnumerable<ShaderBackendLayouts> backendLayouts,
            ShaderProgramTarget targets,
            uint schemaVersion = CurrentSchemaVersion)
        {
            if (schemaVersion != CurrentSchemaVersion)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(schemaVersion),
                    schemaVersion,
                    $"Shader interface manifest schema must be {CurrentSchemaVersion}.");
            }

            ShaderManifestValidation.ValidateSha256(sourceDigest, nameof(sourceDigest));
            if (targets == ShaderProgramTarget.None
                || (targets & ~ShaderProgramTarget.All) != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(targets),
                    targets,
                    "Manifest targets must contain at least one defined target.");
            }

            ShaderToolchainComponent[] toolchainCopy = MaterializeToolchain(toolchainComponents);
            ShaderInterfaceLayout[] layoutCopy = MaterializeLayouts(logicalLayouts);
            ShaderInterfaceVariant[] variantCopy = MaterializeVariants(variants);
            ShaderBackendLayouts[] backendCopy = MaterializeBackends(backendLayouts);
            ValidateArtifactCoverage(variantCopy, targets);

            Dictionary<ShaderLayoutSignature, ShaderInterfaceLayout> layoutsBySignature = new();
            foreach (ShaderInterfaceLayout layout in layoutCopy)
            {
                layoutsBySignature.Add(layout.Signature, layout);
            }

            foreach (ShaderInterfaceVariant variant in variantCopy)
            {
                foreach (ShaderInterfaceEntry entry in variant.Entries)
                {
                    if (!layoutsBySignature.ContainsKey(entry.LogicalLayoutSignature))
                    {
                        throw new ArgumentException(
                            $"Variant {variant.Key} entry {entry.Name} references unknown logical layout {entry.LogicalLayoutSignature}.",
                            nameof(variants));
                    }
                }
            }

            if (backendCopy.Length != layoutCopy.Length)
            {
                throw new ArgumentException(
                    "Every interned logical layout must have exactly one backend-layout set.",
                    nameof(backendLayouts));
            }

            foreach (ShaderBackendLayouts backend in backendCopy)
            {
                if (!layoutsBySignature.TryGetValue(backend.LogicalLayoutSignature, out ShaderInterfaceLayout? logicalLayout))
                {
                    throw new ArgumentException(
                        $"Backend mapping references unknown logical layout {backend.LogicalLayoutSignature}.",
                        nameof(backendLayouts));
                }

                ValidateBackendMappings(logicalLayout, backend);
            }

            SchemaVersion = schemaVersion;
            SourceDigest = sourceDigest;
            Targets = targets;
            m_ToolchainComponents = Array.AsReadOnly(toolchainCopy);
            m_LogicalLayouts = Array.AsReadOnly(layoutCopy);
            m_Variants = Array.AsReadOnly(variantCopy);
            m_BackendLayouts = Array.AsReadOnly(backendCopy);
        }

        public bool Equals(ShaderInterfaceManifest? other)
        {
            return other is not null
                && SchemaVersion == other.SchemaVersion
                && string.Equals(SourceDigest, other.SourceDigest, StringComparison.Ordinal)
                && Targets == other.Targets
                && ShaderManifestValidation.SequenceEqual(m_ToolchainComponents, other.m_ToolchainComponents)
                && ShaderManifestValidation.SequenceEqual(m_LogicalLayouts, other.m_LogicalLayouts)
                && ShaderManifestValidation.SequenceEqual(m_Variants, other.m_Variants)
                && ShaderManifestValidation.SequenceEqual(m_BackendLayouts, other.m_BackendLayouts);
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderInterfaceManifest);

        public override int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(SchemaVersion);
            hash.Add(SourceDigest, StringComparer.Ordinal);
            hash.Add(Targets);
            hash.Add(ShaderManifestValidation.GetSequenceHashCode(m_ToolchainComponents));
            hash.Add(ShaderManifestValidation.GetSequenceHashCode(m_LogicalLayouts));
            hash.Add(ShaderManifestValidation.GetSequenceHashCode(m_Variants));
            hash.Add(ShaderManifestValidation.GetSequenceHashCode(m_BackendLayouts));
            return hash.ToHashCode();
        }

        private static void ValidateArtifactCoverage(
            IReadOnlyList<ShaderInterfaceVariant> variants,
            ShaderProgramTarget targets)
        {
            HashSet<ShaderArtifactKind> required = new();
            if ((targets & ShaderProgramTarget.DirectX12) != 0)
            {
                required.Add(ShaderArtifactKind.Dxil);
            }
            if ((targets & ShaderProgramTarget.Vulkan) != 0)
            {
                required.Add(ShaderArtifactKind.SpirV);
            }
            if ((targets & ShaderProgramTarget.MetalMsl) != 0)
            {
                required.Add(ShaderArtifactKind.MslSource);
            }

            foreach (ShaderInterfaceVariant variant in variants)
            {
                foreach (ShaderInterfaceEntry entry in variant.Entries)
                {
                    if (entry.Artifacts.Count != required.Count)
                    {
                        throw new ArgumentException(
                            $"Variant {variant.Key} entry {entry.Name} has "
                            + $"{entry.Artifacts.Count} artifacts, but targets {targets} "
                            + $"require exactly {required.Count}.",
                            nameof(variants));
                    }

                    HashSet<ShaderArtifactKind> actual = new();
                    foreach (ShaderArtifactIdentity artifact in entry.Artifacts)
                    {
                        actual.Add(artifact.ArtifactKind);
                    }

                    if (!actual.SetEquals(required))
                    {
                        throw new ArgumentException(
                            $"Variant {variant.Key} entry {entry.Name} artifact kinds "
                            + $"[{string.Join(", ", actual)}] do not exactly match "
                            + $"targets {targets}.",
                            nameof(variants));
                    }
                }
            }
        }

        private static ShaderToolchainComponent[] MaterializeToolchain(IEnumerable<ShaderToolchainComponent> components)
        {
            ArgumentNullException.ThrowIfNull(components);
            ShaderToolchainComponent[] copy = new List<ShaderToolchainComponent>(components).ToArray();
            Array.Sort(copy, static (left, right) => string.CompareOrdinal(left?.Name, right?.Name));
            if (copy.Length == 0)
            {
                throw new ArgumentException("Shader interface manifests require toolchain provenance.", nameof(components));
            }

            for (int index = 0; index < copy.Length; ++index)
            {
                ArgumentNullException.ThrowIfNull(copy[index]);
                if (index > 0 && string.Equals(copy[index - 1].Name, copy[index].Name, StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Toolchain component {copy[index].Name} is duplicated.", nameof(components));
                }
            }

            return copy;
        }

        private static ShaderInterfaceLayout[] MaterializeLayouts(IEnumerable<ShaderInterfaceLayout> layouts)
        {
            ArgumentNullException.ThrowIfNull(layouts);
            ShaderInterfaceLayout[] input = new List<ShaderInterfaceLayout>(layouts).ToArray();
            if (input.Length == 0)
            {
                throw new ArgumentException("Shader interface manifests require at least one logical layout.", nameof(layouts));
            }

            Array.Sort(input, static (left, right) => left.Signature.CompareTo(right.Signature));
            List<ShaderInterfaceLayout> interned = new(input.Length);
            foreach (ShaderInterfaceLayout layout in input)
            {
                ArgumentNullException.ThrowIfNull(layout);
                if (interned.Count == 0 || interned[^1].Signature != layout.Signature)
                {
                    interned.Add(layout);
                    continue;
                }

                ShaderInterfaceLayout existing = interned[^1];
                if (!existing.AbiEquals(layout))
                {
                    throw new InvalidOperationException(
                        $"Shader layout signature collision detected for {layout.Signature}; structural equality rejected interning.");
                }

                if (!existing.ContentEquals(layout))
                {
                    throw new ArgumentException(
                        $"ABI-equivalent layout {layout.Signature} contains inconsistent names or aliases; merge reflection metadata before creating the manifest.",
                        nameof(layouts));
                }
            }

            return interned.ToArray();
        }

        private static ShaderInterfaceVariant[] MaterializeVariants(IEnumerable<ShaderInterfaceVariant> variants)
        {
            ArgumentNullException.ThrowIfNull(variants);
            ShaderInterfaceVariant[] copy = new List<ShaderInterfaceVariant>(variants).ToArray();
            Array.Sort(copy, static (left, right) => string.CompareOrdinal(left?.Key, right?.Key));
            if (copy.Length == 0)
            {
                throw new ArgumentException("Shader interface manifests require at least one variant.", nameof(variants));
            }

            for (int index = 0; index < copy.Length; ++index)
            {
                ArgumentNullException.ThrowIfNull(copy[index]);
                if (index > 0 && string.Equals(copy[index - 1].Key, copy[index].Key, StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Shader variant {copy[index].Key} is duplicated.", nameof(variants));
                }
            }

            return copy;
        }

        private static ShaderBackendLayouts[] MaterializeBackends(IEnumerable<ShaderBackendLayouts> backends)
        {
            ArgumentNullException.ThrowIfNull(backends);
            ShaderBackendLayouts[] copy = new List<ShaderBackendLayouts>(backends).ToArray();
            Array.Sort(copy, static (left, right) => left.LogicalLayoutSignature.CompareTo(right.LogicalLayoutSignature));
            for (int index = 0; index < copy.Length; ++index)
            {
                ArgumentNullException.ThrowIfNull(copy[index]);
                if (index > 0 && copy[index - 1].LogicalLayoutSignature == copy[index].LogicalLayoutSignature)
                {
                    throw new ArgumentException(
                        $"Logical layout {copy[index].LogicalLayoutSignature} has more than one backend-layout set.",
                        nameof(backends));
                }
            }

            return copy;
        }

        private static void ValidateBackendMappings(ShaderInterfaceLayout logicalLayout, ShaderBackendLayouts backend)
        {
            Dictionary<ShaderBindingKey, ShaderLogicalBinding> logicalBindings = new();
            foreach (ShaderLogicalBinding binding in logicalLayout.Bindings)
            {
                logicalBindings.Add(binding.Key, binding);
            }

            if (backend.Dx12 is not null)
            {
                ValidateCoverage(
                    logicalBindings,
                    backend.Dx12.Bindings,
                    static mapping => mapping.LogicalBinding,
                    "DX12");
            }

            if (backend.Vulkan is not null)
            {
                ValidateCoverage(
                    logicalBindings,
                    backend.Vulkan.Bindings,
                    static mapping => mapping.LogicalBinding,
                    "Vulkan");
                uint currentLogicalTable = 0;
                uint expectedDescriptorSet = 0;
                uint expectedBinding = 0;
                bool hasCurrentSet = false;
                foreach (VulkanShaderBindingMapping mapping in backend.Vulkan.Bindings)
                {
                    if (!hasCurrentSet || mapping.LogicalBinding.Table != currentLogicalTable)
                    {
                        if (hasCurrentSet)
                        {
                            expectedDescriptorSet = checked(expectedDescriptorSet + 1);
                        }

                        currentLogicalTable = mapping.LogicalBinding.Table;
                        expectedBinding = 0;
                        hasCurrentSet = true;
                    }

                    if (mapping.DescriptorSet != expectedDescriptorSet || mapping.Binding != expectedBinding)
                    {
                        throw new ArgumentException(
                            $"Vulkan binding {mapping.LogicalBinding} must map to dense physical set {expectedDescriptorSet}, binding {expectedBinding}.");
                    }

                    VulkanDescriptorKind expected = ShaderBackendLayoutSemantics.GetVulkanDescriptorKind(logicalBindings[mapping.LogicalBinding]);
                    if (mapping.DescriptorKind != expected)
                    {
                        throw new ArgumentException(
                            $"Vulkan binding {mapping.LogicalBinding} requires descriptor kind {expected}, not {mapping.DescriptorKind}.");
                    }

                    expectedBinding = checked(expectedBinding + 1);
                }
            }

            if (backend.Metal is not null)
            {
                bool requiresReferenceBuffers = RequiresMetalReferenceBuffers(logicalLayout);
                if (requiresReferenceBuffers && backend.Metal.DirectBindings.Count != 0)
                {
                    throw new ArgumentException(
                        "Metal layouts with multiple logical tables or any descriptor array must use reference buffers for every binding.");
                }

                if (!requiresReferenceBuffers && backend.Metal.ReferenceBufferBindings.Count != 0)
                {
                    throw new ArgumentException(
                        "Metal layouts with one scalar-only logical table must use direct argument-table bindings.");
                }

                List<ShaderBindingKey> metalKeys = new();
                Dictionary<ShaderPhysicalBindingNamespace, uint> nextDirectIndex = new();
                foreach (MetalDirectBindingMapping mapping in backend.Metal.DirectBindings)
                {
                    metalKeys.Add(mapping.LogicalBinding);
                    ShaderLogicalBinding logical = logicalBindings.TryGetValue(mapping.LogicalBinding, out ShaderLogicalBinding? value)
                        ? value
                        : throw new ArgumentException($"Metal mapping contains unknown logical binding {mapping.LogicalBinding}.");
                    ShaderPhysicalBindingNamespace expectedNamespace =
                        ShaderBackendLayoutSemantics.GetMetalNamespace(logical);
                    if (mapping.Namespace != expectedNamespace)
                    {
                        throw new ArgumentException(
                            $"Metal binding {mapping.LogicalBinding} requires namespace {expectedNamespace}, not {mapping.Namespace}.");
                    }

                    uint expectedIndex = nextDirectIndex.TryGetValue(expectedNamespace, out uint nextIndex)
                        ? nextIndex
                        : 0;
                    if (mapping.ArgumentTable != MetalShaderBackendLayout.RootArgumentTable || mapping.Index != expectedIndex)
                    {
                        throw new ArgumentException(
                            $"Metal direct binding {mapping.LogicalBinding} must map to dense physical argument table {MetalShaderBackendLayout.RootArgumentTable}, {expectedNamespace} index {expectedIndex}.");
                    }

                    nextDirectIndex[expectedNamespace] = checked(expectedIndex + 1);
                }

                uint currentLogicalTable = 0;
                uint expectedReferenceBufferIndex = 0;
                ulong expectedReferenceOffset = 0;
                bool hasCurrentLogicalTable = false;
                foreach (MetalReferenceBufferBindingMapping mapping in backend.Metal.ReferenceBufferBindings)
                {
                    metalKeys.Add(mapping.LogicalBinding);
                    ShaderLogicalBinding logical = logicalBindings.TryGetValue(mapping.LogicalBinding, out ShaderLogicalBinding? value)
                        ? value
                        : throw new ArgumentException($"Metal reference mapping contains unknown logical binding {mapping.LogicalBinding}.");
                    ShaderPhysicalBindingNamespace expectedNamespace =
                        ShaderBackendLayoutSemantics.GetMetalNamespace(logical);
                    if (mapping.ResourceNamespace != expectedNamespace)
                    {
                        throw new ArgumentException(
                            $"Metal binding {mapping.LogicalBinding} requires namespace {expectedNamespace}, not {mapping.ResourceNamespace}.");
                    }

                    if (!hasCurrentLogicalTable || logical.Key.Table != currentLogicalTable)
                    {
                        if (hasCurrentLogicalTable)
                        {
                            expectedReferenceBufferIndex = checked(expectedReferenceBufferIndex + 1);
                        }

                        currentLogicalTable = logical.Key.Table;
                        expectedReferenceOffset = 0;
                        hasCurrentLogicalTable = true;
                    }

                    if (mapping.ArgumentTable != MetalShaderBackendLayout.RootArgumentTable
                        || mapping.ReferenceBufferIndex != expectedReferenceBufferIndex)
                    {
                        throw new ArgumentException(
                            $"Metal reference binding {mapping.LogicalBinding} must use root argument table {MetalShaderBackendLayout.RootArgumentTable} and dense physical root buffer slot {expectedReferenceBufferIndex}.");
                    }

                    uint? boundedCount = logical.Shape.Array.BoundedElementCount;
                    if (boundedCount.HasValue && mapping.ReferenceCount != boundedCount.Value)
                    {
                        throw new ArgumentException(
                            $"Metal reference binding {mapping.LogicalBinding} requires {boundedCount.Value} 8-byte references, not {mapping.ReferenceCount}.");
                    }

                    if (mapping.ByteOffset != expectedReferenceOffset)
                    {
                        throw new ArgumentException(
                            $"Metal reference binding {mapping.LogicalBinding} must begin at byte offset {expectedReferenceOffset}, not {mapping.ByteOffset}.");
                    }

                    expectedReferenceOffset = checked(
                        expectedReferenceOffset
                        + checked((ulong)mapping.ReferenceCount * MetalReferenceBufferBindingMapping.ReferenceByteSize));
                }

                ValidateCoverage(logicalBindings, metalKeys, static key => key, "Metal");
            }
        }

        private static bool RequiresMetalReferenceBuffers(ShaderInterfaceLayout layout)
        {
            if (layout.Bindings.Count == 0)
            {
                return false;
            }

            uint table = layout.Bindings[0].Key.Table;
            foreach (ShaderLogicalBinding binding in layout.Bindings)
            {
                if (binding.Key.Table != table || binding.Shape.Array.IsArray)
                {
                    return true;
                }
            }

            return false;
        }

        private static void ValidateCoverage<T>(
            IReadOnlyDictionary<ShaderBindingKey, ShaderLogicalBinding> logicalBindings,
            IReadOnlyList<T> mappings,
            Func<T, ShaderBindingKey> keySelector,
            string backendName)
        {
            if (mappings.Count != logicalBindings.Count)
            {
                throw new ArgumentException(
                    $"{backendName} mapping count {mappings.Count} does not cover {logicalBindings.Count} logical bindings.");
            }

            HashSet<ShaderBindingKey> seen = new();
            foreach (T mapping in mappings)
            {
                ShaderBindingKey key = keySelector(mapping);
                if (!logicalBindings.ContainsKey(key))
                {
                    throw new ArgumentException($"{backendName} mapping contains unknown logical binding {key}.");
                }

                if (!seen.Add(key))
                {
                    throw new ArgumentException($"{backendName} mapping contains duplicate logical binding {key}.");
                }
            }
        }
    }

    internal static class ShaderManifestValidation
    {
        public static void ValidateSha256(string value, string parameterName)
        {
            if (value.Length != ShaderLayoutSignature.ByteLength * 2)
            {
                throw new ArgumentException("SHA-256 digests must contain 64 lowercase hexadecimal characters.", parameterName);
            }

            foreach (char character in value)
            {
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f')))
                {
                    throw new ArgumentException("SHA-256 digests must contain 64 lowercase hexadecimal characters.", parameterName);
                }
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

        public static bool SequenceEqual<T>(
            IReadOnlyList<T> left,
            IReadOnlyList<T> right,
            IEqualityComparer<T> comparer)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (int index = 0; index < left.Count; ++index)
            {
                if (!comparer.Equals(left[index], right[index]))
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

        public static int GetSequenceHashCode<T>(IReadOnlyList<T> values, IEqualityComparer<T> comparer)
        {
            HashCode hash = new();
            foreach (T value in values)
            {
                hash.Add(value, comparer);
            }

            return hash.ToHashCode();
        }
    }
}
