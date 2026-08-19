using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.CSharp
{
    public sealed class CSharpShaderCompilation
    {
        private readonly ReadOnlyCollection<CSharpShaderVariantCompilation> m_Variants;

        public CSharpShaderCompilation(IReadOnlyList<CSharpShaderVariantCompilation> variants)
        {
            ArgumentNullException.ThrowIfNull(variants);

            if (variants.Count == 0)
            {
                throw new ArgumentException("At least one variant compilation is required.", nameof(variants));
            }

            m_Variants = Array.AsReadOnly(new List<CSharpShaderVariantCompilation>(variants).ToArray());
        }

        public IReadOnlyList<CSharpShaderVariantCompilation> Variants => m_Variants;

        public CSharpShaderVariantCompilation Primary => m_Variants[0];
    }
}
