using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using SharpShader.Compilation;

namespace SharpShader.ShaderLab
{

    public readonly struct ShaderLabFloatProperty
    {
        public float Value { get; }
        public string Name { get; }

        public ShaderLabFloatProperty(float value, string name)
        {
            Value = value;
            Name = name ?? string.Empty;
        }
    }
}
