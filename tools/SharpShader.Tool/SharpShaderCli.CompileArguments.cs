using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;

using SharpShader.Compilation;
using SharpShader.HLSLCrossCompiler;

[assembly: InternalsVisibleTo("Infinity.Rendering.Tests")]

namespace SharpShader.Tool
{
    internal static partial class SharpShaderCli
    {

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
            public string? AttachmentInterfacePath { get; private set; }
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
                        case "--attachment-interface":
                            result.AttachmentInterfacePath = AssignOnce(
                                result.AttachmentInterfacePath,
                                ReadValue(args, ref index, option),
                                option);
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
                    treatWarningsAsErrors: TreatWarningsAsErrors,
                    attachmentInterfaces: AttachmentInterfacePath is null
                        ? Array.Empty<ShaderAttachmentInterface>()
                        : ShaderAttachmentInterfaceFile.Load(
                            AttachmentInterfacePath));
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

                bool hasPixelEntry = m_Entries.Any(
                    static entry =>
                        entry.Stage == ShaderExecutionStage.Pixel);
                if (hasPixelEntry
                    && string.IsNullOrWhiteSpace(AttachmentInterfacePath))
                {
                    throw new CliUsageException(
                        "--attachment-interface is required when compiling a pixel entry.");
                }

                if (!string.IsNullOrWhiteSpace(AttachmentInterfacePath))
                {
                    AttachmentInterfacePath = Path.GetFullPath(
                        AttachmentInterfacePath);
                    if (!File.Exists(AttachmentInterfacePath))
                    {
                        throw new FileNotFoundException(
                            "Attachment interface file was not found.",
                            AttachmentInterfacePath);
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
                             s_TargetSeparators,
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
}
}
