using System;

namespace SharpShader.CSharp.ShaderLib
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class PushConstantAttribute : Attribute
    {
    }
}
