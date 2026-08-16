using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public enum ShaderResourceDimension
    {
        Unknown,
        Buffer,
        Texture1D,
        Texture1DArray,
        Texture2D,
        Texture2DArray,
        Texture2DMultisampled,
        Texture2DMultisampledArray,
        Texture3D,
        TextureCube,
        TextureCubeArray,
    }
}
