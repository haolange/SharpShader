using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using SharpShader.Compilation;

namespace SharpShader.ShaderLab
{

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
}
