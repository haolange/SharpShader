using System;
using System.Linq;
using SharpShader.Compilation;
using Xunit;

namespace SharpShader.Tests
{
    public class SharpShaderBindingModelTests
    {
        [Fact]
        public void LogicalBindingKey_ShouldKeepRegisterClassesDistinct()
        {
            ShaderBindingKey texture = new(0, 0, ShaderBindingClass.ShaderResource);
            ShaderBindingKey sampler = new(0, 0, ShaderBindingClass.Sampler);
            ShaderBindingKey constantBuffer = new(0, 0, ShaderBindingClass.ConstantBuffer);
            ShaderBindingKey unorderedAccess = new(0, 0, ShaderBindingClass.UnorderedAccess);

            Assert.Equal(4, new[] { texture, sampler, constantBuffer, unorderedAccess }.Distinct().Count());
        }

        [Fact]
        public void ArrayShape_ShouldRepresentBoundedRuntimeAndSpecializationExtents()
        {
            ShaderArrayShape bounded = new(new[]
            {
                ShaderArrayExtent.Bounded(2),
                ShaderArrayExtent.Bounded(3),
            });
            ShaderArrayShape runtime = new(new[] { ShaderArrayExtent.Runtime() });
            ShaderArrayShape specialized = new(new[] { ShaderArrayExtent.SpecializationConstant(7) });

            Assert.Equal(6u, bounded.BoundedElementCount);
            Assert.True(runtime.HasRuntimeExtent);
            Assert.Null(runtime.BoundedElementCount);
            Assert.True(specialized.HasSpecializationConstantExtent);
            Assert.Equal(7u, specialized.Extents[0].SpecializationConstantId);
        }

        [Fact]
        public void ArrayShape_ShouldRejectZeroOverflowAndMisplacedRuntimeExtent()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ShaderArrayExtent.Bounded(0));
            Assert.Throws<OverflowException>(() => new ShaderArrayShape(new[]
            {
                ShaderArrayExtent.Bounded(uint.MaxValue),
                ShaderArrayExtent.Bounded(2),
            }));
            Assert.Throws<ArgumentException>(() => new ShaderArrayShape(new[]
            {
                ShaderArrayExtent.Runtime(),
                ShaderArrayExtent.Bounded(2),
            }));
        }

        [Fact]
        public void ResourceReflection_ShouldRejectBindingRangeOverflowAndClassMismatch()
        {
            ShaderResourceShape textureArray = new(
                ShaderResourceKind.Texture,
                ShaderResourceDimension.Texture2D,
                ShaderResourceAccess.ReadOnly,
                new ShaderArrayShape(new[] { ShaderArrayExtent.Bounded(2) }));
            ShaderPhysicalBindingLocation location = new(
                ShaderBackendKind.DirectX12,
                0,
                uint.MaxValue,
                ShaderPhysicalBindingNamespace.ShaderResource);

            Assert.Throws<OverflowException>(() => new ShaderResourceBindingReflection(
                "Textures",
                new ShaderBindingKey(0, uint.MaxValue, ShaderBindingClass.ShaderResource),
                textureArray,
                ShaderStageMask.Pixel,
                location));

            Assert.Throws<OverflowException>(() => new ShaderResourceBindingReflection(
                "Textures",
                new ShaderBindingKey(0, 0, ShaderBindingClass.ShaderResource),
                textureArray,
                ShaderStageMask.Pixel,
                location));

            Assert.Throws<ArgumentException>(() => new ShaderResourceBindingReflection(
                "Textures",
                new ShaderBindingKey(0, 0, ShaderBindingClass.UnorderedAccess),
                textureArray,
                ShaderStageMask.Pixel,
                new ShaderPhysicalBindingLocation(
                    ShaderBackendKind.Vulkan,
                    0,
                    0,
                    ShaderPhysicalBindingNamespace.Unified)));
        }

        [Fact]
        public void ResourceShape_ShouldExhaustivelyValidateSpecialAccessContracts()
        {
            Assert.Throws<ArgumentException>(() => new ShaderResourceShape(
                ShaderResourceKind.InputAttachment,
                ShaderResourceDimension.Texture2D,
                ShaderResourceAccess.ReadWrite));
            Assert.Throws<ArgumentException>(() => new ShaderResourceShape(
                ShaderResourceKind.AtomicCounter,
                ShaderResourceDimension.Buffer,
                ShaderResourceAccess.ReadOnly));
            Assert.Throws<ArgumentException>(() => new ShaderResourceShape(
                ShaderResourceKind.FeedbackTexture,
                ShaderResourceDimension.Texture2D,
                ShaderResourceAccess.ReadOnly));
            Assert.Throws<ArgumentException>(() => new ShaderResourceShape(
                ShaderResourceKind.ShaderRecordBuffer,
                ShaderResourceDimension.Buffer,
                ShaderResourceAccess.ReadWrite));
        }

        [Fact]
        public void LogicalBinding_ShouldPreserveAliasesProvenanceAndConstantBufferLayout()
        {
            string[] aliases = { "ZConstants", "AConstants" };
            ShaderConstantBufferLayout constantBuffer = CreateConstantBufferLayout();
            ShaderLogicalBinding binding = new(
                new ShaderBindingKey(2, 3, ShaderBindingClass.ConstantBuffer),
                "Constants",
                aliases,
                new ShaderResourceShape(
                    ShaderResourceKind.ConstantBuffer,
                    ShaderResourceDimension.Buffer,
                    ShaderResourceAccess.ReadOnly),
                ShaderStageMask.Vertex | ShaderStageMask.Pixel,
                constantBuffer,
                ShaderBindingProvenance.ExplicitSource);
            aliases[0] = "Mutated";

            Assert.Equal(new[] { "AConstants", "ZConstants" }, binding.Aliases);
            Assert.Equal(ShaderBindingProvenance.ExplicitSource, binding.Provenance);
            Assert.Same(constantBuffer, binding.ConstantBufferLayout);

            ShaderResourceBindingReflection reflection = new(
                binding,
                new ShaderPhysicalBindingLocation(
                    ShaderBackendKind.DirectX12,
                    2,
                    3,
                    ShaderPhysicalBindingNamespace.ConstantBuffer));
            Assert.Same(binding, reflection.LogicalBinding);
            Assert.Same(constantBuffer, reflection.ConstantBufferLayout);
            Assert.Equal(ShaderBindingProvenance.ExplicitSource, reflection.Provenance);
        }

        [Fact]
        public void LogicalBinding_ShouldRejectConstantBufferLayoutOnNonConstantBuffer()
        {
            Assert.Throws<ArgumentException>(() => new ShaderLogicalBinding(
                new ShaderBindingKey(0, 0, ShaderBindingClass.ShaderResource),
                "Texture",
                null,
                new ShaderResourceShape(
                    ShaderResourceKind.Texture,
                    ShaderResourceDimension.Texture2D,
                    ShaderResourceAccess.ReadOnly),
                ShaderStageMask.Pixel,
                CreateConstantBufferLayout()));
        }

        [Fact]
        public void ValueLayout_ShouldPreserveProvenStridesAndCanonicalMemberOrder()
        {
            ShaderValueLayout matrix = new(
                ShaderValueKind.Matrix,
                ShaderScalarType.Float32,
                4,
                4,
                byteSize: 64,
                provenMatrixStride: 16,
                matrixMajorOrder: ShaderMatrixMajorOrder.RowMajor);
            ShaderValueLayout scalar = CreateFloatScalarLayout();
            ShaderValueLayout structure = new(
                ShaderValueKind.Struct,
                null,
                0,
                0,
                byteSize: 80,
                members: new[]
                {
                    new ShaderValueMember("Weight", 64, 4, scalar),
                    new ShaderValueMember("Transform", 0, 64, matrix),
                });

            Assert.Equal("Transform", structure.Members[0].Name);
            Assert.Equal(16u, matrix.ProvenMatrixStride);
            Assert.Equal(ShaderMatrixMajorOrder.RowMajor, matrix.MatrixMajorOrder);

            ShaderValueLayout structurallyEqual = new(
                ShaderValueKind.Struct,
                null,
                0,
                0,
                byteSize: 80,
                members: new[]
                {
                    new ShaderValueMember("Transform", 0, 64, matrix),
                    new ShaderValueMember("Weight", 64, 4, scalar),
                });
            Assert.Equal(structure, structurallyEqual);
            Assert.Equal(structure.GetHashCode(), structurallyEqual.GetHashCode());
        }

        [Fact]
        public void ConstantBufferLayout_ShouldRejectRuntimeSizedAndOverlappingValues()
        {
            ShaderValueLayout runtimeArray = new(
                ShaderValueKind.Scalar,
                ShaderScalarType.Float32,
                1,
                1,
                new ShaderArrayShape(new[] { ShaderArrayExtent.Runtime() }));

            Assert.Throws<ArgumentException>(() => new ShaderConstantBufferLayout(
                "RuntimeConstants",
                16,
                new[] { new ShaderValueMember("Values", 0, 4, runtimeArray) }));
            Assert.Throws<ArgumentException>(() => new ShaderConstantBufferLayout(
                "OverlappingConstants",
                16,
                new[]
                {
                    new ShaderValueMember("Left", 0, 8, CreateFloatScalarLayout()),
                    new ShaderValueMember("Right", 4, 4, CreateFloatScalarLayout()),
                }));
        }

        [Fact]
        public void StageMask_ShouldUnionFineGrainedStages()
        {
            ShaderStageMask stages = ShaderStageMaskUtility.Union(new[]
            {
                ShaderExecutionStage.Vertex,
                ShaderExecutionStage.Pixel,
                ShaderExecutionStage.RayGeneration,
            });

            Assert.Equal(
                ShaderStageMask.Vertex | ShaderStageMask.Pixel | ShaderStageMask.RayGeneration,
                stages);
            ShaderStageMaskUtility.Validate(stages);
            Assert.Throws<ArgumentOutOfRangeException>(() => ShaderStageMaskUtility.Validate(ShaderStageMask.None));
            Assert.Throws<ArgumentOutOfRangeException>(() => ShaderStageMaskUtility.FromStage((ShaderExecutionStage)int.MaxValue));
        }

        [Fact]
        public void EntryPoint_ShouldModelFixedAndSpecializedThreadGroups()
        {
            ShaderThreadGroupSize fixedSize = ShaderThreadGroupSize.Fixed(8, 4, 1);
            ShaderThreadGroupSize specializedSize = new(
                ShaderThreadGroupDimension.SpecializationConstant(9, 8),
                ShaderThreadGroupDimension.Fixed(4),
                ShaderThreadGroupDimension.Fixed(1));

            ShaderEntryPointReflection fixedEntry = new(
                "MainCS",
                ShaderExecutionStage.Compute,
                threadGroupSize: fixedSize);
            ShaderEntryPointReflection specializedEntry = new(
                "SpecializedCS",
                ShaderExecutionStage.Compute,
                threadGroupSize: specializedSize);

            Assert.Equal(8u, fixedEntry.ThreadGroupSize!.Value.X.FixedCount);
            Assert.Equal(9u, specializedEntry.ThreadGroupSize!.Value.X.SpecializationConstantId);
            Assert.Equal(8u, specializedEntry.ThreadGroupSize!.Value.X.DefaultValue);
            Assert.Throws<ArgumentOutOfRangeException>(() => ShaderThreadGroupDimension.Fixed(0));
            Assert.Throws<ArgumentException>(() => new ShaderEntryPointReflection(
                "MainPS",
                ShaderExecutionStage.Pixel,
                threadGroupSize: fixedSize));
        }

        [Fact]
        public void ArtifactReflection_ShouldCanonicalizeOrderingAndBeStructurallyEqual()
        {
            ShaderResourceBindingReflection texture = CreateTextureBinding(slot: 1, name: "Normal");
            ShaderResourceBindingReflection sampler = CreateSamplerBinding();
            ShaderEntryPointReflection pixel = new(
                "MainPS",
                ShaderExecutionStage.Pixel,
                new[] { texture, sampler });
            ShaderEntryPointReflection vertex = new("MainVS", ShaderExecutionStage.Vertex);

            ShaderArtifactReflection left = new(ShaderArtifactKind.Dxil, new[] { pixel, vertex });
            ShaderArtifactReflection right = new(
                ShaderArtifactKind.Dxil,
                new[]
                {
                    new ShaderEntryPointReflection("MainVS", ShaderExecutionStage.Vertex),
                    new ShaderEntryPointReflection(
                        "MainPS",
                        ShaderExecutionStage.Pixel,
                        new[] { CreateSamplerBinding(), CreateTextureBinding(slot: 1, name: "Normal") }),
                });

            Assert.Equal(ShaderExecutionStage.Vertex, left.EntryPoints[0].Stage);
            Assert.Equal(ShaderBindingClass.Sampler, pixel.Resources[0].Key.Type);
            Assert.Equal(left, right);
            Assert.Equal(left.GetHashCode(), right.GetHashCode());
        }

        [Fact]
        public void ArtifactReflection_ShouldRejectBackendMismatch()
        {
            ShaderResourceBindingReflection vulkanResource = CreateTextureBinding(
                backend: ShaderBackendKind.Vulkan,
                bindingNamespace: ShaderPhysicalBindingNamespace.Unified);
            ShaderEntryPointReflection entry = new(
                "MainPS",
                ShaderExecutionStage.Pixel,
                new[] { vulkanResource });

            Assert.Throws<ArgumentException>(() => new ShaderArtifactReflection(
                ShaderArtifactKind.Dxil,
                new[] { entry }));
        }

        [Theory]
        [InlineData(0u)]
        [InlineData(1u)]
        [InlineData(2u)]
        [InlineData(ShaderArtifactReflection.CurrentSchemaVersion + 1)]
        public void ArtifactReflection_ShouldRejectNonCurrentSchema(uint schemaVersion)
        {
            ShaderEntryPointReflection entry = new(
                "MainPS",
                ShaderExecutionStage.Pixel);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ShaderArtifactReflection(
                    ShaderArtifactKind.Dxil,
                    new[] { entry },
                    schemaVersion));
        }
        [Fact]
        public void EntryPoint_ShouldRejectDuplicateLogicalBindings()
        {
            ShaderResourceBindingReflection resource = CreateTextureBinding();

            Assert.Throws<ArgumentException>(() => new ShaderEntryPointReflection(
                "MainPS",
                ShaderExecutionStage.Pixel,
                new[] { resource, CreateTextureBinding() }));
        }

        private static ShaderValueLayout CreateFloatScalarLayout()
        {
            return new ShaderValueLayout(
                ShaderValueKind.Scalar,
                ShaderScalarType.Float32,
                1,
                1,
                byteSize: 4);
        }

        private static ShaderConstantBufferLayout CreateConstantBufferLayout()
        {
            return new ShaderConstantBufferLayout(
                "Constants",
                16,
                new[] { new ShaderValueMember("Exposure", 0, 4, CreateFloatScalarLayout()) });
        }

        private static ShaderResourceBindingReflection CreateTextureBinding(
            uint slot = 0,
            string name = "Albedo",
            ShaderBackendKind backend = ShaderBackendKind.DirectX12,
            ShaderPhysicalBindingNamespace bindingNamespace = ShaderPhysicalBindingNamespace.ShaderResource)
        {
            return new ShaderResourceBindingReflection(
                name,
                new ShaderBindingKey(0, slot, ShaderBindingClass.ShaderResource),
                new ShaderResourceShape(
                    ShaderResourceKind.Texture,
                    ShaderResourceDimension.Texture2D,
                    ShaderResourceAccess.ReadOnly),
                ShaderStageMask.Pixel,
                new ShaderPhysicalBindingLocation(
                    backend,
                    0,
                    slot,
                    bindingNamespace));
        }

        private static ShaderResourceBindingReflection CreateSamplerBinding()
        {
            return new ShaderResourceBindingReflection(
                "LinearSampler",
                new ShaderBindingKey(0, 0, ShaderBindingClass.Sampler),
                new ShaderResourceShape(
                    ShaderResourceKind.Sampler,
                    ShaderResourceDimension.Unknown,
                    ShaderResourceAccess.ReadOnly,
                    samplerKind: ShaderSamplerKind.Regular),
                ShaderStageMask.Pixel,
                new ShaderPhysicalBindingLocation(
                    ShaderBackendKind.DirectX12,
                    0,
                    0,
                    ShaderPhysicalBindingNamespace.Sampler));
        }
    }
}