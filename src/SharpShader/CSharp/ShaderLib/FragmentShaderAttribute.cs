using System;

namespace SharpShader.CSharp.ShaderLib
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class FragmentShaderAttribute : Attribute
    {
        public FragmentShaderAttribute()
            : this("PSMain")
        {
        }

        public FragmentShaderAttribute(string entryName)
        {
            if (string.IsNullOrWhiteSpace(entryName))
            {
                throw new ArgumentException("Fragment entry name must not be empty.", nameof(entryName));
            }

            EntryName = entryName;
        }

        public string EntryName { get; }
    }
}
