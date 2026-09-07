using System;
using System.Linq;
using SharpShader.Compilation;
using SharpShader.Compilation.Internal;
using SharpShader.HLSLCrossCompiler;
using Silk.NET.SPIRV;
using Xunit;

namespace SharpShader.Internal.Tests
{
    public sealed class SpirvArtifactReflectionTests
    {
        [Fact]
        public void ComputeReflection_PreservesRemappedBindingsNormalizedKindsAndLocalSize()
        {
            const string source = """
                cbuffer Constants : register(b0, space0)
                {
                    uint TextureIndex;
                    float3 Tint;
                };

                Texture2D<float4> Textures[3] : register(t0, space0);
                SamplerState LinearSampler : register(s0, space0);
                RWStructuredBuffer<float4> Output : register(u0, space0);

                [numthreads(8, 4, 2)]
                void CSMain(uint3 id : SV_DispatchThreadID)
                {
                    Output[id.x] = Textures[TextureIndex].SampleLevel(
                        LinearSampler,
                        0.5.xx,
                        0) * float4(Tint, 1);
                }
                """;

            ShaderEntryPointReflection entry = Assert.Single(
                CompileAndReflect(CreateComputeRequest(
                    source,
                    "spirv-normalized-bindings.hlsl")).EntryPoints);

            Assert.Equal(ShaderExecutionStage.Compute, entry.Stage);
            Assert.Empty(entry.StageInputs);
            Assert.Empty(entry.StageOutputs);
            Assert.True(entry.ThreadGroupSize.HasValue);
            Assert.Equal(8u, entry.ThreadGroupSize.Value.X.FixedCount);
            Assert.Equal(4u, entry.ThreadGroupSize.Value.Y.FixedCount);
            Assert.Equal(2u, entry.ThreadGroupSize.Value.Z.FixedCount);
            Assert.Equal(4, entry.Resources.Count);
            Assert.All(entry.Resources, resource =>
            {
                Assert.Equal(0u, resource.Key.Table);
                Assert.Equal(ShaderBindingProvenance.Unknown, resource.Provenance);
                Assert.Equal(ShaderBackendKind.Vulkan, resource.PhysicalLocation.Backend);
                Assert.Equal(ShaderPhysicalBindingNamespace.Unified, resource.PhysicalLocation.Namespace);
                Assert.Equal(resource.Key.Table, resource.PhysicalLocation.Group);
                Assert.Equal(resource.Key.Slot, resource.PhysicalLocation.Binding);
            });

            ShaderResourceBindingReflection texture =
                entry.Resources.Single(resource =>
                    resource.Key.Type == ShaderBindingClass.ShaderResource
                    && resource.Key.Slot == 0);
            Assert.Equal(ShaderBindingClass.ShaderResource, texture.Key.Type);
            Assert.Equal(0u, texture.Key.Slot);
            Assert.Equal(ShaderResourceKind.Texture, texture.Shape.Kind);
            Assert.Equal(ShaderResourceDimension.Texture2D, texture.Shape.Dimension);
            Assert.Equal(3u, texture.Shape.Array.BoundedElementCount);

            ShaderResourceBindingReflection sampler =
                entry.Resources.Single(resource =>
                    resource.Key.Type == ShaderBindingClass.Sampler);
            Assert.Equal(ShaderBindingClass.Sampler, sampler.Key.Type);
            Assert.Equal(100u, sampler.Key.Slot);
            Assert.Equal(ShaderSamplerKind.Unknown, sampler.Shape.SamplerKind);

            ShaderResourceBindingReflection constantBuffer =
                entry.Resources.Single(resource =>
                    resource.Key.Type == ShaderBindingClass.ConstantBuffer);
            Assert.Equal(ShaderBindingClass.ConstantBuffer, constantBuffer.Key.Type);
            Assert.Equal(200u, constantBuffer.Key.Slot);
            Assert.NotNull(constantBuffer.ConstantBufferLayout);

            ShaderResourceBindingReflection storageBuffer =
                entry.Resources.Single(resource =>
                    resource.Key.Type == ShaderBindingClass.UnorderedAccess
                    && resource.Key.Slot == 300);
            Assert.Equal(ShaderBindingClass.UnorderedAccess, storageBuffer.Key.Type);
            Assert.Equal(300u, storageBuffer.Key.Slot);
            Assert.Equal(ShaderResourceKind.StorageBuffer, storageBuffer.Shape.Kind);
            Assert.Equal(16u, storageBuffer.Shape.StructureStride);
            Assert.NotEqual(ShaderResourceAccess.ReadOnly, storageBuffer.Shape.Access);
        }

        [Fact]
        public void ConstantBufferReflection_PreservesNestedOffsetsSizesAndProvenStrides()
        {
            const string source = """
                struct MaterialData
                {
                    float2 Axis;
                    float Weight;
                    float Padding;
                };

                cbuffer Params : register(b0, space0)
                {
                    row_major float3x4 Transform;
                    MaterialData Items[2];
                    uint Tail;
                };

                RWStructuredBuffer<float4> Output : register(u0, space0);

                [numthreads(1, 1, 1)]
                void CSMain()
                {
                    Output[0] = float4(
                        Transform[0][0],
                        Items[1].Axis.x,
                        Items[0].Weight,
                        (float)Tail);
                }
                """;

            ShaderEntryPointReflection entry = Assert.Single(
                CompileAndReflect(CreateComputeRequest(
                    source,
                    "spirv-cbuffer-layout.hlsl")).EntryPoints);
            ShaderResourceBindingReflection binding =
                entry.Resources.Single(resource =>
                    resource.Key.Type == ShaderBindingClass.ConstantBuffer);
            ShaderConstantBufferLayout layout = Assert.IsType<ShaderConstantBufferLayout>(
                binding.ConstantBufferLayout);

            Assert.Equal(200u, binding.Key.Slot);
            ShaderValueMember transform =
                layout.Variables.Single(variable => variable.Name == "Transform");
            Assert.Equal(0u, transform.ByteOffset);
            Assert.Equal(ShaderValueKind.Matrix, transform.Value.Kind);
            Assert.Equal(4u, transform.Value.Rows);
            Assert.Equal(3u, transform.Value.Columns);
            Assert.Equal(ShaderMatrixMajorOrder.ColumnMajor, transform.Value.MatrixMajorOrder);
            Assert.Equal(16u, transform.Value.ProvenMatrixStride);

            ShaderValueMember items =
                layout.Variables.Single(variable => variable.Name == "Items");
            Assert.Equal(ShaderValueKind.Struct, items.Value.Kind);
            Assert.Equal(2u, items.Value.Array.BoundedElementCount);
            Assert.Equal(16u, items.Value.ProvenArrayStride);
            Assert.Equal(
                new[] { "Axis", "Weight", "Padding" },
                items.Value.Members.Select(member => member.Name).ToArray());
            Assert.Equal(0u, items.Value.Members[0].ByteOffset);
            Assert.Equal(8u, items.Value.Members[1].ByteOffset);
            Assert.Equal(12u, items.Value.Members[2].ByteOffset);

            ShaderValueMember tail =
                layout.Variables.Single(variable => variable.Name == "Tail");
            Assert.True(tail.ByteOffset > items.ByteOffset);
            Assert.Equal(ShaderScalarType.UInt32, tail.Value.ScalarType);
            Assert.True(layout.ByteSize >= checked(tail.ByteOffset + tail.ByteSize));
        }

        [Fact]
        public void RuntimeDescriptorArray_AndLocalSize_AreReflectedFromArtifact()
        {
            const string source = """
                Texture2D<float4> Textures[] : register(t0, space0);
                SamplerState LinearSampler : register(s0, space0);
                RWStructuredBuffer<float4> Output : register(u0, space0);

                [numthreads(8, 2, 1)]
                void CSMain(uint3 id : SV_DispatchThreadID)
                {
                    Output[id.x] = Textures[id.x].SampleLevel(
                        LinearSampler,
                        0.5.xx,
                        0);
                }
                """;

            ShaderCompileRequest request = CreateComputeRequest(
                source,
                "spirv-runtime-local-size-id.hlsl") with
            {
                SpirvOptions = CreateRemappedOptions(
                    "-fspv-extension=SPV_EXT_descriptor_indexing"),
            };
            ShaderEntryPointReflection entry = Assert.Single(
                CompileAndReflect(request).EntryPoints);
            ShaderResourceBindingReflection textures =
                entry.Resources.Single(resource => resource.Name == "Textures");

            Assert.True(textures.Shape.Array.HasRuntimeExtent);
            Assert.Equal(
                ShaderArrayExtentKind.Runtime,
                Assert.Single(textures.Shape.Array.Extents).Kind);
            Assert.True(entry.ThreadGroupSize.HasValue);
            Assert.Equal(8u, entry.ThreadGroupSize.Value.X.FixedCount);
            Assert.Equal(2u, entry.ThreadGroupSize.Value.Y.FixedCount);
            Assert.Equal(1u, entry.ThreadGroupSize.Value.Z.FixedCount);
        }

        [Fact]
        public void LibraryReflection_UsesPerEntryActiveResourceSubsets()
        {
            const string source = """
                struct Payload { float4 Color; };
                Texture2D<float4> RayTexture : register(t0, space0);
                Texture2D<float4> MissTexture : register(t1, space0);
                RWTexture2D<float4> RayOutput : register(u0, space0);

                [shader("raygeneration")]
                void RayGen()
                {
                    RayOutput[uint2(0, 0)] = RayTexture.Load(int3(0, 0, 0));
                }

                [shader("miss")]
                void MissMain(inout Payload payload)
                {
                    payload.Color = MissTexture.Load(int3(0, 0, 0));
                }
                """;

            ShaderCompileRequest request = new()
            {
                Source = source,
                SourceName = "spirv-active-entry-subsets.hlsl",
                EntryPoint = string.Empty,
                Stage = ShaderStageKind.Library,
                ShaderModel = new ShaderModelVersion(6, 6),
                Target = ShaderTargetKind.SpirV,
                Exports = new[] { "RayGen", "MissMain" },
                SpirvOptions = CreateRemappedOptions(
                    "-fspv-extension=SPV_KHR_ray_tracing"),
            };
            ShaderArtifactReflection reflection = CompileAndReflect(request);

            Assert.Equal(2, reflection.EntryPoints.Count);
            ShaderEntryPointReflection rayGeneration =
                reflection.EntryPoints.Single(entry => entry.Name == "RayGen");
            ShaderEntryPointReflection miss =
                reflection.EntryPoints.Single(entry => entry.Name == "MissMain");
            Assert.Equal(ShaderExecutionStage.RayGeneration, rayGeneration.Stage);
            Assert.Equal(ShaderExecutionStage.Miss, miss.Stage);
            Assert.Equal(
                new[] { 0u, 300u },
                rayGeneration.Resources
                    .Select(resource => resource.Key.Slot)
                    .OrderBy(slot => slot)
                    .ToArray());
            Assert.Equal(
                1u,
                Assert.Single(miss.Resources).Key.Slot);
        }

        [Fact]
        public void MissingDescriptorSetDecoration_FailsWithoutDefaultingToZero()
        {
            const string source = """
                Texture2D<float4> Input : register(t0, space0);
                RWStructuredBuffer<float4> Output : register(u0, space0);

                [numthreads(1, 1, 1)]
                void CSMain()
                {
                    Output[0] = Input.Load(int3(0, 0, 0));
                }
                """;

            ShaderCompileRequest request = CreateComputeRequest(
                source,
                "spirv-missing-set-negative.hlsl");
            ShaderCompileResult compiled = global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(request);
            byte[] withoutDescriptorSets = RemoveDecorations(
                compiled.Bytecode,
                Decoration.DescriptorSet);

            ShaderCompilerException exception = Assert.Throws<ShaderCompilerException>(
                () => SpirvArtifactReflector.Reflect(
                    request,
                    compiled with { Bytecode = withoutDescriptorSets }));
            Assert.Equal(ShaderCompilerErrorCode.CompileFailed, exception.ErrorCode);
            Assert.Contains("no DescriptorSet decoration", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ComputeReflection_RejectsRasterBuiltInInsteadOfSilentlyIgnoringIt()
        {
            const string source = """
                RWStructuredBuffer<uint> Output : register(u0, space0);

                [numthreads(1, 1, 1)]
                void CSMain(uint3 id : SV_DispatchThreadID)
                {
                    Output[0] = id.x;
                }
                """;

            ShaderCompileRequest request = CreateComputeRequest(
                source,
                "spirv-compute-raster-builtin-negative.hlsl");
            ShaderCompileResult compiled = global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(request);
            byte[] withRasterBuiltIn = ReplaceBuiltIn(
                compiled.Bytecode,
                BuiltIn.GlobalInvocationId,
                BuiltIn.Position);

            ShaderCompilerException exception = Assert.Throws<ShaderCompilerException>(
                () => SpirvArtifactReflector.Reflect(
                    request,
                    compiled with { Bytecode = withRasterBuiltIn }));
            Assert.Equal(ShaderCompilerErrorCode.CompileFailed, exception.ErrorCode);
            Assert.Contains(
                "not a recognized compute execution built-in",
                exception.Message,
                StringComparison.Ordinal);
        }

        [Fact]
        public void ComputeReflection_RejectsLocationDecoratedBuiltInStageIo()
        {
            const string source = """
                RWStructuredBuffer<uint> Output : register(u0, space0);

                [numthreads(1, 1, 1)]
                void CSMain(uint3 id : SV_DispatchThreadID)
                {
                    Output[0] = id.x;
                }
                """;

            ShaderCompileRequest request = CreateComputeRequest(
                source,
                "spirv-compute-location-stage-io-negative.hlsl");
            ShaderCompileResult compiled = global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(request);
            byte[] withLocation = AddLocationDecorationToBuiltIn(
                compiled.Bytecode,
                BuiltIn.GlobalInvocationId);

            ShaderCompilerException exception = Assert.Throws<ShaderCompilerException>(
                () => SpirvArtifactReflector.Reflect(
                    request,
                    compiled with { Bytecode = withLocation }));
            Assert.Equal(ShaderCompilerErrorCode.CompileFailed, exception.ErrorCode);
            Assert.Contains(
                "has a Location decoration",
                exception.Message,
                StringComparison.Ordinal);
        }

        private static ShaderCompileRequest CreateComputeRequest(
            string source,
            string sourceName)
        {
            return new ShaderCompileRequest
            {
                Source = source,
                SourceName = sourceName,
                EntryPoint = "CSMain",
                Stage = ShaderStageKind.Compute,
                ShaderModel = new ShaderModelVersion(6, 6),
                Target = ShaderTargetKind.SpirV,
                SpirvOptions = CreateRemappedOptions(),
            };
        }

        private static SpirvCompileOptions CreateRemappedOptions(
            params string[] additionalArguments)
        {
            return new SpirvCompileOptions
            {
                TargetEnvironment = "vulkan1.2",
                BindingShifts = new[]
                {
                    new SpirvBindingShift(SpirvBindingShiftKind.ShaderResource, 0, 0),
                    new SpirvBindingShift(SpirvBindingShiftKind.Sampler, 0, 100),
                    new SpirvBindingShift(SpirvBindingShiftKind.ConstantBuffer, 0, 200),
                    new SpirvBindingShift(SpirvBindingShiftKind.UnorderedAccess, 0, 300),
                },
                AdditionalArguments = additionalArguments,
            };
        }

        private static ShaderArtifactReflection CompileAndReflect(
            ShaderCompileRequest request)
        {
            try
            {
                ShaderCompileResult compiled = global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(request);
                Assert.NotEmpty(compiled.Bytecode);
                return SpirvArtifactReflector.Reflect(
                    request,
                    compiled);
            }
            catch (ShaderCompilerException exception)
            {
                throw new Xunit.Sdk.XunitException(
                    $"{exception.Message}{Environment.NewLine}{exception.Diagnostics}");
            }
        }

        private static byte[] RemoveDecorations(
            byte[] bytecode,
            Decoration decoration)
        {
            Assert.Equal(0, bytecode.Length % sizeof(uint));
            uint[] words = new uint[bytecode.Length / sizeof(uint)];
            Buffer.BlockCopy(bytecode, 0, words, 0, bytecode.Length);
            int removed = 0;
            for (int offset = 5; offset < words.Length;)
            {
                uint instruction = words[offset];
                int wordCount = checked((int)(instruction >> 16));
                uint opcode = instruction & 0xFFFFu;
                Assert.True(wordCount > 0, $"Invalid SPIR-V instruction at word {offset}.");
                Assert.True(offset + wordCount <= words.Length);

                const uint OpDecorate = 71;
                if (opcode == OpDecorate
                    && wordCount >= 3
                    && words[offset + 2] == (uint)decoration)
                {
                    for (int wordIndex = 0; wordIndex < wordCount; ++wordIndex)
                    {
                        words[offset + wordIndex] = 1u << 16;
                    }

                    removed++;
                }

                offset += wordCount;
            }

            Assert.True(removed > 0, $"No {decoration} decorations were found in the compiled SPIR-V artifact.");
            byte[] result = new byte[bytecode.Length];
            Buffer.BlockCopy(words, 0, result, 0, result.Length);
            return result;
        }

        private static byte[] AddLocationDecorationToBuiltIn(
            byte[] bytecode,
            BuiltIn builtIn)
        {
            Assert.Equal(0, bytecode.Length % sizeof(uint));
            uint[] words = new uint[bytecode.Length / sizeof(uint)];
            Buffer.BlockCopy(bytecode, 0, words, 0, bytecode.Length);
            int insertionOffset = -1;
            uint targetId = 0;
            for (int offset = 5; offset < words.Length;)
            {
                uint instruction = words[offset];
                int wordCount = checked((int)(instruction >> 16));
                uint opcode = instruction & 0xFFFFu;
                Assert.True(wordCount > 0, $"Invalid SPIR-V instruction at word {offset}.");
                Assert.True(offset + wordCount <= words.Length);

                const uint OpDecorate = 71;
                if (opcode == OpDecorate
                    && wordCount >= 4
                    && words[offset + 2] == (uint)Decoration.BuiltIn
                    && words[offset + 3] == (uint)builtIn)
                {
                    targetId = words[offset + 1];
                    insertionOffset = offset + wordCount;
                    break;
                }

                offset += wordCount;
            }

            Assert.True(
                insertionOffset >= 0,
                $"No {builtIn} BuiltIn decoration was found in the compiled "
                + "SPIR-V artifact.");
            uint[] expanded = new uint[words.Length + 4];
            Array.Copy(words, 0, expanded, 0, insertionOffset);
            const uint OpDecorateWordCount = 4;
            const uint OpDecorateInstruction = 71;
            expanded[insertionOffset] = (OpDecorateWordCount << 16) | OpDecorateInstruction;
            expanded[insertionOffset + 1] = targetId;
            expanded[insertionOffset + 2] = (uint)Decoration.Location;
            expanded[insertionOffset + 3] = 0;
            Array.Copy(
                words,
                insertionOffset,
                expanded,
                insertionOffset + OpDecorateWordCount,
                words.Length - insertionOffset);

            byte[] result = new byte[expanded.Length * sizeof(uint)];
            Buffer.BlockCopy(expanded, 0, result, 0, result.Length);
            return result;
        }

        private static byte[] ReplaceBuiltIn(
            byte[] bytecode,
            BuiltIn oldBuiltIn,
            BuiltIn newBuiltIn)
        {
            Assert.Equal(0, bytecode.Length % sizeof(uint));
            uint[] words = new uint[bytecode.Length / sizeof(uint)];
            Buffer.BlockCopy(bytecode, 0, words, 0, bytecode.Length);
            int replaced = 0;
            for (int offset = 5; offset < words.Length;)
            {
                uint instruction = words[offset];
                int wordCount = checked((int)(instruction >> 16));
                uint opcode = instruction & 0xFFFFu;
                Assert.True(wordCount > 0, $"Invalid SPIR-V instruction at word {offset}.");
                Assert.True(offset + wordCount <= words.Length);

                const uint OpDecorate = 71;
                if (opcode == OpDecorate
                    && wordCount >= 4
                    && words[offset + 2] == (uint)Decoration.BuiltIn
                    && words[offset + 3] == (uint)oldBuiltIn)
                {
                    words[offset + 3] = (uint)newBuiltIn;
                    replaced++;
                }

                offset += wordCount;
            }

            Assert.True(
                replaced > 0,
                $"No {oldBuiltIn} BuiltIn decorations were found in the compiled "
                + "SPIR-V artifact.");
            byte[] result = new byte[bytecode.Length];
            Buffer.BlockCopy(words, 0, result, 0, result.Length);
            return result;
        }
    }
}
