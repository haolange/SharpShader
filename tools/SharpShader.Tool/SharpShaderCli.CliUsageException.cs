using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;

using SharpShader.Compilation;
using SharpShader.HLSLCrossCompiler;

[assembly: InternalsVisibleTo("Infinity.Rendering.Tests")]

namespace SharpShader.Tool
{
    internal static partial class SharpShaderCli
    {

        private sealed class CliUsageException : Exception
        {
            public CliUsageException(string message)
                : base(message)
            {
            }
        }
}
}
