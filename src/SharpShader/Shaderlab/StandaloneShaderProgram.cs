using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using SharpShader.Compilation;

namespace SharpShader.ShaderLab
{

    public sealed class StandaloneShaderProgram
    {
        public StandaloneShaderProgramKind Kind { get; }
        public string SourcePath { get; }
        public string Source { get; }
        public IReadOnlyList<StandaloneShaderEntry> Entries { get; }
        public IReadOnlyList<ShaderKeywordGroup> KeywordGroups { get; }

        public StandaloneShaderProgram(
            StandaloneShaderProgramKind kind,
            string sourcePath,
            string source,
            IReadOnlyList<StandaloneShaderEntry> entries,
            IReadOnlyList<ShaderKeywordGroup>? keywordGroups = null)
        {
            Kind = kind;
            SourcePath = sourcePath ?? string.Empty;
            Source = source ?? string.Empty;
            Entries = entries ?? Array.Empty<StandaloneShaderEntry>();
            KeywordGroups = keywordGroups ?? Array.Empty<ShaderKeywordGroup>();
        }

        public List<ShaderVariantKey> EnumerateVariantKeys()
        {
            return ShaderVariantEnumeration.Enumerate(KeywordGroups);
        }
    }
}
