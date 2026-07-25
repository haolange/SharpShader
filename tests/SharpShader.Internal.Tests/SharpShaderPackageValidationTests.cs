using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
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

        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public async Task SharpShaderNuGetPackage_ShouldPreserveDependencyDirectionAndResolveAttachmentAbiInFreshConsumer()
        {
            using TemporaryDirectory temporary = new();
            string repositoryRoot = ResolveRepositoryRoot();
            string projectPath = Path.Combine(
                repositoryRoot,
                "Engine",
                "Source",
                "Runtime",
                "Graphics",
                "SharpShader",
                "SharpShader.csproj");
            AssertSharpShaderDependencyDirection(repositoryRoot, projectPath);

            string platform =
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
                {
                    System.Runtime.InteropServices.Architecture.X64 => "x64",
                    System.Runtime.InteropServices.Architecture.Arm64 => "ARM64",
                    var architecture => throw new PlatformNotSupportedException(
                        $"SharpShader package qualification does not define platform {architecture}."),
                };
            string packageOutput = Path.Combine(temporary.Path, "nupkg");
            Directory.CreateDirectory(packageOutput);
            string packageVersionOverride =
                $"0.0.0-w5-{Guid.NewGuid():N}";
            ProcessResult packResult = await RunDotnetAsync(
                repositoryRoot,
                TimeSpan.FromMinutes(2),
                null,
                "pack",
                projectPath,
                "-c",
                "Release",
                $"-p:Platform={platform}",
                "-p:NuGetAudit=false",
                $"-p:PackageVersion={packageVersionOverride}",
                "--no-restore",
                "-o",
                packageOutput);
            AssertProcessSucceeded("SharpShader dotnet pack", packResult);

            string packagePath = Assert.Single(
                Directory.EnumerateFiles(
                    packageOutput,
                    "*.nupkg",
                    SearchOption.TopDirectoryOnly),
                static path => !path.EndsWith(
                    ".symbols.nupkg",
                    StringComparison.OrdinalIgnoreCase));
            string packageId;
            string packageVersion;
            using (ZipArchive package = ZipFile.OpenRead(packagePath))
            {
                ZipArchiveEntry attachmentEntry = Assert.Single(
                    package.Entries,
                    static entry => entry.FullName.EndsWith(
                        "AttachmentABI.hlsl",
                        StringComparison.Ordinal));
                Assert.Equal(
                    "contentFiles/any/any/SharpShader/Includes/AttachmentABI.hlsl",
                    attachmentEntry.FullName);
                using (Stream attachmentStream = attachmentEntry.Open())
                using (MemoryStream packagedContent = new())
                {
                    await attachmentStream.CopyToAsync(packagedContent);
                    string includePath = Path.Combine(
                        Path.GetDirectoryName(projectPath)!,
                        "Includes",
                        "AttachmentABI.hlsl");
                    Assert.Equal(
                        File.ReadAllBytes(includePath),
                        packagedContent.ToArray());
                }

                ZipArchiveEntry nuspecEntry = Assert.Single(
                    package.Entries,
                    static entry => entry.FullName.EndsWith(
                        ".nuspec",
                        StringComparison.OrdinalIgnoreCase));
                using Stream nuspecStream = nuspecEntry.Open();
                XDocument nuspec = XDocument.Load(nuspecStream);
                packageId = Assert.Single(
                    nuspec.Descendants(),
                    static element => element.Name.LocalName == "id").Value;
                packageVersion = Assert.Single(
                    nuspec.Descendants(),
                    static element => element.Name.LocalName == "version").Value;
                Assert.Equal("SharpShader", packageId);
                Assert.Equal(packageVersionOverride, packageVersion);
                Assert.DoesNotContain(
                    nuspec.Descendants(),
                    static element => element.Name.LocalName == "dependency"
                        && string.Equals(
                            (string?)element.Attribute("id"),
                            "SharpGPU",
                            StringComparison.Ordinal));
            }

            string consumerDirectory = Path.Combine(
                temporary.Path,
                "fresh-consumer");
            Directory.CreateDirectory(consumerDirectory);
            string consumerProjectPath = Path.Combine(
                consumerDirectory,
                "AttachmentAbiConsumer.csproj");
            File.WriteAllText(
                consumerProjectPath,
                $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="{{packageId}}" Version="{{packageVersion}}" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(consumerDirectory, "Program.cs"),
                """"
                using System;
                using System.IO;
                using SharpShader.HLSLCrossCompiler;

                string includePath = Path.Combine(
                    AppContext.BaseDirectory,
                    "SharpShader",
                    "Includes",
                    "AttachmentABI.hlsl");
                if (!File.Exists(includePath))
                {
                    throw new FileNotFoundException(
                        "SharpShader package did not copy AttachmentABI.hlsl to the consumer output.",
                        includePath);
                }

                const string source = """
                    #include "SharpShader/Includes/AttachmentABI.hlsl"

                    struct PixelOutput
                    {
                        SHARPSHADER_DECLARE_COLOR_OUTPUT(float4, Color, 0);
                    };

                    PixelOutput Main()
                    {
                        PixelOutput output;
                        output.Color = float4(0.25, 0.5, 0.75, 1.0);
                        return output;
                    }
                    """;
                ShaderCompileResult result = HLSLCrossCompiler.Compile(
                    new ShaderCompileRequest
                    {
                        Source = source,
                        SourceName = "fresh-package-consumer.hlsl",
                        EntryPoint = "Main",
                        Stage = ShaderStageKind.Pixel,
                        ShaderModel = new ShaderModelVersion(6, 6),
                        Target = ShaderTargetKind.SpirV,
                        IncludeDirs = new[] { AppContext.BaseDirectory },
                    });
                if (result.Bytecode.Length == 0)
                {
                    throw new InvalidOperationException(
                        "Fresh SharpShader package consumer produced empty SPIR-V.");
                }

                Console.WriteLine("AttachmentABI fresh consumer compile succeeded.");
                """");

            string isolatedPackagesPath = Path.Combine(
                temporary.Path,
                "packages");
            string globalPackagesFallback =
                ResolveGlobalNuGetPackagesFolder();
            ProcessResult restoreResult = await RunDotnetAsync(
                consumerDirectory,
                TimeSpan.FromMinutes(2),
                null,
                "restore",
                consumerProjectPath,
                "--source",
                packageOutput,
                "--packages",
                isolatedPackagesPath,
                "--ignore-failed-sources",
                $"-p:RestoreAdditionalProjectFallbackFolders={globalPackagesFallback}",
                "-p:NuGetAudit=false");
            AssertProcessSucceeded(
                "fresh SharpShader package consumer restore",
                restoreResult);
            string normalizedPackageId = packageId.ToLowerInvariant();
            string normalizedPackageVersion = packageVersion.ToLowerInvariant();
            string restoredPackagePath = Path.Combine(
                isolatedPackagesPath,
                normalizedPackageId,
                normalizedPackageVersion,
                $"{normalizedPackageId}.{normalizedPackageVersion}.nupkg");
            Assert.True(
                File.Exists(restoredPackagePath),
                $"Fresh restore did not materialize the tested package at "
                + $"{restoredPackagePath}.");
            Assert.Equal(
                File.ReadAllBytes(packagePath),
                File.ReadAllBytes(restoredPackagePath));
            ProcessResult runResult = await RunDotnetAsync(
                consumerDirectory,
                TimeSpan.FromMinutes(2),
                Path.Combine(
                    repositoryRoot,
                    "Engine",
                    "Binaries",
                    "ThirdParty"),
                "run",
                "--project",
                consumerProjectPath,
                "-c",
                "Release",
                "--no-restore");
            AssertProcessSucceeded(
                "fresh SharpShader package consumer compile/run",
                runResult);
            Assert.Contains(
                "AttachmentABI fresh consumer compile succeeded.",
                runResult.StandardOutput,
                StringComparison.Ordinal);
        }

        private static void AssertSharpShaderDependencyDirection(
            string repositoryRoot,
            string sharpShaderProjectPath)
        {
            string graphicsRoot = Path.Combine(
                repositoryRoot,
                "Engine",
                "Source",
                "Runtime",
                "Graphics");
            string sharpGpuProjectPath = Path.Combine(
                graphicsRoot,
                "SharpGPU",
                "SharpGPU.csproj");
            string adapterProjectPath = Path.Combine(
                graphicsRoot,
                "SharpShader.SharpGPU",
                "SharpShader.SharpGPU.csproj");

            Assert.False(
                DeclaresDependency(sharpShaderProjectPath, "SharpGPU"),
                "SharpShader must not declare a dependency on SharpGPU.");
            Assert.False(
                DeclaresDependency(sharpGpuProjectPath, "SharpShader"),
                "SharpGPU must not declare a dependency on SharpShader.");
            Assert.Equal(
                new[] { "SharpGPU.csproj", "SharpShader.csproj" },
                ReadProjectReferenceFileNames(adapterProjectPath));

            string[] sharpShaderAssemblyReferences =
                typeof(global::SharpShader.HLSLCrossCompiler.HLSLCrossCompiler)
                    .Assembly
                    .GetReferencedAssemblies()
                    .Select(static reference => reference.Name)
                    .Where(static name => name is not null)
                    .Select(static name => name!)
                    .ToArray();
            string[] sharpGpuAssemblyReferences =
                typeof(global::SharpGPU.RHIInstance)
                    .Assembly
                    .GetReferencedAssemblies()
                    .Select(static reference => reference.Name)
                    .Where(static name => name is not null)
                    .Select(static name => name!)
                    .ToArray();
            string[] adapterAssemblyReferences =
                typeof(global::SharpShader.SharpGPU.SharpGpuShaderInterfaceAdapter)
                    .Assembly
                    .GetReferencedAssemblies()
                    .Select(static reference => reference.Name)
                    .Where(static name => name is not null)
                    .Select(static name => name!)
                    .ToArray();

            Assert.DoesNotContain("SharpGPU", sharpShaderAssemblyReferences);
            Assert.DoesNotContain("SharpShader", sharpGpuAssemblyReferences);
            Assert.Contains("SharpGPU", adapterAssemblyReferences);
            Assert.Contains("SharpShader", adapterAssemblyReferences);
        }

        private static bool DeclaresDependency(
            string projectPath,
            string dependencyName)
        {
            return XDocument.Load(projectPath)
                .Descendants()
                .Where(static element => element.Name.LocalName is
                    "ProjectReference" or "PackageReference")
                .Select(static element => new
                {
                    Kind = element.Name.LocalName,
                    Include = (string?)element.Attribute("Include"),
                })
                .Where(static dependency =>
                    !string.IsNullOrWhiteSpace(dependency.Include))
                .Select(static dependency =>
                    dependency.Kind == "ProjectReference"
                        ? Path.GetFileNameWithoutExtension(dependency.Include!)
                        : dependency.Include!)
                .Any(candidate => string.Equals(
                    candidate,
                    dependencyName,
                    StringComparison.OrdinalIgnoreCase));
        }
        private static string[] ReadProjectReferenceFileNames(
            string projectPath)
        {
            return XDocument.Load(projectPath)
                .Descendants()
                .Where(static element =>
                    element.Name.LocalName == "ProjectReference")
                .Select(static element =>
                    (string?)element.Attribute("Include"))
                .Where(static include => !string.IsNullOrWhiteSpace(include))
                .Select(static include => Path.GetFileName(include!))
                .OrderBy(static fileName => fileName, StringComparer.Ordinal)
                .ToArray();
        }

        private static async Task<ProcessResult> RunDotnetAsync(
            string workingDirectory,
            TimeSpan timeout,
            string? thirdPartyNativeRoot,
            params string[] arguments)
        {
            ProcessStartInfo startInfo = new("dotnet")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            if (!string.IsNullOrWhiteSpace(thirdPartyNativeRoot))
            {
                startInfo.Environment[
                    "INFINITY_THIRDPARTY_NATIVE_ROOT"] =
                    Path.GetFullPath(thirdPartyNativeRoot);
            }

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Failed to start dotnet process.");
            Task<string> standardOutputTask =
                process.StandardOutput.ReadToEndAsync();
            Task<string> standardErrorTask =
                process.StandardError.ReadToEndAsync();
            using CancellationTokenSource cancellation = new(timeout);
            try
            {
                await process.WaitForExitAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }

                throw new Xunit.Sdk.XunitException(
                    $"dotnet process timed out after {timeout}.");
            }

            return new ProcessResult(
                process.ExitCode,
                await standardOutputTask,
                await standardErrorTask);
        }

        private static void AssertProcessSucceeded(
            string operation,
            ProcessResult result)
        {
            Assert.True(
                result.ExitCode == 0,
                operation
                + " failed.\n"
                + result.StandardOutput
                + "\n"
                + result.StandardError);
        }

        private readonly record struct ProcessResult(
            int ExitCode,
            string StandardOutput,
            string StandardError);
        private static string ResolveRepositoryRoot()
        {
            DirectoryInfo? current = new(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(
                        current.FullName,
                        "InfinityBrowser.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException(
                "Unable to resolve repository root.");
        }
        private static string ResolveGlobalNuGetPackagesFolder()
        {
            string? configured =
                Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            string path = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.UserProfile),
                    ".nuget",
                    "packages")
                : configured;
            path = Path.GetFullPath(path);
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException(
                    $"NuGet transitive dependency fallback does not exist: "
                    + $"{path}.");
            }

            return path;
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
