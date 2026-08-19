using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpShader.CSharp.Frontend
{
    public sealed class CSharpShaderTranslator
    {
        public CSharpShaderTranslation Translate(CSharpShaderTranslateRequest request)
        {
            if (request is null)
            {
                throw new System.ArgumentNullException(nameof(request));
            }

            CSharpParseOptions parseOptions = new CSharpParseOptions(
                LanguageVersion.Latest,
                DocumentationMode.Parse,
                SourceCodeKind.Regular,
                request.PreprocessorSymbols);
            SyntaxTree tree = CSharpSyntaxTree.ParseText(
                request.Source,
                parseOptions,
                request.SourceName);

            MetadataReference[] references = new MetadataReference[request.MetadataReferencePaths.Count];
            for (int index = 0; index < request.MetadataReferencePaths.Count; ++index)
            {
                references[index] = MetadataReference.CreateFromFile(
                    request.MetadataReferencePaths[index]);
            }

            CSharpCompilation compilation = CSharpCompilation.Create(
                "SharpSL.Translation",
                new[] { tree },
                references,
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    allowUnsafe: true));
            return Translate(compilation);
        }

        public CSharpShaderTranslation Translate(
            Compilation compilation,
            CancellationToken cancellationToken = default)
        {
            if (compilation is null)
            {
                throw new System.ArgumentNullException(nameof(compilation));
            }

            return CSharpShaderLowerer.Run(compilation, cancellationToken);
        }
    }
}
