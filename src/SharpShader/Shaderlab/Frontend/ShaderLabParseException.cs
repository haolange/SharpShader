using System;

namespace SharpShader.ShaderLab.Frontend
{
    public sealed class ShaderLabParseException : FormatException
    {
        public int Line { get; }
        public int Column { get; }

        public ShaderLabParseException(int line, int column, string message)
            : base($"[Line {line}, Col {column}] {message}")
        {
            Line = line;
            Column = column;
        }
    }
}
