using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using SharpShader.Compilation;

namespace SharpShader.ShaderLab
{

    public sealed class ShaderLabRenderState
    {
        public int? Cull { get; }
        public int? ZTest { get; }
        public int? ZWrite { get; }
        public ShaderLabStencilOp? StencilOp { get; }
        public ShaderLabStencilOp? StencilOpBack { get; }
        public ShaderLabStencilOp? StencilOpFront { get; }
        public ShaderLabFloatProperty? ColorMask { get; }
        public ShaderLabFloatProperty? AlphaToMask { get; }
        public ShaderLabFloatProperty? OffsetFactor { get; }
        public ShaderLabFloatProperty? OffsetUnits { get; }
        public ShaderLabFloatProperty? BlendOp { get; }
        public ShaderLabFloatProperty? BlendOpAlpha { get; }
        public ShaderLabFloatProperty? SrcBlend { get; }
        public ShaderLabFloatProperty? DstBlend { get; }
        public ShaderLabFloatProperty? SrcBlendAlpha { get; }
        public ShaderLabFloatProperty? DstBlendAlpha { get; }
        public ShaderLabFloatProperty? StencilRef { get; }
        public ShaderLabFloatProperty? StencilReadMask { get; }
        public ShaderLabFloatProperty? StencilWriteMask { get; }

        public ShaderLabRenderState(
            int? cull = null,
            int? zTest = null,
            int? zWrite = null,
            ShaderLabStencilOp? stencilOp = null,
            ShaderLabStencilOp? stencilOpBack = null,
            ShaderLabStencilOp? stencilOpFront = null,
            ShaderLabFloatProperty? colorMask = null,
            ShaderLabFloatProperty? alphaToMask = null,
            ShaderLabFloatProperty? offsetFactor = null,
            ShaderLabFloatProperty? offsetUnits = null,
            ShaderLabFloatProperty? blendOp = null,
            ShaderLabFloatProperty? blendOpAlpha = null,
            ShaderLabFloatProperty? srcBlend = null,
            ShaderLabFloatProperty? dstBlend = null,
            ShaderLabFloatProperty? srcBlendAlpha = null,
            ShaderLabFloatProperty? dstBlendAlpha = null,
            ShaderLabFloatProperty? stencilRef = null,
            ShaderLabFloatProperty? stencilReadMask = null,
            ShaderLabFloatProperty? stencilWriteMask = null)
        {
            Cull = cull;
            ZTest = zTest;
            ZWrite = zWrite;
            StencilOp = stencilOp;
            StencilOpBack = stencilOpBack;
            StencilOpFront = stencilOpFront;
            ColorMask = colorMask;
            AlphaToMask = alphaToMask;
            OffsetFactor = offsetFactor;
            OffsetUnits = offsetUnits;
            BlendOp = blendOp;
            BlendOpAlpha = blendOpAlpha;
            SrcBlend = srcBlend;
            DstBlend = dstBlend;
            SrcBlendAlpha = srcBlendAlpha;
            DstBlendAlpha = dstBlendAlpha;
            StencilRef = stencilRef;
            StencilReadMask = stencilReadMask;
            StencilWriteMask = stencilWriteMask;
        }
    }
}
