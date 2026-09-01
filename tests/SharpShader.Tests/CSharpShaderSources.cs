namespace Infinity.Rendering.Tests
{
    internal static class CSharpShaderSources
    {
        internal const string Mandelbrot = @"
using Infinity.Mathmatics;
using SharpShader.CSharp.ShaderLib;

public static class MandelbrotShader
{
    [Binding(0, 0)]
    public static RWStructuredBuffer<float4> Output;

    [NumThreads(32, 32, 1)]
    [ComputeShader]
    public static void CSMain([SV.DispatchThreadID] uint2 tid)
    {
        uint2 tsize = new uint2(3200, 2400);
        float4 Color()
        {
            float2 uv = new float2((float)tid.x / (float)tsize.x, (float)tid.y / (float)tsize.y);
            float2 z = new float2(0f, 0f);
            float2 c = new float2(-0.445f, 0.0f) + (uv - new float2(0.5f, 0.5f)) * 2.34f;
            float n = 0.0f;
            for (int i = 0; i < 128; i++)
            {
                z = new float2(z.x * z.x - z.y * z.y, 2.0f * z.x * z.y) + c;
                if (math.dot(z, z) > 2.0f)
                {
                    break;
                }

                n += 1.0f;
            }

            float t = n / 128.0f;
            return new float4(t, t, t, 1.0f);
        }

        Output.Store(tid.x + tid.y * tsize.x, Color());
    }
}
";

        internal const string TextureCard = @"
using Infinity.Mathmatics;
using SharpShader.CSharp.ShaderLib;

public struct Root
{
    public float ColorMultiplier;
    public int bFlipUVX;
    public int bFlipUVY;
}

[StageInOut]
public struct VSOut
{
    public float2 uv;
}

public static class TextureCardShader
{
    [PushConstant]
    public static ConstantBuffer<Root> PushConstants;

    [Binding(0, 0)]
    public static Texture2D<float4> SampledTexture;

    [Binding(0, 0)]
    public static SamplerState TextureSampler;

    [VertexShader(""vs"")]
    public static VSOut Vertex([SV.VertexID] uint vertexIndex, [SV.Position] out float4 position)
    {
        float2 pos = new float2(0.0f, 0.0f);
        float2 uv = new float2(0.0f, 0.0f);
        if (vertexIndex == 0)
        {
            pos = new float2(0.5f, 0.5f);
            uv = new float2(1.0f, 1.0f);
        }
        else if (vertexIndex == 1)
        {
            pos = new float2(-0.5f, -0.5f);
            uv = new float2(0.0f, 0.0f);
        }
        else if (vertexIndex == 2)
        {
            pos = new float2(0.5f, -0.5f);
            uv = new float2(1.0f, 0.0f);
        }
        else if (vertexIndex == 3)
        {
            pos = new float2(0.5f, 0.5f);
            uv = new float2(1.0f, 1.0f);
        }
        else if (vertexIndex == 4)
        {
            pos = new float2(-0.5f, 0.5f);
            uv = new float2(0.0f, 1.0f);
        }
        else
        {
            pos = new float2(-0.5f, -0.5f);
            uv = new float2(0.0f, 0.0f);
        }

        VSOut output;
        output.uv = uv;
        position = new float4(pos.x, pos.y, 0.0f, 1.0f);
        return output;
    }

    [FragmentShader(""fs"")]
    public static void Fragment(VSOut psIn, [SV.Target(0)] out float4 color)
    {
        float2 uv = psIn.uv;
        if (PushConstants.Value.bFlipUVX != 0)
        {
            uv.x = 1.0f - uv.x;
        }

        if (PushConstants.Value.bFlipUVY != 0)
        {
            uv.y = 1.0f - uv.y;
        }

        color = SampledTexture.Sample(TextureSampler, uv) * PushConstants.Value.ColorMultiplier;
    }
}
";

        internal const string RayQuery = @"
using Infinity.Mathmatics;
using SharpShader.CSharp.ShaderLib;

public static class RayQueryShader
{
    [Binding(0, 0)]
    public static RWStructuredBuffer<float4> OutputColor;

    [Binding(1, 0)]
    public static RaytracingAccelerationStructure AS;

    [NumThreads(32, 32, 1)]
    [ComputeShader]
    public static void CSMain([SV.DispatchThreadID] uint3 tid)
    {
        RayDesc ray = RayDesc.From(
            new float3((float)tid.x / 3200.0f, (float)tid.y / 2400.0f, 100.0f),
            new float3(0.0f, 0.0f, -1.0f),
            0.01f,
            9999.0f);
        RayQuery query = new RayQuery(RayQueryFlags.AcceptFirstAndEndSearch);
        query.TraceRayInline(AS, 0xff, ray);
        query.Proceed();
        float4 color = new float4(0.0f, 0.0f, 0.0f, 1.0f);
        if (query.CommittedStatus() == HitStatus.HitTriangle)
        {
            float2 bary = query.CommittedTriangleBarycentrics<float2>();
            color = new float4(bary.x, bary.y, 1.0f, 1.0f);
        }

        OutputColor.Store(tid.x + tid.y * 3200u, color);
    }
}
";

        internal const string WaveActiveSum = @"
using Infinity.Mathmatics;
using SharpShader.CSharp.ShaderLib;

public static class WaveActiveSumShader
{
    [Binding(0, 0)]
    public static RWStructuredBuffer<float> Output;

    [NumThreads(32, 1, 1)]
    [ComputeShader]
    public static void CSMain([SV.DispatchThreadID] uint3 tid)
    {
        float value = (float)tid.x;
        Output.Store(tid.x, Hlsl.WaveActiveSum(value));
    }
}
";

        internal const string WriteConstant = @"
using Infinity.Mathmatics;
using SharpShader.CSharp.ShaderLib;

public static class WriteConstantShader
{
    [Binding(0, 0)]
    public static RWStructuredBuffer<uint> Output;

    [NumThreads(1, 1, 1)]
    [ComputeShader]
    public static void CSMain([SV.DispatchThreadID] uint3 tid)
    {
        Output.Store(0, 41u);
    }
}
";
    }
}
