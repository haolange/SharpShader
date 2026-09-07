using System;
using SharpShader.HLSLCrossCompiler;
using Xunit;

namespace SharpShader.Tests
{
    /// <summary>
    /// Host-side SharpShader Raw HLSL compile smoke for Neural General (SM 6.6 wave intrinsics).
    /// ML Binary cook is outside SharpShader (ADR-0046 / ADR-0052).
    /// </summary>
    [Trait("SharpNeuralHostContract", "General")]
    [Trait("Category", "SharpNeuralGeneralCompile")]
    public sealed class SharpNeuralGeneralShaderCompileTests
    {
        private const string WaveIntrinsicsSource = @"
RWStructuredBuffer<float> Output : register(u0);

[numthreads(32, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    float value = (float)id.x;
    uint lane = WaveGetLaneIndex();
    uint count = WaveGetLaneCount();
    float sum = WaveActiveSum(value);
    float broadcast = WaveReadLaneAt(value, 0);
    if (id.x == 0)
        Output[0] = sum + broadcast + (float)lane + (float)count;
}
";

        [Fact]
        public void WaveIntrinsics_Sm66_ShouldCompileToSpirV()
        {
            ShaderCompilerCapabilities capabilities = ShaderCompilerCapabilities.Probe();
            Assert.True(capabilities.IsAnyDxcAvailable, "Native DXC must be available for Neural General compile smoke.");
            Assert.True(
                capabilities.IsProfileSupported(ShaderStageKind.Compute, new ShaderModelVersion(6, 6)),
                "cs_6_6 must be available for Neural General compile smoke.");

            ShaderCompileResult result = global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(CreateGeneralRequest(ShaderTargetKind.SpirV));

            Assert.NotEmpty(result.Bytecode);
        }

        [Fact]
        public void WaveIntrinsics_Sm66_ShouldCompileToDxil_OnWindows()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            ShaderCompilerCapabilities capabilities = ShaderCompilerCapabilities.Probe();
            Assert.True(capabilities.IsAnyDxcAvailable, "Native DXC must be available for Neural General DXIL compile smoke.");
            Assert.True(
                capabilities.IsProfileSupported(ShaderStageKind.Compute, new ShaderModelVersion(6, 6)),
                "cs_6_6 must be available for Neural General DXIL compile smoke.");

            ShaderCompileResult result = global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(CreateGeneralRequest(ShaderTargetKind.Dxil));

            Assert.NotEmpty(result.Bytecode);
        }

        private static ShaderCompileRequest CreateGeneralRequest(ShaderTargetKind target) =>
            new ShaderCompileRequest
            {
                Source = WaveIntrinsicsSource,
                SourceName = "SharpNeural.General.WaveIntrinsics.hlsl",
                EntryPoint = "main",
                Stage = ShaderStageKind.Compute,
                ShaderModel = new ShaderModelVersion(6, 6),
                Target = target,
                // Matches GeneralPipelineSet: Wave/subgroup ops need Vulkan 1.1+ for SPIR-V.
                SpirvOptions = target == ShaderTargetKind.SpirV
                    ? new SpirvCompileOptions { TargetEnvironment = "vulkan1.1" }
                    : SpirvCompileOptions.Default,
                Defines = Array.Empty<ShaderDefine>(),
                Enable16BitTypes = false,
                OptimizationLevel = 3,
            };
    }
}
