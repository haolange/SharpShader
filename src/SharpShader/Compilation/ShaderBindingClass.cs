using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{
    public enum ShaderBindingClass
    {
        ShaderResource,
        Sampler,
        ConstantBuffer,
        UnorderedAccess,
    }
}
