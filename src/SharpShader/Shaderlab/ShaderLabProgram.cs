using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using SharpShader.Compilation;

namespace SharpShader.ShaderLab
{

    public sealed class ShaderLabProgram
    {
        public string Source { get; }
        public IReadOnlyList<ShaderLabProgramEntry> Entries { get; }
        public IReadOnlyList<ShaderKeywordGroup> KeywordGroups { get; }
        public ShaderAttachmentPhase? AttachmentPhase { get; }

        public ShaderLabProgram(
            string source,
            IReadOnlyList<ShaderLabProgramEntry> entries,
            IReadOnlyList<ShaderKeywordGroup>? keywordGroups = null,
            ShaderAttachmentPhase? attachmentPhase = null)
        {
            Source = source ?? string.Empty;
            Entries = entries ?? Array.Empty<ShaderLabProgramEntry>();
            KeywordGroups = keywordGroups ?? Array.Empty<ShaderKeywordGroup>();
            AttachmentPhase = attachmentPhase;
        }

        public List<ShaderVariantKey> EnumerateVariantKeys()
        {
            return ShaderVariantEnumeration.Enumerate(KeywordGroups);
        }
    }
}
