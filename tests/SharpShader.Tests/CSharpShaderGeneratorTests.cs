using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SharpShader.CSharp;
using SharpShader.CSharp.Generators;
using Xunit;

namespace SharpShader.Tests
{
    public sealed class CSharpShaderGeneratorTests
    {
        [Fact]
        public void Generator_EmitsHlslConstant_ForComputeShader()
        {
            CSharpCompilation compilation = CreateCompilation(CSharpShaderSources.WriteConstant);
            CSharpGeneratorDriver driver = CSharpGeneratorDriver.Create(new SharpSLGenerator());
            driver.RunGeneratorsAndUpdateCompilation(
                compilation,
                out Microsoft.CodeAnalysis.Compilation updated,
                out ImmutableArray<Diagnostic> diagnostics);

            Assert.DoesNotContain(
                diagnostics,
                static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            SyntaxTree generated = Assert.Single(
                updated.SyntaxTrees,
                static tree => tree.FilePath.Contains("SharpSL.Translated.g.cs"));
            string text = generated.GetText().ToString();
            Assert.Contains("RWStructuredBuffer<uint> Output", text);
            Assert.Contains("public const string Hlsl", text);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Generator_AllowsHostReferencesToGeneratedOutput(bool separateTree)
        {
            const string host = "public static class Host { public static int Count => SharpSLGenerated.EntryCount; public static string Hlsl => SharpSLGenerated.Hlsl; }";
            CSharpCompilation compilation = CreateCompilation(
                CSharpShaderSources.WriteConstant + (separateTree ? string.Empty : host));
            if (separateTree)
            {
                compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(host, path: "host.cs"));
            }
            CSharpGeneratorDriver.Create(new SharpSLGenerator()).RunGeneratorsAndUpdateCompilation(
                compilation, out Microsoft.CodeAnalysis.Compilation updated, out ImmutableArray<Diagnostic> diagnostics);

            Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            Assert.Single(updated.SyntaxTrees, tree => tree.FilePath.Contains("SharpSL.Translated.g.cs"));
            Assert.DoesNotContain(updated.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Generator_RejectsShaderSemanticErrors(bool inHelper)
        {
            string source = inHelper
                ? CSharpShaderSources.WriteConstant.Replace("41u", "Helper.Value()")
                    + "public static class Helper { public static uint Value() { return \"invalid\"; } }"
                : CSharpShaderSources.WriteConstant.Replace("41u", "\"invalid\"");
            CSharpGeneratorDriver.Create(new SharpSLGenerator()).RunGeneratorsAndUpdateCompilation(
                CreateCompilation(source), out Microsoft.CodeAnalysis.Compilation updated, out ImmutableArray<Diagnostic> diagnostics);

            Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SSCS0009");
            Assert.DoesNotContain(updated.SyntaxTrees, tree => tree.FilePath.Contains("SharpSL.Translated.g.cs"));
        }

        [Fact]
        public void Generator_LeavesUnrelatedHostErrorsToCompiler()
        {
            CSharpCompilation compilation = CreateCompilation(CSharpShaderSources.WriteConstant
                + "public static class Host { public static int Count => MissingHostSymbol; }");
            CSharpGeneratorDriver.Create(new SharpSLGenerator()).RunGeneratorsAndUpdateCompilation(
                compilation, out Microsoft.CodeAnalysis.Compilation updated, out ImmutableArray<Diagnostic> diagnostics);

            Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            Assert.Single(updated.SyntaxTrees, tree => tree.FilePath.Contains("SharpSL.Translated.g.cs"));
            Assert.Contains(updated.GetDiagnostics(), diagnostic => diagnostic.Id == "CS0103");
        }

        private static CSharpCompilation CreateCompilation(string source)
        {
            System.Collections.Generic.List<MetadataReference> references = new();
            foreach (string path in CSharpShaderReferenceResolver.ResolveDefaultReferences())
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }

            return CSharpCompilation.Create(
                "SharpSL.Generator.Tests",
                new[] { CSharpSyntaxTree.ParseText(source, path: "shader.cs") },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }
    }
}
