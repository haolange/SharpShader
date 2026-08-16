using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using SharpShader.Compilation;

namespace SharpShader.ShaderLab
{

    public readonly struct ShaderLabProgramEntry
    {
        public EShaderLabShaderStage Stage { get; }
        public string EntryName { get; }

        public ShaderLabProgramEntry(EShaderLabShaderStage stage, string entryName)
        {
            Stage = stage;
            EntryName = entryName ?? string.Empty;
        }
    }
}
