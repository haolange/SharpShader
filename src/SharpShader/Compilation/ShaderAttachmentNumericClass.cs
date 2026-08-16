using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public enum ShaderAttachmentNumericClass : byte
    {
        FloatingPoint,
        SignedInteger,
        UnsignedInteger,
        DepthStencil,
    }
}
