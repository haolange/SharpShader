using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using SharpShader.Compilation;
using SharpShader.HLSLCrossCompiler;


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
