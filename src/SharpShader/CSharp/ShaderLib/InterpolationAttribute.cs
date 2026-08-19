using System;

namespace SharpShader.CSharp.ShaderLib
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class InterpolationAttribute : Attribute
    {
        public InterpolationAttribute(InterpolationMode mode)
        {
            if (!Enum.IsDefined(typeof(InterpolationMode), mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Interpolation mode is not defined.");
            }

            Mode = mode;
        }

        public InterpolationMode Mode { get; }
    }
}
