using System;
using System.Linq;
using SharpShader.Compilation;
using SharpShader.Compilation.Internal;
using SharpShader.HLSLCrossCompiler;
using SharpShader.HLSLCrossCompiler.Internal;
using Xunit;

namespace Infinity.Rendering.Tests
{
    public sealed class MslPlannedTranslationTests
    {
        private const string DirectSource = """
            cbuffer Constants : register(b0, space0)
            {
                float Scale;
            };

            Texture2D<float4> InputTexture : register(t0, space0);
            SamplerState InputSampler : register(s0, space0);
            RWStructuredBuffer<float4> Output : register(u0, space0);

            [numthreads(1, 1, 1)]
            void CSMain()
            {
                Output[0] = InputTexture.SampleLevel(
                    InputSampler,
                    float2(0.5, 0.5),
                    0) * Scale;
            }
            """;

        private const string ReferenceSource = """
            Texture2D<float4> InputTextures[2] : register(t0, space0);
            SamplerState InputSampler : register(s0, space0);
            RWStructuredBuffer<float4> Output : register(u0, space1);

            [numthreads(1, 1, 1)]
            void CSMain()
            {
                Output[0] =
                    InputTextures[0].SampleLevel(
                        InputSampler,
                        float2(0.25, 0.25),
                        0)
                    + InputTextures[1].SampleLevel(
                        InputSampler,
                        float2(0.75, 0.75),
                        0);
            }
            """;

        private const string TypedBufferSource = """
            Buffer<float4> InputBuffer : register(t0, space0);
            RWBuffer<float4> OutputBuffer : register(u0, space0);

            [numthreads(1, 1, 1)]
            void CSMain()
            {
                OutputBuffer[0] = InputBuffer[0];
            }
            """;

        private const string MultiEntryLibrarySource = """
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void ComputeA()
            {
            }

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void ComputeB()
            {
            }
            """;

        [Fact]
        public void DirectPlan_MapsIndependentMetalNamespaces()
        {
            ShaderCompileRequest request = CreateDirectRequest();
            ShaderCompileResult spirv = CompileSpirV(request);
            VulkanShaderBindingMapping[] vulkan = CreateDirectVulkanBindings();
            MetalShaderBackendLayout metal = CreateDirectMetalLayout();

            ShaderCompileResult msl = Translate(
                request,
                spirv,
                vulkan,
                metal);

            Assert.Contains("[[texture(0)]]", msl.Text, StringComparison.Ordinal);
            Assert.Contains("[[sampler(0)]]", msl.Text, StringComparison.Ordinal);
            Assert.Contains("[[buffer(0)]]", msl.Text, StringComparison.Ordinal);
            Assert.Contains("[[buffer(1)]]", msl.Text, StringComparison.Ordinal);
        }

        [Fact]
        public void ReferencePlan_MapsDenseOuterBuffersAndArrayIds()
        {
            ShaderCompileRequest request = CreateReferenceRequest();
            ShaderCompileResult spirv = CompileSpirV(request);
            VulkanShaderBindingMapping[] vulkan =
                CreateReferenceVulkanBindings();
            MetalShaderBackendLayout metal =
                CreateReferenceMetalLayout(textureCount: 2);

            ShaderCompileResult msl = Translate(
                request,
                spirv,
                vulkan,
                metal);

            Assert.Contains("[[buffer(0)]]", msl.Text, StringComparison.Ordinal);
            Assert.Contains("[[buffer(1)]]", msl.Text, StringComparison.Ordinal);
            Assert.Contains("[[id(0)]]", msl.Text, StringComparison.Ordinal);
            Assert.Contains("[[id(2)]]", msl.Text, StringComparison.Ordinal);
        }

        [Fact]
        public void DirectPlan_MapsTypedBuffersToMetalTextureNamespace()
        {
            ShaderCompileRequest request = new ShaderCompileRequest
            {
                Source = TypedBufferSource,
                SourceName = "msl-plan-typed-buffer.hlsl",
                EntryPoint = "CSMain",
                Stage = ShaderStageKind.Compute,
                ShaderModel = new ShaderModelVersion(6, 6),
                Target = ShaderTargetKind.SpirV,
                SpirvOptions = new SpirvCompileOptions
                {
                    TargetEnvironment = "vulkan1.2",
                    BindingShifts = new[]
                    {
                        new SpirvBindingShift(
                            SpirvBindingShiftKind.ShaderResource,
                            0,
                            0),
                        new SpirvBindingShift(
                            SpirvBindingShiftKind.UnorderedAccess,
                            0,
                            1),
                    },
                },
            };
            ShaderCompileResult spirv = CompileSpirV(request);
            ShaderBindingKey inputKey = new(
                0,
                0,
                ShaderBindingClass.ShaderResource);
            ShaderBindingKey outputKey = new(
                0,
                0,
                ShaderBindingClass.UnorderedAccess);
            VulkanShaderBindingMapping[] vulkan =
            {
                new(
                    inputKey,
                    0,
                    0,
                    VulkanDescriptorKind.UniformTexelBuffer),
                new(
                    outputKey,
                    0,
                    1,
                    VulkanDescriptorKind.StorageTexelBuffer),
            };
            MetalShaderBackendLayout metal = new(
                new[]
                {
                    new MetalDirectBindingMapping(
                        inputKey,
                        MetalShaderBackendLayout.RootBindingTable,
                        ShaderPhysicalBindingNamespace.Texture,
                        0),
                    new MetalDirectBindingMapping(
                        outputKey,
                        MetalShaderBackendLayout.RootBindingTable,
                        ShaderPhysicalBindingNamespace.Texture,
                        1),
                });

            ShaderCompileResult msl = Translate(
                request,
                spirv,
                vulkan,
                metal);

            Assert.Contains("[[texture(0)]]", msl.Text, StringComparison.Ordinal);
            Assert.Contains("[[texture(1)]]", msl.Text, StringComparison.Ordinal);
        }

        [Fact]
        public void MultiEntryLibrary_SelectsRequestedEntryAndIsDeterministic()
        {
            ShaderCompileRequest request = new ShaderCompileRequest
            {
                Source = MultiEntryLibrarySource,
                SourceName = "msl-plan-multi-entry.hlsl",
                EntryPoint = string.Empty,
                Stage = ShaderStageKind.Library,
                ShaderModel = new ShaderModelVersion(6, 6),
                Target = ShaderTargetKind.SpirV,
                Exports = new[] { "ComputeA", "ComputeB" },
                SpirvOptions = new SpirvCompileOptions
                {
                    TargetEnvironment = "vulkan1.2",
                },
            };
            ShaderCompileResult multiEntry = CompileSpirV(request);
            VulkanShaderBindingMapping[] vulkan =
                Array.Empty<VulkanShaderBindingMapping>();
            MetalShaderBackendLayout metal = new();

            ShaderCompileResult first = TranslatePlanned(
                request with { Target = ShaderTargetKind.Msl },
                multiEntry,
                "ComputeB",
                ShaderExecutionStage.Compute,
                vulkan,
                metal);
            ShaderCompileResult second = TranslatePlanned(
                request with { Target = ShaderTargetKind.Msl },
                multiEntry,
                "ComputeB",
                ShaderExecutionStage.Compute,
                vulkan,
                metal);

            Assert.Equal(first.Bytecode, second.Bytecode);
            Assert.Equal(first.Text, second.Text);
            Assert.Contains(
                "kernel void ComputeB",
                first.Text,
                StringComparison.Ordinal);
            Assert.DoesNotContain("ComputeA", first.Text, StringComparison.Ordinal);
        }

        [Fact]
        public void PlannedTranslation_RejectsMissingExtraAndShapeMismatchedBindings()
        {
            ShaderCompileRequest directRequest = CreateDirectRequest();
            ShaderCompileResult directSpirv = CompileSpirV(directRequest);
            VulkanShaderBindingMapping[] directVulkan =
                CreateDirectVulkanBindings();
            MetalShaderBackendLayout directMetal =
                CreateDirectMetalLayout();

            ShaderCompilerException missing = Assert.Throws<ShaderCompilerException>(
                () => TranslatePlanned(
                    directRequest with { Target = ShaderTargetKind.Msl },
                    directSpirv,
                    "CSMain",
                    ShaderExecutionStage.Compute,
                    directVulkan.Take(3).ToArray(),
                    new MetalShaderBackendLayout(
                        directMetal.DirectBindings.Take(3))));
            Assert.Contains(
                "no planned Metal mapping",
                missing.Message,
                StringComparison.Ordinal);

            ShaderBindingKey extraKey = new(
                table: 7,
                slot: 8,
                ShaderBindingClass.ShaderResource);
            ShaderCompilerException extra = Assert.Throws<ShaderCompilerException>(
                () => TranslatePlanned(
                    directRequest with { Target = ShaderTargetKind.Msl },
                    directSpirv,
                    "CSMain",
                    ShaderExecutionStage.Compute,
                    directVulkan.Append(
                        new VulkanShaderBindingMapping(
                            extraKey,
                            descriptorSet: 0,
                            binding: 8,
                            VulkanDescriptorKind.SampledImage)).ToArray(),
                    new MetalShaderBackendLayout(
                        directMetal.DirectBindings.Append(
                            new MetalDirectBindingMapping(
                                extraKey,
                                MetalShaderBackendLayout.RootBindingTable,
                                ShaderPhysicalBindingNamespace.Texture,
                                index: 8)))));
            Assert.Contains(
                "is not active",
                extra.Message,
                StringComparison.Ordinal);

            ShaderCompileRequest referenceRequest =
                CreateReferenceRequest();
            ShaderCompileResult referenceSpirv =
                CompileSpirV(referenceRequest);
            ShaderCompilerException shape = Assert.Throws<ShaderCompilerException>(
                () => Translate(
                    referenceRequest,
                    referenceSpirv,
                    CreateReferenceVulkanBindings(),
                    CreateReferenceMetalLayout(textureCount: 3)));
            Assert.Contains(
                "has 2 array elements",
                shape.Message,
                StringComparison.Ordinal);
        }

        [Fact]
        public void PlannedTranslation_RejectsEntryAndStageMismatch()
        {
            ShaderCompileRequest request = CreateDirectRequest();
            ShaderCompileResult spirv = CompileSpirV(request);
            VulkanShaderBindingMapping[] vulkan = CreateDirectVulkanBindings();
            MetalShaderBackendLayout metal = CreateDirectMetalLayout();

            ShaderCompilerException entry = Assert.Throws<ShaderCompilerException>(
                () => TranslatePlanned(
                    request with { Target = ShaderTargetKind.Msl },
                    spirv,
                    "Missing",
                    ShaderExecutionStage.Compute,
                    vulkan,
                    metal));
            Assert.Contains(
                "identity do not match",
                entry.Message,
                StringComparison.Ordinal);

            ShaderCompilerException stage = Assert.Throws<ShaderCompilerException>(
                () => TranslatePlanned(
                    request with { Target = ShaderTargetKind.Msl },
                    spirv,
                    "CSMain",
                    ShaderExecutionStage.Pixel,
                    vulkan,
                    metal));
            Assert.Contains(
                "identity do not match",
                stage.Message,
                StringComparison.Ordinal);

            ShaderCompilerException unsupported =
                Assert.Throws<ShaderCompilerException>(
                    () => TranslatePlanned(
                        request with { Target = ShaderTargetKind.Msl },
                        spirv,
                        "CSMain",
                        ShaderExecutionStage.Miss,
                        vulkan,
                        metal));
            Assert.Contains(
                "identity do not match",
                unsupported.Message,
                StringComparison.Ordinal);
        }

        private static ShaderCompileRequest CreateDirectRequest()
        {
            return new ShaderCompileRequest
            {
                Source = DirectSource,
                SourceName = "msl-plan-direct.hlsl",
                EntryPoint = "CSMain",
                Stage = ShaderStageKind.Compute,
                ShaderModel = new ShaderModelVersion(6, 6),
                Target = ShaderTargetKind.SpirV,
                SpirvOptions = new SpirvCompileOptions
                {
                    TargetEnvironment = "vulkan1.2",
                    BindingShifts = new[]
                    {
                        new SpirvBindingShift(SpirvBindingShiftKind.ShaderResource, 0, 0),
                        new SpirvBindingShift(SpirvBindingShiftKind.Sampler, 0, 1),
                        new SpirvBindingShift(SpirvBindingShiftKind.ConstantBuffer, 0, 2),
                        new SpirvBindingShift(SpirvBindingShiftKind.UnorderedAccess, 0, 3),
                    },
                },
            };
        }

        private static ShaderCompileRequest CreateReferenceRequest()
        {
            return new ShaderCompileRequest
            {
                Source = ReferenceSource,
                SourceName = "msl-plan-reference.hlsl",
                EntryPoint = "CSMain",
                Stage = ShaderStageKind.Compute,
                ShaderModel = new ShaderModelVersion(6, 6),
                Target = ShaderTargetKind.SpirV,
                SpirvOptions = new SpirvCompileOptions
                {
                    TargetEnvironment = "vulkan1.2",
                    BindingShifts = new[]
                    {
                        new SpirvBindingShift(SpirvBindingShiftKind.ShaderResource, 0, 0),
                        new SpirvBindingShift(SpirvBindingShiftKind.Sampler, 0, 1),
                        new SpirvBindingShift(SpirvBindingShiftKind.UnorderedAccess, 1, 0),
                    },
                },
            };
        }

        private static VulkanShaderBindingMapping[]
            CreateDirectVulkanBindings()
        {
            return new[]
            {
                new VulkanShaderBindingMapping(
                    new ShaderBindingKey(
                        7,
                        0,
                        ShaderBindingClass.ShaderResource),
                    0,
                    0,
                    VulkanDescriptorKind.SampledImage),
                new VulkanShaderBindingMapping(
                    new ShaderBindingKey(
                        7,
                        0,
                        ShaderBindingClass.Sampler),
                    0,
                    1,
                    VulkanDescriptorKind.Sampler),
                new VulkanShaderBindingMapping(
                    new ShaderBindingKey(
                        7,
                        0,
                        ShaderBindingClass.ConstantBuffer),
                    0,
                    2,
                    VulkanDescriptorKind.UniformBuffer),
                new VulkanShaderBindingMapping(
                    new ShaderBindingKey(
                        7,
                        0,
                        ShaderBindingClass.UnorderedAccess),
                    0,
                    3,
                    VulkanDescriptorKind.StorageBuffer),
            };
        }

        private static MetalShaderBackendLayout CreateDirectMetalLayout()
        {
            return new MetalShaderBackendLayout(
                new[]
                {
                    new MetalDirectBindingMapping(
                        new ShaderBindingKey(
                            7,
                            0,
                            ShaderBindingClass.ShaderResource),
                        MetalShaderBackendLayout.RootBindingTable,
                        ShaderPhysicalBindingNamespace.Texture,
                        0),
                    new MetalDirectBindingMapping(
                        new ShaderBindingKey(
                            7,
                            0,
                            ShaderBindingClass.Sampler),
                        MetalShaderBackendLayout.RootBindingTable,
                        ShaderPhysicalBindingNamespace.Sampler,
                        0),
                    new MetalDirectBindingMapping(
                        new ShaderBindingKey(
                            7,
                            0,
                            ShaderBindingClass.ConstantBuffer),
                        MetalShaderBackendLayout.RootBindingTable,
                        ShaderPhysicalBindingNamespace.Buffer,
                        0),
                    new MetalDirectBindingMapping(
                        new ShaderBindingKey(
                            7,
                            0,
                            ShaderBindingClass.UnorderedAccess),
                        MetalShaderBackendLayout.RootBindingTable,
                        ShaderPhysicalBindingNamespace.Buffer,
                        1),
                });
        }

        private static VulkanShaderBindingMapping[]
            CreateReferenceVulkanBindings()
        {
            return new[]
            {
                new VulkanShaderBindingMapping(
                    new ShaderBindingKey(
                        4,
                        0,
                        ShaderBindingClass.ShaderResource),
                    0,
                    0,
                    VulkanDescriptorKind.SampledImage),
                new VulkanShaderBindingMapping(
                    new ShaderBindingKey(
                        4,
                        0,
                        ShaderBindingClass.Sampler),
                    0,
                    1,
                    VulkanDescriptorKind.Sampler),
                new VulkanShaderBindingMapping(
                    new ShaderBindingKey(
                        9,
                        0,
                        ShaderBindingClass.UnorderedAccess),
                    1,
                    0,
                    VulkanDescriptorKind.StorageBuffer),
            };
        }

        private static MetalShaderBackendLayout
            CreateReferenceMetalLayout(uint textureCount)
        {
            return new MetalShaderBackendLayout(
                referenceBufferBindings: new[]
                {
                    new MetalReferenceBufferBindingMapping(
                        new ShaderBindingKey(
                            4,
                            0,
                            ShaderBindingClass.ShaderResource),
                        MetalShaderBackendLayout.RootBindingTable,
                        ShaderPhysicalBindingNamespace.Texture,
                        referenceBufferIndex: 0,
                        byteOffset: 0,
                        referenceCount: textureCount),
                    new MetalReferenceBufferBindingMapping(
                        new ShaderBindingKey(
                            4,
                            0,
                            ShaderBindingClass.Sampler),
                        MetalShaderBackendLayout.RootBindingTable,
                        ShaderPhysicalBindingNamespace.Sampler,
                        referenceBufferIndex: 0,
                        byteOffset: checked(
                            (ulong)textureCount * MetalReferenceBufferBindingMapping.ReferenceByteSize),
                        referenceCount: 1),
                    new MetalReferenceBufferBindingMapping(
                        new ShaderBindingKey(
                            9,
                            0,
                            ShaderBindingClass.UnorderedAccess),
                        MetalShaderBackendLayout.RootBindingTable,
                        ShaderPhysicalBindingNamespace.Buffer,
                        referenceBufferIndex: 1,
                        byteOffset: 0,
                        referenceCount: 1),
                });
        }

        private static ShaderCompileResult TranslatePlanned(
            ShaderCompileRequest request,
            ShaderCompileResult spirv,
            string entryPoint,
            ShaderExecutionStage stage,
            VulkanShaderBindingMapping[] vulkanBindings,
            MetalShaderBackendLayout metalLayout)
        {
            ShaderArtifactReflection reflection = SpirvArtifactReflector.Reflect(
                request with { Target = ShaderTargetKind.SpirV },
                spirv);
            ShaderEntryPointReflection reflectedEntry = reflection.EntryPoints
                .FirstOrDefault(entry =>
                    string.Equals(
                        entry.Name,
                        entryPoint,
                        StringComparison.Ordinal)
                    && entry.Stage == stage)
                ?? reflection.EntryPoints[0];
            return SpirvToMslTranslator.Translate(
                request,
                spirv,
                entryPoint,
                stage,
                vulkanBindings,
                metalLayout,
                new ShaderAttachmentInterface(
                    "default",
                    entryPoint,
                    stage,
                    stage == ShaderExecutionStage.Pixel
                        ? new ShaderAttachmentPhase(0)
                        : null),
                reflectedEntry,
                privateAttachmentDescriptorSet: 31);
        }
        private static ShaderCompileResult Translate(
            ShaderCompileRequest request,
            ShaderCompileResult spirv,
            VulkanShaderBindingMapping[] vulkan,
            MetalShaderBackendLayout metal)
        {
            return TranslatePlanned(
                request with { Target = ShaderTargetKind.Msl },
                spirv,
                "CSMain",
                ShaderExecutionStage.Compute,
                vulkan,
                metal);
        }

        private static ShaderCompileResult CompileSpirV(
            ShaderCompileRequest request)
        {
            try
            {
                ShaderCompileResult compiled =
                    HLSLCrossCompiler.Compile(request);
                Assert.NotEmpty(compiled.Bytecode);
                return compiled;
            }
            catch (ShaderCompilerException exception)
            {
                throw new Xunit.Sdk.XunitException(
                    $"{exception.Message}{Environment.NewLine}{exception.Diagnostics}");
            }
        }
    }
}
