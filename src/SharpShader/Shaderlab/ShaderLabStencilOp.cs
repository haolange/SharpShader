using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using SharpShader.Compilation;

namespace SharpShader.ShaderLab
{

    public sealed class ShaderLabStencilOp
    {
        public ShaderLabFloatProperty Comp { get; }
        public ShaderLabFloatProperty Pass { get; }
        public ShaderLabFloatProperty Fail { get; }
        public ShaderLabFloatProperty ZFail { get; }

        public ShaderLabStencilOp(
            ShaderLabFloatProperty comp = default,
            ShaderLabFloatProperty pass = default,
            ShaderLabFloatProperty fail = default,
            ShaderLabFloatProperty zFail = default)
        {
            Comp = comp;
            Pass = pass;
            Fail = fail;
            ZFail = zFail;
        }
    }
}
