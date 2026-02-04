using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace SharpShader.ShaderGen
{
    public enum ESemanticType : byte
    {
        Color,
        Position,
        TexCoord,
        Normal,
        Tangent,
        Binormal,
        BlendIndices,
        BlendWeights,
        ShadingRate,
        SV_Position,
        SV_Target,
        Pending
    }

    public class SemanticAttribute : Attribute
    {
        public uint Index { get; }
        public string SemanticName { get; }

        public SemanticAttribute(string semanticName)
        {
            Index = 0;
            SemanticName = semanticName;
        }

        public SemanticAttribute(string semanticName, uint index)
        {
            Index = index;
            SemanticName = semanticName;
        }
    }

    public class RegisterAttribute : Attribute
    {
        public uint TableIndex { get; }
        public uint SlotIndex { get; }

        public RegisterAttribute(uint tableIndex, uint slotIndex)
        {
            SlotIndex = slotIndex;
            TableIndex = tableIndex;
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class VertexShaderAttribute : Attribute
    {
        public VertexShaderAttribute()
        {

        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class FragmentShaderAttribute : Attribute
    {
        public FragmentShaderAttribute()
        {

        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class MeshShaderAttribute : Attribute
    {
        public uint GroupCountX { get; }
        public uint GroupCountY { get; }
        public uint GroupCountZ { get; }

        public MeshShaderAttribute(uint groupCountX, uint groupCountY, uint groupCountZ)
        {
            GroupCountX = groupCountX;
            GroupCountY = groupCountY;
            GroupCountZ = groupCountZ;
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class ComputeShaderAttribute : Attribute
    {
        public uint GroupCountX { get; }
        public uint GroupCountY { get; }
        public uint GroupCountZ { get; }

        public ComputeShaderAttribute(uint groupCountX, uint groupCountY, uint groupCountZ)
        {
            GroupCountX = groupCountX;
            GroupCountY = groupCountY;
            GroupCountZ = groupCountZ;
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class RayGenerationShaderAttribute : Attribute
    {
        public RayGenerationShaderAttribute()
        {

        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class IntersectionShaderAttribute : Attribute
    {
        public IntersectionShaderAttribute()
        {

        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class MissShaderAttribute : Attribute
    {
        public MissShaderAttribute()
        {

        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class AnyHitShaderAttribute : Attribute
    {
        public AnyHitShaderAttribute()
        {

        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class ClosestHitShaderAttribute : Attribute
    {
        public ClosestHitShaderAttribute()
        {

        }
    }

    /*public class TestSample
    {
        public Matrix4x4 Projection;
        public Matrix4x4 View;
        public Matrix4x4 World;

        public struct VertexInput
        {
            [Semantic("Position", 0)] 
            public Vector3 Position;
            [TextureCoordinateSemantic] public Vector2 TextureCoord;
        }

        public struct FragmentInput
        {
            [SystemPositionSemanticAttribute] public Vector4 Position;
            [TextureCoordinateSemantic] public Vector2 TextureCoord;
        }

        [VertexShader]
        public FragmentInput VertexShaderFunc(VertexInput input)
        {
            FragmentInput output;
            Vector4 worldPosition = Mul(World, new Vector4(input.Position, 1));
            Vector4 viewPosition = Mul(View, worldPosition);
            output.Position = Mul(Projection, viewPosition);
            output.TextureCoord = input.TextureCoord;
            return output;
        }

        [FragmentShader]
        public Vector4 FragmentShaderFunc(FragmentInput input)
        {
            return Sample(SurfaceTexture, Sampler, input.TextureCoord);
        }
    }*/
}
