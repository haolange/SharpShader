using System;
using System.IO;
using System.Linq;
using SharpShader.Tool;
using Xunit;

namespace Infinity.Rendering.Tests
{
    public sealed class SharpShaderCliTests
    {
        [Fact]
        public void Execute_ShouldCompileInspectValidateAndDetectArtifactCorruption()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            string root = Path.Combine(
                Path.GetTempPath(),
                $"sharpshader-cli-{Guid.NewGuid():N}");
            string sourcePath = Path.Combine(root, "program.compute");
            string outputPath = Path.Combine(root, "package");
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(
                    sourcePath,
                    """
                    RWStructuredBuffer<uint> Output : register(u0, space4);

                    [numthreads(1, 1, 1)]
                    void Main()
                    {
                        Output[0] = 7;
                    }
                    """);

                using StringWriter compileOutput = new();
                using StringWriter compileError = new();
                int compileExit = SharpShaderCli.Execute(
                    new[]
                    {
                        "compile",
                        "--source",
                        sourcePath,
                        "--entry",
                        "compute:Main",
                        "--target",
                        "dx12",
                        "--output",
                        outputPath,
                    },
                    compileOutput,
                    compileError);

                Assert.Equal(0, compileExit);
                Assert.Equal(string.Empty, compileError.ToString());
                Assert.True(File.Exists(
                    Path.Combine(outputPath, "manifest.json")));
                string artifactPath = Assert.Single(
                    Directory.GetFiles(
                        Path.Combine(outputPath, "artifacts"),
                        "*.dxil"));

                using StringWriter inspectOutput = new();
                using StringWriter inspectError = new();
                int inspectExit = SharpShaderCli.Execute(
                    new[] { "inspect", outputPath },
                    inspectOutput,
                    inspectError);

                Assert.Equal(0, inspectExit);
                Assert.Equal(string.Empty, inspectError.ToString());
                Assert.Contains("schema: 1", inspectOutput.ToString());
                Assert.Contains(
                    "dx12",
                    inspectOutput.ToString(),
                    StringComparison.OrdinalIgnoreCase);
                Assert.Contains(
                    "u0,space4",
                    inspectOutput.ToString(),
                    StringComparison.Ordinal);

                using StringWriter validateOutput = new();
                using StringWriter validateError = new();
                int validateExit = SharpShaderCli.Execute(
                    new[] { "validate", outputPath },
                    validateOutput,
                    validateError);

                Assert.Equal(0, validateExit);
                Assert.Equal(string.Empty, validateError.ToString());
                Assert.StartsWith(
                    "valid ",
                    validateOutput.ToString(),
                    StringComparison.Ordinal);

                File.AppendAllBytes(artifactPath, new byte[] { 0x7f });
                using StringWriter corruptOutput = new();
                using StringWriter corruptError = new();
                int corruptExit = SharpShaderCli.Execute(
                    new[] { "validate", outputPath },
                    corruptOutput,
                    corruptError);

                Assert.Equal(1, corruptExit);
                Assert.Equal(string.Empty, corruptOutput.ToString());
                Assert.Contains(
                    "length mismatch",
                    corruptError.ToString(),
                    StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        [Fact]
        public void Execute_ShouldRejectAnExistingOutputBeforeCompilation()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                $"sharpshader-cli-existing-{Guid.NewGuid():N}");
            string sourcePath = Path.Combine(root, "invalid.compute");
            string outputPath = Path.Combine(root, "package");
            Directory.CreateDirectory(outputPath);
            try
            {
                File.WriteAllText(sourcePath, "this source must never compile");
                File.WriteAllText(
                    Path.Combine(outputPath, "owner.txt"),
                    "preserve");

                using StringWriter output = new();
                using StringWriter error = new();
                int exitCode = SharpShaderCli.Execute(
                    new[]
                    {
                        "compile",
                        "--source",
                        sourcePath,
                        "--entry",
                        "compute:Main",
                        "--target",
                        "dx12",
                        "--output",
                        outputPath,
                    },
                    output,
                    error);

                Assert.Equal(1, exitCode);
                Assert.Contains(
                    "already exists",
                    error.ToString(),
                    StringComparison.OrdinalIgnoreCase);
                Assert.Equal(
                    "preserve",
                    File.ReadAllText(
                        Path.Combine(outputPath, "owner.txt")));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        [Fact]
        public void Execute_ShouldReturnUsageForUnknownCommand()
        {
            using StringWriter output = new();
            using StringWriter error = new();

            int exitCode = SharpShaderCli.Execute(
                new[] { "unknown" },
                output,
                error);

            Assert.Equal(2, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Contains(
                "Unknown command",
                error.ToString(),
                StringComparison.Ordinal);
            Assert.Contains(
                "SharpShader.Tool compile",
                error.ToString(),
                StringComparison.Ordinal);
        }
    }
}
