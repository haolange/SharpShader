using System;
using System.Linq;
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

    public enum StandaloneShaderProgramKind
    {
        Compute = 0,
        RayTrace,
    }

    public enum StandaloneShaderStage
    {
        Compute = 0,
        RayGeneration,
        Miss,
        Callable,
        Unknown,
    }

    public sealed class ShaderLab : IDisposable
    {
        public string Name { get; set; } = string.Empty;
        public List<ShaderLabPass> Passes { get; set; } = new List<ShaderLabPass>();
        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public List<ShaderLabProperty> Properties { get; set; } = new List<ShaderLabProperty>();

        public void Dispose()
        {
        }
    }

    public sealed class ShaderKeywordGroup
    {
        public List<string> Keywords { get; set; } = new List<string>();
    }

    public readonly struct ShaderVariantKey : IEquatable<ShaderVariantKey>
    {
        public IReadOnlyList<string> Keywords { get; }

        public ShaderVariantKey(IEnumerable<string> keywords)
        {
            List<string> ordered = keywords
                .Where(static keyword => !string.IsNullOrWhiteSpace(keyword))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static keyword => keyword, StringComparer.Ordinal)
                .ToList();
            Keywords = ordered;
        }

        public bool Equals(ShaderVariantKey other)
        {
            if (Keywords.Count != other.Keywords.Count)
            {
                return false;
            }

            for (int i = 0; i < Keywords.Count; ++i)
            {
                if (!string.Equals(Keywords[i], other.Keywords[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj)
        {
            return obj is ShaderVariantKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            HashCode hash = new HashCode();
            for (int i = 0; i < Keywords.Count; ++i)
            {
                hash.Add(Keywords[i], StringComparer.Ordinal);
            }
            return hash.ToHashCode();
        }

        public override string ToString()
        {
            return Keywords.Count == 0
                ? "<default>"
                : string.Join(';', Keywords);
        }
    }

    public sealed class StandaloneShaderEntry
    {
        public StandaloneShaderStage Stage { get; set; } = StandaloneShaderStage.Unknown;
        public string EntryName { get; set; } = string.Empty;
    }

    public sealed class StandaloneShaderProgram
    {
        public StandaloneShaderProgramKind Kind { get; set; }
        public string SourcePath { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public List<StandaloneShaderEntry> Entries { get; set; } = new List<StandaloneShaderEntry>();
        public List<ShaderKeywordGroup> KeywordGroups { get; set; } = new List<ShaderKeywordGroup>();
        public List<ShaderLabResourceBinding> Bindings { get; set; } = new List<ShaderLabResourceBinding>();
        public List<ShaderLabConstantBuffer> ConstantBuffers { get; set; } = new List<ShaderLabConstantBuffer>();

        public List<ShaderVariantKey> EnumerateVariantKeys()
        {
            List<ShaderVariantKey> result = new List<ShaderVariantKey>();
            if (KeywordGroups.Count == 0)
            {
                result.Add(new ShaderVariantKey(Array.Empty<string>()));
                return result;
            }

            List<string> selected = new List<string>();
            EnumerateRecursive(0, selected, result);
            if (result.Count == 0)
            {
                result.Add(new ShaderVariantKey(Array.Empty<string>()));
            }

            return result;
        }

        private void EnumerateRecursive(int groupIndex, List<string> selectedKeywords, List<ShaderVariantKey> destination)
        {
            if (groupIndex >= KeywordGroups.Count)
            {
                destination.Add(new ShaderVariantKey(selectedKeywords));
                return;
            }

            ShaderKeywordGroup group = KeywordGroups[groupIndex];
            if (group.Keywords.Count == 0)
            {
                EnumerateRecursive(groupIndex + 1, selectedKeywords, destination);
                return;
            }

            for (int i = 0; i < group.Keywords.Count; ++i)
            {
                string keyword = group.Keywords[i];
                bool enableKeyword = !string.Equals(keyword, "_", StringComparison.Ordinal);
                if (enableKeyword)
                {
                    selectedKeywords.Add(keyword);
                }

                EnumerateRecursive(groupIndex + 1, selectedKeywords, destination);

                if (enableKeyword)
                {
                    selectedKeywords.RemoveAt(selectedKeywords.Count - 1);
                }
            }
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
