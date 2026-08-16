using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public enum ShaderDepthExport : byte
    {
        None,
        Depth,
        DepthGreaterEqual,
        DepthLessEqual,
    }
}
