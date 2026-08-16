using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using SharpShader.Compilation;

namespace SharpShader.ShaderLab
{

    public readonly struct ShaderLabVectorProperty
    {
        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        public float W { get; }
        public string Name { get; }

        public ShaderLabVectorProperty(float x, float y, float z, float w, string name)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
            Name = name ?? string.Empty;
        }
    }
}
