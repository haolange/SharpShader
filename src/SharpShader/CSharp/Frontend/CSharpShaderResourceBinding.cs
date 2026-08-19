using System;

namespace SharpShader.CSharp.Frontend
{
    public sealed class CSharpShaderResourceBinding
    {
        public CSharpShaderResourceBinding(
            string name,
            CSharpShaderResourceKind kind,
            string hlslType,
            int register,
            int space,
            bool pushConstant,
            bool groupShared)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Resource name must not be empty.", nameof(name));
            }

            if (string.IsNullOrWhiteSpace(hlslType))
            {
                throw new ArgumentException("HLSL type must not be empty.", nameof(hlslType));
            }

            if (!Enum.IsDefined(typeof(CSharpShaderResourceKind), kind))
            {
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Resource kind is not defined.");
            }

            Name = name;
            Kind = kind;
            HlslType = hlslType;
            Register = register;
            Space = space;
            PushConstant = pushConstant;
            GroupShared = groupShared;
        }

        public string Name { get; }
        public CSharpShaderResourceKind Kind { get; }
        public string HlslType { get; }
        public int Register { get; }
        public int Space { get; }
        public bool PushConstant { get; }
        public bool GroupShared { get; }
    }
}
