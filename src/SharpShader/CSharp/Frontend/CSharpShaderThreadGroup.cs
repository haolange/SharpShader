using System;

namespace SharpShader.CSharp.Frontend
{
    public sealed class CSharpShaderThreadGroup
    {
        public CSharpShaderThreadGroup(int x, int y, int z)
        {
            if (x <= 0 || y <= 0 || z <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(x), "Thread group size must be positive.");
            }

            X = x;
            Y = y;
            Z = z;
        }

        public int X { get; }
        public int Y { get; }
        public int Z { get; }
    }
}
