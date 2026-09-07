using SharpShader.Compilation;
using SharpShader.HLSLCrossCompiler;
using Xunit;

namespace SharpShader.Tests
{
    public sealed class PortableResourceIdentityTests : IDisposable
    {
        private readonly string m_Root = Path.Combine(Path.GetTempPath(), "SharpShader-" + Guid.NewGuid().ToString("N"));
        private const string Source = """
#include "value.hlsl"
RWStructuredBuffer<uint> Output : register(u0);
[numthreads(1,1,1)] void CSMain(uint3 id : SV_DispatchThreadID) { Output[0] = VALUE; }
""";

        [Fact]
        public async Task RelocatedResources_ShouldProduceIdenticalKeysAndArtifacts()
        {
            string first = CreateSource("original", 41);
            string second = CreateSource("中文 relocated root", 41);
            ShaderProgramCompiler compiler = new(new ShaderProgramCompilerOptions(Path.Combine(m_Root, "cache")));
            ShaderProgramCompilation[] result = await Task.WhenAll(
                compiler.CompileAsync(Request(first)), compiler.CompileAsync(Request(second)));
            Assert.Equal(result[0].CacheKey, result[1].CacheKey);
            Assert.Equal(result[0].Artifacts[0].Content.ToArray(), result[1].Artifacts[0].Content.ToArray());
            ShaderProgramCompilation restarted = new ShaderProgramCompiler(
                new ShaderProgramCompilerOptions(Path.Combine(m_Root, "cache"))).Compile(Request(second));
            Assert.Equal(result[1].CacheKey, restarted.CacheKey);
        }

        [Fact]
        public async Task ConcurrentRootsAndWarmIncludes_ShouldKeepTheirOwnDependencies()
        {
            string first = CreateSource("a", 41);
            string second = CreateSource("b", 73);
            ShaderProgramCompiler compiler = new();
            ShaderProgramCompilation[] result = await Task.WhenAll(
                compiler.CompileAsync(Request(first)), compiler.CompileAsync(Request(second)));
            Assert.NotEqual(result[0].CacheKey, result[1].CacheKey);
            Assert.NotEqual(result[0].Artifacts[0].Content.ToArray(), result[1].Artifacts[0].Content.ToArray());
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(second)!, "value.hlsl"), "#define VALUE 99u");
            Assert.NotEqual(result[1].CacheKey, compiler.Compile(Request(second)).CacheKey);
            Assert.Equal(result[0].CacheKey, compiler.Compile(Request(first)).CacheKey);
        }

        [Fact]
        public void IncludePrecedenceAndResourceIdentity_ShouldAffectTheArtifactKey()
        {
            string first = CreateSource("includes-one", 41);
            string second = CreateSource("includes-two", 73);
            string rootSource = Path.Combine(m_Root, "main.hlsl");
            File.WriteAllText(rootSource, Source);
            string[] includes = [Path.GetDirectoryName(first)!, Path.GetDirectoryName(second)!];
            ShaderProgramCompiler compiler = new();
            string key = compiler.Compile(Request(rootSource, includes)).CacheKey;
            Assert.NotEqual(key, compiler.Compile(Request(rootSource, includes.Reverse().ToArray())).CacheKey);
            Assert.NotEqual(key, compiler.Compile(Request(rootSource, includes, "engine/different-resource")).CacheKey);
        }

        [Fact]
        public async Task CancellationAndMissingIncludes_ShouldFailExplicitly()
        {
            string source = CreateSource("errors", 41);
            ShaderProgramCompiler compiler = new();
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compiler.CompileAsync(Request(source), cancellation.Token));
            File.Delete(Path.Combine(Path.GetDirectoryName(source)!, "value.hlsl"));
            Assert.Throws<ShaderCompilerException>(() => compiler.Compile(Request(source)));
        }

        private string CreateSource(string name, uint value)
        {
            string directory = Path.Combine(m_Root, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "value.hlsl"), $"#define VALUE {value}u");
            string path = Path.Combine(directory, "main.hlsl");
            File.WriteAllText(path, Source);
            return path;
        }

        private static ShaderProgramCompileRequest Request(string path, string[]? includes = null, string identity = "engine/compute/value")
        {
            return new ShaderProgramCompileRequest(Source, path,
                [new ShaderProgramEntry("CSMain", ShaderExecutionStage.Compute)],
                [ShaderProgramVariant.Default], ShaderProgramTarget.DirectX12,
                new ShaderModelVersion(6, 6), includeDirectories: includes, sourceIdentity: identity);
        }

        public void Dispose()
        {
            string resolved = Path.GetFullPath(m_Root);
            string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture path escaped the temporary directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
