using System;

namespace SharpShader.CSharp.ShaderLib
{
    public sealed class GpuLayoutField
    {
        public GpuLayoutField(string name, GpuTypeShape type, int offset)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Field name must not be empty.", nameof(name));
            }

            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), offset, "Field offset must be non-negative.");
            }

            Name = name;
            Type = type;
            Offset = offset;
        }

        public string Name { get; }
        public GpuTypeShape Type { get; }
        public int Offset { get; }
    }
}
