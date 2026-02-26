using System;

namespace SharpShader.HLSLCrossCompiler.Internal;

internal static class ProbeShaderSourceFactory
{
    public static ShaderCompileRequest CreateProbeRequest(ShaderStageKind stage, ShaderModelVersion shaderModel)
    {
        (string source, string entryPoint, string[] exports) = stage switch
        {
            ShaderStageKind.Vertex => (VertexShaderSource, "main", Array.Empty<string>()),
            ShaderStageKind.Hull => (HullShaderSource, "main", Array.Empty<string>()),
            ShaderStageKind.Domain => (DomainShaderSource, "main", Array.Empty<string>()),
            ShaderStageKind.Geometry => (GeometryShaderSource, "main", Array.Empty<string>()),
            ShaderStageKind.Pixel => (PixelShaderSource, "main", Array.Empty<string>()),
            ShaderStageKind.Compute => (ComputeShaderSource, "main", Array.Empty<string>()),
            ShaderStageKind.Amplification => (AmplificationShaderSource, "main", Array.Empty<string>()),
            ShaderStageKind.Mesh => (MeshShaderSource, "main", Array.Empty<string>()),
            ShaderStageKind.Library => (LibraryShaderSource, string.Empty, new[] { "ExportA", "ExportB" }),
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown stage."),
        };

        return new ShaderCompileRequest
        {
            Source = source,
            EntryPoint = entryPoint,
            Stage = stage,
            ShaderModel = shaderModel,
            Target = ShaderTargetKind.Dxil,
            Exports = exports,
            Enable16BitTypes = shaderModel.Minor >= 2,
            DisableOptimizations = true,
        };
    }

    private const string VertexShaderSource = @"
float4 main(float4 position : POSITION) : SV_Position
{
    return position;
}";

    private const string PixelShaderSource = @"
float4 main() : SV_Target0
{
    return float4(1.0, 0.0, 0.0, 1.0);
}";

    private const string ComputeShaderSource = @"
[numthreads(1, 1, 1)]
void main(uint3 dispatchId : SV_DispatchThreadID)
{
}";

    private const string GeometryShaderSource = @"
struct GSInput
{
    float4 Position : SV_Position;
};

struct GSOutput
{
    float4 Position : SV_Position;
};

[maxvertexcount(1)]
void main(point GSInput input[1], inout PointStream<GSOutput> stream)
{
    GSOutput output;
    output.Position = input[0].Position;
    stream.Append(output);
}";

    private const string HullShaderSource = @"
struct HSControlPoint
{
    float4 Position : POSITION;
};

struct HSConstants
{
    float Edges[3] : SV_TessFactor;
    float Inside : SV_InsideTessFactor;
};

HSConstants HSConstantsFn(InputPatch<HSControlPoint, 3> patch, uint patchId : SV_PrimitiveID)
{
    HSConstants constants;
    constants.Edges[0] = 1.0;
    constants.Edges[1] = 1.0;
    constants.Edges[2] = 1.0;
    constants.Inside = 1.0;
    return constants;
}

[domain(""tri"")]
[partitioning(""integer"")]
[outputtopology(""triangle_cw"")]
[outputcontrolpoints(3)]
[patchconstantfunc(""HSConstantsFn"")]
HSControlPoint main(InputPatch<HSControlPoint, 3> patch, uint index : SV_OutputControlPointID, uint patchId : SV_PrimitiveID)
{
    return patch[index];
}";

    private const string DomainShaderSource = @"
struct HSControlPoint
{
    float4 Position : POSITION;
};

struct HSConstants
{
    float Edges[3] : SV_TessFactor;
    float Inside : SV_InsideTessFactor;
};

[domain(""tri"")]
float4 main(HSConstants constants, const OutputPatch<HSControlPoint, 3> patch, float3 domainLocation : SV_DomainLocation) : SV_Position
{
    return patch[0].Position;
}";

    private const string AmplificationShaderSource = @"
struct Payload
{
    uint Value;
};

[numthreads(1, 1, 1)]
void main(uint3 dispatchId : SV_DispatchThreadID)
{
    Payload payload;
    payload.Value = 0;
    DispatchMesh(1, 1, 1, payload);
}";

    private const string MeshShaderSource = @"
struct MeshVertex
{
    float4 Position : SV_Position;
};

[outputtopology(""triangle"")]
[numthreads(1, 1, 1)]
void main(out vertices MeshVertex verticesOut[3], out indices uint3 primitives[1])
{
    SetMeshOutputCounts(3, 1);

    verticesOut[0].Position = float4(0.0, 0.5, 0.0, 1.0);
    verticesOut[1].Position = float4(0.5, -0.5, 0.0, 1.0);
    verticesOut[2].Position = float4(-0.5, -0.5, 0.0, 1.0);

    primitives[0] = uint3(0, 1, 2);
}";

    private const string LibraryShaderSource = @"
float4 ExportA(float4 position : POSITION) : SV_Position
{
    return position;
}

float4 ExportB(float4 position : POSITION) : SV_Position
{
    return position * 0.5;
}";
}
