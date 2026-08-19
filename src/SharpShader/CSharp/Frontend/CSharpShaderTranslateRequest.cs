using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.CSharp.Frontend
{
    public sealed class CSharpShaderTranslateRequest
    {
        private readonly ReadOnlyCollection<string> m_MetadataReferencePaths;
        private readonly ReadOnlyCollection<string> m_PreprocessorSymbols;

        public CSharpShaderTranslateRequest(
            string source,
            string sourceName,
            IReadOnlyList<string> metadataReferencePaths,
            IReadOnlyList<string>? preprocessorSymbols = null)
        {
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (string.IsNullOrWhiteSpace(sourceName))
            {
                throw new ArgumentException("Source name must not be empty.", nameof(sourceName));
            }

            if (metadataReferencePaths is null)
            {
                throw new ArgumentNullException(nameof(metadataReferencePaths));
            }

            Source = source;
            SourceName = sourceName;
            m_MetadataReferencePaths = new ReadOnlyCollection<string>(Copy(metadataReferencePaths));
            m_PreprocessorSymbols = new ReadOnlyCollection<string>(
                preprocessorSymbols is null
                    ? Array.Empty<string>()
                    : Copy(preprocessorSymbols));
        }

        public string Source { get; }
        public string SourceName { get; }
        public IReadOnlyList<string> MetadataReferencePaths => m_MetadataReferencePaths;
        public IReadOnlyList<string> PreprocessorSymbols => m_PreprocessorSymbols;

        private static string[] Copy(IReadOnlyList<string> items)
        {
            string[] copy = new string[items.Count];
            for (int index = 0; index < items.Count; ++index)
            {
                copy[index] = items[index];
            }

            return copy;
        }
    }
}
