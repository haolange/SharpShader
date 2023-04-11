using System;
using System.IO;
using System.Numerics;
using Infinity.Mathmatics;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Linq;
using TerraFX.Interop.Windows;
using TerraFX.Interop.DirectX;

/*namespace Infinity.Shaderlib
{
    public enum EShaderlabCullMode : byte
    {
        Front = 0,
        Back = 1,
        None = 2
    };

    public enum EShaderlabComparator : byte
    {
        Less = 0,
        Greater = 1,
        LEqual = 2,
        GEqual = 3,
        NotEqual = 4,
        Always = 5,
        Never = 6
    };

    public enum EShaderlabZWriteMode : byte
    {
        On = 0,
        Off = 1
    };

    public enum EShaderlabChannel : byte
    {
        Off = 0,
        R = 1,
        G = 2,
        B = 4,
        A = 8,
    };

    public enum EShaderlabStateType : byte
    {
        CullMode = 0,
        ZTest = 1,
        ZWriteMode = 2,
        ColorMask = 3
    };

    public enum EShaderlabPropertyType
    {
        Int = 0,
        Float = 1,
        Vector = 2,
    };

    public struct ShaderlabPropertyValue
    {
        public string Name;
        public int IntParam;
        public float FloatParam;
        public float4 VectorParam;
    };

    public struct ShaderlabProperty
    {
        public float2 Range;
        public ShaderlabPropertyValue Value;
        public string ParamName;
        public string DisplayName;
        public List<string> Metas;
    };

    public struct ShaderlabCommonState
    {
        public EShaderlabCullMode CullMode;
        public EShaderlabComparator ZTestMode;
        public EShaderlabZWriteMode ZWriteMode;
        public EShaderlabChannel ColorMaskChannel;
    };

    // Same as Pass in unity
    public struct ShaderlabKernel
    {
        public string HlslCode;
        public ShaderlabCommonState CommonState;
        public Dictionary<string, string> Tags;
    };

    // Same as SubShader in unity, we assusme there is always only one category in one shader file
    public struct ShaderlabCategory
    {
        public List<ShaderlabKernel> Kernels;
        public Dictionary<string, string> Tags;
    };

    // ShaderLab is designed like unity shaderlab
    public struct Shaderlab
    {
        public string Name;
        public ShaderlabCategory Category;
        public uint PropertyCapacity;
        public List<uint> Offsets;
        public List<ShaderlabProperty> Properties;
    };
}*/

namespace Infinity.Shaderlib
{
    public enum EShaderlabBlendOp
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

    public enum EShaderlabCullMode
    {
        Unknown = -1,
        CullOff = 0,
        CullFront,
        CullBack,
        CullFrontAndBack,
        Undefined
    };

    public enum EShaderlabStencilOp
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

    public enum EShaderlabBlendMode
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

    public enum EShaderlabShaderStage
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

    public enum EShaderlabPropertyType
    {
        Int,
        Float,
        Range,
        Color,
        Vector,
        Texture,
        Undefined
    };

    public enum EShaderlabShaderTarget
    {
        ShaderTargetHLSL,
        ShaderTargetVulkan,
        ShaderTargetMetalIOS,
        ShaderTargetMetalMac,
        Undefined
    };

    public enum EShaderlabColorWriteMask
    {
        ColorWriteNone = 0,
        ColorWriteA = 1,
        ColorWriteB = 2,
        ColorWriteG = 4,
        ColorWriteR = 8,
        ColorWriteAll = ColorWriteR | ColorWriteG | ColorWriteB | ColorWriteA,
        Undefined
    };

    public enum EShaderlabCompareFunction
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

    public enum EShaderlabTextureDimension
    {
        Tex2D,
        Tex2DArray,
        Tex3D,
        TexCube,
        Undefined
    };

    public class Shaderlab : IDisposable
    {
        public string Name
        {
            get { return m_Name; }
            set { m_Name = value; }
        }
        public ShaderlabCategory Category
        {
            get { return m_Category; }
            set { m_Category = value; }
        }
        public List<ShaderlabProperty> Properties
        {
            get { return m_Properties; }
            set { m_Properties = value; }
        }

        private string m_Name;
        private ShaderlabCategory m_Category;
        private List<ShaderlabProperty> m_Properties;

        public void Dispose()
        {
            m_Category.Dispose();
        }
    }

    public struct ShaderlabPass
    {
        public string? Name
        {
            get 
            {
                Tags.TryGetValue("Name", out string? name);
                return name;
            }
        }
        public ShaderlabProgram Program
        {
            get { return m_Program; }
            set { m_Program = value; }
        }
        public ShaderlabRenderState? State
        {
            get { return m_State; }
            set { m_State = value; }
        }
        public Dictionary<string, string> Tags
        {
            get { return m_Tags; }
            set { m_Tags = value; }
        }

        private ShaderlabProgram m_Program;
        private ShaderlabRenderState? m_State;
        private Dictionary<string, string> m_Tags;

        public ShaderlabPass(in int capcity = 4)
        {
            m_Tags = new Dictionary<string, string>(capcity);
        }
    };

    public struct ShaderlabProgram
    {
        public string Source
        {
            get { return m_Source; }
            set { m_Source = value; }
        }

        private string m_Source;
    };

    public struct ShaderlabCategory : IDisposable
    {
        public List<ShaderlabPass> Passes
        {
            get { return m_Passes; }
            set { m_Passes = value; }
        }
        public Dictionary<string, string> Tags
        {
            get { return m_Tags; }
            set { m_Tags = value; }
        }

        private List<ShaderlabPass> m_Passes;
        private Dictionary<string, string> m_Tags;

        public ShaderlabCategory(in int capcity = 3)
        {
            m_Passes = new List<ShaderlabPass>(capcity);
            m_Tags = new Dictionary<string, string>(capcity);
        }

        public void Dispose()
        {

        }
    };

    public struct ShaderlabStencilOp
    {
        public ShaderlabFloatProperty Comp
        {
            get { return m_Comp; }
            set { m_Comp = value; }
        }
        public ShaderlabFloatProperty Pass
        {
            get { return m_Pass; }
            set { m_Pass = value; }
        }
        public ShaderlabFloatProperty Fail
        {
            get { return m_Fail; }
            set { m_Fail = value; }
        }
        public ShaderlabFloatProperty ZFail
        {
            get { return m_ZFail; }
            set { m_ZFail = value; }
        }

        private ShaderlabFloatProperty m_Comp;
        private ShaderlabFloatProperty m_Pass;
        private ShaderlabFloatProperty m_Fail;
        private ShaderlabFloatProperty m_ZFail;
    };

    public struct ShaderlabProperty
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
        public EShaderlabPropertyType Type
        {
            get { return m_Type; }
            set { m_Type = value; }
        }
        public Vector4? ValueProperty
        {
            get { return m_ValueProperty; }
            set { m_ValueProperty = value; }
        }
        public ShaderlabTextureProperty? TextureProperty
        {
            get { return m_TextureProperty; }
            set { m_TextureProperty = value; }
        }

        private string m_DisplayName;
        private string m_PropertyName;
        private List<string>? m_Attributes;
        private EShaderlabPropertyType m_Type;
        private Vector4? m_ValueProperty;
        private ShaderlabTextureProperty? m_TextureProperty;

        public ShaderlabProperty(string displayName, string propertyName, List<string>? attributes, in EShaderlabPropertyType type, in Vector4 valueProperty)
        {
            m_DisplayName = displayName;
            m_PropertyName = propertyName;
            m_Attributes = attributes;
            m_Type = type;
            m_ValueProperty = valueProperty;
            m_TextureProperty = null;
        }

        public ShaderlabProperty(string displayName, string propertyName, List<string>? attributes, in EShaderlabPropertyType type, in ShaderlabTextureProperty textureProperty)
        {
            m_DisplayName = displayName;
            m_PropertyName = propertyName;
            m_Attributes = attributes;
            m_Type = type;
            m_ValueProperty = null;
            m_TextureProperty = textureProperty;
        }
    };

    public struct ShaderlabRenderState
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
        public ShaderlabStencilOp? StencilOp
        {
            get { return m_StencilOp; }
            set { m_StencilOp = value; }
        }
        public ShaderlabStencilOp? StencilOpBack
        {
            get { return m_StencilOpBack; }
            set { m_StencilOpBack = value; }
        }
        public ShaderlabStencilOp? StencilOpFront
        {
            get { return m_StencilOpFront; }
            set { m_StencilOpFront = value; }
        }
        public ShaderlabFloatProperty? ColorMask
        {
            get { return m_ColorMask; }
            set { m_ColorMask = value; }
        }
        public ShaderlabFloatProperty? AlphaToMask
        {
            get { return m_AlphaToMask; }
            set { m_AlphaToMask = value; }
        }
        public ShaderlabFloatProperty? OffsetFactor
        {
            get { return m_OffsetFactor; }
            set { m_OffsetFactor = value; }
        }
        public ShaderlabFloatProperty? OffsetUnits
        {
            get { return m_OffsetUnits; }
            set { m_OffsetUnits = value; }
        }
        public ShaderlabFloatProperty? BlendOp
        {
            get { return m_BlendOp; }
            set { m_BlendOp = value; }
        }
        public ShaderlabFloatProperty? BlendOpAlpha
        {
            get { return m_BlendOpAlpha; }
            set { m_BlendOpAlpha = value; }
        }
        public ShaderlabFloatProperty? SrcBlend
        {
            get { return m_SrcBlend; }
            set { m_SrcBlend = value; }
        }
        public ShaderlabFloatProperty? DstBlend
        {
            get { return m_DstBlend; }
            set { m_DstBlend = value; }
        }
        public ShaderlabFloatProperty? SrcBlendAlpha
        {
            get { return m_SrcBlendAlpha; }
            set { m_SrcBlendAlpha = value; }
        }
        public ShaderlabFloatProperty? DstBlendAlpha
        {
            get { return m_DstBlendAlpha; }
            set { m_DstBlendAlpha = value; }
        }
        public ShaderlabFloatProperty? StencilRef
        {
            get { return m_StencilRef; }
            set { m_StencilRef = value; }
        }
        public ShaderlabFloatProperty? StencilReadMask
        {
            get { return m_StencilReadMask; }
            set { m_StencilReadMask = value; }
        }
        public ShaderlabFloatProperty? StencilWriteMask
        {
            get { return m_StencilWriteMask; }
            set { m_StencilWriteMask = value; }
        }

        private int? m_Cull;
        private int? m_ZTest;
        private int? m_ZWrite;
        private ShaderlabStencilOp? m_StencilOp;
        private ShaderlabStencilOp? m_StencilOpBack;
        private ShaderlabStencilOp? m_StencilOpFront;
        private ShaderlabFloatProperty? m_ColorMask;
        private ShaderlabFloatProperty? m_AlphaToMask;
        private ShaderlabFloatProperty? m_OffsetFactor;
        private ShaderlabFloatProperty? m_OffsetUnits;
        private ShaderlabFloatProperty? m_BlendOp;
        private ShaderlabFloatProperty? m_BlendOpAlpha;
        private ShaderlabFloatProperty? m_SrcBlend;
        private ShaderlabFloatProperty? m_DstBlend;
        private ShaderlabFloatProperty? m_SrcBlendAlpha;
        private ShaderlabFloatProperty? m_DstBlendAlpha;
        private ShaderlabFloatProperty? m_StencilRef;
        private ShaderlabFloatProperty? m_StencilReadMask;
        private ShaderlabFloatProperty? m_StencilWriteMask;

        public ShaderlabRenderState(in int cull, in int zTest, in int zWrite)
        {
            //this = new ShaderlabRenderState();
            m_Cull = cull;
            m_ZTest = zTest;
            m_ZWrite = zWrite;
        }
    };

    public struct ShaderlabFloatProperty
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

    public struct ShaderlabVectorProperty
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

    public struct ShaderlabTextureProperty
    {
        public string Name
        {
            get { return m_Name; }
            set { m_Name = value; }
        }
        public EShaderlabTextureDimension Dimension
        {
            get { return m_Dimension; }
            set { m_Dimension = value; }
        }

        private string m_Name;
        private EShaderlabTextureDimension m_Dimension;

        public ShaderlabTextureProperty(string name, in EShaderlabTextureDimension dimension)
        {
            m_Name = name;
            Dimension= dimension;
        }
    };

    public static class ShaderlabUtility
    {
        public static Shaderlab ParseShaderlabFromFile(string filePath)
        {
            string source = File.ReadAllText(filePath);
            return ParseShaderlabFromSource(source);

            /*List<ShaderlabProperties> properties = new List<ShaderlabProperties>(6)
            {
                new ShaderlabProperties("AlbedoMap", "_AlbedoMap", null, EShaderlabPropertyType.Texture, new ShaderlabTextureProperty("white", EShaderlabTextureDimension.Tex2D)),
                new ShaderlabProperties("AlbedoColor", "_AlbedoColor", null, EShaderlabPropertyType.Color, new Vector4(1, 0.25f, 0.2f, 1)),
                new ShaderlabProperties("IntValue", "_IntValue", null, EShaderlabPropertyType.Int, new Vector4(233, 0, 0, 0)),
                new ShaderlabProperties("SpecularValue", "_SpecularValue", null, EShaderlabPropertyType.Range, new Vector4(0.5f, 0, 1, 0)),
                new ShaderlabProperties("MetallicValue", "_MetallicValue", null, EShaderlabPropertyType.Range, new Vector4(1, 0, 1, 0)),
                new ShaderlabProperties("RoughnessValue", "_RoughnessValue", null, EShaderlabPropertyType.Range, new Vector4(0.66f, 0, 1, 0)),
            };
            return shaderLab;*/
        }

        public static Shaderlab ParseShaderlabFromSource(string source)
        {
            Shaderlab shaderlab = new Shaderlab();
            shaderlab.Name = ParseShaderlabName(source);
            shaderlab.Category = ParseShaderlabCategory(source);
            shaderlab.Properties = ParseShaderlabProperties(source);
            return shaderlab;
        }

        internal static string ParseShaderlabName(string source)
        {
            Regex regex = new Regex(@"Shader "".+""");
            foreach (Match match in regex.Matches(source))
            {
                int count = match.Value.Length;
                return match.Value.Substring(8, count - 9);
            }
            throw new NotImplementedException("Shaderlab name is illegal");
        }

        internal static ShaderlabCategory ParseShaderlabCategory(string source)
        {
            string categoryPattern = @"(?<=\b(Category)\b)\s*{(?:[^{}]+|(?<open>{)|(?<-open>})|(?:(?<close-open>)[^{}]*)+)*(?(open)(?!))}";
            MatchCollection categoryMatches = Regex.Matches(source, categoryPattern, RegexOptions.Singleline);

            ShaderlabCategory shaderlabCategory = new ShaderlabCategory(2);

            foreach (Match categoryMatche in categoryMatches)
            {
                string categoryBlock = categoryMatche.Value;

                //string tagsPattern = @"Tags\s*{[^}]*\}(?!Pass)";
                string tagsPattern = @"(?<=Category\s*\{\s*Tags\s*\{\s*)((?:(?:(?!Pass\s*\{)(?!\}\s*\}).)*\n?)*)\}\s*(?=(?:Pass\s*\{|}))";
                MatchCollection tagsMatches = Regex.Matches(source, tagsPattern, RegexOptions.Singleline);
                foreach (Match tagsMatche in tagsMatches)
                {
                    string tagsBlock = tagsMatche.Value;

                    // 使用正则表达式匹配键和值
                    string pattern = @"\""(.*?)\""\s*=\s*\""(.*?)\""(?=\s|}|$)";
                    MatchCollection matches = Regex.Matches(tagsBlock, pattern, RegexOptions.Singleline);

                    // 解析每个键值对
                    foreach (Match match in matches)
                    {
                        string key = match.Groups[1].Value;
                        string value = match.Groups[2].Value;
                        shaderlabCategory.Tags.Add(key, value);
                    }
                }

                string passPattern = @"(?<=Pass\s*\{\s*)(?>[^{}]+|\{(?:[^{}]+|\{(?:[^{}]+|\{[^{}]*\})*\})*\})*(?=\s*\})";
                MatchCollection passMatches = Regex.Matches(categoryBlock, passPattern, RegexOptions.Singleline);
                foreach (Match passMatche in passMatches)
                {
                    string passBlock = passMatche.Value;
                    ShaderlabPass shaderlabPass = new ShaderlabPass(2);

                    string passTagsPattern = @"(?<=Tags\s*{\s*)(?:(?!Pass\s*{)[^}])*(?=})";
                    MatchCollection passTagsMatches = Regex.Matches(passBlock, passTagsPattern, RegexOptions.Singleline);
                    foreach (Match passTagsMatche in passTagsMatches)
                    {
                        string passTagsBlock = passTagsMatche.Value;

                        // 使用正则表达式匹配键和值
                        string pattern = @"\""(.*?)\""\s*=\s*\""(.*?)\""(?=\s|$)";
                        MatchCollection matches = Regex.Matches(passTagsBlock, pattern, RegexOptions.Singleline);

                        // 解析每个键值对
                        foreach (Match match in matches)
                        {
                            string key = match.Groups[1].Value;
                            string value = match.Groups[2].Value;
                            shaderlabPass.Tags.Add(key, value);
                        }
                    }

                    string programPattern = @"(?<=HLSLPROGRAM\s).*?(?=\s*ENDHLSL)";
                    MatchCollection programMatches = Regex.Matches(passBlock, programPattern, RegexOptions.Singleline);
                    foreach (Match programMatche in programMatches)
                    {
                        string programBlock = programMatche.Value;
                        shaderlabPass.Program = new ShaderlabProgram
                        {
                            Source = programBlock,
                        };
                        
                    }
                    shaderlabCategory.Passes.Add(shaderlabPass);
                }
            }

            return shaderlabCategory;
        }

        internal static List<ShaderlabProperty> ParseShaderlabProperties(string source)
        {
            string propertiesBlock;
            string pattern = @"(?<=\b(Properties)\b)\s*{(?:[^{}]+|(?<open>{)|(?<-open>})|(?:(?<close-open>)[^{}]*)+)*(?(open)(?!))}";
            MatchCollection matches = Regex.Matches(source, pattern, RegexOptions.Singleline);

            foreach (Match match in matches)
            {
                propertiesBlock = match.Value;
                propertiesBlock = propertiesBlock.Trim(); // 去除字符串前后的空白字符
                propertiesBlock = Regex.Replace(propertiesBlock, @"^\s+", "", RegexOptions.Multiline); // 删除每一行开头的缩进
                propertiesBlock = propertiesBlock.Remove(0, 1);
                propertiesBlock = propertiesBlock.Remove(propertiesBlock.Length - 1, 1);
            }

            return null;
        }
    }
}