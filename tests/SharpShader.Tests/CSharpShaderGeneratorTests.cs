using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SharpShader.CSharp;
using SharpShader.CSharp.Generators;
using Xunit;

namespace Infinity.Rendering.Tests
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
                out Compilation updated,
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
