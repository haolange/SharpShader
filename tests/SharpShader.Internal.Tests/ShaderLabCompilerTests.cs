using Xunit;
using System;
using System.IO;
using System.Linq;
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
