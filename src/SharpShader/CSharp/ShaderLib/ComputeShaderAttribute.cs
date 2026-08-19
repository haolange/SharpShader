using System;

namespace SharpShader.CSharp.ShaderLib
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class ComputeShaderAttribute : Attribute
    {
        public ComputeShaderAttribute()
            : this("CSMain")
        {
        }

        public ComputeShaderAttribute(string entryName)
        {
            if (string.IsNullOrWhiteSpace(entryName))
            {
                throw new ArgumentException("Compute entry name must not be empty.", nameof(entryName));
            }

            EntryName = entryName;
        }

        public string EntryName { get; }
    }
}
