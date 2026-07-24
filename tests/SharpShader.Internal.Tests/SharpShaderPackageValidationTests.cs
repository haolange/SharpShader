using System;
using System.IO;
using System.Linq;
using SharpShader.Tool;
using Xunit;

namespace Infinity.Rendering.Tests
{
    public sealed class SharpShaderPackageValidationTests
    {
        [Fact]
        public void ValidateManifestPathValidatesTheCompleteArtifactPackage()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using TemporaryDirectory temporary = new();
            string package = CompilePackage(temporary.Path);
            string manifest = Path.Combine(package, "manifest.json");

            CliResult valid = Execute("validate", manifest);
            Assert.Equal(0, valid.ExitCode);
            Assert.Equal(string.Empty, valid.Error);

            string artifact = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(package, "artifacts"),
                "*",
                SearchOption.TopDirectoryOnly));
            File.Delete(artifact);

            CliResult missing = Execute("validate", manifest);
            Assert.Equal(1, missing.ExitCode);
            Assert.Contains(
                "missing",
                missing.Error,
                StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ValidateRejectsEveryEntryOutsideTheCanonicalPackageTree()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using TemporaryDirectory temporary = new();
            string package = CompilePackage(temporary.Path);
            string extraFile = Path.Combine(package, "owner.txt");
            File.WriteAllText(extraFile, "not part of a shader package");
            AssertPackageFailure(package, "unexpected root entry");
            File.Delete(extraFile);

            string extraDirectory = Path.Combine(package, "metadata");
            Directory.CreateDirectory(extraDirectory);
            AssertPackageFailure(package, "unexpected root entry");
            Directory.Delete(extraDirectory);

            string artifacts = Path.Combine(package, "artifacts");
            string nestedDirectory = Path.Combine(artifacts, "nested");
            Directory.CreateDirectory(nestedDirectory);
            AssertPackageFailure(package, "unexpected");
            Directory.Delete(nestedDirectory);

            string extraArtifact = Path.Combine(artifacts, "extra.dxil");
            File.WriteAllBytes(extraArtifact, new byte[] { 1 });
            AssertPackageFailure(package, "unexpected");
        }

        [Fact]
        public void CompileAndValidateRejectOversizeInputsBeforeReadingPayloads()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using TemporaryDirectory temporary = new();
            string source = Path.Combine(temporary.Path, "oversize.compute");
            using (FileStream stream = new(
                       source,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.SetLength(64L * 1024 * 1024 + 1);
            }

            string output = Path.Combine(temporary.Path, "oversize-package");
            CliResult sourceFailure = Execute(
                "compile",
                "--source",
                source,
                "--entry",
                "compute:Main",
                "--target",
                "dx12",
                "--output",
                output);
            Assert.Equal(1, sourceFailure.ExitCode);
            Assert.Contains("64", sourceFailure.Error);
            Assert.Contains("limit", sourceFailure.Error);
            Assert.False(Directory.Exists(output));

            string package = Path.Combine(temporary.Path, "manifest-package");
            Directory.CreateDirectory(package);
            Directory.CreateDirectory(Path.Combine(package, "artifacts"));
            string manifest = Path.Combine(package, "manifest.json");
            using (FileStream stream = new(
                       manifest,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.SetLength(64L * 1024 * 1024 + 1);
            }

            CliResult manifestFailure = Execute("validate", manifest);
            Assert.Equal(1, manifestFailure.ExitCode);
            Assert.Contains("limit", manifestFailure.Error);
        }

        [Fact]
        public void ValidateRejectsOversizeArtifactBeforeHashing()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using TemporaryDirectory temporary = new();
            string package = CompilePackage(temporary.Path);
            string artifact = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(package, "artifacts"),
                "*",
                SearchOption.TopDirectoryOnly));
            using (FileStream stream = new(
                       artifact,
                       FileMode.Open,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.SetLength(1024L * 1024 * 1024 + 1);
            }

            CliResult failure = Execute("validate", package);
            Assert.Equal(1, failure.ExitCode);
            Assert.Contains("exceeds", failure.Error);
            Assert.Contains("limit", failure.Error);
        }

        private static string CompilePackage(string root)
        {
            string source = Path.Combine(root, "program.compute");
            string package = Path.Combine(root, "package");
            File.WriteAllText(
                source,
                "RWStructuredBuffer<uint> Output : register(u0); "
                + "[numthreads(1,1,1)] void Main() { Output[0] = 101; }");
            CliResult result = Execute(
                "compile",
                "--source",
                source,
                "--entry",
                "compute:Main",
                "--target",
                "dx12",
                "--output",
                package);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.Error);
            return package;
        }

        private static void AssertPackageFailure(
            string package,
            string expectedMessage)
        {
            CliResult result = Execute("validate", package);
            Assert.Equal(1, result.ExitCode);
            Assert.Contains(
                expectedMessage,
                result.Error,
                StringComparison.OrdinalIgnoreCase);
        }

        private static CliResult Execute(params string[] arguments)
        {
            using StringWriter output = new();
            using StringWriter error = new();
            int exitCode = SharpShaderCli.Execute(arguments, output, error);
            return new CliResult(
                exitCode,
                output.ToString(),
                error.ToString());
        }

        private readonly record struct CliResult(
            int ExitCode,
            string Output,
            string Error);

        private sealed class TemporaryDirectory : IDisposable
        {
            public string Path { get; }

            public TemporaryDirectory()
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "SharpShader.PackageValidation.Tests",
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
