using System;

namespace SharpShader.CSharp.ShaderLib
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class NumThreadsAttribute : Attribute
    {
        public NumThreadsAttribute(int x, int y, int z)
        {
            if (x <= 0 || y <= 0 || z <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(x),
                    "NumThreads components must be positive.");
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
