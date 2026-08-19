using System;

namespace SharpShader.CSharp.ShaderLib
{
    [AttributeUsage(
        AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Constructor,
        AllowMultiple = false,
        Inherited = false)]
    public sealed class IntrinsicAttribute : Attribute
    {
        public IntrinsicAttribute(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("Intrinsic id must not be empty.", nameof(id));
            }

            Id = id;
        }

        public string Id { get; }
    }
}
