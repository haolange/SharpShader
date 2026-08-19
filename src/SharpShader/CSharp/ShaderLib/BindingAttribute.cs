using System;

namespace SharpShader.CSharp.ShaderLib
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class BindingAttribute : Attribute
    {
        public BindingAttribute(int register, int space)
        {
            if (register < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(register), register, "Register must be non-negative.");
            }

            if (space < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(space), space, "Space must be non-negative.");
            }

            Register = register;
            Space = space;
        }

        public int Register { get; }
        public int Space { get; }
    }
}
