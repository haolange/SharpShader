using System;

namespace SharpShader.Compilation
{

    public enum ShaderStageIoBuiltIn : byte
    {
        None,
        Position,
        Color,
        Depth,
        DepthGreaterEqual,
        DepthLessEqual,
        StencilReference,
        PrimitiveId,
        RenderTargetArrayIndex,
        ViewportArrayIndex,
        SampleIndex,
        Coverage,
        ClipDistance,
        CullDistance,
        VertexId,
        InstanceId,
        FrontFace,
        TessellationFactor,
        InsideTessellationFactor,
        Barycentrics,
        ShadingRate,
        CullPrimitive,
        InnerCoverage,
        PointSize,
        PointCoordinate,
        SamplePosition,
        Layer,
        TessellationCoordinate,
        InvocationId,
    }
}
