using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.Compilation
{

    public sealed class ShaderProgramArtifact
    {
        private readonly byte[] m_Content;

        public string VariantKey { get; }
        public string EntryPoint { get; }
        public ShaderExecutionStage Stage { get; }
        public ShaderArtifactIdentity Identity { get; }
        public ReadOnlyMemory<byte> Content => new((byte[])m_Content.Clone());
        public string? Text { get; }
        public void CopyContentTo(System.IO.Stream destination)
        {
            ArgumentNullException.ThrowIfNull(destination);
            byte[] copy = (byte[])m_Content.Clone();
            destination.Write(copy, 0, copy.Length);
        }


        internal ShaderProgramArtifact(
            string variantKey,
            string entryPoint,
            ShaderExecutionStage stage,
            ShaderArtifactIdentity identity,
            ReadOnlySpan<byte> content,
            string? text)
        {
            if (string.IsNullOrWhiteSpace(variantKey))
            {
                throw new ArgumentException("Artifact variant key must not be empty.", nameof(variantKey));
            }

            if (string.IsNullOrWhiteSpace(entryPoint))
            {
                throw new ArgumentException("Artifact entry point must not be empty.", nameof(entryPoint));
            }

            ArgumentNullException.ThrowIfNull(identity);
            _ = ShaderStageMaskUtility.FromStage(stage);
            if ((ulong)content.Length != identity.ByteLength)
            {
                throw new ArgumentException(
                    "Artifact content length does not match its identity.",
                    nameof(content));
            }

            VariantKey = variantKey;
            EntryPoint = entryPoint;
            Stage = stage;
            Identity = identity;
            m_Content = content.ToArray();
            Text = text;
        }

        internal byte[] CopyContent()
        {
            return (byte[])m_Content.Clone();
        }
    }
}
