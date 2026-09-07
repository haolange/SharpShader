using System;
using System.Collections.Generic;
using SharpShader.CSharp.ShaderLib;

namespace SharpShader.CSharp.Frontend
{
    internal static class CSharpShaderNameMap
    {
        internal const string MathNamespace = "SharpMath";
        internal const string ShaderLibNamespace = "SharpShader.CSharp.ShaderLib";

        internal static readonly Dictionary<string, string> MathTypes =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { MathNamespace + ".float2", "float2" },
                { MathNamespace + ".float3", "float3" },
                { MathNamespace + ".float4", "float4" },
                { MathNamespace + ".int2", "int2" },
                { MathNamespace + ".int3", "int3" },
                { MathNamespace + ".int4", "int4" },
                { MathNamespace + ".uint2", "uint2" },
                { MathNamespace + ".uint3", "uint3" },
                { MathNamespace + ".uint4", "uint4" },
                { MathNamespace + ".bool2", "bool2" },
                { MathNamespace + ".bool3", "bool3" },
                { MathNamespace + ".bool4", "bool4" },
                { MathNamespace + ".float2x2", "float2x2" },
                { MathNamespace + ".float3x3", "float3x3" },
                { MathNamespace + ".float3x4", "float3x4" },
                { MathNamespace + ".float4x4", "float4x4" },
            };

        internal static readonly Dictionary<string, string> MathFunctions =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "sin", "sin" },
                { "cos", "cos" },
                { "tan", "tan" },
                { "asin", "asin" },
                { "acos", "acos" },
                { "atan", "atan" },
                { "atan2", "atan2" },
                { "sinh", "sinh" },
                { "cosh", "cosh" },
                { "tanh", "tanh" },
                { "exp", "exp" },
                { "exp2", "exp2" },
                { "log", "log" },
                { "log2", "log2" },
                { "log10", "log10" },
                { "sqrt", "sqrt" },
                { "rsqrt", "rsqrt" },
                { "pow", "pow" },
                { "abs", "abs" },
                { "sign", "sign" },
                { "floor", "floor" },
                { "ceil", "ceil" },
                { "round", "round" },
                { "frac", "frac" },
                { "saturate", "saturate" },
                { "clamp", "clamp" },
                { "lerp", "lerp" },
                { "step", "step" },
                { "smoothstep", "smoothstep" },
                { "min", "min" },
                { "max", "max" },
                { "all", "all" },
                { "any", "any" },
                { "select", "select" },
                { "dot", "dot" },
                { "cross", "cross" },
                { "length", "length" },
                { "normalize", "normalize" },
                { "distance", "distance" },
                { "reflect", "reflect" },
                { "refract", "refract" },
                { "transpose", "transpose" },
                { "determinant", "determinant" },
                { "inverse", "inverse" },
                { "asfloat", "asfloat" },
                { "asint", "asint" },
                { "asuint", "asuint" },
                { "isfinite", "isfinite" },
                { "isinf", "isinf" },
                { "isnan", "isnan" },
                { "countbits", "countbits" },
                { "lzcnt", "firstbithigh" },
                { "tzcnt", "firstbitlow" },
                { "mul", "mul" },
            };

        internal static readonly Dictionary<string, string> Semantics =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { ShaderLibNamespace + ".SV.PositionAttribute", "SV_Position" },
                { ShaderLibNamespace + ".SV.VertexIDAttribute", "SV_VertexID" },
                { ShaderLibNamespace + ".SV.InstanceIDAttribute", "SV_InstanceID" },
                { ShaderLibNamespace + ".SV.DispatchThreadIDAttribute", "SV_DispatchThreadID" },
                { ShaderLibNamespace + ".SV.GroupIDAttribute", "SV_GroupID" },
                { ShaderLibNamespace + ".SV.GroupThreadIDAttribute", "SV_GroupThreadID" },
                { ShaderLibNamespace + ".SV.GroupIndexAttribute", "SV_GroupIndex" },
                { ShaderLibNamespace + ".SV.IsFrontFaceAttribute", "SV_IsFrontFace" },
                { ShaderLibNamespace + ".SV.DepthAttribute", "SV_Depth" },
                { ShaderLibNamespace + ".SV.SampleIndexAttribute", "SV_SampleIndex" },
                { ShaderLibNamespace + ".SV.CoverageAttribute", "SV_Coverage" },
            };

        internal static bool IsBoolVector(string metadataName)
        {
            return metadataName == MathNamespace + ".bool2"
                || metadataName == MathNamespace + ".bool3"
                || metadataName == MathNamespace + ".bool4";
        }

        internal static bool TryHostScalar(string metadataName, out GpuScalarKind kind, out int count)
        {
            kind = GpuScalarKind.Float;
            count = 1;
            if (!MathTypes.ContainsKey(metadataName))
            {
                return false;
            }

            if (metadataName.IndexOf("bool", StringComparison.Ordinal) >= 0)
            {
                kind = GpuScalarKind.Bool;
            }
            else if (metadataName.IndexOf("uint", StringComparison.Ordinal) >= 0)
            {
                kind = GpuScalarKind.UInt;
            }
            else if (metadataName.IndexOf(".int", StringComparison.Ordinal) >= 0)
            {
                kind = GpuScalarKind.Int;
            }
            else
            {
                kind = GpuScalarKind.Float;
            }

            if (metadataName.EndsWith("4x4", StringComparison.Ordinal)
                || metadataName.EndsWith("3x3", StringComparison.Ordinal)
                || metadataName.EndsWith("3x4", StringComparison.Ordinal)
                || metadataName.EndsWith("2x2", StringComparison.Ordinal))
            {
                return false;
            }

            char last = metadataName[metadataName.Length - 1];
            count = last == '2' ? 2 : last == '3' ? 3 : last == '4' ? 4 : 1;
            return true;
        }
    }
}
