using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    [Flags]
    public enum ShaderStageMask : ulong
    {
        None = 0,
        Vertex = 1UL << 0,
        Hull = 1UL << 1,
        Domain = 1UL << 2,
        Geometry = 1UL << 3,
        Pixel = 1UL << 4,
        Compute = 1UL << 5,
        Amplification = 1UL << 6,
        Mesh = 1UL << 7,
        RayGeneration = 1UL << 8,
        Intersection = 1UL << 9,
        AnyHit = 1UL << 10,
        ClosestHit = 1UL << 11,
        Miss = 1UL << 12,
        Callable = 1UL << 13,
        Node = 1UL << 14,
        All = (1UL << 15) - 1,
    }
}
