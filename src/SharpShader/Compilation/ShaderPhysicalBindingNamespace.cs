using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public enum ShaderPhysicalBindingNamespace
    {
        Unified,
        ShaderResource,
        Sampler,
        ConstantBuffer,
        UnorderedAccess,
        Buffer,
        Texture,
    }
}
