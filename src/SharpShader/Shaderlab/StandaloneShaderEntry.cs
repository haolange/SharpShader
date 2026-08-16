using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using SharpShader.Compilation;

namespace SharpShader.ShaderLab
{

    public sealed class StandaloneShaderEntry
    {
        public StandaloneShaderStage Stage { get; }
        public string EntryName { get; }

        public StandaloneShaderEntry(StandaloneShaderStage stage, string entryName)
        {
            Stage = stage;
            EntryName = entryName ?? string.Empty;
        }
    }
}
