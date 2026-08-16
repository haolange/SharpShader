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

    public sealed class ShaderLabPassCompilation
    {
        public int PassIndex { get; }
        public string PassName { get; }
        public ShaderProgramCompilation Program { get; }

        internal ShaderLabPassCompilation(
            int passIndex,
            string passName,
            ShaderProgramCompilation program)
        {
            if (passIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(passIndex));
            }

            ArgumentNullException.ThrowIfNull(program);
            PassIndex = passIndex;
            PassName = passName;
            Program = program;
        }
    }
}
