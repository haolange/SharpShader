using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using SharpShader.Compilation;

namespace SharpShader.ShaderLab
{

    public readonly struct ShaderLabTextureProperty
    {
        public string Name { get; }
        public EShaderLabTextureDimension Dimension { get; }
        public string DefaultValue { get; }

        public ShaderLabTextureProperty(string name, EShaderLabTextureDimension dimension, string defaultValue = "white")
        {
            Name = name ?? string.Empty;
            Dimension = dimension;
            DefaultValue = defaultValue ?? "white";
        }
    }
}
