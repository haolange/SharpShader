using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.Compilation
{
    [Flags]
    public enum ShaderProgramTarget
    {
        None = 0,
        DirectX12 = 1 << 0,
        Vulkan = 1 << 1,
        MetalMsl = 1 << 2,
        All = DirectX12 | Vulkan | MetalMsl,
    }
}
