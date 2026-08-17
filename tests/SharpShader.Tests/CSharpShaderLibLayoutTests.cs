using SharpShader.CSharp.ShaderLib;
using Xunit;

namespace Infinity.Rendering.Tests
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
        public void MathFunctionMap_ContainsFrozenIntrinsics()
        {
            Assert.Contains("dot", SharpShader.CSharp.Frontend.CSharpShaderNameMap.MathFunctions.Keys);
            Assert.Contains("saturate", SharpShader.CSharp.Frontend.CSharpShaderNameMap.MathFunctions.Keys);
            Assert.Contains("mul", SharpShader.CSharp.Frontend.CSharpShaderNameMap.MathFunctions.Keys);
        }
    }
}
