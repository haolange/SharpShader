using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.CSharp.Frontend
{
    public sealed class CSharpShaderTranslation
    {
        private readonly ReadOnlyCollection<CSharpShaderEntryTranslation> m_Entries;
        private readonly ReadOnlyCollection<CSharpShaderResourceBinding> m_Resources;
        private readonly ReadOnlyCollection<CSharpShaderDiagnostic> m_Diagnostics;

        public CSharpShaderTranslation(
            string hlsl,
            string dump,
            IReadOnlyList<CSharpShaderEntryTranslation> entries,
            IReadOnlyList<CSharpShaderResourceBinding> resources,
            IReadOnlyList<CSharpShaderDiagnostic> diagnostics)
        {
            if (hlsl is null)
            {
                throw new ArgumentNullException(nameof(hlsl));
            }

            if (dump is null)
            {
                throw new ArgumentNullException(nameof(dump));
            }

            if (entries is null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            if (resources is null)
            {
                throw new ArgumentNullException(nameof(resources));
            }

            if (diagnostics is null)
            {
                throw new ArgumentNullException(nameof(diagnostics));
            }

            Hlsl = hlsl;
            Dump = dump;
            m_Entries = new ReadOnlyCollection<CSharpShaderEntryTranslation>(Copy(entries));
            m_Resources = new ReadOnlyCollection<CSharpShaderResourceBinding>(Copy(resources));
            m_Diagnostics = new ReadOnlyCollection<CSharpShaderDiagnostic>(Copy(diagnostics));
        }

        public string Hlsl { get; }
        public string Dump { get; }
        public IReadOnlyList<CSharpShaderEntryTranslation> Entries => m_Entries;
        public IReadOnlyList<CSharpShaderResourceBinding> Resources => m_Resources;
        public IReadOnlyList<CSharpShaderDiagnostic> Diagnostics => m_Diagnostics;

        public bool HasErrors
        {
            get
            {
                for (int index = 0; index < m_Diagnostics.Count; ++index)
                {
                    if (m_Diagnostics[index].Severity == CSharpShaderDiagnosticSeverity.Error)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private static T[] Copy<T>(IReadOnlyList<T> items)
        {
            T[] copy = new T[items.Count];
            for (int index = 0; index < items.Count; ++index)
            {
                copy[index] = items[index];
            }

            return copy;
        }
    }
}
