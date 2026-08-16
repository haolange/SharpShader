using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using SharpShader.Compilation;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.ShaderLab
{

    public sealed class ShaderLabCompilation
    {
        private readonly ReadOnlyCollection<ShaderLabPassCompilation> m_Passes;

        public string SourcePath { get; }
        public IReadOnlyList<ShaderLabPassCompilation> Passes => m_Passes;

        internal ShaderLabCompilation(
            string sourcePath,
            IEnumerable<ShaderLabPassCompilation> passes)
        {
            ArgumentNullException.ThrowIfNull(passes);
            ShaderLabPassCompilation[] copy = passes.ToArray();
            Array.Sort(copy, static (left, right) =>
                left.PassIndex.CompareTo(right.PassIndex));
            for (int index = 0; index < copy.Length; ++index)
            {
                ArgumentNullException.ThrowIfNull(copy[index]);
                if (copy[index].PassIndex != index)
                {
                    throw new ArgumentException(
                        "ShaderLab pass compilations must be contiguous and ordered by pass index.",
                        nameof(passes));
                }
            }

            SourcePath = sourcePath;
            m_Passes = Array.AsReadOnly(copy);
        }

        public ShaderLabPassCompilation GetPass(int passIndex)
        {
            if ((uint)passIndex >= (uint)m_Passes.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(passIndex));
            }

            return m_Passes[passIndex];
        }
    }
}
