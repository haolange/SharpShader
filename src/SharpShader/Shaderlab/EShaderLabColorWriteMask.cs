using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using SharpShader.Compilation;

namespace SharpShader.ShaderLab
{

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
}
