using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.Compilation
{

    public sealed class ShaderProgramEntry
    {
        public string Name { get; }
        public ShaderExecutionStage Stage { get; }

        public ShaderProgramEntry(string name, ShaderExecutionStage stage)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Shader program entry name must not be empty.", nameof(name));
            }

            _ = ShaderStageMaskUtility.FromStage(stage);
            Name = name;
            Stage = stage;
        }
    }
}
