using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.Compilation
{

    public sealed class ShaderProgramVariant
    {
        private readonly ReadOnlyCollection<ShaderDefine> m_Defines;

        public static ShaderProgramVariant Default { get; } = new("default");

        public string Key { get; }
        public IReadOnlyList<ShaderDefine> Defines => m_Defines;

        public ShaderProgramVariant(string key, IEnumerable<ShaderDefine>? defines = null)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Shader program variant key must not be empty.", nameof(key));
            }

            ShaderDefine[] copy = defines is null
                ? Array.Empty<ShaderDefine>()
                : new List<ShaderDefine>(defines).ToArray();
            Array.Sort(copy, ShaderProgramModelValidation.CompareDefines);
            ShaderProgramModelValidation.ValidateDefines(copy, nameof(defines));

            Key = key;
            m_Defines = Array.AsReadOnly(copy);
        }
    }
}
