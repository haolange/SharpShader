using System;

namespace SharpShader.CSharp.Frontend
{
    public sealed class CSharpShaderTranslationException : Exception
    {
        public CSharpShaderTranslationException(string message, CSharpShaderTranslation translation)
            : base(message)
        {
            Translation = translation ?? throw new ArgumentNullException(nameof(translation));
        }

        public CSharpShaderTranslation Translation { get; }
    }
}
