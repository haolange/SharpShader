using Xunit;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using SharpShader.Compilation;
using SharpShader.Compilation.Internal;
using SharpShader.HLSLCrossCompiler;
using SharpShader.ShaderLab;

namespace Infinity.Rendering.Tests
{
    public sealed class ShaderLabCompilerTests
    {
        [Fact]
        public void CompileStandalone_ShouldUseArtifactReflectionForBindingsAndVariants()
        {
            const string source = @"
#pragma kernel Main
#pragma multi_compile _ USE_TINT

// Texture2D<float4> CommentTexture : register(t7, space7);
/* cbuffer CommentConstants : register(b7, space7)
{
    float4 CommentColor;
}; */
#define UNUSED_RESOURCE Texture2D<float4> MacroTexture : register(t8, space8)

cbuffer Constants : register(b0, space3)
{
    float4 Tint;
};
Texture2D<float4> Input : register(t0, space3);
SamplerState LinearSampler : register(s0, space3);
RWStructuredBuffer<float4> Output : register(u0, space3);

[numthreads(1, 1, 1)]
void Main(uint3 id : SV_DispatchThreadID)
{
    Output[id.x] = Input.SampleLevel(
        LinearSampler,
        float2(0.5, 0.5),
        0.0) + Tint;
}
";
            StandaloneShaderProgram program =
                ShaderLabUtil.ParseComputeProgramFromSource(source);

            ShaderProgramCompilation compilation =
                ShaderLabCompiler.Shared.CompileStandalone(
                    program,
                    new ShaderLabCompilerOptions(
                        ShaderProgramTarget.DirectX12));

            Assert.Equal(2, compilation.Manifest.Variants.Count);
            Assert.Contains(
                compilation.Manifest.Variants,
                static variant => variant.Key == "default");
            Assert.Contains(
                compilation.Manifest.Variants,
                static variant => variant.Key == "USE_TINT");

            ShaderInterfaceLayout layout =
                Assert.Single(compilation.Manifest.LogicalLayouts);
            Assert.Equal(4, layout.Bindings.Count);
            Assert.DoesNotContain(layout.Bindings, static binding =>
                binding.CanonicalName is "CommentTexture"
                    or "CommentConstants"
                    or "MacroTexture");

            Assert.Contains(layout.Bindings, static binding =>
                binding.Key == new ShaderBindingKey(
                    3,
                    0,
                    ShaderBindingClass.ConstantBuffer));
            Assert.Contains(layout.Bindings, static binding =>
                binding.Key == new ShaderBindingKey(
                    3,
                    0,
                    ShaderBindingClass.ShaderResource));
            Assert.Contains(layout.Bindings, static binding =>
                binding.Key == new ShaderBindingKey(
                    3,
                    0,
                    ShaderBindingClass.Sampler));
            Assert.Contains(layout.Bindings, static binding =>
                binding.Key == new ShaderBindingKey(
                    3,
                    0,
                    ShaderBindingClass.UnorderedAccess));

            ShaderBackendLayouts backendLayout =
                Assert.Single(compilation.Manifest.BackendLayouts);
            Assert.NotNull(backendLayout.Dx12);
            Assert.Equal(4, backendLayout.Dx12!.Bindings.Count);
            Assert.All(backendLayout.Dx12.Bindings, static mapping =>
            {
                Assert.Equal((uint)3, mapping.RegisterSpace);
                Assert.Equal((uint)0, mapping.ShaderRegister);
                Assert.Equal(
                    mapping.LogicalBinding.Type,
                    mapping.RegisterClass);
            });
        }

        [Fact]
        public void CompileProgram_ShouldFailClosedForUnknownStage()
        {
            ShaderLabProgram program = new ShaderLabProgram
            {
                Source = "float4 Main() : SV_Target { return 1; }",
            };
            program.Entries.Add(new ShaderLabProgramEntry
            {
                Stage = EShaderLabShaderStage.Undefined,
                EntryName = "Main",
            });

            ArgumentOutOfRangeException exception =
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    ShaderLabCompiler.Shared.CompileProgram(
                        program,
                        "unknown-stage.hlsl",
                        "<memory>",
                        new ShaderLabCompilerOptions(
                            ShaderProgramTarget.DirectX12)));
            Assert.Contains(
                "stage",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void CompileStandalone_RayLibraryRequiresExplicitDx12Target()
        {
            const string source = @"
#pragma raygeneration RayGen

[shader(""raygeneration"")]
void RayGen()
{
}
";
            StandaloneShaderProgram program =
                ShaderLabUtil.ParseRayTraceProgramFromSource(source);

            ShaderCompilerException exception =
                Assert.Throws<ShaderCompilerException>(() =>
                    ShaderLabCompiler.Shared.CompileStandalone(
                        program,
                        new ShaderLabCompilerOptions()));
            Assert.Equal(
                ShaderCompilerErrorCode.InvalidRequest,
                exception.ErrorCode);
            Assert.Contains(
                "All",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Contains(
                "RayGen (RayGeneration)",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Contains(
                "DirectX12 only",
                exception.Message,
                StringComparison.Ordinal);

            ShaderProgramCompilation compilation =
                ShaderLabCompiler.Shared.CompileStandalone(
                    program,
                    new ShaderLabCompilerOptions(
                        ShaderProgramTarget.DirectX12));
            Assert.False(compilation.GetArtifact(
                "default",
                "RayGen",
                ShaderExecutionStage.RayGeneration,
                ShaderArtifactKind.Dxil).Content.IsEmpty);
        }

        [Fact]
        public void Compile_PhysicalSource_ShouldUseAbsolutePassOriginAndPreserveIncludeOrder()
        {
            using TemporaryDirectory directory = new TemporaryDirectory();
            string secondInclude = Path.Combine(directory.Path, "z-second");
            string firstInclude = Path.Combine(directory.Path, "a-first");
            Directory.CreateDirectory(secondInclude);
            Directory.CreateDirectory(firstInclude);
            string sourcePath = Path.Combine(directory.Path, "Material.shader");
            ShaderCompileRequest? capturedRequest = null;
            ShaderLabCompiler compiler = CreateCapturingCompiler(
                request => capturedRequest = request);

            CaptureCompleteException exception =
                Assert.Throws<CaptureCompleteException>(() =>
                    compiler.Compile(
                        CreateShaderLab(sourcePath),
                        new ShaderLabCompilerOptions(
                            targets: ShaderProgramTarget.DirectX12,
                            includeDirectories: new[]
                            {
                                secondInclude,
                                firstInclude,
                            })));

            Assert.NotNull(exception);
            Assert.NotNull(capturedRequest);
            string expectedSourceName = Path.Combine(
                directory.Path,
                "Material.pass-0-Forward_Main.hlsl");
            Assert.True(Path.IsPathFullyQualified(capturedRequest!.SourceName));
            Assert.Equal(expectedSourceName, capturedRequest.SourceName);
            Assert.Equal(
                new[]
                {
                    directory.Path,
                    secondInclude,
                    firstInclude,
                },
                capturedRequest.IncludeDirs);
        }

        [Fact]
        public void CompileStandalone_ShouldRejectRelativeAndDuplicateIncludeRoots()
        {
            ShaderLabCompiler compiler = new ShaderLabCompiler();
            StandaloneShaderProgram memoryProgram =
                CreateStandaloneProgram("<memory>");
            ArgumentException relative = Assert.Throws<ArgumentException>(() =>
                compiler.CompileStandalone(
                    memoryProgram,
                    new ShaderLabCompilerOptions(
                        targets: ShaderProgramTarget.DirectX12,
                        includeDirectories: new[] { "relative-root" })));
            Assert.Contains(
                "absolute path",
                relative.Message,
                StringComparison.Ordinal);

            using TemporaryDirectory directory = new TemporaryDirectory();
            StandaloneShaderProgram physicalProgram = CreateStandaloneProgram(
                Path.Combine(directory.Path, "Source.compute"));
            ArgumentException duplicate = Assert.Throws<ArgumentException>(() =>
                compiler.CompileStandalone(
                    physicalProgram,
                    new ShaderLabCompilerOptions(
                        targets: ShaderProgramTarget.DirectX12,
                        includeDirectories: new[] { directory.Path })));
            Assert.Contains(
                "duplicated",
                duplicate.Message,
                StringComparison.Ordinal);
        }

        [Fact]
        public void CompileStandalone_ShouldRejectReparseIncludeRoot()
        {
            using TemporaryDirectory directory = new TemporaryDirectory();
            string targetPath = Path.Combine(directory.Path, "target");
            string linkPath = Path.Combine(directory.Path, "linked");
            Directory.CreateDirectory(targetPath);
            if (!TryCreateDirectorySymbolicLink(linkPath, targetPath))
            {
                return;
            }

            ShaderLabCompiler compiler = new ShaderLabCompiler();
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                compiler.CompileStandalone(
                    CreateStandaloneProgram("<memory>"),
                    new ShaderLabCompilerOptions(
                        targets: ShaderProgramTarget.DirectX12,
                        includeDirectories: new[] { linkPath })));
            Assert.Contains(
                "reparse point",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public void AttachmentInterfaceParser_ShouldIgnoreCommentAndStringNoise()
        {
            string source = CreateAttachmentShaderLabSource()
                .Replace(
                    "            AttachmentInterface",
                    "            // AttachmentInterface { Unknown 1 }\n"
                    + "            /* AttachmentInterface { Phase 99 } */\n"
                    + "            AttachmentInterface",
                    StringComparison.Ordinal)
                .Replace(
                    "            ENDHLSL",
                    "            // AttachmentInterface in HLSL comment\n"
                    + "            static const char* kAttachmentNoise = "
                    + "\"AttachmentInterface { Missing }\";\n"
                    + "            ENDHLSL",
                    StringComparison.Ordinal);

            ShaderLab shader = ShaderLabUtil.ParseShaderLabFromSource(source);
            ShaderAttachmentPhase phase = Assert.IsType<ShaderAttachmentPhase>(
                Assert.Single(shader.Passes).Program.AttachmentPhase);
            Assert.Equal(0u, phase.Phase);
            Assert.Single(phase.Attachments);
        }

        [Theory]
        [InlineData("unknown")]
        [InlineData("duplicate")]
        [InlineData("missing")]
        [Trait("Category", "SharpShaderAttachment")]
        public void AttachmentInterfaceParser_ShouldRejectMalformedFields(
            string mutation)
        {
            string source = CreateAttachmentShaderLabSource();
            source = mutation switch
            {
                "unknown" => source.Replace(
                    "Phase 0",
                    "Phase 0\nUnknownField 1",
                    StringComparison.Ordinal),
                "duplicate" => source.Replace(
                    "Phase 0",
                    "Phase 0\nPhase 0",
                    StringComparison.Ordinal),
                "missing" => source.Replace(
                    "LayerMode SingleLayer",
                    string.Empty,
                    StringComparison.Ordinal),
                _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
            };

            Assert.ThrowsAny<FormatException>(() =>
                ShaderLabUtil.ParseShaderLabFromSource(source));
        }

        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public void Compile_ShouldBuildAllFourteenCanonicalGraphicsAttachmentPasses()
        {
            string[] paths =
            {
                ResolveShaderPath(
                    "Shaders", "ShaderLab", "Global", "DrawFullScreen.shader"),
                ResolveShaderPath(
                    "Shaders", "ShaderLab", "Global", "DrawSystemLUT.shader"),
                ResolveShaderPath(
                    "Shaders", "ShaderLab", "Global", "HybridFullscreen.shader"),
                ResolveShaderPath(
                    "Shaders", "ShaderLab", "Material", "InfinityLit.shader"),
            };
            int graphicsPassCount = 0;
            foreach (string path in paths)
            {
                ShaderLab shader = ShaderLabUtil.ParseShaderLabFromFile(path);
                var graphicsPasses = shader.Passes
                    .Select(static (pass, index) => new
                    {
                        Pass = pass,
                        Index = index,
                    })
                    .Where(static item => item.Pass.Program.Entries.Any(
                        static entry => entry.Stage
                            == EShaderLabShaderStage.ProgramFragment));
                foreach (var graphicsPass in graphicsPasses)
                {
                    string sourceName =
                        $"{path}.pass-{graphicsPass.Index}.hlsl";
                    ShaderProgramCompilation compilation;
                    try
                    {
                        compilation = ShaderLabCompiler.Shared.CompileProgram(
                            graphicsPass.Pass.Program,
                            sourceName,
                            shader.SourcePath,
                            new ShaderLabCompilerOptions(
                                ShaderProgramTarget.All));
                    }
                    catch (ShaderCompilerException exception)
                    {
                        throw new InvalidOperationException(
                            $"Canonical ShaderLab pass compile failed for "
                            + $"'{path}' pass {graphicsPass.Index}: "
                            + exception.Diagnostics,
                            exception);
                    }

                    ++graphicsPassCount;
                    Assert.Equal(
                        ShaderProgramTarget.All,
                        compilation.Manifest.Targets);
                    ShaderAttachmentPhase expectedPhase =
                        Assert.IsType<ShaderAttachmentPhase>(
                            graphicsPass.Pass.Program.AttachmentPhase);
                    foreach (ShaderInterfaceVariant variant in
                             compilation.Manifest.Variants)
                    {
                        ShaderInterfaceEntry pixelEntry = Assert.Single(
                            variant.Entries,
                            static entry => entry.Stage
                                == ShaderExecutionStage.Pixel);
                        ShaderAttachmentPhase actualPhase =
                            Assert.IsType<ShaderAttachmentPhase>(
                                pixelEntry.AttachmentInterface.Phase);
                        Assert.Equal(expectedPhase, actualPhase);
                        Assert.Equal(
                            new[]
                            {
                                ShaderArtifactKind.Dxil,
                                ShaderArtifactKind.SpirV,
                                ShaderArtifactKind.MslSource,
                            },
                            pixelEntry.Artifacts.Select(static artifact =>
                                artifact.ArtifactKind));

                        ShaderProgramArtifact dxil = AssertExactArtifact(
                            compilation,
                            variant.Key,
                            pixelEntry,
                            ShaderArtifactKind.Dxil);
                        ShaderProgramArtifact spirv = AssertExactArtifact(
                            compilation,
                            variant.Key,
                            pixelEntry,
                            ShaderArtifactKind.SpirV);
                        ShaderProgramArtifact msl = AssertExactArtifact(
                            compilation,
                            variant.Key,
                            pixelEntry,
                            ShaderArtifactKind.MslSource);

                        AssertReflectionMatchesPhase(
                            ReflectArtifact(
                                graphicsPass.Pass.Program,
                                shader.SourcePath,
                                sourceName,
                                variant,
                                pixelEntry,
                                dxil,
                                ShaderTargetKind.Dxil),
                            ShaderArtifactKind.Dxil,
                            pixelEntry,
                            actualPhase);
                        AssertReflectionMatchesPhase(
                            ReflectArtifact(
                                graphicsPass.Pass.Program,
                                shader.SourcePath,
                                sourceName,
                                variant,
                                pixelEntry,
                                spirv,
                                ShaderTargetKind.SpirV),
                            ShaderArtifactKind.SpirV,
                            pixelEntry,
                            actualPhase);
                        AssertMslMatchesPhase(msl, pixelEntry, actualPhase);
                    }
                }
            }

            Assert.Equal(14, graphicsPassCount);
        }

        private static ShaderProgramArtifact AssertExactArtifact(
            ShaderProgramCompilation compilation,
            string variantKey,
            ShaderInterfaceEntry entry,
            ShaderArtifactKind artifactKind)
        {
            ShaderArtifactIdentity identity = Assert.Single(
                entry.Artifacts,
                artifact => artifact.ArtifactKind == artifactKind);
            ShaderProgramArtifact artifact = compilation.GetArtifact(
                variantKey,
                entry.Name,
                entry.Stage,
                artifactKind);
            Assert.Equal(identity, artifact.Identity);
            Assert.False(artifact.Content.IsEmpty);
            Assert.Equal(
                identity.ContentDigest,
                Convert.ToHexStringLower(SHA256.HashData(
                    artifact.Content.Span)));
            Assert.Equal(
                identity.ByteLength,
                checked((ulong)artifact.Content.Length));
            return artifact;
        }

        private static ShaderArtifactReflection ReflectArtifact(
            ShaderLabProgram program,
            string sourcePath,
            string sourceName,
            ShaderInterfaceVariant variant,
            ShaderInterfaceEntry entry,
            ShaderProgramArtifact artifact,
            ShaderTargetKind target)
        {
            ShaderCompileRequest request = new()
            {
                Source = program.Source,
                SourceName = sourceName,
                EntryPoint = entry.Name,
                Stage = ShaderStageKind.Pixel,
                ShaderModel = new ShaderModelVersion(6, 8),
                Target = target,
                Defines = variant.Defines.Select(static define =>
                    new ShaderDefine(define, "1")).ToArray(),
                IncludeDirs = new[]
                {
                    Path.GetDirectoryName(sourcePath)
                        ?? throw new InvalidOperationException(
                            "Canonical shader source requires a directory."),
                },
            };
            if (target == ShaderTargetKind.Dxil)
            {
                ShaderCompileResult compiled = HLSLCrossCompiler.Compile(request);
                Assert.Equal(artifact.Content.ToArray(), compiled.Bytecode);
                return DxilArtifactReflector.Reflect(request, compiled);
            }

            if (target == ShaderTargetKind.SpirV)
            {
                return SpirvArtifactReflector.Reflect(
                    request,
                    new ShaderCompileResult
                    {
                        Bytecode = artifact.Content.ToArray(),
                    });
            }

            throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "Canonical artifact reflection target is not supported.");
        }

        private static void AssertReflectionMatchesPhase(
            ShaderArtifactReflection reflection,
            ShaderArtifactKind expectedArtifactKind,
            ShaderInterfaceEntry entry,
            ShaderAttachmentPhase phase)
        {
            Assert.Equal(expectedArtifactKind, reflection.ArtifactKind);

            ShaderEntryPointReflection reflectedEntry = Assert.Single(
                reflection.EntryPoints);
            Assert.Equal(entry.Name, reflectedEntry.Name);
            Assert.Equal(entry.Stage, reflectedEntry.Stage);

            ShaderStageIoReflection[] reflectedOutputs = reflectedEntry.StageOutputs
                .Where(static output =>
                    output.Direction == ShaderStageIoDirection.Output
                    && output.Location.HasValue
                    && output.BuiltIn is ShaderStageIoBuiltIn.None
                        or ShaderStageIoBuiltIn.Color)
                .OrderBy(static output => output.Location)
                .ThenBy(static output => output.Index)
                .ThenBy(static output => output.Component)
                .ToArray();
            if (reflection.ArtifactKind == ShaderArtifactKind.Dxil)
            {
                var expectedDxilOutputs = phase.Attachments
                    .Where(static attachment =>
                        attachment.OutputLocation.HasValue)
                    .Select(static attachment => (
                        Target: attachment.OutputIndex == 0
                            ? attachment.OutputLocation!.Value
                            : attachment.OutputIndex,
                        attachment.OutputComponent))
                    .OrderBy(static output => output.Target)
                    .ThenBy(static output => output.OutputComponent)
                    .ToArray();
                Assert.Equal(
                    expectedDxilOutputs,
                    reflectedOutputs.Select(static output => (
                            Target: output.Location!.Value,
                            OutputComponent: output.Component))
                        .ToArray());
            }
            else
            {
                var expectedSpirvOutputs = phase.Attachments
                    .Where(static attachment =>
                        attachment.OutputLocation.HasValue)
                    .Select(static attachment => (
                        Location: attachment.OutputLocation!.Value,
                        attachment.OutputIndex,
                        attachment.OutputComponent))
                    .OrderBy(static output => output.Location)
                    .ThenBy(static output => output.OutputIndex)
                    .ThenBy(static output => output.OutputComponent)
                    .ToArray();
                Assert.Equal(
                    expectedSpirvOutputs,
                    reflectedOutputs.Select(static output => (
                            Location: output.Location!.Value,
                            OutputIndex: output.Index,
                            OutputComponent: output.Component))
                        .ToArray());

                uint[] expectedInputs = phase.Attachments
                    .Where(static attachment =>
                        attachment.InputIndex.HasValue)
                    .Select(static attachment =>
                        attachment.InputIndex!.Value)
                    .OrderBy(static index => index)
                    .ToArray();
                Assert.Equal(
                    expectedInputs,
                    reflectedEntry.InputAttachments
                        .Select(static input => input.InputAttachmentIndex)
                        .OrderBy(static index => index)
                        .ToArray());
            }
        }

        private static void AssertMslMatchesPhase(
            ShaderProgramArtifact artifact,
            ShaderInterfaceEntry entry,
            ShaderAttachmentPhase phase)
        {
            Assert.Equal(ShaderArtifactKind.MslSource, artifact.Identity.ArtifactKind);
            string source = Assert.IsType<string>(artifact.Text);
            Assert.False(string.IsNullOrWhiteSpace(source));
            MslArtifactReflection reflection = MslArtifactReflector.Reflect(
                source,
                entry.Name,
                entry.Stage);
            Assert.Equal(entry.Name, reflection.EntryPoint);
            Assert.Equal(entry.Stage, reflection.Stage);

            (uint Location, uint Index, ShaderAttachmentNumericClass NumericClass)[]
                expectedOutputs = phase.Attachments
                .Where(static attachment =>
                    attachment.OutputLocation.HasValue)
                .Select(static attachment => (
                    attachment.OutputLocation!.Value,
                    attachment.OutputIndex,
                    attachment.NumericClass))
                .Distinct()
                .OrderBy(static output => output.Value)
                .ThenBy(static output => output.OutputIndex)
                .ToArray();
            (uint Location, uint Index, ShaderAttachmentNumericClass NumericClass)[]
                actualOutputs = reflection.ColorOutputs
                .Select(static output => (
                    output.Location,
                    output.Index,
                    output.NumericClass))
                .ToArray();
            Assert.Equal(expectedOutputs, actualOutputs);

            (uint Location, ShaderAttachmentNumericClass NumericClass)[]
                expectedInputs = phase.Attachments
                .Where(static attachment =>
                    attachment.InputIndex.HasValue
                    && attachment.Ordering
                        != ShaderAttachmentOrdering.RasterOrdered)
                .Select(static attachment => (
                    attachment.LogicalAttachmentId,
                    attachment.NumericClass))
                .Distinct()
                .OrderBy(static input => input.LogicalAttachmentId)
                .ToArray();
            (uint Location, ShaderAttachmentNumericClass NumericClass)[]
                actualInputs = reflection.ColorInputs
                .Select(static input => (
                    input.Location,
                    input.NumericClass))
                .ToArray();
            Assert.Equal(expectedInputs, actualInputs);
            Assert.Equal(phase.DepthExport, reflection.DepthExport);
            Assert.Equal(phase.StencilExport, reflection.StencilExport);

            int expectedRasterOrderGroups = phase.Attachments
                .Where(static attachment =>
                    attachment.Ordering
                        == ShaderAttachmentOrdering.RasterOrdered)
                .Select(static attachment => attachment.LogicalAttachmentId)
                .Distinct()
                .Count();
            Assert.Equal(
                expectedRasterOrderGroups,
                reflection.RasterOrderGroups.Count);
            Assert.All(
                reflection.RasterOrderGroups,
                static group =>
                {
                    Assert.Equal(MslResourceBindingKind.Texture, group.BindingKind);
                    Assert.Equal(0u, group.Group);
                });
        }

        private static ShaderLabCompiler CreateCapturingCompiler(
            Action<ShaderCompileRequest> capture)
        {
            ShaderProgramCompiler compiler = new ShaderProgramCompiler(
                new ShaderProgramCompilerOptions(),
                new ShaderProgramCompilerExecutionContext
                {
                    PreprocessOverride = (request, _) =>
                    {
                        capture(request);
                        throw new CaptureCompleteException();
                    },
                    ToolchainComponentsOverride = _ => new[]
                    {
                        new ShaderToolchainComponent(
                            "ShaderLabOriginTestToolchain",
                            "1.0",
                            new string('a', 64)),
                    },
                });
            return new ShaderLabCompiler(compiler);
        }

        private static ShaderLab CreateShaderLab(string sourcePath)
        {
            ShaderLabProgram program = new ShaderLabProgram
            {
                Source = "float4 VSMain() : SV_Position { return 0; }",
            };
            program.Entries.Add(new ShaderLabProgramEntry
            {
                Stage = EShaderLabShaderStage.ProgramVertex,
                EntryName = "VSMain",
            });
            ShaderLabPass pass = new ShaderLabPass
            {
                Program = program,
            };
            pass.Tags.Add("Name", "Forward/Main");
            ShaderLab shaderLab = new ShaderLab
            {
                SourcePath = sourcePath,
            };
            shaderLab.Passes.Add(pass);
            return shaderLab;
        }

        private static string CreateAttachmentShaderLabSource()
        {
            return """
                Shader "Test/Attachment"
                {
                    Pass
                    {
                        AttachmentInterface
                        {
                            AbiRevision 1
                            Phase 0
                            DepthStencilAccess None
                            DepthExport None
                            StencilExport None
                            Attachment
                            {
                                LogicalAttachmentId 0
                                InputIndex None
                                OutputLocation 0
                                OutputIndex 0
                                OutputComponent 0
                                Aspect Color
                                NumericClass FloatingPoint
                                SampleMode SingleSample
                                LayerMode SingleLayer
                                Ordering None
                                Feedback None
                                SampledFeedbackBinding None
                            }
                        }
                        HLSLPROGRAM
                        #pragma vertex VSMain
                        #pragma fragment PSMain
                        float4 VSMain() : SV_Position { return 0; }
                        float4 PSMain() : SV_Target0 { return 1; }
                        ENDHLSL
                    }
                }
                """;
        }

        private static string ResolveShaderPath(params string[] parts)
        {
            DirectoryInfo? current = new(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(
                        current.FullName,
                        "InfinityBrowser.sln")))
                {
                    return Path.Combine(
                        new[] { current.FullName, "Engine" }
                            .Concat(parts)
                            .ToArray());
                }

                current = current.Parent;
            }

            throw new InvalidOperationException(
                "Unable to resolve repository root.");
        }
        private static StandaloneShaderProgram CreateStandaloneProgram(
            string sourcePath)
        {
            StandaloneShaderProgram program = new StandaloneShaderProgram
            {
                Kind = StandaloneShaderProgramKind.Compute,
                SourcePath = sourcePath,
                Source = "[numthreads(1,1,1)] void CSMain() { }",
            };
            program.Entries.Add(new StandaloneShaderEntry
            {
                Stage = StandaloneShaderStage.Compute,
                EntryName = "CSMain",
            });
            return program;
        }

        private static bool TryCreateDirectorySymbolicLink(
            string linkPath,
            string targetPath)
        {
            try
            {
                Directory.CreateSymbolicLink(linkPath, targetPath);
                return true;
            }
            catch (IOException exception) when (
                OperatingSystem.IsWindows()
                && (exception.HResult & 0xffff) == 1314)
            {
                return false;
            }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
            {
                return false;
            }
            catch (PlatformNotSupportedException)
            {
                return false;
            }
        }

        private sealed class CaptureCompleteException : Exception
        {
        }

        private sealed class TemporaryDirectory : IDisposable
        {
            public string Path { get; }

            public TemporaryDirectory()
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "SharpShader.ShaderLabCompiler.Tests",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public void Dispose()
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
        }
    }
}
