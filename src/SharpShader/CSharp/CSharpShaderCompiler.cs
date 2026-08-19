using System;
using System.Collections.Generic;
using System.Threading;
using SharpShader.Compilation;
using SharpShader.CSharp.Frontend;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.CSharp
{
    public sealed class CSharpShaderCompiler
    {
        private static readonly Lazy<CSharpShaderCompiler> s_Shared =
            new Lazy<CSharpShaderCompiler>(
                static () => new CSharpShaderCompiler(new ShaderProgramCompiler()),
                LazyThreadSafetyMode.ExecutionAndPublication);

        private readonly ShaderProgramCompiler m_ProgramCompiler;
        private readonly CSharpShaderTranslator m_Translator;

        public static CSharpShaderCompiler Shared => s_Shared.Value;

        public CSharpShaderCompiler()
            : this(new ShaderProgramCompiler())
        {
        }

        public CSharpShaderCompiler(ShaderProgramCompiler programCompiler)
        {
            m_ProgramCompiler = programCompiler
                ?? throw new ArgumentNullException(nameof(programCompiler));
            m_Translator = new CSharpShaderTranslator();
        }

        public CSharpShaderCompilation Compile(
            string source,
            string sourceName,
            CSharpShaderCompilerOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);

            if (string.IsNullOrWhiteSpace(sourceName))
            {
                throw new ArgumentException("Source name must not be empty.", nameof(sourceName));
            }

            CSharpShaderCompilerOptions effective = options ?? new CSharpShaderCompilerOptions();
            IReadOnlyList<string> references = effective.MetadataReferencePaths.Count == 0
                ? CSharpShaderReferenceResolver.ResolveDefaultReferences()
                : effective.MetadataReferencePaths;

            List<CSharpShaderVariantCompilation> variants = new List<CSharpShaderVariantCompilation>();
            for (int index = 0; index < effective.Variants.Count; ++index)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ShaderProgramVariant variant = effective.Variants[index];
                List<string> symbols = new List<string>();
                foreach (ShaderDefine define in variant.Defines)
                {
                    symbols.Add(define.Name);
                }

                CSharpShaderTranslation translation = m_Translator.Translate(
                    new CSharpShaderTranslateRequest(
                        source,
                        sourceName,
                        references,
                        symbols));
                if (translation.HasErrors)
                {
                    throw new CSharpShaderTranslationException(
                        "SharpSL translation failed.",
                        translation);
                }

                ShaderProgramTarget targets = effective.Targets;
                bool usesRayQuery = false;
                for (int entryIndex = 0; entryIndex < translation.Entries.Count; ++entryIndex)
                {
                    if (translation.Entries[entryIndex].UsesRayQuery)
                    {
                        usesRayQuery = true;
                        break;
                    }
                }

                if (usesRayQuery && (targets & ShaderProgramTarget.MetalMsl) != 0)
                {
                    targets &= ~ShaderProgramTarget.MetalMsl;
                }

                if (targets == ShaderProgramTarget.None)
                {
                    throw new InvalidOperationException(
                        "RayQuery is not supported on Metal; no remaining shader targets were requested.");
                }

                List<ShaderProgramEntry> entries = new List<ShaderProgramEntry>();
                List<ShaderAttachmentInterface> attachments = new List<ShaderAttachmentInterface>();
                for (int entryIndex = 0; entryIndex < translation.Entries.Count; ++entryIndex)
                {
                    CSharpShaderEntryTranslation entry = translation.Entries[entryIndex];
                    ShaderExecutionStage stage = MapStage(entry.Stage);
                    entries.Add(new ShaderProgramEntry(entry.Name, stage));
                    if (stage == ShaderExecutionStage.Pixel)
                    {
                        attachments.Add(CreateColorAttachment(variant.Key, entry.Name, entry.ColorTargetCount));
                    }
                }

                ShaderProgramCompilation program = m_ProgramCompiler.Compile(
                    new ShaderProgramCompileRequest(
                        translation.Hlsl,
                        sourceName,
                        entries,
                        new[] { new ShaderProgramVariant(variant.Key, variant.Defines) },
                        targets,
                        effective.ShaderModel,
                        enableDebugInfo: effective.EnableDebugInfo,
                        disableOptimizations: effective.DisableOptimizations,
                        optimizationLevel: effective.OptimizationLevel,
                        attachmentInterfaces: attachments),
                    cancellationToken);
                variants.Add(new CSharpShaderVariantCompilation(variant.Key, translation, program));
            }

            return new CSharpShaderCompilation(variants);
        }

        private static ShaderExecutionStage MapStage(CSharpShaderStage stage)
        {
            return stage switch
            {
                CSharpShaderStage.Compute => ShaderExecutionStage.Compute,
                CSharpShaderStage.Vertex => ShaderExecutionStage.Vertex,
                CSharpShaderStage.Fragment => ShaderExecutionStage.Pixel,
                _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unsupported SharpSL stage."),
            };
        }

        private static ShaderAttachmentInterface CreateColorAttachment(
            string variantKey,
            string entryName,
            int colorTargetCount)
        {
            int count = colorTargetCount <= 0 ? 1 : colorTargetCount;
            ShaderAttachmentDeclaration[] declarations = new ShaderAttachmentDeclaration[count];
            for (int index = 0; index < count; ++index)
            {
                declarations[index] = new ShaderAttachmentDeclaration(
                    logicalAttachmentId: (uint)index,
                    inputIndex: null,
                    outputLocation: (uint)index,
                    ShaderAttachmentAspect.Color,
                    ShaderAttachmentNumericClass.FloatingPoint,
                    ShaderAttachmentSampleMode.SingleSample,
                    ShaderAttachmentLayerMode.SingleLayer);
            }

            return new ShaderAttachmentInterface(
                variantKey,
                entryName,
                ShaderExecutionStage.Pixel,
                new ShaderAttachmentPhase(0, declarations));
        }
    }
}
