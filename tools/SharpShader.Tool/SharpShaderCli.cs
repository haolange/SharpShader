using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;

using SharpShader.Compilation;
using SharpShader.HLSLCrossCompiler;

[assembly: InternalsVisibleTo("Infinity.Rendering.Tests")]

namespace SharpShader.Tool
{
    internal static class SharpShaderCli
    {
        private const long MaximumSourceFileBytes = 64L * 1024 * 1024;
        private static readonly UTF8Encoding s_StrictUtf8 = new(false, true);
        private const int SuccessExitCode = 0;
        private const int FailureExitCode = 1;
        private const int UsageExitCode = 2;

        public static int Execute(
            string[] args,
            TextWriter output,
            TextWriter error)
        {
            ArgumentNullException.ThrowIfNull(args);
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(error);

            if (args.Length == 0)
            {
                WriteUsage(error);
                return UsageExitCode;
            }

            try
            {
                string command = args[0].ToLowerInvariant();
                return command switch
                {
                    "compile" => Compile(args.AsSpan(1), output),
                    "inspect" => Inspect(args.AsSpan(1), output),
                    "validate" => Validate(args.AsSpan(1), output),
                    "help" or "--help" or "-h" => Help(output),
                    _ => throw new CliUsageException(
                        $"Unknown command '{args[0]}'."),
                };
            }
            catch (CliUsageException exception)
            {
                error.WriteLine(exception.Message);
                WriteUsage(error);
                return UsageExitCode;
            }
            catch (Exception exception)
            {
                error.WriteLine($"SharpShader.Tool: {exception.Message}");
                return FailureExitCode;
            }
        }

        private static int Compile(
            ReadOnlySpan<string> args,
            TextWriter output)
        {
            CompileArguments parsed = CompileArguments.Parse(args);
            ShaderProgramCompiler compiler = new(
                new ShaderProgramCompilerOptions(parsed.CacheDirectory));
            ShaderProgramCompilation compilation = compiler.Compile(
                parsed.CreateRequest());

            ShaderArtifactPackage.Write(
                parsed.OutputDirectory,
                compilation);
            output.WriteLine(
                $"compiled cache-key={compilation.CacheKey} "
                + $"variants={compilation.Manifest.Variants.Count} "
                + $"artifacts={compilation.Artifacts.Count}");
            output.WriteLine(
                Path.Combine(parsed.OutputDirectory, ShaderArtifactPackage.ManifestFileName));
            return SuccessExitCode;
        }

        private static int Inspect(
            ReadOnlySpan<string> args,
            TextWriter output)
        {
            string path = ParseSinglePath(args, "inspect");
            ShaderInterfaceManifest manifest =
                ShaderArtifactPackage.LoadManifest(path);
            ShaderArtifactPackage.WriteSummary(manifest, output);
            return SuccessExitCode;
        }

        private static int Validate(
            ReadOnlySpan<string> args,
            TextWriter output)
        {
            string path = ParseSinglePath(args, "validate");
            ShaderArtifactPackage.Validate(path);
            output.WriteLine($"valid {Path.GetFullPath(path)}");
            return SuccessExitCode;
        }

        private static int Help(TextWriter output)
        {
            WriteUsage(output);
            return SuccessExitCode;
        }

        private static string ParseSinglePath(
            ReadOnlySpan<string> args,
            string command)
        {
            if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
            {
                throw new CliUsageException(
                    $"Usage: SharpShader.Tool {command} <package-directory|manifest.json>");
            }

            return args[0];
        }

        private static void WriteUsage(TextWriter writer)
        {
            writer.WriteLine("SharpShader.Tool compile --source <file> --entry <stage>:<name> --output <directory> [options]");
            writer.WriteLine("SharpShader.Tool inspect <package-directory|manifest.json>");
            writer.WriteLine("SharpShader.Tool validate <package-directory|manifest.json>");
            writer.WriteLine("Compile options:");
            writer.WriteLine("  --entry <stage>:<name>                 Repeat for every entry.");
            writer.WriteLine("  --target <dx12|vulkan|metal|all>[,...] Default: all.");
            writer.WriteLine("  --variant <key>                        Repeat for every variant; default: default.");
            writer.WriteLine("  --variant-define <key>:<name>[=<value>] Attach a define to a declared variant.");
            writer.WriteLine("  --define <name>[=<value>]               Add a global define.");
            writer.WriteLine("  --include <directory>                   Add an include directory.");
            writer.WriteLine("  --cache <directory>                     Enable the content-addressed persistent cache.");
            writer.WriteLine("  --shader-model <6.0-6.8>                Default: 6.8.");
            writer.WriteLine("  --metal-capacity <table>:<slot>:<t|s|b|u>=<count>");
            writer.WriteLine("  --debug --disable-optimizations --optimization-level <0-3> --warnings-as-errors");
        }
        private static string ReadUtf8TextBounded(string path)
        {
            FileInfo file = new(path);
            if (file.Length > MaximumSourceFileBytes
                || file.Length > int.MaxValue)
            {
                throw new IOException(
                    $"Shader source {path} contains {file.Length} bytes, "
                    + $"exceeding the {MaximumSourceFileBytes}-byte limit.");
            }

            byte[] bytes = new byte[checked((int)file.Length)];
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                throw new IOException(
                    $"Shader source {path} changed while being read.");
            }

            string source;
            try
            {
                source = s_StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    $"Shader source {path} is not valid UTF-8.",
                    exception);
            }

            return source.Length > 0 && source[0] == '\uFEFF'
                ? source[1..]
                : source;
        }


        private sealed class CompileArguments
        {
            private readonly List<ShaderProgramEntry> m_Entries = new();
            private readonly List<string> m_VariantKeys = new();
            private readonly Dictionary<string, List<ShaderDefine>> m_VariantDefines =
                new(StringComparer.Ordinal);
            private readonly List<ShaderDefine> m_GlobalDefines = new();
            private readonly List<string> m_IncludeDirectories = new();
            private readonly Dictionary<ShaderBindingKey, uint> m_MetalCapacities = new();

            public string SourcePath { get; private set; } = string.Empty;
            public string OutputDirectory { get; private set; } = string.Empty;
            public string? CacheDirectory { get; private set; }
            public ShaderProgramTarget Targets { get; private set; } =
                ShaderProgramTarget.All;
            public ShaderModelVersion ShaderModel { get; private set; } =
                new(6, 8);
            public bool EnableDebugInfo { get; private set; }
            public bool DisableOptimizations { get; private set; }
            public int OptimizationLevel { get; private set; } = 3;
            public bool TreatWarningsAsErrors { get; private set; }

            public static CompileArguments Parse(ReadOnlySpan<string> args)
            {
                CompileArguments result = new();
                bool targetSpecified = false;
                bool shaderModelSpecified = false;
                bool optimizationLevelSpecified = false;

                for (int index = 0; index < args.Length; ++index)
                {
                    string option = args[index];
                    switch (option)
                    {
                        case "--source":
                            result.SourcePath = AssignOnce(
                                result.SourcePath,
                                ReadValue(args, ref index, option),
                                option);
                            break;
                        case "--output":
                            result.OutputDirectory = AssignOnce(
                                result.OutputDirectory,
                                ReadValue(args, ref index, option),
                                option);
                            break;
                        case "--cache":
                            result.CacheDirectory = AssignOnce(
                                result.CacheDirectory,
                                ReadValue(args, ref index, option),
                                option);
                            break;
                        case "--entry":
                            result.m_Entries.Add(ParseEntry(
                                ReadValue(args, ref index, option)));
                            break;
                        case "--target":
                            if (targetSpecified)
                            {
                                throw new CliUsageException(
                                    "--target may be specified only once.");
                            }

                            result.Targets = ParseTargets(
                                ReadValue(args, ref index, option));
                            targetSpecified = true;
                            break;
                        case "--variant":
                            result.AddVariant(
                                ReadValue(args, ref index, option));
                            break;
                        case "--variant-define":
                            result.AddVariantDefine(
                                ReadValue(args, ref index, option));
                            break;
                        case "--define":
                            result.m_GlobalDefines.Add(ParseDefine(
                                ReadValue(args, ref index, option)));
                            break;
                        case "--include":
                            result.m_IncludeDirectories.Add(
                                ReadValue(args, ref index, option));
                            break;
                        case "--shader-model":
                            if (shaderModelSpecified)
                            {
                                throw new CliUsageException(
                                    "--shader-model may be specified only once.");
                            }

                            result.ShaderModel = ParseShaderModel(
                                ReadValue(args, ref index, option));
                            shaderModelSpecified = true;
                            break;
                        case "--metal-capacity":
                            result.AddMetalCapacity(
                                ReadValue(args, ref index, option));
                            break;
                        case "--optimization-level":
                            if (optimizationLevelSpecified)
                            {
                                throw new CliUsageException(
                                    "--optimization-level may be specified only once.");
                            }

                            result.OptimizationLevel = ParseOptimizationLevel(
                                ReadValue(args, ref index, option));
                            optimizationLevelSpecified = true;
                            break;
                        case "--debug":
                            result.EnableDebugInfo = SetFlag(
                                result.EnableDebugInfo,
                                option);
                            break;
                        case "--disable-optimizations":
                            result.DisableOptimizations = SetFlag(
                                result.DisableOptimizations,
                                option);
                            break;
                        case "--warnings-as-errors":
                            result.TreatWarningsAsErrors = SetFlag(
                                result.TreatWarningsAsErrors,
                                option);
                            break;
                        default:
                            throw new CliUsageException(
                                $"Unknown compile option '{option}'.");
                    }
                }

                result.Validate();
                return result;
            }

            public ShaderProgramCompileRequest CreateRequest()
            {
                string sourcePath = Path.GetFullPath(SourcePath);
                string source = ReadUtf8TextBounded(sourcePath);
                List<string> includeDirectories = ResolveIncludeDirectories(
                    sourcePath,
                    m_IncludeDirectories);
                ShaderProgramVariant[] variants = CreateVariants();

                return new ShaderProgramCompileRequest(
                    source,
                    sourcePath,
                    m_Entries,
                    variants,
                    Targets,
                    ShaderModel,
                    m_GlobalDefines,
                    includeDirectories,
                    metalArrayCapacities: m_MetalCapacities,
                    enableDebugInfo: EnableDebugInfo,
                    disableOptimizations: DisableOptimizations,
                    optimizationLevel: OptimizationLevel,
                    treatWarningsAsErrors: TreatWarningsAsErrors);
            }

            private void Validate()
            {
                if (string.IsNullOrWhiteSpace(SourcePath))
                {
                    throw new CliUsageException("--source is required.");
                }

                SourcePath = Path.GetFullPath(SourcePath);
                if (!File.Exists(SourcePath))
                {
                    throw new FileNotFoundException(
                        "Shader source file was not found.",
                        SourcePath);
                }

                if (string.IsNullOrWhiteSpace(OutputDirectory))
                {
                    throw new CliUsageException("--output is required.");
                }

                OutputDirectory = Path.GetFullPath(OutputDirectory);
                if (Directory.Exists(OutputDirectory)
                    || File.Exists(OutputDirectory))
                {
                    throw new IOException(
                        $"Output path already exists: {OutputDirectory}");
                }

                if (m_Entries.Count == 0)
                {
                    throw new CliUsageException(
                        "At least one --entry is required.");
                }

                if (m_VariantKeys.Count == 0)
                {
                    AddVariant(ShaderProgramVariant.Default.Key);
                }

                foreach (string variantKey in m_VariantDefines.Keys)
                {
                    if (!m_VariantKeys.Contains(
                            variantKey,
                            StringComparer.Ordinal))
                    {
                        throw new CliUsageException(
                            $"--variant-define references undeclared variant '{variantKey}'.");
                    }
                }

                CacheDirectory = CacheDirectory is null
                    ? null
                    : Path.GetFullPath(CacheDirectory);
            }

            private void AddVariant(string key)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new CliUsageException(
                        "Variant keys must not be empty.");
                }

                if (m_VariantKeys.Contains(key, StringComparer.Ordinal))
                {
                    throw new CliUsageException(
                        $"Variant '{key}' is duplicated.");
                }

                m_VariantKeys.Add(key);
            }

            private void AddVariantDefine(string value)
            {
                int separator = value.IndexOf(':');
                if (separator <= 0 || separator == value.Length - 1)
                {
                    throw new CliUsageException(
                        "--variant-define must use <variant>:<name>[=<value>].");
                }

                string variantKey = value[..separator];
                ShaderDefine define = ParseDefine(value[(separator + 1)..]);
                if (!m_VariantDefines.TryGetValue(
                        variantKey,
                        out List<ShaderDefine>? definitions))
                {
                    definitions = new List<ShaderDefine>();
                    m_VariantDefines.Add(variantKey, definitions);
                }

                definitions.Add(define);
            }

            private void AddMetalCapacity(string value)
            {
                int equals = value.LastIndexOf('=');
                if (equals <= 0 || equals == value.Length - 1)
                {
                    throw new CliUsageException(
                        "--metal-capacity must use <table>:<slot>:<t|s|b|u>=<count>.");
                }

                string[] identity = value[..equals].Split(
                    ':',
                    StringSplitOptions.TrimEntries);
                if (identity.Length != 3
                    || !uint.TryParse(
                        identity[0],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out uint table)
                    || !uint.TryParse(
                        identity[1],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out uint slot)
                    || !uint.TryParse(
                        value[(equals + 1)..],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out uint count)
                    || count == 0)
                {
                    throw new CliUsageException(
                        "--metal-capacity contains an invalid table, slot, or count.");
                }

                ShaderBindingClass bindingClass = identity[2].ToLowerInvariant() switch
                {
                    "t" or "srv" => ShaderBindingClass.ShaderResource,
                    "s" or "sampler" => ShaderBindingClass.Sampler,
                    "b" or "cbv" => ShaderBindingClass.ConstantBuffer,
                    "u" or "uav" => ShaderBindingClass.UnorderedAccess,
                    _ => throw new CliUsageException(
                        $"Unknown binding class '{identity[2]}' in --metal-capacity."),
                };
                ShaderBindingKey key = new(table, slot, bindingClass);
                if (!m_MetalCapacities.TryAdd(key, count))
                {
                    throw new CliUsageException(
                        $"Metal capacity for {key} is duplicated.");
                }
            }

            private ShaderProgramVariant[] CreateVariants()
            {
                ShaderProgramVariant[] variants =
                    new ShaderProgramVariant[m_VariantKeys.Count];
                for (int index = 0; index < variants.Length; ++index)
                {
                    string key = m_VariantKeys[index];
                    variants[index] = new ShaderProgramVariant(
                        key,
                        m_VariantDefines.TryGetValue(
                            key,
                            out List<ShaderDefine>? defines)
                                ? defines
                                : null);
                }

                return variants;
            }

            private static List<string> ResolveIncludeDirectories(
                string sourcePath,
                IReadOnlyList<string> requested)
            {
                StringComparer comparer = OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal;
                HashSet<string> unique = new(comparer);
                List<string> result = new();

                string? sourceDirectory = Path.GetDirectoryName(sourcePath);
                if (!string.IsNullOrWhiteSpace(sourceDirectory))
                {
                    unique.Add(sourceDirectory);
                    result.Add(sourceDirectory);
                }

                foreach (string directory in requested)
                {
                    string fullPath = Path.GetFullPath(directory);
                    if (!Directory.Exists(fullPath))
                    {
                        throw new DirectoryNotFoundException(
                            $"Shader include directory was not found: {fullPath}");
                    }

                    if (unique.Add(fullPath))
                    {
                        result.Add(fullPath);
                    }
                }

                return result;
            }

            private static ShaderProgramEntry ParseEntry(string value)
            {
                int separator = value.IndexOf(':');
                if (separator <= 0 || separator == value.Length - 1)
                {
                    throw new CliUsageException(
                        "--entry must use <stage>:<name>.");
                }

                ShaderExecutionStage stage = ParseStage(value[..separator]);
                return new ShaderProgramEntry(
                    value[(separator + 1)..],
                    stage);
            }

            private static ShaderExecutionStage ParseStage(string value)
            {
                return value.ToLowerInvariant() switch
                {
                    "vertex" or "vs" => ShaderExecutionStage.Vertex,
                    "hull" or "hs" => ShaderExecutionStage.Hull,
                    "domain" or "ds" => ShaderExecutionStage.Domain,
                    "geometry" or "gs" => ShaderExecutionStage.Geometry,
                    "pixel" or "fragment" or "ps" or "fs" =>
                        ShaderExecutionStage.Pixel,
                    "compute" or "cs" => ShaderExecutionStage.Compute,
                    "amplification" or "task" or "as" =>
                        ShaderExecutionStage.Amplification,
                    "mesh" or "ms" => ShaderExecutionStage.Mesh,
                    "raygeneration" or "raygen" =>
                        ShaderExecutionStage.RayGeneration,
                    "intersection" => ShaderExecutionStage.Intersection,
                    "anyhit" => ShaderExecutionStage.AnyHit,
                    "closesthit" => ShaderExecutionStage.ClosestHit,
                    "miss" => ShaderExecutionStage.Miss,
                    "callable" => ShaderExecutionStage.Callable,
                    "node" => ShaderExecutionStage.Node,
                    _ => throw new CliUsageException(
                        $"Unknown shader stage '{value}'."),
                };
            }

            private static ShaderProgramTarget ParseTargets(string value)
            {
                ShaderProgramTarget targets = ShaderProgramTarget.None;
                foreach (string item in value.Split(
                             new[] { ',', '+' },
                             StringSplitOptions.RemoveEmptyEntries
                             | StringSplitOptions.TrimEntries))
                {
                    targets |= item.ToLowerInvariant() switch
                    {
                        "dx12" or "directx12" => ShaderProgramTarget.DirectX12,
                        "vulkan" or "spirv" => ShaderProgramTarget.Vulkan,
                        "metal" or "msl" => ShaderProgramTarget.MetalMsl,
                        "all" => ShaderProgramTarget.All,
                        _ => throw new CliUsageException(
                            $"Unknown shader target '{item}'."),
                    };
                }

                if (targets == ShaderProgramTarget.None)
                {
                    throw new CliUsageException(
                        "--target must select at least one target.");
                }

                return targets;
            }

            private static ShaderModelVersion ParseShaderModel(string value)
            {
                string[] parts = value.Split('.');
                if (parts.Length != 2
                    || !int.TryParse(
                        parts[0],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int major)
                    || !int.TryParse(
                        parts[1],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int minor))
                {
                    throw new CliUsageException(
                        "--shader-model must use <major>.<minor>.");
                }

                try
                {
                    return new ShaderModelVersion(major, minor);
                }
                catch (ArgumentOutOfRangeException exception)
                {
                    throw new CliUsageException(exception.Message);
                }
            }

            private static int ParseOptimizationLevel(string value)
            {
                if (!int.TryParse(
                        value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int level)
                    || level is < 0 or > 3)
                {
                    throw new CliUsageException(
                        "--optimization-level must be in [0, 3].");
                }

                return level;
            }

            private static ShaderDefine ParseDefine(string value)
            {
                int equals = value.IndexOf('=');
                string name = equals < 0 ? value : value[..equals];
                string? defineValue = equals < 0
                    ? null
                    : value[(equals + 1)..];
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new CliUsageException(
                        "Shader define names must not be empty.");
                }

                return new ShaderDefine(name, defineValue);
            }

            private static string ReadValue(
                ReadOnlySpan<string> args,
                ref int index,
                string option)
            {
                if (index + 1 >= args.Length
                    || args[index + 1].StartsWith(
                        "--",
                        StringComparison.Ordinal))
                {
                    throw new CliUsageException(
                        $"{option} requires a value.");
                }

                ++index;
                return args[index];
            }

            private static string AssignOnce(
                string? current,
                string value,
                string option)
            {
                if (!string.IsNullOrEmpty(current))
                {
                    throw new CliUsageException(
                        $"{option} may be specified only once.");
                }

                return value;
            }

            private static bool SetFlag(bool current, string option)
            {
                if (current)
                {
                    throw new CliUsageException(
                        $"{option} may be specified only once.");
                }

                return true;
            }
        }

        private sealed class CliUsageException : Exception
        {
            public CliUsageException(string message)
                : base(message)
            {
            }
        }
    }

    internal static class ShaderArtifactPackage
    {
        public const string ManifestFileName = "manifest.json";
        private const string ArtifactsDirectoryName = "artifacts";
        private const long MaximumManifestBytes = 64L * 1024 * 1024;
        private const long MaximumArtifactBytes = 1024L * 1024 * 1024;
        private const long MaximumPackageBytes = 4L * 1024 * 1024 * 1024;

        public static void Write(
            string outputDirectory,
            ShaderProgramCompilation compilation)
        {
            ArgumentNullException.ThrowIfNull(compilation);
            string destination = Path.GetFullPath(outputDirectory);
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                throw new IOException(
                    $"Output path already exists: {destination}");
            }

            string? parent = Path.GetDirectoryName(destination);
            if (string.IsNullOrWhiteSpace(parent))
            {
                throw new IOException(
                    $"Output directory has no parent: {destination}");
            }

            Directory.CreateDirectory(parent);
            string staging = Path.Combine(
                parent,
                $".{Path.GetFileName(destination)}.tmp-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            try
            {
                WriteStaged(staging, compilation);
                Directory.Move(staging, destination);
            }
            catch
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }

                throw;
            }
        }

        public static ShaderInterfaceManifest LoadManifest(string path)
        {
            string manifestPath = ResolveManifestPath(path);
            byte[] bytes = ReadFileBounded(
                manifestPath,
                MaximumManifestBytes,
                "Shader interface manifest");
            return ShaderInterfaceManifestSerializer.Deserialize(bytes);
        }

        public static void Validate(string path)
        {
            string manifestPath = ResolveManifestPath(path);
            string? packageDirectory = Path.GetDirectoryName(manifestPath);
            if (string.IsNullOrWhiteSpace(packageDirectory))
            {
                throw new InvalidDataException(
                    $"Shader package manifest has no parent directory: {manifestPath}");
            }

            ValidatePackageRoot(packageDirectory, manifestPath);
            ShaderInterfaceManifest manifest = LoadManifest(manifestPath);
            string artifactsDirectory = Path.Combine(
                packageDirectory,
                ArtifactsDirectoryName);
            StringComparer fileNameComparer =
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal;
            Dictionary<string, ShaderArtifactIdentity> expectedFiles =
                new(fileNameComparer);
            foreach (ShaderInterfaceVariant variant in manifest.Variants)
            {
                foreach (ShaderInterfaceEntry entry in variant.Entries)
                {
                    foreach (ShaderArtifactIdentity artifact in entry.Artifacts)
                    {
                        string fileName = GetArtifactFileName(artifact);
                        if (expectedFiles.TryGetValue(
                                fileName,
                                out ShaderArtifactIdentity? existing))
                        {
                            if (!existing.Equals(artifact))
                            {
                                throw new InvalidDataException(
                                    $"Manifest maps incompatible artifacts to {fileName}.");
                            }
                        }
                        else
                        {
                            expectedFiles.Add(fileName, artifact);
                        }
                    }
                }
            }

            long packageBytes = CheckedPackageAdd(
                0,
                new FileInfo(manifestPath).Length);
            int actualEntryCount = 0;
            int expectedEntryCount = expectedFiles.Count;
            foreach (string actualEntry in Directory.EnumerateFileSystemEntries(
                         artifactsDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                ++actualEntryCount;
                if (actualEntryCount > expectedEntryCount)
                {
                    throw new InvalidDataException(
                        "Shader artifact package contains unexpected entries.");
                }

                string fileName = Path.GetFileName(actualEntry);
                if (!expectedFiles.Remove(
                        fileName,
                        out ShaderArtifactIdentity? identity))
                {
                    throw new InvalidDataException(
                        $"Shader artifact package contains unexpected entry {actualEntry}.");
                }

                packageBytes = CheckedPackageAdd(
                    packageBytes,
                    ValidateArtifactFile(actualEntry, identity));
            }

            if (actualEntryCount != expectedEntryCount
                || expectedFiles.Count != 0)
            {
                throw new InvalidDataException(
                    "Shader artifact package contains missing artifact files.");
            }
        }

        public static void WriteSummary(
            ShaderInterfaceManifest manifest,
            TextWriter output)
        {
            output.WriteLine($"schema: {manifest.SchemaVersion}");
            output.WriteLine($"source: {manifest.SourceDigest}");
            output.WriteLine($"toolchain-components: {manifest.ToolchainComponents.Count}");
            output.WriteLine($"logical-layouts: {manifest.LogicalLayouts.Count}");
            output.WriteLine($"variants: {manifest.Variants.Count}");
            foreach (ShaderInterfaceVariant variant in manifest.Variants)
            {
                output.WriteLine(
                    $"variant {variant.Key} entries={variant.Entries.Count}");
                foreach (ShaderInterfaceEntry entry in variant.Entries)
                {
                    output.WriteLine(
                        $"  {entry.Stage}:{entry.Name} "
                        + $"layout={entry.LogicalLayoutSignature} "
                        + $"artifacts={entry.Artifacts.Count}");
                    foreach (ShaderArtifactIdentity artifact in entry.Artifacts)
                    {
                        output.WriteLine(
                            $"    {artifact.ArtifactKind} "
                            + $"{artifact.ContentDigest} "
                            + $"{artifact.ByteLength}");
                    }
                }
            }

            foreach (ShaderInterfaceLayout layout in manifest.LogicalLayouts)
            {
                output.WriteLine(
                    $"layout {layout.Signature} bindings={layout.Bindings.Count}");
                foreach (ShaderLogicalBinding binding in layout.Bindings)
                {
                    output.WriteLine(
                        $"  logical table={binding.Key.Table} "
                        + $"slot={binding.Key.Slot} "
                        + $"type={binding.Key.Type} "
                        + $"name={binding.CanonicalName} "
                        + $"kind={binding.Shape.Kind} "
                        + $"dimension={binding.Shape.Dimension} "
                        + $"access={binding.Shape.Access} "
                        + $"stages={binding.StageMask}");
                }
            }

            foreach (ShaderBackendLayouts layouts in manifest.BackendLayouts)
            {
                output.WriteLine(
                    $"backend-layout {layouts.LogicalLayoutSignature}");
                if (layouts.Dx12 is not null)
                {
                    foreach (Dx12ShaderBindingMapping mapping in layouts.Dx12.Bindings)
                    {
                        output.WriteLine(
                            $"  dx12 {mapping.LogicalBinding} -> "
                            + $"{GetRegisterPrefix(mapping.RegisterClass)}"
                            + $"{mapping.ShaderRegister},space{mapping.RegisterSpace}");
                    }
                }

                if (layouts.Vulkan is not null)
                {
                    foreach (VulkanShaderBindingMapping mapping in layouts.Vulkan.Bindings)
                    {
                        output.WriteLine(
                            $"  vulkan {mapping.LogicalBinding} -> "
                            + $"set={mapping.DescriptorSet},binding={mapping.Binding},"
                            + $"kind={mapping.DescriptorKind}");
                    }
                }

                if (layouts.Metal is not null)
                {
                    foreach (MetalDirectBindingMapping mapping in layouts.Metal.DirectBindings)
                    {
                        output.WriteLine(
                            $"  metal-direct {mapping.LogicalBinding} -> "
                            + $"table={mapping.ArgumentTable},"
                            + $"namespace={mapping.Namespace},index={mapping.Index}");
                    }

                    foreach (MetalReferenceBufferBindingMapping mapping in
                             layouts.Metal.ReferenceBufferBindings)
                    {
                        output.WriteLine(
                            $"  metal-reference {mapping.LogicalBinding} -> "
                            + $"table={mapping.ArgumentTable},"
                            + $"buffer={mapping.ReferenceBufferIndex},"
                            + $"offset={mapping.ByteOffset},"
                            + $"count={mapping.ReferenceCount},"
                            + $"namespace={mapping.ResourceNamespace}");
                    }
                }
            }
        }

        private static void WriteStaged(
            string stagingDirectory,
            ShaderProgramCompilation compilation)
        {
            string artifactsDirectory = Path.Combine(
                stagingDirectory,
                ArtifactsDirectoryName);
            Directory.CreateDirectory(artifactsDirectory);
            byte[] manifestBytes =
                ShaderInterfaceManifestSerializer.SerializeToUtf8Bytes(
                    compilation.Manifest);
            if (manifestBytes.LongLength > MaximumManifestBytes)
            {
                throw new InvalidDataException(
                    $"Shader interface manifest exceeds the "
                    + $"{MaximumManifestBytes}-byte limit.");
            }
            long packageBytes = manifestBytes.LongLength;

            Dictionary<ArtifactLookupKey, ShaderArtifactIdentity> identities =
                CollectManifestArtifacts(compilation.Manifest);
            HashSet<string> writtenFiles = new(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);
            foreach (ShaderProgramArtifact artifact in compilation.Artifacts)
            {
                ArtifactLookupKey key = new(
                    artifact.VariantKey,
                    artifact.EntryPoint,
                    artifact.Stage,
                    artifact.Identity.ArtifactKind);
                if (!identities.TryGetValue(
                        key,
                        out ShaderArtifactIdentity? expected)
                    || !expected.Equals(artifact.Identity))
                {
                    throw new InvalidDataException(
                        $"Compiled artifact {key} does not match the manifest.");
                }

                string fileName = GetArtifactFileName(expected);
                string artifactPath = Path.Combine(
                    artifactsDirectory,
                    fileName);
                if (writtenFiles.Add(fileName))
                {
                    if (expected.ByteLength > (ulong)MaximumArtifactBytes)
                    {
                        throw new InvalidDataException(
                            $"Shader artifact {fileName} exceeds the "
                            + $"{MaximumArtifactBytes}-byte limit.");
                    }

                    packageBytes = CheckedPackageAdd(
                        packageBytes,
                        checked((long)expected.ByteLength));
                    using FileStream stream = new(
                        artifactPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 64 * 1024,
                        FileOptions.WriteThrough);
                    artifact.CopyContentTo(stream);
                    stream.Flush(flushToDisk: true);
                }
                else
                {
                    _ = ValidateArtifactFile(artifactPath, expected);
                }

                identities.Remove(key);
            }

            if (identities.Count != 0)
            {
                throw new InvalidDataException(
                    "The manifest references artifacts that were not returned by the compiler.");
            }

            File.WriteAllBytes(
                Path.Combine(stagingDirectory, ManifestFileName),
                manifestBytes);
        }

        private static Dictionary<ArtifactLookupKey, ShaderArtifactIdentity>
            CollectManifestArtifacts(ShaderInterfaceManifest manifest)
        {
            Dictionary<ArtifactLookupKey, ShaderArtifactIdentity> result = new();
            foreach (ShaderInterfaceVariant variant in manifest.Variants)
            {
                foreach (ShaderInterfaceEntry entry in variant.Entries)
                {
                    foreach (ShaderArtifactIdentity artifact in entry.Artifacts)
                    {
                        ArtifactLookupKey key = new(
                            variant.Key,
                            entry.Name,
                            entry.Stage,
                            artifact.ArtifactKind);
                        if (!result.TryAdd(key, artifact))
                        {
                            throw new InvalidDataException(
                                $"Manifest contains duplicate artifact {key}.");
                        }
                    }
                }
            }

            return result;
        }

        private static long ValidateArtifactFile(
            string path,
            ShaderArtifactIdentity identity)
        {
            FileInfo file = new(path);
            if (!file.Exists)
            {
                throw new FileNotFoundException(
                    "Shader artifact is missing.",
                    path);
            }

            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0
                || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Shader artifact must be a regular file: {path}");
            }

            if (file.Length > MaximumArtifactBytes
                || identity.ByteLength > (ulong)MaximumArtifactBytes)
            {
                throw new InvalidDataException(
                    $"Shader artifact {path} exceeds the "
                    + $"{MaximumArtifactBytes}-byte limit.");
            }

            if (checked((ulong)file.Length) != identity.ByteLength)
            {
                throw new InvalidDataException(
                    $"Shader artifact length mismatch: {path}");
            }

            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            string digest = Convert.ToHexStringLower(
                SHA256.HashData(stream));
            if (!string.Equals(
                    digest,
                    identity.ContentDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Shader artifact digest mismatch: {path}");
            }

            return file.Length;
        }

        private static string ResolveManifestPath(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (Directory.Exists(fullPath))
            {
                fullPath = Path.Combine(fullPath, ManifestFileName);
            }
            else if (!string.Equals(
                         Path.GetFileName(fullPath),
                         ManifestFileName,
                         StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Shader package manifest must be named {ManifestFileName}.");
            }

            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException(
                    "Shader interface manifest was not found.",
                    fullPath);
            }

            FileAttributes attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.Directory) != 0
                || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Shader package manifest must be a regular file: {fullPath}");
            }

            return fullPath;
        }
        private static void ValidatePackageRoot(
            string packageDirectory,
            string manifestPath)
        {
            FileAttributes rootAttributes =
                File.GetAttributes(packageDirectory);
            if ((rootAttributes & FileAttributes.Directory) == 0
                || (rootAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Shader package root must be a regular directory: {packageDirectory}");
            }

            string artifactsDirectory = Path.Combine(
                packageDirectory,
                ArtifactsDirectoryName);
            int entryCount = 0;
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                         packageDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                ++entryCount;
                string name = Path.GetFileName(entry);
                if (!string.Equals(
                        name,
                        ManifestFileName,
                        StringComparison.Ordinal)
                    && !string.Equals(
                        name,
                        ArtifactsDirectoryName,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Shader package contains unexpected root entry {entry}.");
                }

                if (entryCount > 2)
                {
                    throw new InvalidDataException(
                        "Shader package root contains more than two entries.");
                }
            }

            if (entryCount != 2
                || !File.Exists(manifestPath)
                || !Directory.Exists(artifactsDirectory))
            {
                throw new InvalidDataException(
                    "Shader package root must contain exactly manifest.json and artifacts.");
            }

            FileAttributes artifactAttributes =
                File.GetAttributes(artifactsDirectory);
            if ((artifactAttributes & FileAttributes.Directory) == 0
                || (artifactAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Shader artifacts path must be a regular directory: {artifactsDirectory}");
            }
        }

        private static byte[] ReadFileBounded(
            string path,
            long maximumBytes,
            string description)
        {
            FileInfo file = new(path);
            if (file.Length <= 0
                || file.Length > maximumBytes
                || file.Length > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"{description} {path} has invalid length {file.Length}; "
                    + $"the limit is {maximumBytes} bytes.");
            }

            byte[] bytes = new byte[checked((int)file.Length)];
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                throw new InvalidDataException(
                    $"{description} {path} changed while being read.");
            }

            return bytes;
        }

        private static long CheckedPackageAdd(
            long current,
            long additional)
        {
            if (additional < 0
                || current > MaximumPackageBytes - additional)
            {
                throw new InvalidDataException(
                    $"Shader artifact package exceeds the "
                    + $"{MaximumPackageBytes}-byte limit.");
            }

            return current + additional;
        }


        private static string GetArtifactFileName(
            ShaderArtifactIdentity artifact)
        {
            string extension = artifact.ArtifactKind switch
            {
                ShaderArtifactKind.Dxil => ".dxil",
                ShaderArtifactKind.SpirV => ".spv",
                ShaderArtifactKind.MslSource => ".metal",
                ShaderArtifactKind.MetalLibrary => ".metallib",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(artifact),
                    artifact.ArtifactKind,
                    "Shader artifact kind is not defined."),
            };
            return artifact.ContentDigest.ToLowerInvariant() + extension;
        }

        private static char GetRegisterPrefix(ShaderBindingClass bindingClass)
        {
            return bindingClass switch
            {
                ShaderBindingClass.ShaderResource => 't',
                ShaderBindingClass.Sampler => 's',
                ShaderBindingClass.ConstantBuffer => 'b',
                ShaderBindingClass.UnorderedAccess => 'u',
                _ => throw new ArgumentOutOfRangeException(
                    nameof(bindingClass),
                    bindingClass,
                    "Shader binding class is not defined."),
            };
        }

        private readonly record struct ArtifactLookupKey(
            string Variant,
            string EntryPoint,
            ShaderExecutionStage Stage,
            ShaderArtifactKind Kind);
    }
}
