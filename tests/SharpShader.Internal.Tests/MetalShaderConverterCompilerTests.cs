using Xunit;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Xunit.Abstractions;
using System.Threading.Tasks;
using System.Collections.Generic;
using SharpShader.HLSLCrossCompiler;
using SharpShader.HLSLCrossCompiler.Internal;

namespace Infinity.Rendering.Tests
{
    [Collection("MetalShaderConverterSerial")]
    public sealed class MetalShaderConverterCompilerTests
    {
        private const string ComputeSource = """
            RWByteAddressBuffer Output : register(u0);

            [numthreads(1, 1, 1)]
            void CSMain()
            {
                Output.Store(0, 42);
            }
            """;

        private readonly ITestOutputHelper m_Output;

        public MetalShaderConverterCompilerTests(ITestOutputHelper output)
        {
            m_Output = output;
        }

        [Fact]
        public void InstalledConverter_ShouldProduceValidatedMetallibAndReflection()
        {
            if (!TryResolveInstalledConverter(out string converterPath))
            {
                m_Output.WriteLine("SKIP: Metal Shader Converter is not installed at the canonical Windows location.");
                return;
            }

            ShaderCompileResult result = HLSLCrossCompiler.CompileMetalLibrary(
                CreateRequest(converterPath));

            Assert.True(result.Bytecode.Length >= 4);
            Assert.Equal("MTLB", Encoding.ASCII.GetString(result.Bytecode, 0, 4));
            Assert.NotEmpty(result.ReflectionData);
            using JsonDocument reflection = JsonDocument.Parse(result.ReflectionData);
            Assert.True(
                reflection.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array,
                $"Unexpected reflection root kind: {reflection.RootElement.ValueKind}.");
            m_Output.WriteLine(
                $"Validated Metal Shader Converter output: metallib={result.Bytecode.Length} bytes, reflection={result.ReflectionData.Length} bytes.");
        }

        [Fact]
        public void ConfiguredConverterPath_ShouldRequireAbsolutePath()
        {
            ShaderCompileRequest request = CreateRequest("metal-shaderconverter.exe");
            ShaderCompilerExecutionContext context = CreateDxilOverrideContext();

            ShaderCompilerException exception = Assert.Throws<ShaderCompilerException>(
                () => HLSLCrossCompiler.CompileForTesting(request, context));

            Assert.Equal(ShaderCompilerErrorCode.InvalidRequest, exception.ErrorCode);
            Assert.Contains("fully-qualified path", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void PreCanceledRequest_ShouldNotInvokeDxilCompilerOrCreateTemporaryDirectory()
        {
            using CancellationTokenSource cancellationSource = new CancellationTokenSource();
            cancellationSource.Cancel();
            bool nativeCompileInvoked = false;
            ShaderCompileRequest request = CreateRequest(GetExistingExecutablePath()) with
            {
                CancellationToken = cancellationSource.Token,
            };
            ShaderCompilerExecutionContext context = new ShaderCompilerExecutionContext
            {
                NativeCompileOverride = _ =>
                {
                    nativeCompileInvoked = true;
                    return CreateFakeDxilResult();
                },
            };
            HashSet<string> before = SnapshotTemporaryDirectories();

            Assert.ThrowsAny<OperationCanceledException>(
                () => HLSLCrossCompiler.CompileForTesting(request, context));

            Assert.False(nativeCompileInvoked);
            AssertNoNewTemporaryDirectories(before);
        }

        [Fact]
        public void InvalidExecutable_ShouldReportLaunchFailureAndCleanTemporaryDirectory()
        {
            if (!OperatingSystem.IsWindows())
            {
                m_Output.WriteLine("SKIP: invalid Win32 executable launch semantics are Windows-qualified.");
                return;
            }

            string testDirectory = CreateTestDirectory();
            string invalidExecutablePath = Path.Combine(testDirectory, "invalid-converter.exe");
            File.WriteAllText(invalidExecutablePath, "not a Windows executable");
            HashSet<string> before = SnapshotTemporaryDirectories();

            try
            {
                ShaderCompilerException exception = Assert.Throws<ShaderCompilerException>(
                    () => HLSLCrossCompiler.CompileForTesting(
                        CreateRequest(invalidExecutablePath),
                        CreateDxilOverrideContext()));

                Assert.Equal(ShaderCompilerErrorCode.ToolLaunchFailed, exception.ErrorCode);
                AssertNoNewTemporaryDirectories(before);
            }
            finally
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }

        [Fact]
        public void ConverterExitValidation_ShouldRejectNonZeroExitCode()
        {
            ShaderCompilerException exception = Assert.Throws<ShaderCompilerException>(
                () => MetalShaderConverterCompiler.ValidateConverterExitCode(
                    17,
                    "fake stderr",
                    "cs_6_6"));

            Assert.Equal(ShaderCompilerErrorCode.CompileFailed, exception.ErrorCode);
            Assert.Contains("exited with code 17", exception.Message, StringComparison.Ordinal);
            Assert.Equal("fake stderr", exception.Diagnostics);
        }

        [Fact]
        public void OutputReader_ShouldRejectMissingEmptyAndOversizeFiles()
        {
            string testDirectory = CreateTestDirectory();
            string missingPath = Path.Combine(testDirectory, "missing.metallib");
            string emptyPath = Path.Combine(testDirectory, "empty.metallib");
            string oversizePath = Path.Combine(testDirectory, "oversize.metallib");

            try
            {
                using (File.Create(emptyPath))
                {
                }

                using (FileStream oversizeStream = new FileStream(
                    oversizePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    oversizeStream.SetLength(9);
                }

                ShaderCompilerException missing = Assert.Throws<ShaderCompilerException>(
                    () => MetalShaderConverterCompiler.ReadBoundedOutputFile(
                        missingPath,
                        "metallib",
                        8,
                        "diagnostics",
                        "cs_6_6"));
                ShaderCompilerException empty = Assert.Throws<ShaderCompilerException>(
                    () => MetalShaderConverterCompiler.ReadBoundedOutputFile(
                        emptyPath,
                        "metallib",
                        8,
                        "diagnostics",
                        "cs_6_6"));
                ShaderCompilerException oversize = Assert.Throws<ShaderCompilerException>(
                    () => MetalShaderConverterCompiler.ReadBoundedOutputFile(
                        oversizePath,
                        "metallib",
                        8,
                        "diagnostics",
                        "cs_6_6"));

                Assert.Contains("did not produce", missing.Message, StringComparison.Ordinal);
                Assert.Contains("empty metallib", empty.Message, StringComparison.Ordinal);
                Assert.Contains("exceeding the 8-byte limit", oversize.Message, StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }

        [Fact]
        public void OutputValidators_ShouldRejectMalformedMetallibAndReflection()
        {
            ShaderCompilerException metallib = Assert.Throws<ShaderCompilerException>(
                () => MetalShaderConverterCompiler.ValidateMetalLibrary(
                    Encoding.ASCII.GetBytes("NOT-A-METALLIB"),
                    "diagnostics",
                    "cs_6_6"));
            ShaderCompilerException invalidJson = Assert.Throws<ShaderCompilerException>(
                () => MetalShaderConverterCompiler.ValidateReflection(
                    Encoding.UTF8.GetBytes("{ invalid json"),
                    "diagnostics",
                    "cs_6_6"));
            ShaderCompilerException scalarJson = Assert.Throws<ShaderCompilerException>(
                () => MetalShaderConverterCompiler.ValidateReflection(
                    Encoding.UTF8.GetBytes("42"),
                    "diagnostics",
                    "cs_6_6"));

            Assert.Contains("MTLB header", metallib.Message, StringComparison.Ordinal);
            Assert.Contains("invalid reflection JSON", invalidJson.Message, StringComparison.Ordinal);
            Assert.Contains("root must be an object or array", scalarJson.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task BoundedOutputCapture_ShouldDrainWithoutUnboundedRetention()
        {
            string source = new string('x', 4096);
            using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(source));
            using StreamReader reader = new StreamReader(stream, Encoding.UTF8);

            string captured = await MetalShaderConverterCompiler.ReadBoundedAsync(reader, 64);

            Assert.StartsWith(new string('x', 64), captured, StringComparison.Ordinal);
            Assert.Contains("omitted 4032 characters", captured, StringComparison.Ordinal);
            Assert.True(captured.Length < 160, $"Bounded capture retained {captured.Length} characters.");
        }

        [Fact]
        public void InstalledConverter_ShouldHonorTimeoutAndCleanTemporaryDirectory()
        {
            if (!TryResolveInstalledConverter(out string converterPath))
            {
                m_Output.WriteLine("SKIP: Metal Shader Converter is required for the qualified timeout path.");
                return;
            }

            ShaderCompileRequest request = CreateRequest(converterPath) with
            {
                MetalShaderConverterTimeout = TimeSpan.FromMilliseconds(1),
            };
            HashSet<string> before = SnapshotTemporaryDirectories();

            ShaderCompilerException exception = Assert.Throws<ShaderCompilerException>(
                () => HLSLCrossCompiler.CompileForTesting(
                    request,
                    CreateDxilOverrideContext()));

            Assert.Equal(ShaderCompilerErrorCode.ToolTimedOut, exception.ErrorCode);
            AssertNoNewTemporaryDirectories(before);
        }

        [Fact]
        public void InstalledConverter_ShouldHonorCancellationAndCleanTemporaryDirectory()
        {
            if (!TryResolveInstalledConverter(out string converterPath))
            {
                m_Output.WriteLine("SKIP: Metal Shader Converter is required for the qualified cancellation path.");
                return;
            }

            using CancellationTokenSource cancellationSource = new CancellationTokenSource();
            cancellationSource.CancelAfter(TimeSpan.FromMilliseconds(1));
            ShaderCompileRequest request = CreateRequest(converterPath) with
            {
                CancellationToken = cancellationSource.Token,
            };
            HashSet<string> before = SnapshotTemporaryDirectories();

            Assert.ThrowsAny<OperationCanceledException>(
                () => HLSLCrossCompiler.CompileForTesting(
                    request,
                    CreateDxilOverrideContext()));

            AssertNoNewTemporaryDirectories(before);
        }

        private static ShaderCompileRequest CreateRequest(string converterPath)
        {
            return new ShaderCompileRequest
            {
                Source = ComputeSource,
                SourceName = "MetalShaderConverterCompilerTests.hlsl",
                EntryPoint = "CSMain",
                Stage = ShaderStageKind.Compute,
                ShaderModel = new ShaderModelVersion(6, 6),
                Target = ShaderTargetKind.MetalLibrary,
                MetalShaderConverterPath = converterPath,
                MetalShaderConverterTimeout = TimeSpan.FromSeconds(30),
                Enable16BitTypes = true,
                MslOptions = new MslCompileOptions
                {
                    Platform = MslTargetPlatform.MacOS,
                },
            };
        }

        private static ShaderCompilerExecutionContext CreateDxilOverrideContext()
        {
            return new ShaderCompilerExecutionContext
            {
                NativeCompileOverride = _ => CreateFakeDxilResult(),
            };
        }

        private static ShaderCompileResult CreateFakeDxilResult()
        {
            return new ShaderCompileResult
            {
                Bytecode = Encoding.ASCII.GetBytes("DXIL"),
            };
        }

        private static bool TryResolveInstalledConverter(out string converterPath)
        {
            converterPath = string.Empty;
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            string programFilesPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrWhiteSpace(programFilesPath))
            {
                return false;
            }

            string candidatePath = Path.Combine(
                programFilesPath,
                "Metal Shader Converter",
                "bin",
                "metal-shaderconverter.exe");
            if (!File.Exists(candidatePath))
            {
                return false;
            }

            converterPath = Path.GetFullPath(candidatePath);
            return true;
        }

        private static string GetExistingExecutablePath()
        {
            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "where.exe");
            }

            return Environment.ProcessPath ??
                throw new InvalidOperationException("Current process path is unavailable.");
        }

        private static string CreateTestDirectory()
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                $"SharpShader-MetalShaderConverterTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        private static HashSet<string> SnapshotTemporaryDirectories()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "Infinity",
                "SharpShader",
                "MetalShaderConverter");
            if (!Directory.Exists(root))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return Directory.GetDirectories(root)
                .Select(Path.GetFileName)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static void AssertNoNewTemporaryDirectories(HashSet<string> before)
        {
            HashSet<string> after = SnapshotTemporaryDirectories();
            string[] leakedDirectories = after
                .Except(before, StringComparer.OrdinalIgnoreCase)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.Empty(leakedDirectories);
        }
    }

    [CollectionDefinition("MetalShaderConverterSerial", DisableParallelization = true)]
    public sealed class MetalShaderConverterSerialCollection
    {
    }
}
