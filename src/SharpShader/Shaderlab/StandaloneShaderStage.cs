using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using SharpShader.Compilation;

namespace SharpShader.ShaderLab
{

    public enum StandaloneShaderStage
    {
        Compute = 0,
        RayGeneration,
        Miss,
        Callable,
        Unknown,
    }
}
