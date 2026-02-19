using System;
using System.Collections.Generic;
using System.Numerics;

namespace SharpShader.ShaderLab
{
    public enum EShaderLabBlendOp
    {
        BlendOpAdd = 0,
        BlendOpSub,
        BlendOpRevSub,
        BlendOpMin,
        BlendOpMax,
        BlendOpLogicalClear,
        BlendOpLogicalSet,
        BlendOpLogicalCopy,
        BlendOpLogicalCopyInverted,
        BlendOpLogicalNoop,
        BlendOpLogicalInvert,
        BlendOpLogicalAnd,
        BlendOpLogicalNand,
        BlendOpLogicalOr,
        BlendOpLogicalNor,
        BlendOpLogicalXor,
        BlendOpLogicalEquiv,
        BlendOpLogicalAndReverse,
        BlendOpLogicalAndInverted,
        BlendOpLogicalOrReverse,
        BlendOpLogicalOrInverted,
        Undefined,
    }

    public enum EShaderLabCullMode
    {
        Unknown = -1,
        CullOff = 0,
        CullFront,
        CullBack,
        CullFrontAndBack,
        Undefined
    }

    public enum EShaderLabStencilOp
    {
        StencilOpKeep = 0,
        StencilOpZero,
        StencilOpReplace,
        StencilOpIncrSat,
        StencilOpDecrSat,
        StencilOpInvert,
        StencilOpIncrWrap,
        StencilOpDecrWrap,
        Undefined
    }

    public enum EShaderLabBlendMode
    {
        BlendZero = 0,
        BlendOne,
        BlendDstColor,
        BlendSrcColor,
        BlendOneMinusDstColor,
        BlendSrcAlpha,
        BlendOneMinusSrcColor,
        BlendDstAlpha,
        BlendOneMinusDstAlpha,
        BlendSrcAlphaSaturate,
        BlendOneMinusSrcAlpha,
        Undefined
    }

    public enum EShaderLabShaderStage
    {
        ProgramVertex = 0,
        ProgramFragment,
        ProgramMesh,
        ProgramTask,
        ProgramCompute,
        ProgramRayGen,
        ProgramRayInt,
        ProgramRayAHit,
        ProgramRayCHit,
        ProgramRayMiss,
        ProgramRayRcall,
        Undefined
    }

    [Flags]
    public enum EShaderLabStageMask
    {
        None = 0,
        Vertex = 1 << 0,
        Fragment = 1 << 1,
        Mesh = 1 << 2,
        Task = 1 << 3,
        Compute = 1 << 4,
        RayGen = 1 << 5,
        RayIntersection = 1 << 6,
        RayAnyHit = 1 << 7,
        RayClosestHit = 1 << 8,
        RayMiss = 1 << 9,
        RayCallable = 1 << 10,
    }

    public enum EShaderLabPropertyType
    {
        Int,
        Float,
        Range,
        Color,
        Vector,
        Texture,
        Undefined
    }

    public enum EShaderLabShaderTarget
    {
        ShaderTargetHLSL,
        ShaderTargetVulkan,
        ShaderTargetMetalIOS,
        ShaderTargetMetalMac,
        Undefined
    }

    public enum EShaderLabColorWriteMask
    {
        ColorWriteNone = 0,
        ColorWriteA = 1,
        ColorWriteB = 2,
        ColorWriteG = 4,
        ColorWriteR = 8,
        ColorWriteAll = ColorWriteR | ColorWriteG | ColorWriteB | ColorWriteA,
        Undefined
    }

    public enum EShaderLabCompareFunction
    {
        FuncDisabled = 0,
        FuncNever,
        FuncLess,
        FuncEqual,
        FuncLEqual,
        FuncGreater,
        FuncNotEqual,
        FuncGEqual,
        FuncAlways,
        Undefined
    }

    public enum EShaderLabTextureDimension
    {
        Tex2D,
        Tex2DArray,
        Tex3D,
        TexCube,
        Undefined
    }

    public enum EShaderLabRegisterType
    {
        None = 0,
        RegisterB,
        RegisterT,
        RegisterS,
        RegisterU,
    }

    public enum EShaderLabBindType
    {
        Sampler,
        Buffer,
        AccelStruct,
        StorageBuffer,
        UniformBuffer,
        Texture2D,
        Texture2DMS,
        Texture2DArray,
        Texture2DArrayMS,
        TextureCube,
        TextureCubeArray,
        Texture3D,
        StorageTexture2D,
        StorageTexture2DMS,
        StorageTexture2DArray,
        StorageTexture2DArrayMS,
        StorageTextureCube,
        StorageTextureCubeArray,
        StorageTexture3D,
        Unknown,
    }

    public enum EShaderLabResourceSourceKind
    {
        Unknown = 0,
        ConstantBuffer,
        Texture,
        Sampler,
        Buffer,
        AccelerationStructure,
    }

    public sealed class ShaderLab : IDisposable
    {
        public string Name { get; set; } = string.Empty;
        public ShaderLabCategory Category { get; set; } = new ShaderLabCategory();
        public List<ShaderLabProperty> Properties { get; set; } = new List<ShaderLabProperty>();

        public void Dispose()
        {
        }
    }

    public sealed class ShaderLabPass
    {
        public string? Name
        {
            get
            {
                return Tags.TryGetValue("Name", out string? name) ? name : null;
            }
        }

        public ShaderLabProgram Program { get; set; } = new ShaderLabProgram();
        public ShaderLabRenderState? State { get; set; }
        public Dictionary<string, string> Tags { get; set; }

        public ShaderLabPass(int capacity = 4)
        {
            Tags = new Dictionary<string, string>(capacity, StringComparer.Ordinal);
        }
    }

    public sealed class ShaderLabProgram
    {
        public string Source { get; set; } = string.Empty;
        public List<ShaderLabProgramEntry> Entries { get; set; } = new List<ShaderLabProgramEntry>();
        public List<ShaderLabResourceBinding> Bindings { get; set; } = new List<ShaderLabResourceBinding>();
        public List<ShaderLabConstantBuffer> ConstantBuffers { get; set; } = new List<ShaderLabConstantBuffer>();
    }

    public struct ShaderLabProgramEntry
    {
        public EShaderLabShaderStage Stage;
        public string EntryName;
    }

    public struct ShaderLabResourceBinding
    {
        public string Name;
        public EShaderLabBindType BindType;
        public EShaderLabRegisterType RegisterType;
        public int Slot;
        public int Space;
        public EShaderLabStageMask StageMask;
        public EShaderLabResourceSourceKind SourceKind;
        public string TypeName;
    }

    public struct ShaderLabConstantMember
    {
        public string TypeName;
        public string Name;
        public int ArraySize;
    }

    public sealed class ShaderLabConstantBuffer
    {
        public string Name { get; set; } = string.Empty;
        public int Slot { get; set; } = -1;
        public int Space { get; set; } = -1;
        public List<ShaderLabConstantMember> Members { get; set; } = new List<ShaderLabConstantMember>();
    }

    public sealed class ShaderLabCategory : IDisposable
    {
        public List<ShaderLabPass> Passes { get; set; }
        public Dictionary<string, string> Tags { get; set; }

        public ShaderLabCategory(int capacity = 3)
        {
            Passes = new List<ShaderLabPass>(capacity);
            Tags = new Dictionary<string, string>(capacity, StringComparer.Ordinal);
        }

        public void Dispose()
        {
        }
    }

    public sealed class ShaderLabStencilOp
    {
        public ShaderLabFloatProperty Comp { get; set; }
        public ShaderLabFloatProperty Pass { get; set; }
        public ShaderLabFloatProperty Fail { get; set; }
        public ShaderLabFloatProperty ZFail { get; set; }
    }

    public sealed class ShaderLabProperty
    {
        public string DisplayName { get; set; } = string.Empty;
        public string PropertyName { get; set; } = string.Empty;
        public List<string>? Attributes { get; set; }
        public EShaderLabPropertyType Type { get; set; }
        public Vector4? ValueProperty { get; set; }
        public ShaderLabTextureProperty? TextureProperty { get; set; }
        public float? RangeMin { get; set; }
        public float? RangeMax { get; set; }

        public ShaderLabProperty()
        {
        }

        public ShaderLabProperty(string displayName, string propertyName, List<string>? attributes, EShaderLabPropertyType type, Vector4 valueProperty)
        {
            DisplayName = displayName;
            PropertyName = propertyName;
            Attributes = attributes;
            Type = type;
            ValueProperty = valueProperty;
            TextureProperty = null;
        }

        public ShaderLabProperty(string displayName, string propertyName, List<string>? attributes, EShaderLabPropertyType type, ShaderLabTextureProperty textureProperty)
        {
            DisplayName = displayName;
            PropertyName = propertyName;
            Attributes = attributes;
            Type = type;
            ValueProperty = null;
            TextureProperty = textureProperty;
        }
    }

    public sealed class ShaderLabRenderState
    {
        public int? Cull { get; set; }
        public int? ZTest { get; set; }
        public int? ZWrite { get; set; }
        public ShaderLabStencilOp? StencilOp { get; set; }
        public ShaderLabStencilOp? StencilOpBack { get; set; }
        public ShaderLabStencilOp? StencilOpFront { get; set; }
        public ShaderLabFloatProperty? ColorMask { get; set; }
        public ShaderLabFloatProperty? AlphaToMask { get; set; }
        public ShaderLabFloatProperty? OffsetFactor { get; set; }
        public ShaderLabFloatProperty? OffsetUnits { get; set; }
        public ShaderLabFloatProperty? BlendOp { get; set; }
        public ShaderLabFloatProperty? BlendOpAlpha { get; set; }
        public ShaderLabFloatProperty? SrcBlend { get; set; }
        public ShaderLabFloatProperty? DstBlend { get; set; }
        public ShaderLabFloatProperty? SrcBlendAlpha { get; set; }
        public ShaderLabFloatProperty? DstBlendAlpha { get; set; }
        public ShaderLabFloatProperty? StencilRef { get; set; }
        public ShaderLabFloatProperty? StencilReadMask { get; set; }
        public ShaderLabFloatProperty? StencilWriteMask { get; set; }

        public ShaderLabRenderState()
        {
        }

        public ShaderLabRenderState(int cull, int zTest, int zWrite)
        {
            Cull = cull;
            ZTest = zTest;
            ZWrite = zWrite;
        }
    }

    public struct ShaderLabFloatProperty
    {
        public float Value;
        public string Name;

        public ShaderLabFloatProperty(float value, string name)
        {
            Value = value;
            Name = name;
        }
    }

    public struct ShaderLabVectorProperty
    {
        public float X;
        public float Y;
        public float Z;
        public float W;
        public string Name;
    }

    public struct ShaderLabTextureProperty
    {
        public string Name;
        public EShaderLabTextureDimension Dimension;
        public string DefaultValue;

        public ShaderLabTextureProperty(string name, EShaderLabTextureDimension dimension, string defaultValue = "white")
        {
            Name = name;
            Dimension = dimension;
            DefaultValue = defaultValue;
        }
    }
}
