using System;

namespace SharpShader.CSharp.ShaderLib
{
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    public sealed class GpuLayoutAttribute : Attribute
    {
        public GpuLayoutAttribute(GpuLayoutKind kind = GpuLayoutKind.ConstantBuffer)
        {
            if (!Enum.IsDefined(typeof(GpuLayoutKind), kind))
            {
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Gpu layout kind is not defined.");
            }

            Kind = kind;
        }

        public GpuLayoutKind Kind { get; }
    }
}
