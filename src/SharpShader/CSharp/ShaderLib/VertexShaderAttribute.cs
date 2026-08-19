using System;

namespace SharpShader.CSharp.ShaderLib
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class VertexShaderAttribute : Attribute
    {
        public VertexShaderAttribute()
            : this("VSMain")
        {
        }

        public VertexShaderAttribute(string entryName)
        {
            if (string.IsNullOrWhiteSpace(entryName))
            {
                throw new ArgumentException("Vertex entry name must not be empty.", nameof(entryName));
            }

            EntryName = entryName;
        }

        public string EntryName { get; }
    }
}
