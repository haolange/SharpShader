using SharpShader.CSharp.ShaderLib;
using Xunit;

namespace SharpShader.Tests
{
    public sealed class CSharpShaderLibLayoutTests
    {
        [Fact]
        public void ConstantBuffer_Float3ThenFloat_PacksInto16()
        {
            GpuTypeShape shape = GpuLayoutCalculator.Structure(
                "Packed",
                new[]
                {
                    new System.Collections.Generic.KeyValuePair<string, GpuTypeShape>(
                        "Normal",
                        GpuLayoutCalculator.Vector(GpuScalarKind.Float, 3, GpuLayoutKind.ConstantBuffer)),
                    new System.Collections.Generic.KeyValuePair<string, GpuTypeShape>(
                        "Roughness",
                        GpuLayoutCalculator.Scalar(GpuScalarKind.Float)),
                },
                GpuLayoutKind.ConstantBuffer);

            Assert.Equal(16, shape.Size);
            Assert.Equal(0, shape.Fields[0].Offset);
            Assert.Equal(12, shape.Fields[1].Offset);
        }

        [Fact]
        public void StructuredBuffer_Float3ThenFloat_Is16()
        {
            GpuTypeShape shape = GpuLayoutCalculator.Structure(
                "Packed",
                new[]
                {
                    new System.Collections.Generic.KeyValuePair<string, GpuTypeShape>(
                        "Normal",
                        GpuLayoutCalculator.Vector(GpuScalarKind.Float, 3, GpuLayoutKind.StructuredBuffer)),
                    new System.Collections.Generic.KeyValuePair<string, GpuTypeShape>(
                        "Roughness",
                        GpuLayoutCalculator.Scalar(GpuScalarKind.Float)),
                },
                GpuLayoutKind.StructuredBuffer);

            Assert.Equal(16, shape.Size);
            Assert.Equal(0, shape.Fields[0].Offset);
            Assert.Equal(12, shape.Fields[1].Offset);
        }

        [Fact]
        public void HostSequential_Float3_Is12BytesAlign4()
        {
            Assert.Equal(12, GpuLayoutCalculator.HostSequentialSize(GpuScalarKind.Float, 3));
            Assert.Equal(4, GpuLayoutCalculator.HostSequentialAlign(GpuScalarKind.Float, 3));
        }

        [Fact]
        public void MathIntrinsics_ShouldTranslateThroughThePublicFrontend()
        {
            const string source = """
using SharpMath;
using SharpShader.CSharp.ShaderLib;
public static class Intrinsics
{
    [Binding(0, 0)] public static RWStructuredBuffer<float4> Output;
    [NumThreads(1, 1, 1)] [ComputeShader]
    public static void CSMain([SV.DispatchThreadID] uint3 tid)
    {
        float value = math.saturate(math.dot(new float2(1f, 2f), new float2(3f, 4f)));
        Output.Store(tid.x, math.mul(new float4x4(1f), new float4(value)));
    }
}
""";
            var translation = new global::SharpShader.CSharp.Frontend.CSharpShaderTranslator().Translate(
                new global::SharpShader.CSharp.Frontend.CSharpShaderTranslateRequest(
                    source, "intrinsics.cs", global::SharpShader.CSharp.CSharpShaderReferenceResolver.ResolveDefaultReferences()));
            Assert.False(translation.HasErrors, string.Join("\n", translation.Diagnostics.Select(item => item.Message)));
            Assert.Contains("dot(", translation.Hlsl);
            Assert.Contains("saturate(", translation.Hlsl);
            Assert.Contains("mul(", translation.Hlsl);
        }
    }
}
