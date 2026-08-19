using System;
using SharpShader.Compilation;
using SharpShader.CSharp.Frontend;

namespace SharpShader.CSharp
{
    public sealed class CSharpShaderVariantCompilation
    {
        public CSharpShaderVariantCompilation(
            string variantKey,
            CSharpShaderTranslation translation,
            ShaderProgramCompilation program)
        {
            if (string.IsNullOrWhiteSpace(variantKey))
            {
                throw new ArgumentException("Variant key must not be empty.", nameof(variantKey));
            }

            VariantKey = variantKey;
            Translation = translation ?? throw new ArgumentNullException(nameof(translation));
            Program = program ?? throw new ArgumentNullException(nameof(program));
        }

        public string VariantKey { get; }
        public CSharpShaderTranslation Translation { get; }
        public ShaderProgramCompilation Program { get; }
    }
}
