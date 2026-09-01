using SharpShader.CSharp;
using SharpShader.CSharp.Frontend;
using Xunit;

namespace Infinity.Rendering.Tests
{
    public sealed class CSharpShaderFrontendTests
    {
        [Fact]
        public void Translate_Mandelbrot_EmitsComputeAndLiftedLocalFunction()
        {
            CSharpShaderTranslation translation = Translate(CSharpShaderSources.Mandelbrot);
            Assert.False(translation.HasErrors, Format(translation));
            Assert.Contains("#pragma pack_matrix(column_major)", translation.Hlsl);
            Assert.Contains("RWStructuredBuffer<float4> Output : register(u0, space0);", translation.Hlsl);
            Assert.Contains("[numthreads(32, 32, 1)]", translation.Hlsl);
            Assert.Contains("void CSMain(uint2 tid : SV_DispatchThreadID)", translation.Hlsl);
            Assert.Contains("float4 Color(uint2 tid, uint2 tsize)", translation.Hlsl);
            Assert.Contains("Color(tid, tsize)", translation.Hlsl);
            Assert.Contains("dot(", translation.Hlsl);
            Assert.Contains("Output[", translation.Hlsl);
            Assert.Contains("entries:\n  Compute CSMain", translation.Dump.Replace("\r\n", "\n"));
        }

        [Fact]
        public void Translate_TextureCard_EmitsStageInOutAndPushConstant()
        {
            CSharpShaderTranslation translation = Translate(CSharpShaderSources.TextureCard);
            Assert.False(translation.HasErrors, Format(translation));
            Assert.Contains("struct VSOut", translation.Hlsl);
            Assert.Contains("float2 uv : TEXCOORD0;", translation.Hlsl);
            Assert.Contains("ConstantBuffer<Root> PushConstants : register(b0, space0);", translation.Hlsl);
            Assert.Contains("Texture2D<float4> SampledTexture : register(t0, space0);", translation.Hlsl);
            Assert.Contains("SamplerState TextureSampler : register(s0, space0);", translation.Hlsl);
            Assert.Contains("VSOut vs(uint vertexIndex : SV_VertexID, out float4 position : SV_Position)", translation.Hlsl);
            Assert.Contains("void fs(VSOut psIn, out float4 color : SV_Target0)", translation.Hlsl);
            Assert.Contains(".Sample(", translation.Hlsl);
            Assert.Equal(2, translation.Entries.Count);
        }

        [Fact]
        public void Translate_WaveActiveSum_EmitsHlslWaveIntrinsic()
        {
            CSharpShaderTranslation translation = Translate(CSharpShaderSources.WaveActiveSum);
            Assert.False(translation.HasErrors, Format(translation));
            Assert.Contains("WaveActiveSum(", translation.Hlsl);
            Assert.DoesNotContain("WaveMMA", translation.Hlsl);
            Assert.DoesNotContain("CooperativeMatrix", translation.Hlsl);
            Assert.True(translation.Entries[0].UsesWaveOperations);
            Assert.False(translation.Entries[0].UsesRayQuery);
        }

        [Fact]
        public void Translate_RayQuery_EmitsInlineQuery()
        {
            CSharpShaderTranslation translation = Translate(CSharpShaderSources.RayQuery);
            Assert.False(translation.HasErrors, Format(translation));
            Assert.Contains("RaytracingAccelerationStructure AS : register(t1, space0);", translation.Hlsl);
            Assert.Contains("RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH>", translation.Hlsl);
            Assert.Contains("TraceRayInline(", translation.Hlsl);
            Assert.Contains("COMMITTED_TRIANGLE_HIT", translation.Hlsl);
            Assert.True(translation.Entries[0].UsesRayQuery);
        }

        [Fact]
        public void Translate_NoEntry_ReportsSSCS0001()
        {
            CSharpShaderTranslation translation = Translate("public static class Empty { public static void Main() {} }");
            Assert.True(translation.HasErrors);
            Assert.Contains(translation.Diagnostics, static diagnostic =>
                diagnostic.Id == CSharpShaderDiagnosticIds.NoEntry);
        }

        [Fact]
        public void Translate_TryCatch_ReportsUnsupported()
        {
            const string source = @"
using Infinity.Mathmatics;
using SharpShader.CSharp.ShaderLib;
public static class Bad {
    [Binding(0,0)] public static RWStructuredBuffer<float> Output;
    [NumThreads(1,1,1)] [ComputeShader]
    public static void CSMain() {
        try { Output.Store(0, 1f); } catch { }
    }
}";
            CSharpShaderTranslation translation = Translate(source);
            Assert.Contains(translation.Diagnostics, static diagnostic =>
                diagnostic.Id == CSharpShaderDiagnosticIds.UnsupportedSyntax);
        }

        [Fact]
        public void Translate_GpuLayoutMismatch_ReportsSSCS0006()
        {
            const string source = @"
using Infinity.Mathmatics;
using SharpShader.CSharp.ShaderLib;
[GpuLayout(GpuLayoutKind.ConstantBuffer)]
public struct Packed { public float3 A; public float3 B; }
public static class LayoutShader {
    [Binding(0,0)] public static RWStructuredBuffer<Packed> Output;
    [NumThreads(1,1,1)] [ComputeShader]
    public static void CSMain() { Output.Store(0, default); }
}";
            CSharpShaderTranslation translation = Translate(source);
            Assert.Contains(translation.Diagnostics, static diagnostic =>
                diagnostic.Id == CSharpShaderDiagnosticIds.LayoutMismatch);
        }

        [Fact]
        public void Translate_StructuredGpuLayout_AllowsFloat3Packing()
        {
            const string source = @"
using Infinity.Mathmatics;
using SharpShader.CSharp.ShaderLib;
[GpuLayout(GpuLayoutKind.StructuredBuffer)]
public struct Packed { public float3 Normal; public float Roughness; }
public static class LayoutShader {
    [Binding(0,0)] public static RWStructuredBuffer<Packed> Output;
    [NumThreads(1,1,1)] [ComputeShader]
    public static void CSMain() { Output.Store(0, default); }
}";
            CSharpShaderTranslation translation = Translate(source);
            Assert.False(translation.HasErrors, Format(translation));
            Assert.Contains("struct Packed", translation.Hlsl);
        }

        private static CSharpShaderTranslation Translate(string source)
        {
            return new CSharpShaderTranslator().Translate(
                new CSharpShaderTranslateRequest(
                    source,
                    "test.cs",
                    CSharpShaderReferenceResolver.ResolveDefaultReferences()));
        }

        private static string Format(CSharpShaderTranslation translation)
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            foreach (CSharpShaderDiagnostic diagnostic in translation.Diagnostics)
            {
                builder.Append(diagnostic.Id);
                builder.Append(": ");
                builder.AppendLine(diagnostic.Message);
            }

            builder.AppendLine(translation.Hlsl);
            return builder.ToString();
        }
    }
}
