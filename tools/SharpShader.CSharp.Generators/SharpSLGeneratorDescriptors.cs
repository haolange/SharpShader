using Microsoft.CodeAnalysis;
using SharpShader.CSharp.Frontend;

namespace SharpShader.CSharp.Generators
{
    internal static class SharpSLGeneratorDescriptors
    {
        internal static DiagnosticDescriptor Get(string id, CSharpShaderDiagnosticSeverity severity)
        {
            return new DiagnosticDescriptor(
                id,
                "SharpSL",
                "{0}",
                "SharpSL",
                Map(severity),
                isEnabledByDefault: true);
        }

        private static DiagnosticSeverity Map(CSharpShaderDiagnosticSeverity severity)
        {
            switch (severity)
            {
                case CSharpShaderDiagnosticSeverity.Warning:
                    return DiagnosticSeverity.Warning;
                case CSharpShaderDiagnosticSeverity.Info:
                    return DiagnosticSeverity.Info;
                default:
                    return DiagnosticSeverity.Error;
            }
        }
    }
}
