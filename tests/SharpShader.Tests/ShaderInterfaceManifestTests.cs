using System;
using System.Linq;
using System.Text.Json;
using SharpShader.Compilation;
using Xunit;

namespace Infinity.Rendering.Tests
{
    public sealed class ShaderInterfaceManifestTests
    {
        private const string SourceDigest =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string ArtifactDigest =
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string ToolDigest =
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

        [Fact]
        public void LayoutSignature_ShouldBeOrderIndependentAndAbiSensitive()
        {
            ShaderLogicalBinding texture = CreateTextureBinding();
            ShaderLogicalBinding sampler = CreateSamplerBinding();
            ShaderInterfaceLayout forward = new(new[] { texture, sampler });
            ShaderInterfaceLayout reverse = new(new[] { sampler, texture });
            ShaderInterfaceLayout changed = new(new[]
            {
                CreateTextureBinding(ShaderResourceDimension.Texture3D),
                sampler,
            });

            Assert.Equal(forward.Signature, reverse.Signature);
            Assert.Equal(forward, reverse);
            Assert.NotEqual(forward.Signature, changed.Signature);
        }

        [Fact]
        public void LayoutSignature_ShouldExcludeNamesAliasesAndProvenance()
        {
            ShaderLogicalBinding original = CreateTextureBinding();
            ShaderLogicalBinding renamed = new(
                original.Key,
                "RenamedTexture",
                new[] { "LegacyTextureName" },
                original.Shape,
                original.StageMask,
                provenance: ShaderBindingProvenance.ExplicitSource);

            ShaderInterfaceLayout left = new(new[] { original });
            ShaderInterfaceLayout right = new(new[] { renamed });

            Assert.Equal(left.Signature, right.Signature);
            Assert.NotEqual(left, right);
        }

        [Fact]
        public void LayoutPool_ShouldRejectStructuralHashCollision()
        {
            ShaderLayoutSignature forcedSignature = new(new byte[ShaderLayoutSignature.ByteLength]);
            ShaderInterfaceLayout first = new(
                new[] { CreateTextureBinding() },
                _ => forcedSignature);
            ShaderInterfaceLayout second = new(
                new[] { CreateTextureBinding(ShaderResourceDimension.Texture3D) },
                _ => forcedSignature);

            Assert.Throws<InvalidOperationException>(() => new ShaderInterfaceManifest(
                SourceDigest,
                CreateToolchain(),
                new[] { first, second },
                new[] { CreateVariant(forcedSignature) },
                new[] { CreateBackendLayouts(first) },
                ShaderProgramTarget.All));
        }

        [Fact]
        public void ManifestSerializer_ShouldRoundTripAndRemainCanonical()
        {
            ShaderInterfaceManifest canonical = CreateManifest(reverseInputs: false);
            ShaderInterfaceManifest reversed = CreateManifest(reverseInputs: true);

            string canonicalJson = ShaderInterfaceManifestSerializer.Serialize(canonical);
            string reversedJson = ShaderInterfaceManifestSerializer.Serialize(reversed);
            ShaderInterfaceManifest roundTrip =
                ShaderInterfaceManifestSerializer.Deserialize(canonicalJson);

            Assert.Equal(canonicalJson, reversedJson);
            Assert.Equal(canonical, roundTrip);
            Assert.Equal(
                canonicalJson,
                ShaderInterfaceManifestSerializer.Serialize(roundTrip));
            Assert.Contains("\"schemaVersion\":2", canonicalJson, StringComparison.Ordinal);
            Assert.Contains("\"logicalLayoutSignature\"", canonicalJson, StringComparison.Ordinal);
            Assert.Contains("\"referenceBufferBindings\":[]", canonicalJson, StringComparison.Ordinal);
        }

        [Theory]
        [Trait("Category", "SharpShaderAttachment")]
        [InlineData("unknown")]
        [InlineData("duplicate")]
        [InlineData("missing")]
        [InlineData("schema")]
        [InlineData("numericEnum")]
        [InlineData("caseChangedEnum")]
        [InlineData("signature")]
        public void ManifestSerializer_ShouldRejectNonCanonicalOrInvalidDocuments(string mutation)
        {
            ShaderInterfaceManifest manifest = CreateManifest();
            string json = ShaderInterfaceManifestSerializer.Serialize(manifest);
            string signature = manifest.LogicalLayouts[0].Signature.ToString();
            string mutated = mutation switch
            {
                "unknown" => json.Replace(
                    "{\"schemaVersion\":2,",
                    "{\"schemaVersion\":2,\"unexpected\":true,",
                    StringComparison.Ordinal),
                "duplicate" => json.Replace(
                    "{\"schemaVersion\":2,",
                    "{\"schemaVersion\":2,\"schemaVersion\":2,",
                    StringComparison.Ordinal),
                "missing" => json.Replace(
                    $"\"sourceDigest\":\"{SourceDigest}\",",
                    string.Empty,
                    StringComparison.Ordinal),
                "schema" => json.Replace(
                    "\"schemaVersion\":2",
                    "\"schemaVersion\":1",
                    StringComparison.Ordinal),
                "numericEnum" => json.Replace(
                    "\"stage\":\"Pixel\"",
                    "\"stage\":4",
                    StringComparison.Ordinal),
                "caseChangedEnum" => json.Replace(
                    "\"stage\":\"Pixel\"",
                    "\"stage\":\"pixel\"",
                    StringComparison.Ordinal),
                "signature" => json.Replace(
                    $"\"signature\":\"{signature}\"",
                    $"\"signature\":\"{new string('0', 64)}\"",
                    StringComparison.Ordinal),
                _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
            };

            Assert.NotEqual(json, mutated);
            Assert.Throws<JsonException>(() => ShaderInterfaceManifestSerializer.Deserialize(mutated));
        }

        [Fact]
        public void Manifest_ShouldRejectIncompleteOrTypeIncorrectBackendMappings()
        {
            ShaderInterfaceLayout layout = CreateLayout();
            ShaderLogicalBinding[] bindings = layout.Bindings.ToArray();
            ShaderBindingKey texture = bindings.Single(
                binding => binding.Shape.Kind == ShaderResourceKind.Texture).Key;
            ShaderBindingKey sampler = bindings.Single(
                binding => binding.Shape.Kind == ShaderResourceKind.Sampler).Key;
            ShaderBindingKey constants = bindings.Single(
                binding => binding.Shape.Kind == ShaderResourceKind.ConstantBuffer).Key;

            ShaderBackendLayouts incomplete = new(
                layout.Signature,
                vulkan: new VulkanShaderBackendLayout(new[]
                {
                    new VulkanShaderBindingMapping(
                        texture,
                        descriptorSet: 0,
                        binding: 0,
                        VulkanDescriptorKind.SampledImage),
                    new VulkanShaderBindingMapping(
                        sampler,
                        descriptorSet: 0,
                        binding: 1,
                        VulkanDescriptorKind.Sampler),
                }));
            ShaderBackendLayouts wrongKind = new(
                layout.Signature,
                vulkan: new VulkanShaderBackendLayout(new[]
                {
                    new VulkanShaderBindingMapping(
                        texture,
                        descriptorSet: 0,
                        binding: 0,
                        VulkanDescriptorKind.StorageImage),
                    new VulkanShaderBindingMapping(
                        sampler,
                        descriptorSet: 0,
                        binding: 1,
                        VulkanDescriptorKind.Sampler),
                    new VulkanShaderBindingMapping(
                        constants,
                        descriptorSet: 0,
                        binding: 2,
                        VulkanDescriptorKind.UniformBuffer),
                }));

            Assert.Throws<ArgumentException>(() => CreateManifest(layout, incomplete));
            Assert.Throws<ArgumentException>(() => CreateManifest(layout, wrongKind));
        }

        [Fact]
        public void Manifest_ShouldRejectSparsePhysicalBackendTables()
        {
            ShaderInterfaceLayout layout = CreateLayout();
            ShaderBackendLayouts planned = ShaderBackendLayoutPlanner.Plan(layout);
            ShaderBackendLayouts sparseVulkan = new(
                layout.Signature,
                vulkan: new VulkanShaderBackendLayout(
                    planned.Vulkan!.Bindings.Select(
                        mapping => new VulkanShaderBindingMapping(
                            mapping.LogicalBinding,
                            mapping.LogicalBinding.Table,
                            mapping.Binding,
                            mapping.DescriptorKind))));
            ShaderBackendLayouts sparseMetal = new(
                layout.Signature,
                metal: new MetalShaderBackendLayout(
                    planned.Metal!.DirectBindings.Select(
                        mapping => new MetalDirectBindingMapping(
                            mapping.LogicalBinding,
                            mapping.LogicalBinding.Table,
                            mapping.Namespace,
                            mapping.Index))));

            Assert.Throws<ArgumentException>(() => CreateManifest(layout, sparseVulkan));
            Assert.Throws<ArgumentException>(() => CreateManifest(layout, sparseMetal));
        }

        [Fact]
        public void Manifest_ShouldValidateMetalReferenceArrayCount()
        {
            ShaderInterfaceLayout layout = CreateLayout(textureArray: true);
            ShaderBackendLayouts planned = ShaderBackendLayoutPlanner.Plan(layout);
            MetalReferenceBufferBindingMapping[] mappings = planned.Metal!.ReferenceBufferBindings
                .Select(
                    mapping => mapping.LogicalBinding.Type == ShaderBindingClass.ShaderResource
                        ? new MetalReferenceBufferBindingMapping(
                            mapping.LogicalBinding,
                            mapping.BindingTable,
                            mapping.ResourceNamespace,
                            mapping.ReferenceBufferIndex,
                            mapping.ByteOffset,
                            referenceCount: 1)
                        : mapping)
                .ToArray();
            ShaderBackendLayouts invalid = new(
                layout.Signature,
                metal: new MetalShaderBackendLayout(referenceBufferBindings: mappings));

            Assert.Throws<ArgumentException>(() => CreateManifest(layout, invalid));
        }

        [Fact]
        public void Layout_ShouldRejectOverlappingSameClassRangesWithIntersectingStages()
        {
            ShaderLogicalBinding array = CreateTextureBinding(array: true);
            ShaderLogicalBinding overlap = new(
                new ShaderBindingKey(2, 1, ShaderBindingClass.ShaderResource),
                "OverlappingTexture",
                null,
                new ShaderResourceShape(
                    ShaderResourceKind.Texture,
                    ShaderResourceDimension.Texture2D,
                    ShaderResourceAccess.ReadOnly),
                ShaderStageMask.Pixel);

            Assert.Throws<ArgumentException>(() => new ShaderInterfaceLayout(new[] { array, overlap }));
        }

        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public void Manifest_ShouldRequireExactArtifactCoverageForEveryEntry()
        {
            ShaderInterfaceLayout layout = CreateLayout();
            ShaderBackendLayouts backends = CreateBackendLayouts(layout);

            Assert.Throws<ArgumentException>(() => CreateCoverageManifest(
                ShaderProgramTarget.DirectX12 | ShaderProgramTarget.Vulkan,
                CreateCoverageEntry("MissingSpirV", ShaderArtifactKind.Dxil)));
            Assert.Throws<ArgumentException>(() => CreateCoverageManifest(
                ShaderProgramTarget.DirectX12,
                CreateCoverageEntry(
                    "ExtraSpirV",
                    ShaderArtifactKind.Dxil,
                    ShaderArtifactKind.SpirV)));
            Assert.Throws<ArgumentException>(() => CreateCoverageManifest(
                ShaderProgramTarget.DirectX12 | ShaderProgramTarget.Vulkan,
                CreateCoverageEntry(
                    "CompleteEntry",
                    ShaderArtifactKind.Dxil,
                    ShaderArtifactKind.SpirV),
                CreateCoverageEntry(
                    "IncompleteSibling",
                    ShaderArtifactKind.Dxil)));

            ShaderInterfaceManifest CreateCoverageManifest(
                ShaderProgramTarget targets,
                params ShaderInterfaceEntry[] entries)
            {
                return new ShaderInterfaceManifest(
                    SourceDigest,
                    CreateToolchain(),
                    new[] { layout },
                    new[]
                    {
                        new ShaderInterfaceVariant(
                            "coverage",
                            defines: null,
                            entries),
                    },
                    new[] { backends },
                    targets);
            }

            ShaderInterfaceEntry CreateCoverageEntry(
                string name,
                params ShaderArtifactKind[] artifactKinds)
            {
                ShaderArtifactIdentity[] artifacts = artifactKinds
                    .Select((kind, artifactIndex) =>
                        new ShaderArtifactIdentity(
                            kind,
                            ArtifactDigest,
                            byteLength: (ulong)(artifactIndex + 1),
                            artifactName:
                                $"{name}.{kind.ToString().ToLowerInvariant()}"))
                    .ToArray();
                return new ShaderInterfaceEntry(
                    name,
                    ShaderExecutionStage.Compute,
                    layout.Signature,
                    new ShaderAttachmentInterface(
                        "coverage",
                        name,
                        ShaderExecutionStage.Compute),
                    artifacts);
            }
        }
        private static ShaderInterfaceManifest CreateManifest(bool reverseInputs = false)
        {
            ShaderInterfaceLayout layout = CreateLayout(reverseInputs);
            return CreateManifest(layout, CreateBackendLayouts(layout, reverseInputs), reverseInputs);
        }

        private static ShaderInterfaceManifest CreateManifest(
            ShaderInterfaceLayout layout,
            ShaderBackendLayouts backendLayouts,
            bool reverseInputs = false)
        {
            ShaderToolchainComponent[] toolchain = CreateToolchain();
            ShaderInterfaceVariant variant = CreateVariant(layout.Signature, reverseInputs);
            if (reverseInputs)
            {
                Array.Reverse(toolchain);
            }

            return new ShaderInterfaceManifest(
                SourceDigest,
                toolchain,
                new[] { layout },
                new[] { variant },
                new[] { backendLayouts },
                ShaderProgramTarget.All);
        }

        private static ShaderInterfaceLayout CreateLayout(
            bool reverseInputs = false,
            bool textureArray = false)
        {
            ShaderLogicalBinding[] bindings =
            {
                CreateTextureBinding(array: textureArray),
                CreateSamplerBinding(),
                CreateConstantBufferBinding(),
            };
            if (reverseInputs)
            {
                Array.Reverse(bindings);
            }

            return new ShaderInterfaceLayout(bindings);
        }

        private static ShaderToolchainComponent[] CreateToolchain()
        {
            return new[]
            {
                new ShaderToolchainComponent("DXC", "1.9.0.5347", ToolDigest),
                new ShaderToolchainComponent("SharpShader", "1.0.0"),
            };
        }

        private static ShaderInterfaceVariant CreateVariant(
            ShaderLayoutSignature signature,
            bool reverseInputs = false)
        {
            ShaderArtifactIdentity[] artifacts =
            {
                new ShaderArtifactIdentity(ShaderArtifactKind.Dxil, ArtifactDigest, 128, "shader.dxil"),
                new ShaderArtifactIdentity(ShaderArtifactKind.SpirV, ArtifactDigest, 256, "shader.spv"),
                new ShaderArtifactIdentity(ShaderArtifactKind.MslSource, ArtifactDigest, 512, "shader.metal"),
            };
            string[] defines = { "QUALITY=HIGH", "USE_FOG=1" };
            if (reverseInputs)
            {
                Array.Reverse(artifacts);
                Array.Reverse(defines);
            }

            return new ShaderInterfaceVariant(
                "quality-high",
                defines,
                new[]
                {
                    new ShaderInterfaceEntry(
                        "MainPS",
                        ShaderExecutionStage.Pixel,
                        signature,
                        new ShaderAttachmentInterface(
                            "quality-high",
                            "MainPS",
                            ShaderExecutionStage.Pixel,
                            new ShaderAttachmentPhase(0)),
                        artifacts),
                });
        }

        private static ShaderBackendLayouts CreateBackendLayouts(
            ShaderInterfaceLayout layout,
            bool reverseInputs = false)
        {
            ShaderLogicalBinding[] bindings = layout.Bindings.ToArray();
            ShaderBindingKey texture = bindings.Single(
                binding => binding.Shape.Kind == ShaderResourceKind.Texture).Key;
            ShaderBindingKey sampler = bindings.Single(
                binding => binding.Shape.Kind == ShaderResourceKind.Sampler).Key;
            ShaderBindingKey constants = bindings.Single(
                binding => binding.Shape.Kind == ShaderResourceKind.ConstantBuffer).Key;

            Dx12ShaderBindingMapping[] dx12 =
            {
                new Dx12ShaderBindingMapping(texture, 2, 0, ShaderBindingClass.ShaderResource),
                new Dx12ShaderBindingMapping(sampler, 2, 0, ShaderBindingClass.Sampler),
                new Dx12ShaderBindingMapping(constants, 2, 0, ShaderBindingClass.ConstantBuffer),
            };
            VulkanShaderBindingMapping[] vulkan =
            {
                new VulkanShaderBindingMapping(texture, 0, 0, VulkanDescriptorKind.SampledImage),
                new VulkanShaderBindingMapping(sampler, 0, 1, VulkanDescriptorKind.Sampler),
                new VulkanShaderBindingMapping(constants, 0, 2, VulkanDescriptorKind.UniformBuffer),
            };
            MetalDirectBindingMapping[] metal =
            {
                new MetalDirectBindingMapping(
                    texture,
                    MetalShaderBackendLayout.RootBindingTable,
                    ShaderPhysicalBindingNamespace.Texture,
                    0),
                new MetalDirectBindingMapping(
                    sampler,
                    MetalShaderBackendLayout.RootBindingTable,
                    ShaderPhysicalBindingNamespace.Sampler,
                    0),
                new MetalDirectBindingMapping(
                    constants,
                    MetalShaderBackendLayout.RootBindingTable,
                    ShaderPhysicalBindingNamespace.Buffer,
                    0),
            };
            if (reverseInputs)
            {
                Array.Reverse(dx12);
                Array.Reverse(vulkan);
                Array.Reverse(metal);
            }

            return new ShaderBackendLayouts(
                layout.Signature,
                new Dx12ShaderBackendLayout(dx12),
                new VulkanShaderBackendLayout(vulkan),
                new MetalShaderBackendLayout(metal));
        }

        private static ShaderLogicalBinding CreateTextureBinding(
            ShaderResourceDimension dimension = ShaderResourceDimension.Texture2D,
            bool array = false)
        {
            return new ShaderLogicalBinding(
                new ShaderBindingKey(2, 0, ShaderBindingClass.ShaderResource),
                "Textures",
                new[] { "AlbedoTextures" },
                new ShaderResourceShape(
                    ShaderResourceKind.Texture,
                    dimension,
                    ShaderResourceAccess.ReadOnly,
                    array
                        ? new ShaderArrayShape(new[] { ShaderArrayExtent.Bounded(2) })
                        : null),
                ShaderStageMask.Pixel,
                provenance: ShaderBindingProvenance.ExplicitSource);
        }

        private static ShaderLogicalBinding CreateSamplerBinding()
        {
            return new ShaderLogicalBinding(
                new ShaderBindingKey(2, 0, ShaderBindingClass.Sampler),
                "LinearSampler",
                null,
                new ShaderResourceShape(
                    ShaderResourceKind.Sampler,
                    ShaderResourceDimension.Unknown,
                    ShaderResourceAccess.ReadOnly,
                    samplerKind: ShaderSamplerKind.Regular),
                ShaderStageMask.Pixel,
                provenance: ShaderBindingProvenance.ExplicitSource);
        }

        private static ShaderLogicalBinding CreateConstantBufferBinding()
        {
            ShaderValueLayout scalar = new(
                ShaderValueKind.Scalar,
                ShaderScalarType.Float32,
                rows: 1,
                columns: 1,
                byteSize: 4);
            ShaderConstantBufferLayout constants = new(
                "FrameConstants",
                16,
                new[] { new ShaderValueMember("Exposure", 0, 4, scalar) });
            return new ShaderLogicalBinding(
                new ShaderBindingKey(2, 0, ShaderBindingClass.ConstantBuffer),
                "FrameConstants",
                null,
                new ShaderResourceShape(
                    ShaderResourceKind.ConstantBuffer,
                    ShaderResourceDimension.Buffer,
                    ShaderResourceAccess.ReadOnly),
                ShaderStageMask.Pixel,
                constants,
                ShaderBindingProvenance.ExplicitSource);
        }
    }
}
