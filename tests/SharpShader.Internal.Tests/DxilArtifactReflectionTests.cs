using System;
using System.Linq;
using SharpShader.Compilation;
using SharpShader.Compilation.Internal;
using SharpShader.HLSLCrossCompiler;
using Xunit;

namespace SharpShader.Internal.Tests
{
    public sealed class DxilArtifactReflectionTests
    {
        [Fact]
        public void ComputeReflection_PreservesDx12NamespacesArraysProvenanceAndThreadGroup()
        {
            const string source = """
                cbuffer Constants : register(b0, space7)
                {
                    uint TextureIndex;
                    float3 Tint;
                };

                Texture2D<float4> Textures[3] : register(t0, space7);
                SamplerState LinearSampler : register(s0, space7);
                RWTexture2D<float4> Output : register(u0, space7);

                [numthreads(8, 4, 2)]
                void CSMain(uint3 id : SV_DispatchThreadID)
                {
                    Output[id.xy] = Textures[TextureIndex].SampleLevel(LinearSampler, 0.5.xx, 0)
                        * float4(Tint, 1);
                }
                """;

            ShaderArtifactReflection reflection = CompileAndReflect(
                CreateComputeRequest(source, "namespace-array-reflection.hlsl"));
            ShaderEntryPointReflection entry = Assert.Single(reflection.EntryPoints);

            Assert.Equal(ShaderExecutionStage.Compute, entry.Stage);
            Assert.True(entry.ThreadGroupSize.HasValue);
            Assert.Equal(8u, entry.ThreadGroupSize.Value.X.FixedCount);
            Assert.Equal(4u, entry.ThreadGroupSize.Value.Y.FixedCount);
            Assert.Equal(2u, entry.ThreadGroupSize.Value.Z.FixedCount);
            Assert.Equal(4, entry.Resources.Count);
            Assert.All(entry.Resources, resource =>
            {
                Assert.Equal(7u, resource.Key.Table);
                Assert.Equal(0u, resource.Key.Slot);
                Assert.Equal(ShaderBackendKind.DirectX12, resource.PhysicalLocation.Backend);
                Assert.Equal(resource.Key.Table, resource.PhysicalLocation.Group);
                Assert.Equal(resource.Key.Slot, resource.PhysicalLocation.Binding);
            });

            ShaderResourceBindingReflection texture = Find(entry, ShaderBindingClass.ShaderResource);
            Assert.Equal("Textures", texture.Name);
            Assert.Equal(ShaderResourceKind.Texture, texture.Shape.Kind);
            Assert.Equal(ShaderResourceDimension.Texture2D, texture.Shape.Dimension);
            Assert.Equal(3u, texture.Shape.Array.BoundedElementCount);
            Assert.Equal(ShaderPhysicalBindingNamespace.ShaderResource, texture.PhysicalLocation.Namespace);
            Assert.Equal(ShaderBindingProvenance.Unknown, texture.Provenance);

            ShaderResourceBindingReflection sampler = Find(entry, ShaderBindingClass.Sampler);
            Assert.Equal(ShaderSamplerKind.Regular, sampler.Shape.SamplerKind);
            Assert.Equal(ShaderPhysicalBindingNamespace.Sampler, sampler.PhysicalLocation.Namespace);
            Assert.Equal(ShaderBindingProvenance.Unknown, sampler.Provenance);

            ShaderResourceBindingReflection constantBuffer = Find(entry, ShaderBindingClass.ConstantBuffer);
            Assert.NotNull(constantBuffer.ConstantBufferLayout);
            Assert.Equal(ShaderBindingProvenance.ExplicitSource, constantBuffer.Provenance);
            Assert.Equal("Constants", constantBuffer.ConstantBufferLayout!.Name);
            Assert.Equal(ShaderPhysicalBindingNamespace.ConstantBuffer, constantBuffer.PhysicalLocation.Namespace);

            ShaderResourceBindingReflection unorderedAccess = Find(entry, ShaderBindingClass.UnorderedAccess);
            Assert.Equal(ShaderResourceAccess.ReadWrite, unorderedAccess.Shape.Access);
            Assert.Equal(ShaderPhysicalBindingNamespace.UnorderedAccess, unorderedAccess.PhysicalLocation.Namespace);
            Assert.Equal(ShaderBindingProvenance.Unknown, unorderedAccess.Provenance);
        }

        [Fact]
        public void RuntimeDescriptorArrays_UseEmpiricallyProvenZeroBindCountSentinel()
        {
            const string source = """
                Texture2D<float4> Textures[] : register(t3, space2);
                SamplerState Samplers[] : register(s5, space2);
                RWStructuredBuffer<float4> Output : register(u1, space0);

                [numthreads(1, 1, 1)]
                void CSMain(uint3 id : SV_DispatchThreadID)
                {
                    Output[0] = Textures[id.x].SampleLevel(Samplers[id.x], 0.5.xx, 0);
                }
                """;

            ShaderEntryPointReflection entry = Assert.Single(
                CompileAndReflect(CreateComputeRequest(source, "runtime-array-reflection.hlsl")).EntryPoints);
            ShaderResourceBindingReflection textures = entry.Resources.Single(resource => resource.Name == "Textures");
            ShaderResourceBindingReflection samplers = entry.Resources.Single(resource => resource.Name == "Samplers");

            Assert.True(textures.Shape.Array.HasRuntimeExtent);
            Assert.True(samplers.Shape.Array.HasRuntimeExtent);
            Assert.Equal(ShaderArrayExtentKind.Runtime, Assert.Single(textures.Shape.Array.Extents).Kind);
            Assert.Equal(ShaderArrayExtentKind.Runtime, Assert.Single(samplers.Shape.Array.Extents).Kind);
            Assert.Equal(3u, textures.Key.Slot);
            Assert.Equal(5u, samplers.Key.Slot);
            Assert.False(entry.Resources.Single(resource => resource.Name == "Output").Shape.Array.IsArray);
        }

        [Fact]
        public void ConstantBufferReflection_PreservesTopLevelRangesAndRecursiveTypeShape()
        {
            const string source = """
                struct MaterialData
                {
                    float2 Axis;
                    float Weight;
                    float Padding;
                };

                cbuffer Params : register(b2, space3)
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
                CompileAndReflect(CreateComputeRequest(source, "cbuffer-layout-reflection.hlsl")).EntryPoints);
            ShaderResourceBindingReflection binding =
                entry.Resources.Single(resource => resource.Name == "Params");
            ShaderConstantBufferLayout layout = Assert.IsType<ShaderConstantBufferLayout>(
                binding.ConstantBufferLayout);

            Assert.Equal(3u, binding.Key.Table);
            Assert.Equal(2u, binding.Key.Slot);
            Assert.Equal(ShaderBindingProvenance.ExplicitSource, binding.Provenance);
            Assert.True(layout.ByteSize >= 84);

            ShaderValueMember transform = layout.Variables.Single(variable => variable.Name == "Transform");
            Assert.Equal(0u, transform.ByteOffset);
            Assert.Equal(ShaderValueKind.Matrix, transform.Value.Kind);
            Assert.Equal(3u, transform.Value.Rows);
            Assert.Equal(4u, transform.Value.Columns);
            Assert.Equal(ShaderMatrixMajorOrder.RowMajor, transform.Value.MatrixMajorOrder);
            Assert.Null(transform.Value.ProvenMatrixStride);

            ShaderValueMember items = layout.Variables.Single(variable => variable.Name == "Items");
            Assert.Equal(ShaderValueKind.Struct, items.Value.Kind);
            Assert.Equal(2u, items.Value.Array.BoundedElementCount);
            Assert.Null(items.Value.ProvenArrayStride);
            Assert.Equal(
                new[] { "Axis", "Weight", "Padding" },
                items.Value.Members.Select(member => member.Name).ToArray());
            Assert.Equal(0u, items.Value.Members[0].ByteOffset);
            Assert.Equal(8u, items.Value.Members[1].ByteOffset);
            Assert.Equal(12u, items.Value.Members[2].ByteOffset);

            ShaderValueMember tail = layout.Variables.Single(variable => variable.Name == "Tail");
            Assert.True(tail.ByteOffset > items.ByteOffset);
            Assert.Equal(ShaderValueKind.Scalar, tail.Value.Kind);
            Assert.Equal(ShaderScalarType.UInt32, tail.Value.ScalarType);
        }

        [Fact]
        public void LibraryReflection_DecodesEntryNamesStagesAndPerEntryResources()
        {
            const string source = """
                struct Payload { float4 Color; };
                RaytracingAccelerationStructure Scene : register(t0, space0);
                Texture2D<float4> Albedo : register(t0, space1);
                RWTexture2D<float4> Output : register(u0, space0);
                cbuffer Params : register(b0, space0) { float4 Tint; };

                [shader("raygeneration")]
                void RayGen()
                {
                    Payload payload = (Payload)0;
                    RayDesc ray = (RayDesc)0;
                    ray.TMax = 1;
                    TraceRay(Scene, 0, 0xFF, 0, 1, 0, ray, payload);
                    Output[uint2(0, 0)] = Tint;
                }

                [shader("miss")]
                void Miss(inout Payload payload)
                {
                    payload.Color = Albedo.Load(int3(0, 0, 0));
                }
                """;

            ShaderCompileRequest request = new()
            {
                Source = source,
                SourceName = "library-reflection.hlsl",
                EntryPoint = string.Empty,
                Stage = ShaderStageKind.Library,
                ShaderModel = new ShaderModelVersion(6, 6),
                Target = ShaderTargetKind.Dxil,
            };
            ShaderArtifactReflection reflection = CompileAndReflect(request);

            Assert.Equal(2, reflection.EntryPoints.Count);
            ShaderEntryPointReflection rayGeneration =
                reflection.EntryPoints.Single(entry => entry.Name == "RayGen");
            ShaderEntryPointReflection miss =
                reflection.EntryPoints.Single(entry => entry.Name == "Miss");
            Assert.Equal(ShaderExecutionStage.RayGeneration, rayGeneration.Stage);
            Assert.Equal(ShaderExecutionStage.Miss, miss.Stage);
            Assert.Equal(
                new[] { "Params", "Scene", "Output" }.OrderBy(name => name, StringComparer.Ordinal),
                rayGeneration.Resources.Select(resource => resource.Name).OrderBy(name => name, StringComparer.Ordinal));
            Assert.Equal("Albedo", Assert.Single(miss.Resources).Name);
            Assert.NotNull(rayGeneration.Resources.Single(resource => resource.Name == "Params").ConstantBufferLayout);
            Assert.Equal(
                ShaderResourceKind.AccelerationStructure,
                rayGeneration.Resources.Single(resource => resource.Name == "Scene").Shape.Kind);
        }

        [Fact]
        public void BindingsWithoutPositiveUserpackedEvidence_AreReportedAsUnknown()
        {
            const string source = """
                Texture2D<float4> InputTexture;
                SamplerComparisonState ComparisonSampler;
                RWTexture2D<float4> OutputTexture;

                [numthreads(1, 1, 1)]
                void CSMain()
                {
                    float value = InputTexture.SampleCmpLevelZero(ComparisonSampler, 0.5.xx, 0.5);
                    OutputTexture[uint2(0, 0)] = value.xxxx;
                }
                """;

            ShaderEntryPointReflection entry = Assert.Single(
                CompileAndReflect(CreateComputeRequest(source, "implicit-binding-reflection.hlsl")).EntryPoints);
            Assert.All(
                entry.Resources,
                resource => Assert.Equal(ShaderBindingProvenance.Unknown, resource.Provenance));
            Assert.Equal(
                ShaderSamplerKind.Comparison,
                entry.Resources.Single(resource => resource.Name == "ComparisonSampler").Shape.SamplerKind);
        }

        [Fact]
        public void InvalidArtifactAndStageMismatch_FailWithTypedCompilerErrors()
        {
            ShaderCompileRequest request = CreateComputeRequest(
                """
                RWStructuredBuffer<uint> Output : register(u0);
                [numthreads(1, 1, 1)]
                void CSMain() { Output[0] = 1; }
                """,
                "invalid-reflection-negative.hlsl");
            ShaderCompileResult compiled = global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(request);

            ShaderCompilerException empty = Assert.Throws<ShaderCompilerException>(
                () => DxilArtifactReflector.Reflect(
                    request,
                    compiled with { ReflectionData = Array.Empty<byte>() }));
            Assert.Equal(ShaderCompilerErrorCode.CompileFailed, empty.ErrorCode);

            ShaderCompilerException corrupt = Assert.Throws<ShaderCompilerException>(
                () => DxilArtifactReflector.Reflect(
                    request,
                    compiled with { ReflectionData = new byte[] { 0x44, 0x58, 0x49, 0x4C } }));
            Assert.Equal(ShaderCompilerErrorCode.CompileFailed, corrupt.ErrorCode);

            ShaderCompilerException mismatch = Assert.Throws<ShaderCompilerException>(
                () => DxilArtifactReflector.Reflect(
                    request with { Stage = ShaderStageKind.Pixel },
                    compiled));
            Assert.Equal(ShaderCompilerErrorCode.CompileFailed, mismatch.ErrorCode);
            Assert.Contains("reports stage Compute", mismatch.Message, StringComparison.Ordinal);
        }

        private static ShaderResourceBindingReflection Find(
            ShaderEntryPointReflection entry,
            ShaderBindingClass bindingClass)
        {
            return entry.Resources.Single(resource => resource.Key.Type == bindingClass);
        }

        private static ShaderCompileRequest CreateComputeRequest(string source, string sourceName)
        {
            return new ShaderCompileRequest
            {
                Source = source,
                SourceName = sourceName,
                EntryPoint = "CSMain",
                Stage = ShaderStageKind.Compute,
                ShaderModel = new ShaderModelVersion(6, 6),
                Target = ShaderTargetKind.Dxil,
            };
        }

        private static ShaderArtifactReflection CompileAndReflect(ShaderCompileRequest request)
        {
            ShaderCompileResult compiled = global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(request);
            Assert.NotEmpty(compiled.ReflectionData);
            return DxilArtifactReflector.Reflect(request, compiled);
        }
    }
}
