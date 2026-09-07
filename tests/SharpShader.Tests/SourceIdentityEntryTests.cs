using SharpShader.Compilation;
using SharpShader.CSharp;
using SharpShader.HLSLCrossCompiler;
using SharpShader.ShaderLab;
using Xunit;

namespace SharpShader.Tests
{
    public sealed class SourceIdentityEntryTests
    {
        [Theory]
        [InlineData("/absolute/shader")]
        [InlineData("C:/absolute/shader")]
        [InlineData("C:\\absolute\\shader")]
        [InlineData("\\\\host\\share\\shader")]
        [InlineData("shader\nidentity")]
        public void PhysicalIdentities_ShouldBeRejectedOnEveryHost(string identity)
        {
            Assert.Throws<ArgumentException>(() => new ShaderProgramCompileRequest(
                "void CSMain() {}", "source.hlsl",
                [new ShaderProgramEntry("CSMain", ShaderExecutionStage.Compute)],
                [ShaderProgramVariant.Default], ShaderProgramTarget.DirectX12,
                new ShaderModelVersion(6, 6), sourceIdentity: identity));
        }

        [Theory]
        [InlineData(ShaderProgramTarget.DirectX12, "second-root")]
        [InlineData(ShaderProgramTarget.DirectX12, "second root")]
        [InlineData(ShaderProgramTarget.DirectX12, "中文")]
        [InlineData(ShaderProgramTarget.DirectX12, "中文 second root")]
        [InlineData(ShaderProgramTarget.Vulkan, "中文 second root")]
        public void CSharpEntry_ShouldPropagateStableIdentity(ShaderProgramTarget target, string directoryName)
        {
            CSharpShaderCompiler compiler = new();
            CSharpShaderCompilerOptions options = new(target);
            string firstPath = Path.Combine(Path.GetTempPath(), "first-root", "shader.cs");
            string secondPath = Path.Combine(Path.GetTempPath(), directoryName, "shader.cs");
            ShaderProgramCompilation first = compiler.Compile(CSharpShaderSources.WriteConstant,
                firstPath, options, sourceIdentity: "infinity://id/example/1").Primary.Program;
            ShaderProgramCompilation second;
            try
            {
                second = compiler.Compile(CSharpShaderSources.WriteConstant,
                    secondPath, options, sourceIdentity: "infinity://id/example/1").Primary.Program;
            }
            catch (ShaderCompilerException error)
            {
                throw new InvalidOperationException(error.Diagnostics, error);
            }
            Assert.Equal(first.CacheKey, second.CacheKey);
            Assert.Equal(first.Artifacts[0].Content.ToArray(), second.Artifacts[0].Content.ToArray());
            Assert.NotEqual(first.CacheKey, compiler.Compile(CSharpShaderSources.WriteConstant,
                firstPath, options, sourceIdentity: "infinity://id/different/1").Primary.Program.CacheKey);
        }

        [Theory]
        [InlineData(ShaderProgramTarget.DirectX12, false)]
        [InlineData(ShaderProgramTarget.DirectX12, true)]
        [InlineData(ShaderProgramTarget.Vulkan, false)]
        [InlineData(ShaderProgramTarget.Vulkan, true)]
        public void UnicodeMemorySource_ShouldRemainAuthoritativeAndResolveIncludes(
            ShaderProgramTarget target, bool conflictingDiskSource)
        {
            string root = Path.Combine(Path.GetTempPath(), "SharpShader-" + Guid.NewGuid().ToString("N"));
            string directory = Path.Combine(root, "中文 source");
            Directory.CreateDirectory(directory);
            try
            {
                string sourcePath = Path.Combine(directory, "内存 shader.hlsl");
                File.WriteAllText(Path.Combine(directory, "constants.hlsl"), "#define RESULT 41u");
                if (conflictingDiskSource)
                {
                    File.WriteAllText(sourcePath, "#error Disk source must never replace the supplied source");
                }

                const string source = """
#include "constants.hlsl"
RWStructuredBuffer<uint> Output : register(u0);
[numthreads(1,1,1)] void CSMain(uint3 id : SV_DispatchThreadID) { Output[0] = RESULT; }
""";
                ShaderProgramCompiler compiler = new();
                ShaderProgramCompileRequest request = new(source, sourcePath,
                    [new ShaderProgramEntry("CSMain", ShaderExecutionStage.Compute)],
                    [ShaderProgramVariant.Default], target, new ShaderModelVersion(6, 6),
                    sourceIdentity: "资源/compute");
                ShaderProgramCompilation first = compiler.Compile(request);
                Assert.NotEmpty(first.Artifacts[0].Content.ToArray());
                Assert.Equal(first.CacheKey, compiler.Compile(request).CacheKey);
                File.WriteAllText(Path.Combine(directory, "constants.hlsl"), "#define RESULT 42u");
                Assert.NotEqual(first.CacheKey, compiler.Compile(request).CacheKey);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void ShaderLabStandalone_ShouldPropagateStableIdentity()
        {
            const string source = """
#pragma kernel CSMain
RWStructuredBuffer<uint> Output : register(u0);
[numthreads(1,1,1)] void CSMain(uint3 id : SV_DispatchThreadID) { Output[0] = 41u; }
""";
            ShaderLabCompiler compiler = new();
            ShaderLabCompilerOptions options = new(ShaderProgramTarget.DirectX12);
            StandaloneShaderProgram program = ShaderLabUtil.ParseComputeProgramFromSource(source);
            string first = compiler.CompileStandalone(program, options, sourceIdentity: "compute/first").CacheKey;
            string second = compiler.CompileStandalone(program, options, sourceIdentity: "compute/second").CacheKey;
            Assert.NotEqual(first, second);
            Assert.Equal(first, compiler.CompileStandalone(program, options, sourceIdentity: "compute/first").CacheKey);
        }
    }
}
