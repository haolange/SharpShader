using System;
using System.Numerics;
using System.Collections.Generic;

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
    };

    public enum EShaderLabCullMode
    {
        Unknown = -1,
        CullOff = 0,
        CullFront,
        CullBack,
        CullFrontAndBack,
        Undefined
    };

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
    };

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
    };

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
    };

    public enum EShaderLabPropertyType
    {
        Int,
        Float,
        Range,
        Color,
        Vector,
        Texture,
        Undefined
    };

    public enum EShaderLabShaderTarget
    {
        ShaderTargetHLSL,
        ShaderTargetVulkan,
        ShaderTargetMetalIOS,
        ShaderTargetMetalMac,
        Undefined
    };

    public enum EShaderLabColorWriteMask
    {
        ColorWriteNone = 0,
        ColorWriteA = 1,
        ColorWriteB = 2,
        ColorWriteG = 4,
        ColorWriteR = 8,
        ColorWriteAll = ColorWriteR | ColorWriteG | ColorWriteB | ColorWriteA,
        Undefined
    };

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
    };

    public enum EShaderLabTextureDimension
    {
        Tex2D,
        Tex2DArray,
        Tex3D,
        TexCube,
        Undefined
    };

    public class ShaderLab : IDisposable
    {
        public string Name
        {
            get { return m_Name; }
            set { m_Name = value; }
        }
        public ShaderLabCategory Category
        {
            get { return m_Category; }
            set { m_Category = value; }
        }
        public List<ShaderLabProperty> Properties
        {
            get { return m_Properties; }
            set { m_Properties = value; }
        }

        private string m_Name;
        private ShaderLabCategory m_Category;
        private List<ShaderLabProperty> m_Properties;

        public void Dispose()
        {
            m_Category.Dispose();
        }
    }

    public struct ShaderLabPass
    {
        public string? Name
        {
            get 
            {
                Tags.TryGetValue("Name", out string? name);
                return name;
            }
        }
        public ShaderLabProgram Program
        {
            get { return m_Program; }
            set { m_Program = value; }
        }
        public ShaderLabRenderState? State
        {
            get { return m_State; }
            set { m_State = value; }
        }
        public Dictionary<string, string> Tags
        {
            get { return m_Tags; }
            set { m_Tags = value; }
        }

        private ShaderLabProgram m_Program;
        private ShaderLabRenderState? m_State;
        private Dictionary<string, string> m_Tags;

        public ShaderLabPass(in int capcity = 4)
        {
            m_Tags = new Dictionary<string, string>(capcity);
        }
    };

    public struct ShaderLabProgram
    {
        public string Source
        {
            get { return m_Source; }
            set { m_Source = value; }
        }

        private string m_Source;
    };

    public struct ShaderLabCategory : IDisposable
    {
        public List<ShaderLabPass> Passes
        {
            get { return m_Passes; }
            set { m_Passes = value; }
        }
        public Dictionary<string, string> Tags
        {
            get { return m_Tags; }
            set { m_Tags = value; }
        }

        private List<ShaderLabPass> m_Passes;
        private Dictionary<string, string> m_Tags;

        public ShaderLabCategory(in int capcity = 3)
        {
            m_Passes = new List<ShaderLabPass>(capcity);
            m_Tags = new Dictionary<string, string>(capcity);
        }

        public void Dispose()
        {

        }
    };

    public struct ShaderLabStencilOp
    {
        public ShaderLabFloatProperty Comp
        {
            get { return m_Comp; }
            set { m_Comp = value; }
        }
        public ShaderLabFloatProperty Pass
        {
            get { return m_Pass; }
            set { m_Pass = value; }
        }
        public ShaderLabFloatProperty Fail
        {
            get { return m_Fail; }
            set { m_Fail = value; }
        }
        public ShaderLabFloatProperty ZFail
        {
            get { return m_ZFail; }
            set { m_ZFail = value; }
        }

        private ShaderLabFloatProperty m_Comp;
        private ShaderLabFloatProperty m_Pass;
        private ShaderLabFloatProperty m_Fail;
        private ShaderLabFloatProperty m_ZFail;
    };

    public struct ShaderLabProperty
    {
        public string DisplayName
        {
            get { return m_DisplayName; }
            set { m_DisplayName = value; }
        }
        public string PropertyName
        {
            get { return m_PropertyName; }
            set { m_PropertyName = value; }
        }
        public List<string>? Attributes
        {
            get { return m_Attributes; }
            set { m_Attributes = value; }
        }
        public EShaderLabPropertyType Type
        {
            get { return m_Type; }
            set { m_Type = value; }
        }
        public Vector4? ValueProperty
        {
            get { return m_ValueProperty; }
            set { m_ValueProperty = value; }
        }
        public ShaderLabTextureProperty? TextureProperty
        {
            get { return m_TextureProperty; }
            set { m_TextureProperty = value; }
        }

        private string m_DisplayName;
        private string m_PropertyName;
        private List<string>? m_Attributes;
        private EShaderLabPropertyType m_Type;
        private Vector4? m_ValueProperty;
        private ShaderLabTextureProperty? m_TextureProperty;

        public ShaderLabProperty(string displayName, string propertyName, List<string>? attributes, in EShaderLabPropertyType type, in Vector4 valueProperty)
        {
            m_DisplayName = displayName;
            m_PropertyName = propertyName;
            m_Attributes = attributes;
            m_Type = type;
            m_ValueProperty = valueProperty;
            m_TextureProperty = null;
        }

        public ShaderLabProperty(string displayName, string propertyName, List<string>? attributes, in EShaderLabPropertyType type, in ShaderLabTextureProperty textureProperty)
        {
            m_DisplayName = displayName;
            m_PropertyName = propertyName;
            m_Attributes = attributes;
            m_Type = type;
            m_ValueProperty = null;
            m_TextureProperty = textureProperty;
        }
    };

    public struct ShaderLabRenderState
    {
        public int? Cull
        {
            get { return m_Cull; }
            set { m_Cull = value; }
        }
        public int? ZTest
        {
            get { return m_ZTest; }
            set { m_ZTest = value; }
        }
        public int? ZWrite
        {
            get { return m_ZWrite; }
            set { m_ZWrite = value; }
        }
        public ShaderLabStencilOp? StencilOp
        {
            get { return m_StencilOp; }
            set { m_StencilOp = value; }
        }
        public ShaderLabStencilOp? StencilOpBack
        {
            get { return m_StencilOpBack; }
            set { m_StencilOpBack = value; }
        }
        public ShaderLabStencilOp? StencilOpFront
        {
            get { return m_StencilOpFront; }
            set { m_StencilOpFront = value; }
        }
        public ShaderLabFloatProperty? ColorMask
        {
            get { return m_ColorMask; }
            set { m_ColorMask = value; }
        }
        public ShaderLabFloatProperty? AlphaToMask
        {
            get { return m_AlphaToMask; }
            set { m_AlphaToMask = value; }
        }
        public ShaderLabFloatProperty? OffsetFactor
        {
            get { return m_OffsetFactor; }
            set { m_OffsetFactor = value; }
        }
        public ShaderLabFloatProperty? OffsetUnits
        {
            get { return m_OffsetUnits; }
            set { m_OffsetUnits = value; }
        }
        public ShaderLabFloatProperty? BlendOp
        {
            get { return m_BlendOp; }
            set { m_BlendOp = value; }
        }
        public ShaderLabFloatProperty? BlendOpAlpha
        {
            get { return m_BlendOpAlpha; }
            set { m_BlendOpAlpha = value; }
        }
        public ShaderLabFloatProperty? SrcBlend
        {
            get { return m_SrcBlend; }
            set { m_SrcBlend = value; }
        }
        public ShaderLabFloatProperty? DstBlend
        {
            get { return m_DstBlend; }
            set { m_DstBlend = value; }
        }
        public ShaderLabFloatProperty? SrcBlendAlpha
        {
            get { return m_SrcBlendAlpha; }
            set { m_SrcBlendAlpha = value; }
        }
        public ShaderLabFloatProperty? DstBlendAlpha
        {
            get { return m_DstBlendAlpha; }
            set { m_DstBlendAlpha = value; }
        }
        public ShaderLabFloatProperty? StencilRef
        {
            get { return m_StencilRef; }
            set { m_StencilRef = value; }
        }
        public ShaderLabFloatProperty? StencilReadMask
        {
            get { return m_StencilReadMask; }
            set { m_StencilReadMask = value; }
        }
        public ShaderLabFloatProperty? StencilWriteMask
        {
            get { return m_StencilWriteMask; }
            set { m_StencilWriteMask = value; }
        }

        private int? m_Cull;
        private int? m_ZTest;
        private int? m_ZWrite;
        private ShaderLabStencilOp? m_StencilOp;
        private ShaderLabStencilOp? m_StencilOpBack;
        private ShaderLabStencilOp? m_StencilOpFront;
        private ShaderLabFloatProperty? m_ColorMask;
        private ShaderLabFloatProperty? m_AlphaToMask;
        private ShaderLabFloatProperty? m_OffsetFactor;
        private ShaderLabFloatProperty? m_OffsetUnits;
        private ShaderLabFloatProperty? m_BlendOp;
        private ShaderLabFloatProperty? m_BlendOpAlpha;
        private ShaderLabFloatProperty? m_SrcBlend;
        private ShaderLabFloatProperty? m_DstBlend;
        private ShaderLabFloatProperty? m_SrcBlendAlpha;
        private ShaderLabFloatProperty? m_DstBlendAlpha;
        private ShaderLabFloatProperty? m_StencilRef;
        private ShaderLabFloatProperty? m_StencilReadMask;
        private ShaderLabFloatProperty? m_StencilWriteMask;

        public ShaderLabRenderState(in int cull, in int zTest, in int zWrite)
        {
            //this = new ShaderLabRenderState();
            m_Cull = cull;
            m_ZTest = zTest;
            m_ZWrite = zWrite;
        }
    };

    public struct ShaderLabFloatProperty
    {
        public float Value
        {
            get { return m_Value; }
            set { m_Value = value; }
        }
        public string Name
        {
            get { return m_Name; }
            set { m_Name = value; }
        }

        private float m_Value;
        private string m_Name;
    };

    public struct ShaderLabVectorProperty
    {
        public float X
        {
            get { return m_X; }
            set { m_X = value; }
        }
        public float Y
        {
            get { return m_Y; }
            set { m_Y = value; }
        }
        public float Z
        {
            get { return m_Z; }
            set { m_Z = value; }
        }
        public float W
        {
            get { return m_W; }
            set { m_W = value; }
        }
        public string Name
        {
            get { return m_Name; }
            set { m_Name = value; }
        }

        private float m_X;
        private float m_Y;
        private float m_Z;
        private float m_W;
        private string m_Name;
    };

    public struct ShaderLabTextureProperty
    {
        public string Name
        {
            get { return m_Name; }
            set { m_Name = value; }
        }
        public EShaderLabTextureDimension Dimension
        {
            get { return m_Dimension; }
            set { m_Dimension = value; }
        }

        private string m_Name;
        private EShaderLabTextureDimension m_Dimension;

        public ShaderLabTextureProperty(string name, in EShaderLabTextureDimension dimension)
        {
            m_Name = name;
            Dimension= dimension;
        }
    };
}