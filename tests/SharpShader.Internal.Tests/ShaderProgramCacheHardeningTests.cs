using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SharpShader.Compilation;
using SharpShader.Compilation.Internal;
using SharpShader.HLSLCrossCompiler;
using Xunit;

namespace SharpShader.Internal.Tests
{
    public sealed class ShaderProgramCacheHardeningTests
    {
        [Fact]
        public void CompilationArtifactsAndSpirvOptionsDoNotExposeMutableAliases()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            List<SpirvBindingShift> shifts = new()
            {
                new SpirvBindingShift(
                    SpirvBindingShiftKind.ShaderResource,
                    0,
                    4),
            };
            List<string> arguments = new() { "-fspv-preserve-bindings" };
            ShaderProgramCompileRequest request = CreateRequest(
                71,
                new SpirvCompileOptions
                {
                    BindingShifts = shifts,
                    AdditionalArguments = arguments,
                });

            shifts.Clear();
            arguments.Clear();
            IList<SpirvBindingShift> frozenShifts =
                Assert.IsAssignableFrom<IList<SpirvBindingShift>>(
                    request.SpirvOptions.BindingShifts);
            IList<string> frozenArguments =
                Assert.IsAssignableFrom<IList<string>>(
                    request.SpirvOptions.AdditionalArguments);
            Assert.True(frozenShifts.IsReadOnly);
            Assert.True(frozenArguments.IsReadOnly);
            Assert.Throws<NotSupportedException>(
                () => frozenShifts[0] = new SpirvBindingShift(
                    SpirvBindingShiftKind.ShaderResource,
                    0,
                    8));
            Assert.Throws<NotSupportedException>(
                () => frozenArguments[0] = "-fspv-reflect");
            Assert.Single(request.SpirvOptions.BindingShifts);
            Assert.Single(request.SpirvOptions.AdditionalArguments);

            ShaderProgramCompiler compiler = CreateCompiler();
            ShaderProgramCompileRequest compileRequest = CreateRequest(71);
            ShaderProgramCompilation compilation = compiler.Compile(compileRequest);
            ShaderProgramArtifact artifact = Assert.Single(compilation.Artifacts);
            byte[] expected = artifact.Content.ToArray();
            ReadOnlyMemory<byte> exposed = artifact.Content;
            Assert.True(MemoryMarshal.TryGetArray(
                exposed,
                out ArraySegment<byte> exposedArray));
            Assert.NotNull(exposedArray.Array);
            exposedArray.Array![exposedArray.Offset] ^= 0x7f;

            using MemoryStream copied = new();
            artifact.CopyContentTo(copied);
            copied.GetBuffer()[0] ^= 0x3f;

            Assert.Equal(expected, artifact.Content.ToArray());
            ShaderProgramCompilation cached = compiler.Compile(compileRequest);
            Assert.Same(compilation, cached);
            Assert.Equal(expected, Assert.Single(cached.Artifacts).Content.ToArray());
        }

        [Fact]
        public async Task CorruptEntryWithTwoReadersAndValidRepublisherKeepsValidWinner()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using TemporaryDirectory cache = new();
            ShaderProgramCompileRequest request = CreateRequest(73);
            ShaderProgramCompilation initial =
                CreateCompiler(cache.Path).Compile(request);
            string livePath = GetLivePath(cache.Path, initial.CacheKey);
            File.WriteAllText(livePath, "{\"schemaVersion\":3,\"broken\":true}");

            using CountdownEvent readersEntered = new(2);
            using ManualResetEventSlim releaseReaders = new(false);
            ShaderProgramCompiler firstReader = CreateCompiler(
                cache.Path,
                compile: CompileAfterReaderBarrier);
            ShaderProgramCompiler secondReader = CreateCompiler(
                cache.Path,
                compile: CompileAfterReaderBarrier);

            Task<ShaderProgramCompilation> first =
                firstReader.CompileAsync(request);
            Task<ShaderProgramCompilation> second =
                secondReader.CompileAsync(request);
            Assert.True(
                readersEntered.Wait(TimeSpan.FromSeconds(20)),
                "Both cache readers must reach native compilation.");

            ShaderProgramCompilation republished =
                CreateCompiler(cache.Path).Compile(request);
            Assert.True(File.Exists(livePath));
            releaseReaders.Set();
            ShaderProgramCompilation[] readerResults =
                await Task.WhenAll(first, second).WaitAsync(
                    TimeSpan.FromSeconds(30));

            Assert.All(
                readerResults,
                result => Assert.Equal(republished.CacheKey, result.CacheKey));
            Assert.Single(Directory.EnumerateFiles(
                cache.Path,
                "*.sharpshader-cache-r3.json",
                SearchOption.TopDirectoryOnly));
            Assert.Single(Directory.EnumerateFiles(
                cache.Path,
                "*.corrupt-*",
                SearchOption.TopDirectoryOnly));
            Assert.Empty(Directory.EnumerateFiles(
                cache.Path,
                "*.claim-*",
                SearchOption.TopDirectoryOnly));
            Assert.Empty(Directory.EnumerateFiles(
                cache.Path,
                "*.tmp",
                SearchOption.TopDirectoryOnly));

            int warmInvocationCount = 0;
            ShaderProgramCompilation warm = CreateCompiler(
                cache.Path,
                compile: nativeRequest =>
                {
                    Interlocked.Increment(ref warmInvocationCount);
                    return global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(nativeRequest);
                }).Compile(request);
            Assert.Equal(0, Volatile.Read(ref warmInvocationCount));
            Assert.Equal(republished.CacheKey, warm.CacheKey);

            ShaderCompileResult CompileAfterReaderBarrier(
                ShaderCompileRequest nativeRequest)
            {
                readersEntered.Signal();
                if (!releaseReaders.Wait(TimeSpan.FromSeconds(20)))
                {
                    throw new TimeoutException(
                        "Timed out waiting for the valid cache republisher.");
                }

                return global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(nativeRequest);
            }
        }

        [Fact]
        public void PersistentRetentionBoundsLiveQuarantineAndProtectsCurrentWinner()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using TemporaryDirectory cache = new();
            ShaderProgramCacheLimits limits = new(
                maximumCachePackageBytes: 16L * 1024 * 1024,
                maximumPersistentCacheEntries: 2,
                maximumPersistentCacheBytes: 20L * 1024 * 1024,
                maximumQuarantineEntries: 1,
                maximumQuarantineBytes: 4L * 1024 * 1024);
            ShaderProgramCompileRequest[] requests =
            {
                CreateRequest(79),
                CreateRequest(83),
                CreateRequest(89),
            };
            ShaderProgramCompilation[] results = requests
                .Select(request =>
                    CreateCompiler(cache.Path, limits).Compile(request))
                .ToArray();

            Assert.Equal(2, CountLiveEntries(cache.Path));
            string newestPath = GetLivePath(
                cache.Path,
                results[^1].CacheKey);
            Assert.True(File.Exists(newestPath));

            Dictionary<string, ShaderProgramCompileRequest> requestsByKey =
                results.Select((result, index) => (result.CacheKey, requests[index]))
                    .ToDictionary(pair => pair.CacheKey, pair => pair.Item2);

            ShaderProgramCompilation[] liveResults = results
                .Where(result => File.Exists(
                    GetLivePath(cache.Path, result.CacheKey)))
                .ToArray();
            Assert.Equal(2, liveResults.Length);
            foreach (ShaderProgramCompilation result in liveResults)
            {
                string path = GetLivePath(cache.Path, result.CacheKey);
                File.WriteAllText(path, "{\"corrupt\":true}");
                _ = CreateCompiler(cache.Path, limits).Compile(requestsByKey[result.CacheKey]);
            }

            Assert.True(CountLiveEntries(cache.Path) <= 2);
            Assert.True(Directory.EnumerateFiles(
                    cache.Path,
                    "*.corrupt-*",
                    SearchOption.TopDirectoryOnly)
                .Count() <= 1);
            long retainedBytes = Directory.EnumerateFiles(
                    cache.Path,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Where(path =>
                    path.EndsWith(
                        ".sharpshader-cache-r3.json",
                        StringComparison.Ordinal)
                    || path.Contains(
                        ".sharpshader-cache-r3.json.corrupt-",
                        StringComparison.Ordinal))
                .Sum(path => new FileInfo(path).Length);
            Assert.InRange(retainedBytes, 1, limits.MaximumPersistentCacheBytes);
            Assert.True(File.Exists(GetLivePath(
                cache.Path,
                liveResults[^1].CacheKey)));
        }

        [Fact]
        public void CachePackageLimitFailsDuringStreamingAndRemovesPartialFile()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using TemporaryDirectory cache = new();
            ShaderProgramCacheLimits limits = new(
                maximumCachePackageBytes: 512,
                maximumPersistentCacheEntries: 1,
                maximumPersistentCacheBytes: 512,
                maximumQuarantineEntries: 0,
                maximumQuarantineBytes: 0);

            IOException exception = Assert.Throws<IOException>(
                () => CreateCompiler(cache.Path, limits).Compile(
                    CreateRequest(97)));
            Assert.Contains("512-byte limit", exception.Message);
            Assert.Empty(Directory.EnumerateFiles(
                cache.Path,
                "*.sharpshader-cache-r3.json",
                SearchOption.TopDirectoryOnly));
            Assert.Empty(Directory.EnumerateFiles(
                cache.Path,
                "*.tmp",
                SearchOption.TopDirectoryOnly));
        }

        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public void Revision2NamespaceIsNeverReadAndRevision2SchemaIsQuarantined()
        {
            using TemporaryDirectory cache = new();
            (ShaderProgramCompilation compilation,
                ShaderProgramDependencySnapshot dependencies) =
                CreatePortableCacheFixture();
            ShaderProgramPersistentCache persistent = new(
                cache.Path,
                ShaderProgramCacheLimits.Default);
            persistent.Store(compilation, dependencies);

            string livePath = GetLivePath(
                cache.Path,
                compilation.CacheKey);
            string canonical = File.ReadAllText(livePath);
            string oldSchema = canonical.Replace(
                "\"schemaVersion\":3",
                "\"schemaVersion\":2",
                StringComparison.Ordinal);
            Assert.NotEqual(canonical, oldSchema);
            File.WriteAllText(livePath, oldSchema);

            string retiredPath = Path.Combine(
                cache.Path,
                compilation.CacheKey + ".sharpshader-cache-r2.json");
            const string retiredSentinel =
                "retired cache namespace must never be read";
            File.WriteAllText(retiredPath, retiredSentinel);

            Assert.False(persistent.TryLoadPair(
                dependencies.ProvisionalKey,
                out ShaderProgramDependencySnapshot? rejectedDependencies,
                out ShaderProgramCompilation? rejectedCompilation));
            Assert.Null(rejectedDependencies);
            Assert.Null(rejectedCompilation);
            Assert.Equal(retiredSentinel, File.ReadAllText(retiredPath));
            Assert.False(File.Exists(livePath));
            Assert.Single(Directory.EnumerateFiles(
                cache.Path,
                "*.sharpshader-cache-r3.json.corrupt-*",
                SearchOption.TopDirectoryOnly));

            persistent.Store(compilation, dependencies);
            Assert.True(File.Exists(livePath));
            Assert.Contains(
                "\"schemaVersion\":3",
                File.ReadAllText(livePath),
                StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(
                cache.Path,
                "*.claim-*",
                SearchOption.TopDirectoryOnly));
            Assert.Empty(Directory.EnumerateFiles(
                cache.Path,
                "*.tmp",
                SearchOption.TopDirectoryOnly));
        }

        private static (
            ShaderProgramCompilation Compilation,
            ShaderProgramDependencySnapshot Dependencies)
            CreatePortableCacheFixture()
        {
            const string sourceDigest =
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string provisionalKey =
                "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
            const string finalKey =
                "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
            byte[] content = { 0x44, 0x58, 0x49, 0x4c };
            ShaderArtifactIdentity identity = new(
                ShaderArtifactKind.Dxil,
                Convert.ToHexStringLower(SHA256.HashData(content)),
                checked((ulong)content.Length),
                "portable.dxil");
            ShaderInterfaceLayout layout = new(
                Array.Empty<ShaderLogicalBinding>());
            ShaderInterfaceEntry entry = new(
                "CSMain",
                ShaderExecutionStage.Compute,
                layout.Signature,
                new ShaderAttachmentInterface(
                    "default",
                    "CSMain",
                    ShaderExecutionStage.Compute),
                new[] { identity });
            ShaderInterfaceManifest manifest = new(
                sourceDigest,
                new[]
                {
                    new ShaderToolchainComponent(
                        "PortableCacheFixture",
                        "1.0"),
                },
                new[] { layout },
                new[]
                {
                    new ShaderInterfaceVariant(
                        "default",
                        defines: null,
                        new[] { entry }),
                },
                new[] { ShaderBackendLayoutPlanner.Plan(layout) },
                ShaderProgramTarget.DirectX12);
            ShaderProgramArtifact artifact = new(
                "default",
                "CSMain",
                ShaderExecutionStage.Compute,
                identity,
                content,
                text: null);
            ShaderProgramCompilation compilation = new(
                finalKey,
                manifest,
                new[] { artifact });
            ShaderProgramDependencySnapshot dependencies = new(
                provisionalKey,
                finalKey,
                isWarmable: true,
                Array.Empty<ShaderProgramDependencyFile>(),
                Array.Empty<ShaderProgramDirectoryTopology>());
            return (compilation, dependencies);
        }
        private static int CountLiveEntries(string directory)
        {
            return Directory.EnumerateFiles(
                    directory,
                    "*.sharpshader-cache-r3.json",
                    SearchOption.TopDirectoryOnly)
                .Count();
        }

        private static string GetLivePath(
            string directory,
            string cacheKey)
        {
            return Path.Combine(
                directory,
                cacheKey + ".sharpshader-cache-r3.json");
        }

        private static ShaderProgramCompileRequest CreateRequest(
            uint value,
            SpirvCompileOptions? spirvOptions = null)
        {
            return new ShaderProgramCompileRequest(
                "RWStructuredBuffer<uint> Output : register(u0); "
                + "[numthreads(1,1,1)] void CSMain() "
                + $"{{ Output[0] = {value}; }}",
                $"cache-hardening-{value}.hlsl",
                new[]
                {
                    new ShaderProgramEntry(
                        "CSMain",
                        ShaderExecutionStage.Compute),
                },
                new[] { ShaderProgramVariant.Default },
                ShaderProgramTarget.DirectX12,
                new ShaderModelVersion(6, 6),
                spirvOptions: spirvOptions);
        }

        private static ShaderProgramCompiler CreateCompiler(
            string? cacheDirectory = null,
            ShaderProgramCacheLimits? limits = null,
            Func<ShaderCompileRequest, ShaderCompileResult>? compile = null)
        {
            return new ShaderProgramCompiler(
                new ShaderProgramCompilerOptions(cacheDirectory, limits),
                new ShaderProgramCompilerExecutionContext
                {
                    CompileOverride = compile ?? global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile,
                    ToolchainComponentsOverride = _ => new[]
                    {
                        new ShaderToolchainComponent(
                            "CacheHardeningTestToolchain",
                            "1.0",
                            new string('c', 64)),
                    },
                });
        }

        private sealed class TemporaryDirectory : IDisposable
        {
            public string Path { get; }

            public TemporaryDirectory()
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "SharpShader.CacheHardening.Tests",
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
