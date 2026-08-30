using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.Compilation.Internal;

namespace SharpShader.Compilation
{

    public sealed class ShaderInterfaceManifest : IEquatable<ShaderInterfaceManifest>
    {
        public const uint CurrentSchemaVersion = 3;

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
                    if (mapping.BindingTable != MetalShaderBackendLayout.RootBindingTable || mapping.Index != expectedIndex)
                    {
                        throw new ArgumentException(
                            $"Metal direct binding {mapping.LogicalBinding} must map to dense physical binding table {MetalShaderBackendLayout.RootBindingTable}, {expectedNamespace} index {expectedIndex}.");
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

                    if (mapping.BindingTable != MetalShaderBackendLayout.RootBindingTable
                        || mapping.ReferenceBufferIndex != expectedReferenceBufferIndex)
                    {
                        throw new ArgumentException(
                            $"Metal reference binding {mapping.LogicalBinding} must use root binding table {MetalShaderBackendLayout.RootBindingTable} and dense physical root buffer slot {expectedReferenceBufferIndex}.");
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
}
