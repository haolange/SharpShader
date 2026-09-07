using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpShader.Compilation;
using SharpShader.Compilation.Internal;
using SharpShader.HLSLCrossCompiler;
using SharpShader.HLSLCrossCompiler.Internal;
using Xunit;

namespace SharpShader.Internal.Tests
{
    public sealed class ShaderProgramCompilerTests
    {
        private const string MixedBindingSource = """
            cbuffer Constants : register(b0, space4)
            {
                uint TextureIndex;
            };

            Texture2D<float4> Textures[2] : register(t0, space4);
            SamplerState LinearSampler : register(s0, space4);
            RWStructuredBuffer<float4> Output : register(u0, space9);

            [numthreads(1, 1, 1)]
            void CSMain()
            {
                Output[0] = Textures[TextureIndex].SampleLevel(
                    LinearSampler,
                    float2(0.5, 0.5),
                    0);
            }
            """;

        [Fact]
        public void Compile_ProducesDx12IdentityVulkanDenseAndMetalReferenceArtifacts()
        {
            ShaderProgramCompilation result = new ShaderProgramCompiler().Compile(
                CreateComputeRequest(
                    MixedBindingSource,
                    ShaderProgramTarget.All,
                    "mixed-all-targets.hlsl"));

            ShaderInterfaceLayout layout = Assert.Single(result.Manifest.LogicalLayouts);
            ShaderBackendLayouts backends = Assert.Single(result.Manifest.BackendLayouts);
            Assert.Equal(4, layout.Bindings.Count);
            Assert.Equal(
                layout.Bindings.Select(binding =>
                    (
                        binding.Key.Table,
                        binding.Key.Slot,
                        binding.Key.Type)),
                backends.Dx12!.Bindings.Select(mapping =>
                    (
                        mapping.RegisterSpace,
                        mapping.ShaderRegister,
                        mapping.RegisterClass)));
            Assert.Equal(
                new[] { (0u, 0u), (0u, 1u), (0u, 2u), (1u, 0u) },
                backends.Vulkan!.Bindings.Select(mapping =>
                    (mapping.DescriptorSet, mapping.Binding)));
            Assert.Empty(backends.Metal!.DirectBindings);
            Assert.Equal(4, backends.Metal.ReferenceBufferBindings.Count);
            Assert.Equal(
                2u,
                backends.Metal.ReferenceBufferBindings
                    .Single(mapping =>
                        mapping.LogicalBinding.Type
                        == ShaderBindingClass.ShaderResource)
                    .ReferenceCount);

            Assert.Equal(3, result.Artifacts.Count);
            Assert.False(result.GetArtifact(
                "default",
                "CSMain",
                ShaderExecutionStage.Compute,
                ShaderArtifactKind.Dxil).Content.IsEmpty);
            Assert.False(result.GetArtifact(
                "default",
                "CSMain",
                ShaderExecutionStage.Compute,
                ShaderArtifactKind.SpirV).Content.IsEmpty);
            ShaderProgramArtifact msl = result.GetArtifact(
                "default",
                "CSMain",
                ShaderExecutionStage.Compute,
                ShaderArtifactKind.MslSource);
            Assert.False(string.IsNullOrWhiteSpace(msl.Text));
            Assert.Contains("kernel", msl.Text!, StringComparison.Ordinal);
        }

        [Fact]
        public void Compile_MapsImplicitMixedRegisterClassesWithoutLogicalCollisions()
        {
            const string source = """
                cbuffer Constants { uint Index; };
                Texture2D<float4> Input;
                SamplerState LinearSampler;
                RWStructuredBuffer<float4> Output;

                [numthreads(1, 1, 1)]
                void CSMain()
                {
                    Output[0] = Input.SampleLevel(LinearSampler, Index.xx, 0);
                }
                """;

            ShaderProgramCompilation result = new ShaderProgramCompiler().Compile(
                CreateComputeRequest(
                    source,
                    ShaderProgramTarget.DirectX12 | ShaderProgramTarget.Vulkan,
                    "implicit-mixed-bindings.hlsl"));
            ShaderBackendLayouts backends = Assert.Single(result.Manifest.BackendLayouts);

            Assert.Equal(
                new[]
                {
                    ShaderBindingClass.ShaderResource,
                    ShaderBindingClass.Sampler,
                    ShaderBindingClass.ConstantBuffer,
                    ShaderBindingClass.UnorderedAccess,
                },
                backends.Dx12!.Bindings.Select(mapping => mapping.RegisterClass));
            Assert.All(
                backends.Dx12.Bindings,
                mapping => Assert.Equal(0u, mapping.ShaderRegister));
            Assert.Equal(
                new uint[] { 0, 1, 2, 3 },
                backends.Vulkan!.Bindings.Select(mapping => mapping.Binding));
        }

        [Fact]
        public void Compile_DensifiesSparseLogicalTablesFourAndNine()
        {
            ShaderProgramCompilation result = new ShaderProgramCompiler().Compile(
                CreateComputeRequest(
                    MixedBindingSource,
                    ShaderProgramTarget.Vulkan,
                    "sparse-table-densification.hlsl"));
            VulkanShaderBackendLayout vulkan =
                Assert.Single(result.Manifest.BackendLayouts).Vulkan!;

            Assert.Equal(
                new uint[] { 0, 0, 0, 1 },
                vulkan.Bindings.Select(mapping => mapping.DescriptorSet));
            Assert.DoesNotContain(
                vulkan.Bindings,
                mapping => mapping.DescriptorSet is 4 or 9);
        }

        [Fact]
        public void Compile_MergesEntryBindingsIntoOneProgramUnionLayout()
        {
            const string source = """
                cbuffer Shared : register(b0, space2) { float4 Tint; };
                Texture2D<float4> VertexTexture : register(t0, space2);
                Texture2D<float4> PixelTexture : register(t1, space2);

                float4 VSMain(uint id : SV_VertexID) : SV_Position
                {
                    return VertexTexture.Load(int3(id, 0, 0)) + Tint;
                }

                float4 PSMain() : SV_Target
                {
                    return PixelTexture.Load(int3(0, 0, 0)) + Tint;
                }
                """;
            ShaderProgramCompileRequest request = CreateRequest(
                source,
                "program-union.hlsl",
                new[]
                {
                    new ShaderProgramEntry("VSMain", ShaderExecutionStage.Vertex),
                    new ShaderProgramEntry("PSMain", ShaderExecutionStage.Pixel),
                },
                ShaderProgramTarget.DirectX12);

            ShaderProgramCompilation result = new ShaderProgramCompiler().Compile(request);
            ShaderInterfaceVariant variant = Assert.Single(result.Manifest.Variants);
            ShaderInterfaceLayout layout = Assert.Single(result.Manifest.LogicalLayouts);
            ShaderLogicalBinding shared = layout.Bindings.Single(binding =>
                binding.Key.Type == ShaderBindingClass.ConstantBuffer);

            Assert.Equal(
                ShaderStageMask.Vertex | ShaderStageMask.Pixel,
                shared.StageMask);
            Assert.Equal(3, layout.Bindings.Count);
            Assert.All(
                variant.Entries,
                entry => Assert.Equal(layout.Signature, entry.LogicalLayoutSignature));
            Assert.Equal(2, result.Artifacts.Count);
        }

        [Fact]
        public void Compile_RejectsUnionBindingShapeConflict()
        {
            const string source = """
                Texture2D<float4> VertexTexture : register(t0);
                StructuredBuffer<float4> PixelBuffer : register(t0);

                float4 VSMain(uint id : SV_VertexID) : SV_Position
                {
                    return VertexTexture.Load(int3(id, 0, 0));
                }

                float4 PSMain() : SV_Target
                {
                    return PixelBuffer[0];
                }
                """;
            ShaderProgramCompileRequest request = CreateRequest(
                source,
                "program-union-conflict.hlsl",
                new[]
                {
                    new ShaderProgramEntry("VSMain", ShaderExecutionStage.Vertex),
                    new ShaderProgramEntry("PSMain", ShaderExecutionStage.Pixel),
                },
                ShaderProgramTarget.DirectX12);

            ShaderCompilerException exception = Assert.Throws<ShaderCompilerException>(
                () => new ShaderProgramCompiler().Compile(request));

            Assert.Equal(ShaderCompilerErrorCode.CompileFailed, exception.ErrorCode);
            Assert.Contains(
                "conflicting resource shape",
                exception.Message,
                StringComparison.Ordinal);
        }

        [Fact]
        public void Compile_Dx12LibraryPublishesArtifactsPerRequestedEntry()
        {
            const string source = """
                struct Payload { float4 Color; };
                RaytracingAccelerationStructure Scene : register(t0);
                RWTexture2D<float4> Output : register(u0);

                [shader("raygeneration")]
                void RayGen()
                {
                    Payload payload = (Payload)0;
                    RayDesc ray = (RayDesc)0;
                    ray.TMax = 1;
                    TraceRay(Scene, 0, 0xFF, 0, 1, 0, ray, payload);
                    Output[uint2(0, 0)] = payload.Color;
                }

                [shader("miss")]
                void Miss(inout Payload payload)
                {
                    payload.Color = float4(1, 0, 0, 1);
                }
                """;
            ShaderProgramCompileRequest request = CreateRequest(
                source,
                "dx12-ray-library.hlsl",
                new[]
                {
                    new ShaderProgramEntry(
                        "RayGen",
                        ShaderExecutionStage.RayGeneration),
                    new ShaderProgramEntry(
                        "Miss",
                        ShaderExecutionStage.Miss),
                },
                ShaderProgramTarget.DirectX12);

            ShaderProgramCompilation result = new ShaderProgramCompiler().Compile(request);

            Assert.Equal(2, result.Artifacts.Count);
            Assert.Equal(
                result.GetArtifact(
                    "default",
                    "RayGen",
                    ShaderExecutionStage.RayGeneration,
                    ShaderArtifactKind.Dxil).Identity.ContentDigest,
                result.GetArtifact(
                    "default",
                    "Miss",
                    ShaderExecutionStage.Miss,
                    ShaderArtifactKind.Dxil).Identity.ContentDigest);
        }

        [Fact]
        public void Compile_RejectsCrossBackendLibraryBeforeNativeInvocation()
        {
            int invocationCount = 0;
            ShaderProgramCompiler compiler = CreateCountingCompiler(
                _ =>
                {
                    Interlocked.Increment(ref invocationCount);
                    throw new InvalidOperationException("must not be called");
                });
            ShaderProgramCompileRequest request = CreateRequest(
                "[shader(\"raygeneration\")] void RayGen() {}",
                "unsupported-ray-cross-backend.hlsl",
                new[]
                {
                    new ShaderProgramEntry(
                        "RayGen",
                        ShaderExecutionStage.RayGeneration),
                },
                ShaderProgramTarget.All);

            ShaderCompilerException exception = Assert.Throws<ShaderCompilerException>(
                () => compiler.Compile(request));

            Assert.Equal(ShaderCompilerErrorCode.InvalidRequest, exception.ErrorCode);
            Assert.Equal(0, Volatile.Read(ref invocationCount));
            Assert.Contains("All", exception.Message, StringComparison.Ordinal);
            Assert.Contains(
                "RayGen (RayGeneration)",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Contains("DirectX12 only", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task CompileAsync_UsesOneNativeFlightForConcurrentIdenticalRequests()
        {
            int invocationCount = 0;
            ShaderProgramCompiler compiler = CreateCountingCompiler(
                request =>
                {
                    Interlocked.Increment(ref invocationCount);
                    return global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(request);
                });
            ShaderProgramCompileRequest request = CreateComputeRequest(
                "RWStructuredBuffer<uint> Output : register(u0); "
                + "[numthreads(1,1,1)] void CSMain() { Output[0] = 7; }",
                ShaderProgramTarget.DirectX12,
                "single-flight.hlsl");

            Task<ShaderProgramCompilation>[] tasks = Enumerable
                .Range(0, 16)
                .Select(_ => compiler.CompileAsync(request))
                .ToArray();
            ShaderProgramCompilation[] results = await Task.WhenAll(tasks);

            Assert.Equal(1, Volatile.Read(ref invocationCount));
            Assert.All(results, result => Assert.Same(results[0], result));
        }

        [Fact]
        public async Task CompileAsync_CallerCancellationOnlyCancelsThatWaiter()
        {
            using ManualResetEventSlim entered = new(false);
            using ManualResetEventSlim release = new(false);
            int invocationCount = 0;
            ShaderProgramCompiler compiler = CreateCountingCompiler(
                request =>
                {
                    Interlocked.Increment(ref invocationCount);
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(20)))
                    {
                        throw new TimeoutException(
                            "Timed out waiting to release the injected native compiler.");
                    }

                    return global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(request);
                });
            ShaderProgramCompileRequest request = CreateComputeRequest(
                "RWStructuredBuffer<uint> Output : register(u0); "
                + "[numthreads(1,1,1)] void CSMain() { Output[0] = 8; }",
                ShaderProgramTarget.DirectX12,
                "single-flight-cancellation.hlsl");

            Task<ShaderProgramCompilation> survivor = compiler.CompileAsync(request);
            Assert.True(
                entered.Wait(TimeSpan.FromSeconds(10)),
                "The injected native compiler did not enter.");
            using CancellationTokenSource cancellation = new();
            Task<ShaderProgramCompilation> cancelled =
                compiler.CompileAsync(request, cancellation.Token);

            try
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    async () => await cancelled.WaitAsync(
                        TimeSpan.FromSeconds(5)));
            }
            finally
            {
                release.Set();
            }

            ShaderProgramCompilation result = await survivor.WaitAsync(
                TimeSpan.FromSeconds(20));
            Assert.NotEmpty(result.Artifacts);
            Assert.Equal(1, Volatile.Read(ref invocationCount));
        }

        [Fact]
        public async Task CompileAsync_SnapshotsAllCallerOwnedMutableInputs()
        {
            const string source = """
                RWStructuredBuffer<uint> Output : register(u0);
                [numthreads(1,1,1)]
                void CSMain() { Output[0] = VALUE; }
                """;
            List<ShaderProgramEntry> entries = new()
            {
                new ShaderProgramEntry("CSMain", ShaderExecutionStage.Compute),
            };
            List<ShaderDefine> variantDefines = new();
            List<ShaderProgramVariant> variants = new()
            {
                new ShaderProgramVariant("default", variantDefines),
            };
            List<ShaderDefine> globalDefines = new()
            {
                new ShaderDefine("VALUE", "17"),
            };
            List<string> includeDirectories = new();
            List<SpirvBindingShift> bindingShifts = new();
            List<string> spirvArguments = new();
            Dictionary<ShaderBindingKey, uint> capacities = new();
            SpirvCompileOptions spirvOptions = new()
            {
                BindingShifts = bindingShifts,
                AdditionalArguments = spirvArguments,
            };
            MslCompileOptions mslOptions = new()
            {
                Platform = MslTargetPlatform.MacOS,
                MslVersion = 24000,
                ForceNativeArrays = true,
            };
            ShaderProgramCompileRequest request = new(
                source,
                "mutable-snapshot.hlsl",
                entries,
                variants,
                ShaderProgramTarget.Vulkan,
                new ShaderModelVersion(6, 6),
                globalDefines,
                includeDirectories,
                spirvOptions,
                mslOptions,
                capacities);
            using ManualResetEventSlim entered = new(false);
            using ManualResetEventSlim release = new(false);
            int invocationCount = 0;
            ShaderProgramCompiler compiler = CreateCountingCompiler(
                nativeRequest =>
                {
                    Interlocked.Increment(ref invocationCount);
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(20)))
                    {
                        throw new TimeoutException(
                            "Timed out waiting to release the injected native compiler.");
                    }

                    return global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(nativeRequest);
                });

            Task<ShaderProgramCompilation> compilation = compiler.CompileAsync(request);
            try
            {
                Assert.True(
                    entered.Wait(TimeSpan.FromSeconds(10)),
                    "The injected native compiler did not enter.");
                entries.Clear();
                variants.Clear();
                variantDefines.Add(new ShaderDefine("VALUE", "99"));
                globalDefines.Add(new ShaderDefine("OTHER", "1"));
                includeDirectories.Add("caller-mutated-include-directory");
                bindingShifts.Add(new SpirvBindingShift(
                    SpirvBindingShiftKind.UnorderedAccess,
                    0,
                    400));
                spirvArguments.Add("-fvk-u-shift");
                capacities.Add(
                    new ShaderBindingKey(
                        0,
                        0,
                        ShaderBindingClass.UnorderedAccess),
                    64);

                Assert.Single(request.Entries);
                Assert.Single(request.Variants);
                Assert.Single(request.GlobalDefines);
                Assert.Empty(request.IncludeDirectories);
                Assert.Empty(request.SpirvOptions.BindingShifts);
                Assert.Empty(request.SpirvOptions.AdditionalArguments);
                Assert.Empty(request.MetalArrayCapacities);
                Assert.NotSame(spirvOptions, request.SpirvOptions);
                Assert.NotSame(mslOptions, request.MslOptions);
            }
            finally
            {
                release.Set();
            }

            ShaderProgramCompilation actual = await compilation.WaitAsync(
                TimeSpan.FromSeconds(20));
            ShaderProgramCompileRequest baselineRequest = new(
                source,
                "mutable-snapshot.hlsl",
                new[]
                {
                    new ShaderProgramEntry("CSMain", ShaderExecutionStage.Compute),
                },
                new[] { ShaderProgramVariant.Default },
                ShaderProgramTarget.Vulkan,
                new ShaderModelVersion(6, 6),
                globalDefines: new[] { new ShaderDefine("VALUE", "17") },
                spirvOptions: new SpirvCompileOptions(),
                mslOptions: mslOptions);
            ShaderProgramCompilation expected =
                CreateCountingCompiler(global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile)
                    .Compile(baselineRequest);

            Assert.Equal(2, Volatile.Read(ref invocationCount));
            Assert.Equal(expected.CacheKey, actual.CacheKey);
            Assert.Equal(
                ShaderInterfaceManifestSerializer.Serialize(expected.Manifest),
                ShaderInterfaceManifestSerializer.Serialize(actual.Manifest));
            Assert.Equal(
                expected.Artifacts.Select(artifact => artifact.Identity.ContentDigest),
                actual.Artifacts.Select(artifact => artifact.Identity.ContentDigest));
        }

        [Fact]
        public void PersistentCache_WarmReadDoesNotPreprocessOrCompile()
        {
            using TemporaryDirectory directory = new();
            int coldPreprocessCount = 0;
            int coldInvocationCount = 0;
            ShaderProgramCompileRequest request = CreateComputeRequest(
                "RWStructuredBuffer<uint> Output : register(u0); "
                + "[numthreads(1,1,1)] void CSMain() { Output[0] = 9; }",
                ShaderProgramTarget.DirectX12,
                "persistent-cache.hlsl");
            ShaderProgramCompiler cold = CreateCountingCompiler(
                nativeRequest =>
                {
                    Interlocked.Increment(ref coldInvocationCount);
                    return global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(nativeRequest);
                },
                directory.Path,
                preprocess: (nativeRequest, limits) =>
                {
                    Interlocked.Increment(ref coldPreprocessCount);
                    return NativeDxcCompiler.Preprocess(nativeRequest, limits);
                });
            ShaderProgramCompilation expected = cold.Compile(request);
            Assert.Equal(1, coldPreprocessCount);
            Assert.Equal(1, coldInvocationCount);

            int warmPreprocessCount = 0;
            int warmInvocationCount = 0;
            ShaderProgramCompiler warm = CreateCountingCompiler(
                _ =>
                {
                    Interlocked.Increment(ref warmInvocationCount);
                    throw new InvalidOperationException(
                        "Warm cache must not invoke native compilation.");
                },
                directory.Path,
                preprocess: (_, _) =>
                {
                    Interlocked.Increment(ref warmPreprocessCount);
                    throw new InvalidOperationException(
                        "Warm cache must not invoke native preprocessing.");
                });
            ShaderProgramCompilation actual = warm.Compile(request);

            Assert.Equal(0, warmPreprocessCount);
            Assert.Equal(0, warmInvocationCount);
            Assert.Equal(expected.CacheKey, actual.CacheKey);
            Assert.Equal(
                ShaderInterfaceManifestSerializer.Serialize(expected.Manifest),
                ShaderInterfaceManifestSerializer.Serialize(actual.Manifest));
            Assert.Equal(
                expected.Artifacts.Select(artifact => artifact.Identity.ContentDigest),
                actual.Artifacts.Select(artifact => artifact.Identity.ContentDigest));
        }

        [Fact]
        public void MemoryCache_EnforcesConfiguredEntryBound()
        {
            int invocationCount = 0;
            ShaderProgramCacheLimits limits = new(
                maximumMemoryCacheEntries: 1,
                maximumMemoryCacheBytes: 64L * 1024 * 1024);
            ShaderProgramCompiler compiler = CreateCountingCompiler(
                nativeRequest =>
                {
                    Interlocked.Increment(ref invocationCount);
                    return global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(nativeRequest);
                },
                cacheLimits: limits);
            ShaderProgramCompileRequest first = CreateComputeRequest(
                "RWStructuredBuffer<uint> Output : register(u0); "
                + "[numthreads(1,1,1)] void CSMain() { Output[0] = 31; }",
                ShaderProgramTarget.DirectX12,
                "memory-cache-first.hlsl");
            ShaderProgramCompileRequest second = CreateComputeRequest(
                "RWStructuredBuffer<uint> Output : register(u0); "
                + "[numthreads(1,1,1)] void CSMain() { Output[0] = 37; }",
                ShaderProgramTarget.DirectX12,
                "memory-cache-second.hlsl");

            _ = compiler.Compile(first);
            _ = compiler.Compile(second);
            _ = compiler.Compile(first);

            Assert.Equal(3, Volatile.Read(ref invocationCount));
        }

        [Fact]
        public void MemoryCache_SameKeyTopologyChurnKeepsOrderStateBounded()
        {
            using TemporaryDirectory directory = new();
            string sourceName = System.IO.Path.Combine(
                directory.Path,
                "topology-churn.hlsl");
            string topologyMarker = System.IO.Path.Combine(
                directory.Path,
                "topology-marker.tmp");
            ShaderProgramCompileRequest request = CreateComputeRequest(
                "RWStructuredBuffer<uint> Output : register(u0); "
                + "[numthreads(1,1,1)] void CSMain() { Output[0] = 43; }",
                ShaderProgramTarget.DirectX12,
                sourceName,
                new[] { directory.Path });
            ShaderProgramCacheLimits limits = new(
                maximumMemoryCacheEntries: 1024,
                maximumMemoryCacheBytes: 256L * 1024 * 1024);
            int preprocessCount = 0;
            int compileCount = 0;
            ShaderProgramCompiler compiler = CreateCountingCompiler(
                nativeRequest =>
                {
                    Interlocked.Increment(ref compileCount);
                    return global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(nativeRequest);
                },
                cacheLimits: limits,
                preprocess: (nativeRequest, captureLimits) =>
                {
                    Interlocked.Increment(ref preprocessCount);
                    return NativeDxcCompiler.Preprocess(
                        nativeRequest,
                        captureLimits);
                });

            ShaderProgramCompilation initial = compiler.Compile(request);
            const int churnCount = 64;
            for (int index = 0; index < churnCount; ++index)
            {
                if ((index & 1) == 0)
                {
                    File.WriteAllText(topologyMarker, "not-an-include");
                }
                else
                {
                    File.Delete(topologyMarker);
                }

                ShaderProgramCompilation current = compiler.Compile(request);
                Assert.Equal(initial.CacheKey, current.CacheKey);
            }

            Assert.Equal(churnCount + 1, Volatile.Read(ref preprocessCount));
            Assert.Equal(churnCount + 1, Volatile.Read(ref compileCount));
            Assert.Equal(1, GetPrivateCollectionCount(
                compiler,
                "m_MemoryCache"));
            Assert.Equal(1, GetPrivateCollectionCount(
                compiler,
                "m_DependencyIndex"));
            Assert.Equal(1, GetPrivateCollectionCount(
                compiler,
                "m_MemoryInsertionOrder"));
            Assert.Equal(1, GetPrivateCollectionCount(
                compiler,
                "m_DependencyInsertionOrder"));

            ShaderProgramCompilation warm = compiler.Compile(request);
            Assert.Equal(initial.CacheKey, warm.CacheKey);
            Assert.Equal(churnCount + 1, Volatile.Read(ref preprocessCount));
            Assert.Equal(churnCount + 1, Volatile.Read(ref compileCount));
        }

        [Fact]
        public void PersistentCache_CorruptionIsQuarantinedAndRebuilt()
        {
            using TemporaryDirectory directory = new();
            ShaderProgramCompileRequest request = CreateComputeRequest(
                "RWStructuredBuffer<uint> Output : register(u0); "
                + "[numthreads(1,1,1)] void CSMain() { Output[0] = 11; }",
                ShaderProgramTarget.DirectX12,
                "corrupt-cache.hlsl");
            ShaderProgramCompilation initial = CreateCountingCompiler(
                global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile,
                directory.Path).Compile(request);
            string cachePath = Directory
                .EnumerateFiles(
                    directory.Path,
                    "*.sharpshader-cache-r3.json",
                    SearchOption.TopDirectoryOnly)
                .Single();
            File.WriteAllText(cachePath, "{\"schemaVersion\":3,\"broken\":true}");

            int rebuildCount = 0;
            ShaderProgramCompilation rebuilt = CreateCountingCompiler(
                nativeRequest =>
                {
                    Interlocked.Increment(ref rebuildCount);
                    return global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(nativeRequest);
                },
                directory.Path).Compile(request);

            Assert.Equal(1, rebuildCount);
            Assert.Equal(initial.CacheKey, rebuilt.CacheKey);
            Assert.Single(
                Directory.EnumerateFiles(
                    directory.Path,
                    "*.corrupt-*",
                    SearchOption.TopDirectoryOnly));
            Assert.True(File.Exists(cachePath));
        }

        [Fact]
        public async Task PersistentCache_ConcurrentPublishAcceptsValidWinner()
        {
            using TemporaryDirectory directory = new();
            using Barrier publishBarrier = new(2);
            int nativeInvocationCount = 0;
            ShaderProgramCompileRequest request = CreateComputeRequest(
                "RWStructuredBuffer<uint> Output : register(u0); "
                + "[numthreads(1,1,1)] void CSMain() { Output[0] = 12; }",
                ShaderProgramTarget.DirectX12,
                "concurrent-persistent-cache.hlsl");
            ShaderProgramCompiler firstCompiler = CreateCountingCompiler(
                CompileAndSynchronize,
                directory.Path);
            ShaderProgramCompiler secondCompiler = CreateCountingCompiler(
                CompileAndSynchronize,
                directory.Path);

            ShaderProgramCompilation[] results = await Task.WhenAll(
                firstCompiler.CompileAsync(request),
                secondCompiler.CompileAsync(request));

            Assert.Equal(2, Volatile.Read(ref nativeInvocationCount));
            Assert.Equal(results[0].CacheKey, results[1].CacheKey);
            Assert.Single(
                Directory.EnumerateFiles(
                    directory.Path,
                    "*.sharpshader-cache-r3.json",
                    SearchOption.TopDirectoryOnly));
            Assert.Empty(
                Directory.EnumerateFiles(
                    directory.Path,
                    "*.tmp",
                    SearchOption.TopDirectoryOnly));

            int warmInvocationCount = 0;
            ShaderProgramCompiler warmCompiler = CreateCountingCompiler(
                _ =>
                {
                    Interlocked.Increment(ref warmInvocationCount);
                    throw new InvalidOperationException(
                        "A concurrently published cache entry must be reusable.");
                },
                directory.Path);
            ShaderProgramCompilation warm = warmCompiler.Compile(request);

            Assert.Equal(0, Volatile.Read(ref warmInvocationCount));
            Assert.Equal(results[0].CacheKey, warm.CacheKey);

            ShaderCompileResult CompileAndSynchronize(
                ShaderCompileRequest nativeRequest)
            {
                ShaderCompileResult result = global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(nativeRequest);
                Interlocked.Increment(ref nativeInvocationCount);
                if (!publishBarrier.SignalAndWait(TimeSpan.FromSeconds(20)))
                {
                    throw new TimeoutException(
                        "Timed out synchronizing concurrent persistent cache publishers.");
                }

                return result;
            }
        }

        [Fact]
        public void Compile_ErrorAndCancellationDoNotPoisonFutureAttempts()
        {
            int invocationCount = 0;
            ShaderProgramCompiler compiler = CreateCountingCompiler(
                request =>
                {
                    int invocation = Interlocked.Increment(ref invocationCount);
                    if (invocation == 1)
                    {
                        throw new ShaderCompilerException(
                            ShaderCompilerErrorCode.CompileFailed,
                            "injected retryable failure");
                    }

                    return global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(request);
                });
            ShaderProgramCompileRequest request = CreateComputeRequest(
                "RWStructuredBuffer<uint> Output : register(u0); "
                + "[numthreads(1,1,1)] void CSMain() { Output[0] = 13; }",
                ShaderProgramTarget.DirectX12,
                "retry-after-failure.hlsl");

            Assert.Throws<ShaderCompilerException>(() => compiler.Compile(request));
            ShaderProgramCompilation result = compiler.Compile(request);
            Assert.NotEmpty(result.Artifacts);
            Assert.Equal(2, invocationCount);

            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(
                () => compiler.Compile(request, cancellation.Token));
            Assert.Equal(2, invocationCount);
        }

        [Fact]
        public void Compile_IsDeterministicAcrossVariantAndRequestOrdering()
        {
            const string source = """
                RWStructuredBuffer<uint> Output : register(u0);
                [numthreads(1,1,1)]
                void CSMain() { Output[0] = VALUE; }
                """;
            ShaderProgramCompileRequest first = new(
                source,
                "deterministic-variants.hlsl",
                new[]
                {
                    new ShaderProgramEntry("CSMain", ShaderExecutionStage.Compute),
                },
                new[]
                {
                    new ShaderProgramVariant(
                        "two",
                        new[] { new ShaderDefine("VALUE", "2") }),
                    new ShaderProgramVariant(
                        "one",
                        new[] { new ShaderDefine("VALUE", "1") }),
                },
                ShaderProgramTarget.DirectX12,
                new ShaderModelVersion(6, 6));
            ShaderProgramCompileRequest second = new(
                source,
                "deterministic-variants.hlsl",
                new[]
                {
                    new ShaderProgramEntry("CSMain", ShaderExecutionStage.Compute),
                },
                new[]
                {
                    new ShaderProgramVariant(
                        "one",
                        new[] { new ShaderDefine("VALUE", "1") }),
                    new ShaderProgramVariant(
                        "two",
                        new[] { new ShaderDefine("VALUE", "2") }),
                },
                ShaderProgramTarget.DirectX12,
                new ShaderModelVersion(6, 6));
            int leftNativeCount = 0;
            ShaderProgramCompiler leftCompiler = CreateCountingCompiler(
                request =>
                {
                    Interlocked.Increment(ref leftNativeCount);
                    return global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(request);
                });
            int rightNativeCount = 0;
            ShaderProgramCompiler rightCompiler = CreateCountingCompiler(
                request =>
                {
                    Interlocked.Increment(ref rightNativeCount);
                    return global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(request);
                });

            ShaderProgramCompilation left = leftCompiler.Compile(first);
            ShaderProgramCompilation right = rightCompiler.Compile(second);

            Assert.Equal(2, Volatile.Read(ref leftNativeCount));
            Assert.Equal(2, Volatile.Read(ref rightNativeCount));
            Assert.Equal(left.CacheKey, right.CacheKey);
            Assert.Equal(
                ShaderInterfaceManifestSerializer.Serialize(left.Manifest),
                ShaderInterfaceManifestSerializer.Serialize(right.Manifest));
            Assert.Equal(
                left.Artifacts.Select(artifact => artifact.Identity.ContentDigest),
                right.Artifacts.Select(artifact => artifact.Identity.ContentDigest));
        }

        [Fact]
        public void IncludeGraph_TracksActualDependencyClosureAndLimits()
        {
            using TemporaryDirectory directory = new();
            string nestedDirectory = System.IO.Path.Combine(directory.Path, "nested");
            Directory.CreateDirectory(nestedDirectory);
            string outerPath = System.IO.Path.Combine(directory.Path, "value.hlsli");
            string nestedPath = System.IO.Path.Combine(
                nestedDirectory,
                "nested-value.hlsli");
            string unrelatedPath = System.IO.Path.Combine(
                directory.Path,
                "unrelated.hlsli");
            File.WriteAllText(
                outerPath,
                "/* #include \"missing-commented.hlsli\" */\n"
                + "#include \"nested/nested-value.hlsli\"");
            File.WriteAllText(nestedPath, "#define INCLUDED_VALUE 17");
            File.WriteAllText(unrelatedPath, new string('x', 1024));
            const string source = """
                #include <value.hlsli>
                RWStructuredBuffer<uint> Output : register(u0);
                [numthreads(1,1,1)]
                void CSMain() { Output[0] = INCLUDED_VALUE; }
                """;
            ShaderProgramCompileRequest request = CreateComputeRequest(
                source,
                ShaderProgramTarget.DirectX12,
                "include-identity.hlsl",
                new[] { directory.Path });
            int invocationCount = 0;
            ShaderProgramCompiler compiler = CreateCountingCompiler(
                nativeRequest =>
                {
                    Interlocked.Increment(ref invocationCount);
                    return global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(nativeRequest);
                });
            string first = compiler.Compile(request).CacheKey;

            File.WriteAllText(unrelatedPath, new string('y', 2048));
            string afterUnrelatedChange = compiler.Compile(request).CacheKey;
            Assert.Equal(first, afterUnrelatedChange);
            Assert.Equal(1, Volatile.Read(ref invocationCount));

            File.WriteAllText(nestedPath, "#define INCLUDED_VALUE 19");
            string afterNestedChange = compiler.Compile(request).CacheKey;

            Assert.NotEqual(first, afterNestedChange);
            Assert.Equal(2, Volatile.Read(ref invocationCount));

            ShaderProgramCompiler bounded = new(
                new ShaderProgramCompilerOptions(
                    cacheLimits: new ShaderProgramCacheLimits(
                        maximumSourceBytes: 1024 * 1024,
                        maximumIncludeFileCount: 8,
                        maximumIncludeFileBytes: 4,
                        maximumIncludeTotalBytes: 8,
                        maximumCachePackageBytes: 1024 * 1024)));
            ShaderCompilerException exception = Assert.Throws<ShaderCompilerException>(
                () => bounded.Compile(request));
            Assert.Equal(ShaderCompilerErrorCode.InvalidRequest, exception.ErrorCode);
            Assert.Contains("per-file byte limit", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task IncludeGraph_RejectsMutationDuringNativeCompilationAndRetries()
        {
            using TemporaryDirectory directory = new();
            string includePath = System.IO.Path.Combine(directory.Path, "value.hlsli");
            File.WriteAllText(includePath, "#define INCLUDED_VALUE 41");
            ShaderProgramCompileRequest request = CreateComputeRequest(
                "#include \"value.hlsli\"\n"
                + "RWStructuredBuffer<uint> Output : register(u0); "
                + "[numthreads(1,1,1)] void CSMain() "
                + "{ Output[0] = INCLUDED_VALUE; }",
                ShaderProgramTarget.DirectX12,
                "include-toctou.hlsl",
                new[] { directory.Path });
            using ManualResetEventSlim entered = new(false);
            using ManualResetEventSlim release = new(false);
            int invocationCount = 0;
            ShaderProgramCompiler compiler = CreateCountingCompiler(
                nativeRequest =>
                {
                    Interlocked.Increment(ref invocationCount);
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(20)))
                    {
                        throw new TimeoutException(
                            "Timed out waiting to release the injected native compiler.");
                    }

                    return global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(nativeRequest);
                });

            Task<ShaderProgramCompilation> firstAttempt =
                compiler.CompileAsync(request);
            try
            {
                Assert.True(
                    entered.Wait(TimeSpan.FromSeconds(10)),
                    "The injected native compiler did not enter.");
                File.WriteAllText(includePath, "#define INCLUDED_VALUE 43");
            }
            finally
            {
                release.Set();
            }

            ShaderCompilerException changed =
                await Assert.ThrowsAsync<ShaderCompilerException>(
                    async () => await firstAttempt);
            Assert.Equal(ShaderCompilerErrorCode.CompileFailed, changed.ErrorCode);
            Assert.Contains(
                "changed between DXC preprocessing",
                changed.Message,
                StringComparison.Ordinal);

            ShaderProgramCompilation retry = compiler.Compile(request);
            Assert.NotEmpty(retry.Artifacts);
            Assert.Equal(2, Volatile.Read(ref invocationCount));
        }

        [Fact]
        public void IncludeGraph_UsesActualMacroPreprocessingAndDoesNotWarmReparsePaths()
        {
            using TemporaryDirectory directory = new();
            ShaderProgramCompileRequest missingRequest = CreateComputeRequest(
                "#include \"missing.hlsli\"\n"
                + "RWStructuredBuffer<uint> Output : register(u0); "
                + "[numthreads(1,1,1)] void CSMain() { Output[0] = 1; }",
                ShaderProgramTarget.DirectX12,
                "missing-include.hlsl",
                new[] { directory.Path });
            ShaderCompilerException missing = Assert.Throws<ShaderCompilerException>(
                () => new ShaderProgramCompiler().Compile(missingRequest));
            Assert.Equal(ShaderCompilerErrorCode.CompileFailed, missing.ErrorCode);
            Assert.Contains("missing.hlsli", missing.Diagnostics, StringComparison.Ordinal);

            string macroIncludePath = System.IO.Path.Combine(
                directory.Path,
                "value.hlsli");
            File.WriteAllText(macroIncludePath, "#define INCLUDED_VALUE 1");
            ShaderProgramCompileRequest dynamicRequest = CreateComputeRequest(
                "#define INCLUDE_FILE \"value.hlsli\"\n"
                + "#include INCLUDE_FILE\n"
                + "RWStructuredBuffer<uint> Output : register(u0); "
                + "[numthreads(1,1,1)] void CSMain() { Output[0] = 1; }",
                ShaderProgramTarget.DirectX12,
                "dynamic-include.hlsl",
                new[] { directory.Path });
            Assert.NotEmpty(
                new ShaderProgramCompiler().Compile(dynamicRequest).Artifacts);

            string targetPath = System.IO.Path.Combine(directory.Path, "target.hlsli");
            string linkPath = System.IO.Path.Combine(directory.Path, "linked.hlsli");
            File.WriteAllText(targetPath, "#define INCLUDED_VALUE 23");
            if (!TryCreateFileSymbolicLink(linkPath, targetPath))
            {
                return;
            }

            try
            {
                using TemporaryDirectory cacheDirectory = new();
                ShaderProgramCompileRequest reparseRequest = CreateComputeRequest(
                    "#include \"linked.hlsli\"\n"
                    + "RWStructuredBuffer<uint> Output : register(u0); "
                    + "[numthreads(1,1,1)] void CSMain() { Output[0] = INCLUDED_VALUE; }",
                    ShaderProgramTarget.DirectX12,
                    "reparse-include.hlsl",
                    new[] { directory.Path });
                int nativeInvocationCount = 0;
                ShaderProgramCompiler reparseCompiler = CreateCountingCompiler(
                    nativeRequest =>
                    {
                        Interlocked.Increment(ref nativeInvocationCount);
                        return global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(nativeRequest);
                    },
                    cacheDirectory.Path);

                Assert.NotEmpty(reparseCompiler.Compile(reparseRequest).Artifacts);
                Assert.NotEmpty(reparseCompiler.Compile(reparseRequest).Artifacts);
                Assert.Equal(2, Volatile.Read(ref nativeInvocationCount));
            }
            finally
            {
                File.Delete(linkPath);
            }
        }

        [Fact]
        public void IncludeTopology_RejectsShadowCandidateAppearingAfterPreprocess()
        {
            using TemporaryDirectory primaryDirectory = new();
            using TemporaryDirectory fallbackDirectory = new();
            using TemporaryDirectory cacheDirectory = new();
            string fallbackInclude = System.IO.Path.Combine(
                fallbackDirectory.Path,
                "value.hlsli");
            string shadowInclude = System.IO.Path.Combine(
                primaryDirectory.Path,
                "value.hlsli");
            File.WriteAllText(
                fallbackInclude,
                "#define INCLUDED_VALUE 17");

            ShaderProgramCompileRequest request = CreateComputeRequest(
                "#include \"value.hlsli\"\n"
                + "RWStructuredBuffer<uint> Output : register(u0); "
                + "[numthreads(1,1,1)] void CSMain() "
                + "{ Output[0] = INCLUDED_VALUE; }",
                ShaderProgramTarget.DirectX12,
                "shadow-candidate-race.hlsl",
                new[]
                {
                    primaryDirectory.Path,
                    fallbackDirectory.Path,
                });
            int preprocessCount = 0;
            int compileCount = 0;
            ShaderProgramCompiler compiler = CreateCountingCompiler(
                nativeRequest =>
                {
                    Interlocked.Increment(ref compileCount);
                    return global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(nativeRequest);
                },
                cacheDirectory.Path,
                preprocess: (nativeRequest, limits) =>
                {
                    DxcPreprocessedSource preprocessed =
                        NativeDxcCompiler.Preprocess(nativeRequest, limits);
                    if (Interlocked.Increment(ref preprocessCount) == 1)
                    {
                        File.WriteAllText(
                            shadowInclude,
                            "#define INCLUDED_VALUE 99");
                    }

                    return preprocessed;
                });

            ShaderCompilerException changed =
                Assert.Throws<ShaderCompilerException>(
                    () => compiler.Compile(request));
            Assert.Equal(ShaderCompilerErrorCode.CompileFailed, changed.ErrorCode);
            Assert.Contains(
                "include-candidate topology changed",
                changed.Message,
                StringComparison.Ordinal);
            Assert.Equal(1, Volatile.Read(ref preprocessCount));
            Assert.Equal(1, Volatile.Read(ref compileCount));
            Assert.Empty(Directory.EnumerateFiles(
                cacheDirectory.Path,
                "*.sharpshader-dependencies.json"));
            Assert.Empty(Directory.EnumerateFiles(
                cacheDirectory.Path,
                "*.sharpshader-cache-r3.json"));

            File.Delete(shadowInclude);
            Assert.NotEmpty(compiler.Compile(request).Artifacts);
            Assert.NotEmpty(compiler.Compile(request).Artifacts);
            Assert.Equal(2, Volatile.Read(ref preprocessCount));
            Assert.Equal(2, Volatile.Read(ref compileCount));
        }

        [Fact]
        public void Compile_RejectsCallerOwnedHighLevelBindingOverrides()
        {
            ShaderProgramCompileRequest request = new(
                "RWStructuredBuffer<uint> Output : register(u0); "
                + "[numthreads(1,1,1)] void CSMain() { Output[0] = 1; }",
                "reserved-binding-options.hlsl",
                new[]
                {
                    new ShaderProgramEntry("CSMain", ShaderExecutionStage.Compute),
                },
                new[] { new ShaderProgramVariant("default") },
                ShaderProgramTarget.Vulkan,
                new ShaderModelVersion(6, 6),
                spirvOptions: new SpirvCompileOptions
                {
                    BindingShifts = new[]
                    {
                        new SpirvBindingShift(
                            SpirvBindingShiftKind.UnorderedAccess,
                            0,
                            100),
                    },
                });

            ShaderCompilerException exception = Assert.Throws<ShaderCompilerException>(
                () => new ShaderProgramCompiler().Compile(request));

            Assert.Equal(ShaderCompilerErrorCode.InvalidRequest, exception.ErrorCode);
            Assert.Contains(
                "owns all SPIR-V binding shifts",
                exception.Message,
                StringComparison.Ordinal);
        }

        [Fact]
        public void Compile_RejectsCallerOwnedMetalBindingOptionsBeforeNativeWork()
        {
            MslCompileOptions[] options =
            {
                new() { EnableArgumentBuffers = true },
                new() { ArgumentBuffersTier = 1 },
                new() { EnableDecorateArgumentBufferIndex = true },
            };
            int invocationCount = 0;
            ShaderProgramCompiler compiler = CreateCountingCompiler(
                nativeRequest =>
                {
                    Interlocked.Increment(ref invocationCount);
                    return global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(nativeRequest);
                });

            foreach (MslCompileOptions mslOptions in options)
            {
                ShaderProgramCompileRequest request = new(
                    "RWStructuredBuffer<uint> Output : register(u0); "
                    + "[numthreads(1,1,1)] void CSMain() { Output[0] = 1; }",
                    "owned-metal-options.hlsl",
                    new[]
                    {
                        new ShaderProgramEntry(
                            "CSMain",
                            ShaderExecutionStage.Compute),
                    },
                    new[] { ShaderProgramVariant.Default },
                    ShaderProgramTarget.MetalMsl,
                    new ShaderModelVersion(6, 6),
                    mslOptions: mslOptions);

                ShaderCompilerException exception =
                    Assert.Throws<ShaderCompilerException>(
                        () => compiler.Compile(request));
                Assert.Equal(
                    ShaderCompilerErrorCode.InvalidRequest,
                    exception.ErrorCode);
                Assert.Contains(
                    "owns argument-buffer",
                    exception.Message,
                    StringComparison.Ordinal);
            }

            Assert.Equal(0, Volatile.Read(ref invocationCount));
        }

        [Fact]
        public void Compile_RequiresDxMemoryLayoutForCrossBackendArtifacts()
        {
            SpirvCompileOptions[] options =
            {
                new() { UseDxLayout = false },
                new() { UseDxLayout = false, UseGlLayout = true },
                new() { UseDxLayout = false, UseScalarLayout = true },
            };
            int invocationCount = 0;
            ShaderProgramCompiler compiler = CreateCountingCompiler(
                nativeRequest =>
                {
                    Interlocked.Increment(ref invocationCount);
                    return global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(nativeRequest);
                });

            foreach (SpirvCompileOptions spirvOptions in options)
            {
                ShaderProgramCompileRequest request = new(
                    "RWStructuredBuffer<uint> Output : register(u0); "
                    + "[numthreads(1,1,1)] void CSMain() { Output[0] = 1; }",
                    "owned-spirv-layout.hlsl",
                    new[]
                    {
                        new ShaderProgramEntry(
                            "CSMain",
                            ShaderExecutionStage.Compute),
                    },
                    new[] { ShaderProgramVariant.Default },
                    ShaderProgramTarget.Vulkan,
                    new ShaderModelVersion(6, 6),
                    spirvOptions: spirvOptions);

                ShaderCompilerException exception =
                    Assert.Throws<ShaderCompilerException>(
                        () => compiler.Compile(request));
                Assert.Equal(
                    ShaderCompilerErrorCode.InvalidRequest,
                    exception.ErrorCode);
                Assert.Contains(
                    "requires SPIR-V DX memory layout",
                    exception.Message,
                    StringComparison.Ordinal);
            }

            Assert.Equal(0, Volatile.Read(ref invocationCount));
        }

        [Fact]
        public void Compile_VerifiesFinalSpirvConstantBufferLayoutAgainstDxilTruth()
        {
            const string source = """
                cbuffer LayoutProbe : register(b0, space3)
                {
                    float4 Color;
                    row_major float3x4 Transform;
                    uint Index;
                };

                RWStructuredBuffer<uint> Output : register(u0, space4);

                [numthreads(1,1,1)]
                void CSMain()
                {
                    Output[0] = asuint(Color.x + Transform[0][0]) + Index;
                }
                """;
            ShaderProgramCompilation result = new ShaderProgramCompiler().Compile(
                CreateComputeRequest(
                    source,
                    ShaderProgramTarget.Vulkan,
                    "constant-buffer-layout-verification.hlsl"));

            ShaderInterfaceLayout logicalLayout =
                Assert.Single(result.Manifest.LogicalLayouts);
            ShaderLogicalBinding constantBuffer = Assert.Single(
                logicalLayout.Bindings,
                binding => binding.Key.Type == ShaderBindingClass.ConstantBuffer);
            ShaderConstantBufferLayout layout =
                Assert.IsType<ShaderConstantBufferLayout>(
                    constantBuffer.ConstantBufferLayout);
            Assert.True(layout.ByteSize >= 68);
            Assert.Single(
                layout.Variables,
                variable => variable.Name == "Color");
            Assert.Single(
                layout.Variables,
                variable => variable.Name == "Transform");
            Assert.Single(
                layout.Variables,
                variable => variable.Name == "Index");
            Assert.False(result.GetArtifact(
                "default",
                "CSMain",
                ShaderExecutionStage.Compute,
                ShaderArtifactKind.SpirV).Content.IsEmpty);
        }

        private static bool TryCreateFileSymbolicLink(
            string linkPath,
            string targetPath)
        {
            try
            {
                File.CreateSymbolicLink(linkPath, targetPath);
                return true;
            }
            catch (IOException ex) when (
                OperatingSystem.IsWindows()
                && (ex.HResult & 0xffff) == 1314)
            {
                return false;
            }
            catch (PlatformNotSupportedException)
            {
                return false;
            }
        }

        private static ShaderProgramCompileRequest CreateComputeRequest(
            string source,
            ShaderProgramTarget targets,
            string sourceName,
            IReadOnlyList<string>? includeDirectories = null)
        {
            return CreateRequest(
                source,
                sourceName,
                new[]
                {
                    new ShaderProgramEntry(
                        "CSMain",
                        ShaderExecutionStage.Compute),
                },
                targets,
                includeDirectories);
        }

        private static ShaderProgramCompileRequest CreateRequest(
            string source,
            string sourceName,
            IReadOnlyList<ShaderProgramEntry> entries,
            ShaderProgramTarget targets,
            IReadOnlyList<string>? includeDirectories = null)
        {
            ShaderAttachmentInterface[] attachmentInterfaces = entries
                .Where(static entry =>
                    entry.Stage == ShaderExecutionStage.Pixel)
                .Select(static entry => new ShaderAttachmentInterface(
                    "default",
                    entry.Name,
                    entry.Stage,
                    new ShaderAttachmentPhase(
                        0,
                        new[]
                        {
                            new ShaderAttachmentDeclaration(
                                logicalAttachmentId: 0,
                                inputIndex: null,
                                outputLocation: 0,
                                ShaderAttachmentAspect.Color,
                                ShaderAttachmentNumericClass.FloatingPoint,
                                ShaderAttachmentSampleMode.SingleSample,
                                ShaderAttachmentLayerMode.SingleLayer),
                        })))
                .ToArray();
            return new ShaderProgramCompileRequest(
                source,
                sourceName,
                entries,
                new[] { new ShaderProgramVariant("default") },
                targets,
                new ShaderModelVersion(6, 6),
                includeDirectories: includeDirectories,
                attachmentInterfaces: attachmentInterfaces);
        }

        private static ShaderProgramCompiler CreateCountingCompiler(
            Func<ShaderCompileRequest, ShaderCompileResult> compile,
            string? cacheDirectory = null,
            ShaderProgramCacheLimits? cacheLimits = null,
            Func<ShaderCompileRequest, DxcDependencyCaptureLimits,
                DxcPreprocessedSource>? preprocess = null)
        {
            return new ShaderProgramCompiler(
                new ShaderProgramCompilerOptions(
                    cacheDirectory,
                    cacheLimits),
                new ShaderProgramCompilerExecutionContext
                {
                    CompileOverride = compile,
                    PreprocessOverride = preprocess,
                    ToolchainComponentsOverride = _ => new[]
                    {
                        new ShaderToolchainComponent(
                            "TestToolchain",
                            "1.0",
                            new string('a', 64)),
                    },
                });
        }

        private static int GetPrivateCollectionCount(
            ShaderProgramCompiler compiler,
            string fieldName)
        {
            FieldInfo field = typeof(ShaderProgramCompiler).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    $"ShaderProgramCompiler field {fieldName} was not found.");
            object collection = field.GetValue(compiler)
                ?? throw new InvalidOperationException(
                    $"ShaderProgramCompiler field {fieldName} is null.");
            PropertyInfo count = collection.GetType().GetProperty("Count")
                ?? throw new InvalidOperationException(
                    $"ShaderProgramCompiler field {fieldName} has no Count property.");
            return (int)(count.GetValue(collection)
                ?? throw new InvalidOperationException(
                    $"ShaderProgramCompiler field {fieldName} returned a null Count."));
        }

        private sealed class TemporaryDirectory : IDisposable
        {
            public string Path { get; }

            public TemporaryDirectory()
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "SharpShader.ProgramCompiler.Tests",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public void Dispose()
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
        }
    }
}
