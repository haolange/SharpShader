using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace SharpShader.HLSLCrossCompiler.Internal
{
    internal sealed class DxcPreprocessedSource
    {
        private readonly byte[] m_Content;

        public string Source { get; }
        public int ByteLength => m_Content.Length;
        public IReadOnlyList<DxcCapturedInclude> Includes { get; }
        public string ContentDigest { get; }

        public DxcPreprocessedSource(
            string source,
            ReadOnlySpan<byte> content,
            IReadOnlyList<DxcCapturedInclude> includes)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(includes);
            Source = source;
            m_Content = content.ToArray();
            Includes = Array.AsReadOnly(
                new List<DxcCapturedInclude>(includes).ToArray());
            ContentDigest = Convert.ToHexStringLower(SHA256.HashData(m_Content));
        }

        public byte[] CopyContent()
        {
            return (byte[])m_Content.Clone();
        }
    }
}
