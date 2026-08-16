using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.Compilation.Internal
{
    internal enum MslResourceBindingKind : byte
    {
        Texture,
        Buffer,
        ArgumentBufferId,
    }
}
