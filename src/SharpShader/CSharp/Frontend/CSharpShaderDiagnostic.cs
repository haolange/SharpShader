using System;
using Microsoft.CodeAnalysis;

namespace SharpShader.CSharp.Frontend
{
    public sealed class CSharpShaderDiagnostic
    {
        public CSharpShaderDiagnostic(
            string id,
            CSharpShaderDiagnosticSeverity severity,
            string message,
            string? path = null,
            int line = 0,
            int column = 0)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("Diagnostic id must not be empty.", nameof(id));
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("Diagnostic message must not be empty.", nameof(message));
            }

            if (!Enum.IsDefined(typeof(CSharpShaderDiagnosticSeverity), severity))
            {
                throw new ArgumentOutOfRangeException(nameof(severity), severity, "Severity is not defined.");
            }

            Id = id;
            Severity = severity;
            Message = message;
            Path = path ?? string.Empty;
            Line = line;
            Column = column;
        }

        public string Id { get; }
        public CSharpShaderDiagnosticSeverity Severity { get; }
        public string Message { get; }
        public string Path { get; }
        public int Line { get; }
        public int Column { get; }

        public static CSharpShaderDiagnostic FromLocation(
            string id,
            CSharpShaderDiagnosticSeverity severity,
            string message,
            Location? location)
        {
            FileLinePositionSpan span = location?.GetLineSpan() ?? default;
            return new CSharpShaderDiagnostic(
                id,
                severity,
                message,
                span.Path,
                span.StartLinePosition.Line + 1,
                span.StartLinePosition.Character + 1);
        }
    }
}
