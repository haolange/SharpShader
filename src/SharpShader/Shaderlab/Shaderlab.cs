using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using SharpShader.Compilation;

namespace SharpShader.ShaderLab
{

    public sealed class ShaderLab
    {
        public string Name { get; }
        public string SourcePath { get; }
        public IReadOnlyList<ShaderLabPass> Passes { get; }
        public IReadOnlyDictionary<string, string> Tags { get; }
        public IReadOnlyList<ShaderLabProperty> Properties { get; }

        public ShaderLab(
            string name,
            string sourcePath,
            IReadOnlyList<ShaderLabPass> passes,
            IReadOnlyDictionary<string, string> tags,
            IReadOnlyList<ShaderLabProperty> properties)
        {
            Name = name ?? string.Empty;
            SourcePath = string.IsNullOrWhiteSpace(sourcePath) ? "<memory>" : sourcePath;
            Passes = passes ?? Array.Empty<ShaderLabPass>();
            Tags = tags ?? new Dictionary<string, string>(StringComparer.Ordinal);
            Properties = properties ?? Array.Empty<ShaderLabProperty>();
        }
    }
}
