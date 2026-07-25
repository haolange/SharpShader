using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using SharpShader.Compilation;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.ShaderLab
{
    public sealed class ShaderLabCompilerOptions
    {
        private readonly ReadOnlyCollection<string> m_IncludeDirectories;
        private readonly ReadOnlyCollection<ShaderDefine> m_GlobalDefines;
        private readonly ReadOnlyDictionary<ShaderBindingKey, uint> m_MetalArrayCapacities;

        public ShaderProgramTarget Targets { get; }
        public ShaderModelVersion ShaderModel { get; }
        public IReadOnlyList<string> IncludeDirectories => m_IncludeDirectories;
        public IReadOnlyList<ShaderDefine> GlobalDefines => m_GlobalDefines;
        public SpirvCompileOptions SpirvOptions { get; }
        public MslCompileOptions MslOptions { get; }
        public IReadOnlyDictionary<ShaderBindingKey, uint> MetalArrayCapacities =>
            m_MetalArrayCapacities;
        public bool Enable16BitTypes { get; }
        public bool EnableDebugInfo { get; }
        public bool DisableOptimizations { get; }
        public int OptimizationLevel { get; }
        public bool SkipValidation { get; }
        public bool TreatWarningsAsErrors { get; }

        public ShaderLabCompilerOptions(
            ShaderProgramTarget targets = ShaderProgramTarget.All,
            ShaderModelVersion? shaderModel = null,
            IEnumerable<string>? includeDirectories = null,
            IEnumerable<ShaderDefine>? globalDefines = null,
            SpirvCompileOptions? spirvOptions = null,
            MslCompileOptions? mslOptions = null,
            IReadOnlyDictionary<ShaderBindingKey, uint>? metalArrayCapacities = null,
            bool enable16BitTypes = true,
            bool enableDebugInfo = false,
            bool disableOptimizations = false,
            int optimizationLevel = 3,
            bool skipValidation = false,
            bool treatWarningsAsErrors = false)
        {
            if (targets == ShaderProgramTarget.None
                || (targets & ~ShaderProgramTarget.All) != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(targets),
                    targets,
                    "ShaderLab compilation requires at least one defined target.");
            }

            if (optimizationLevel is < 0 or > 3)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(optimizationLevel),
                    optimizationLevel,
                    "Optimization level must be in [0, 3].");
            }

            Targets = targets;
            ShaderModel = shaderModel ?? new ShaderModelVersion(6, 8);
            m_IncludeDirectories = Array.AsReadOnly(
                includeDirectories?.ToArray() ?? Array.Empty<string>());
            m_GlobalDefines = Array.AsReadOnly(
                globalDefines?.ToArray() ?? Array.Empty<ShaderDefine>());
            SpirvOptions = spirvOptions ?? SpirvCompileOptions.Default;
            MslOptions = mslOptions ?? MslCompileOptions.Default;
            m_MetalArrayCapacities = new ReadOnlyDictionary<ShaderBindingKey, uint>(
                metalArrayCapacities is null
                    ? new Dictionary<ShaderBindingKey, uint>()
                    : new Dictionary<ShaderBindingKey, uint>(metalArrayCapacities));
            Enable16BitTypes = enable16BitTypes;
            EnableDebugInfo = enableDebugInfo;
            DisableOptimizations = disableOptimizations;
            OptimizationLevel = optimizationLevel;
            SkipValidation = skipValidation;
            TreatWarningsAsErrors = treatWarningsAsErrors;
        }
    }

    public sealed class ShaderLabPassCompilation
    {
        public int PassIndex { get; }
        public string PassName { get; }
        public ShaderProgramCompilation Program { get; }

        internal ShaderLabPassCompilation(
            int passIndex,
            string passName,
            ShaderProgramCompilation program)
        {
            if (passIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(passIndex));
            }

            ArgumentNullException.ThrowIfNull(program);
            PassIndex = passIndex;
            PassName = passName;
            Program = program;
        }
    }

    public sealed class ShaderLabCompilation
    {
        private readonly ReadOnlyCollection<ShaderLabPassCompilation> m_Passes;

        public string SourcePath { get; }
        public IReadOnlyList<ShaderLabPassCompilation> Passes => m_Passes;

        internal ShaderLabCompilation(
            string sourcePath,
            IEnumerable<ShaderLabPassCompilation> passes)
        {
            ArgumentNullException.ThrowIfNull(passes);
            ShaderLabPassCompilation[] copy = passes.ToArray();
            Array.Sort(copy, static (left, right) =>
                left.PassIndex.CompareTo(right.PassIndex));
            for (int index = 0; index < copy.Length; ++index)
            {
                ArgumentNullException.ThrowIfNull(copy[index]);
                if (copy[index].PassIndex != index)
                {
                    throw new ArgumentException(
                        "ShaderLab pass compilations must be contiguous and ordered by pass index.",
                        nameof(passes));
                }
            }

            SourcePath = sourcePath;
            m_Passes = Array.AsReadOnly(copy);
        }

        public ShaderLabPassCompilation GetPass(int passIndex)
        {
            if ((uint)passIndex >= (uint)m_Passes.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(passIndex));
            }

            return m_Passes[passIndex];
        }
    }

    public sealed class ShaderLabCompiler
    {
        private static readonly Lazy<ShaderLabCompiler> s_Shared =
            new Lazy<ShaderLabCompiler>(
                static () => new ShaderLabCompiler(new ShaderProgramCompiler()),
                LazyThreadSafetyMode.ExecutionAndPublication);

        private readonly ShaderProgramCompiler m_ProgramCompiler;

        public static ShaderLabCompiler Shared => s_Shared.Value;

        public ShaderLabCompiler()
            : this(new ShaderProgramCompiler())
        {
        }

        public ShaderLabCompiler(ShaderProgramCompiler programCompiler)
        {
            m_ProgramCompiler = programCompiler
                ?? throw new ArgumentNullException(nameof(programCompiler));
        }

        public ShaderLabCompilation Compile(
            ShaderLab shaderLab,
            ShaderLabCompilerOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(shaderLab);
            ShaderLabCompilerOptions effectiveOptions =
                options ?? new ShaderLabCompilerOptions();
            string sourcePath = NormalizeSourcePath(shaderLab.SourcePath);
            List<ShaderLabPassCompilation> passes =
                new List<ShaderLabPassCompilation>(shaderLab.Passes.Count);

            for (int passIndex = 0; passIndex < shaderLab.Passes.Count; ++passIndex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ShaderLabPass pass = shaderLab.Passes[passIndex]
                    ?? throw new ArgumentException(
                        $"ShaderLab pass {passIndex} is null.",
                        nameof(shaderLab));
                string passName = string.IsNullOrWhiteSpace(pass.Name)
                    ? $"pass-{passIndex}"
                    : pass.Name!;
                string programSourceName = CreatePassSourceName(
                    sourcePath,
                    passIndex,
                    passName);
                ShaderProgramCompilation program = CompileProgram(
                    pass.Program,
                    programSourceName,
                    sourcePath,
                    effectiveOptions,
                    cancellationToken);
                passes.Add(new ShaderLabPassCompilation(
                    passIndex,
                    passName,
                    program));
            }

            return new ShaderLabCompilation(sourcePath, passes);
        }

        public ShaderProgramCompilation CompileStandalone(
            StandaloneShaderProgram program,
            ShaderLabCompilerOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(program);
            ShaderLabCompilerOptions effectiveOptions =
                options ?? new ShaderLabCompilerOptions();
            string sourcePath = NormalizeSourcePath(program.SourcePath);
            ShaderProgramCompileRequest request = BuildRequest(
                program.Source,
                sourcePath,
                program.Entries.Select(MapEntry),
                program.EnumerateVariantKeys(),
                attachmentPhase: null,
                sourcePath,
                effectiveOptions);
            return m_ProgramCompiler.Compile(request, cancellationToken);
        }

        public ShaderProgramCompilation CompileProgram(
            ShaderLabProgram program,
            string sourceName,
            string sourcePath,
            ShaderLabCompilerOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(program);
            ShaderLabCompilerOptions effectiveOptions =
                options ?? new ShaderLabCompilerOptions();
            ShaderProgramCompileRequest request = BuildRequest(
                program.Source,
                sourceName,
                program.Entries.Select(MapEntry),
                program.EnumerateVariantKeys(),
                program.AttachmentPhase,
                NormalizeSourcePath(sourcePath),
                effectiveOptions);
            return m_ProgramCompiler.Compile(request, cancellationToken);
        }

        private static ShaderProgramCompileRequest BuildRequest(
            string source,
            string sourceName,
            IEnumerable<ShaderProgramEntry> entries,
            IReadOnlyList<ShaderVariantKey> variants,
            ShaderAttachmentPhase? attachmentPhase,
            string sourcePath,
            ShaderLabCompilerOptions options)
        {
            List<string> includeDirectories = BuildIncludeDirectories(
                sourcePath,
                options.IncludeDirectories);
            ShaderProgramEntry[] programEntries = entries.ToArray();
            ShaderProgramVariant[] programVariants = variants
                .Select(static variant => new ShaderProgramVariant(
                    GetVariantKey(variant),
                    variant.Keywords.Select(static keyword =>
                        new ShaderDefine(keyword, "1"))))
                .ToArray();
            ShaderAttachmentInterface[] attachmentInterfaces =
                BuildAttachmentInterfaces(
                    programEntries,
                    programVariants,
                    attachmentPhase);
            return new ShaderProgramCompileRequest(
                source,
                sourceName,
                programEntries,
                programVariants,
                options.Targets,
                options.ShaderModel,
                options.GlobalDefines,
                includeDirectories,
                options.SpirvOptions,
                options.MslOptions,
                options.MetalArrayCapacities,
                options.Enable16BitTypes,
                options.EnableDebugInfo,
                options.DisableOptimizations,
                options.OptimizationLevel,
                options.SkipValidation,
                options.TreatWarningsAsErrors,
                attachmentInterfaces);
        }

        private static ShaderAttachmentInterface[] BuildAttachmentInterfaces(
            IReadOnlyList<ShaderProgramEntry> entries,
            IReadOnlyList<ShaderProgramVariant> variants,
            ShaderAttachmentPhase? attachmentPhase)
        {
            int pixelEntryCount = 0;
            foreach (ShaderProgramEntry entry in entries)
            {
                if (entry.Stage == ShaderExecutionStage.Pixel)
                {
                    ++pixelEntryCount;
                }
            }

            if (pixelEntryCount == 0)
            {
                if (attachmentPhase is not null)
                {
                    throw new ArgumentException(
                        "A ShaderLab AttachmentInterface block requires at least "
                        + "one pixel shader entry.",
                        nameof(attachmentPhase));
                }

                return Array.Empty<ShaderAttachmentInterface>();
            }

            if (attachmentPhase is null)
            {
                throw new ArgumentException(
                    "Every ShaderLab program with a pixel shader entry requires "
                    + "one explicit AttachmentInterface block.",
                    nameof(attachmentPhase));
            }

            List<ShaderAttachmentInterface> result = new(
                checked(pixelEntryCount * variants.Count));
            foreach (ShaderProgramVariant variant in variants)
            {
                foreach (ShaderProgramEntry entry in entries)
                {
                    if (entry.Stage != ShaderExecutionStage.Pixel)
                    {
                        continue;
                    }

                    result.Add(new ShaderAttachmentInterface(
                        variant.Key,
                        entry.Name,
                        entry.Stage,
                        attachmentPhase));
                }
            }

            return result.ToArray();
        }

        private static List<string> BuildIncludeDirectories(
            string sourcePath,
            IReadOnlyList<string> requestedDirectories)
        {
            List<string> directories = new List<string>(
                requestedDirectories.Count + 1);
            HashSet<string> identities =
                new HashSet<string>(GetPathComparer());
            if (!IsMemorySource(sourcePath))
            {
                string? sourceDirectory = Path.GetDirectoryName(sourcePath);
                if (!string.IsNullOrWhiteSpace(sourceDirectory))
                {
                    AddIncludeDirectory(
                        directories,
                        identities,
                        sourceDirectory,
                        nameof(sourcePath));
                }
            }

            foreach (string directory in requestedDirectories)
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    throw new ArgumentException(
                        "Shader include directories must not contain empty paths.",
                        nameof(requestedDirectories));
                }

                AddIncludeDirectory(
                    directories,
                    identities,
                    directory,
                    nameof(requestedDirectories));
            }

            return directories;
        }

        private static void AddIncludeDirectory(
            List<string> directories,
            HashSet<string> identities,
            string directory,
            string parameterName)
        {
            if (!Path.IsPathFullyQualified(directory))
            {
                throw new ArgumentException(
                    $"Shader include directory {directory} must be an absolute path. "
                    + "ShaderLab compilation never resolves include roots against "
                    + "the process working directory.",
                    parameterName);
            }

            string normalizedDirectory;
            try
            {
                normalizedDirectory = Path.GetFullPath(directory);
            }
            catch (Exception exception) when (
                exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
            {
                throw new ArgumentException(
                    $"Shader include directory {directory} is not a valid path.",
                    parameterName,
                    exception);
            }

            if (!Directory.Exists(normalizedDirectory))
            {
                throw new ArgumentException(
                    $"Shader include directory {normalizedDirectory} does not exist.",
                    parameterName);
            }

            FileAttributes attributes = File.GetAttributes(normalizedDirectory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException(
                    $"Shader include directory {normalizedDirectory} is a reparse point. "
                    + "Reparse points are not followed by the content-addressed source graph.",
                    parameterName);
            }

            if (!identities.Add(normalizedDirectory))
            {
                throw new ArgumentException(
                    $"Shader include directory {normalizedDirectory} is duplicated.",
                    parameterName);
            }

            directories.Add(normalizedDirectory);
        }

        private static ShaderProgramEntry MapEntry(ShaderLabProgramEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry.EntryName))
            {
                throw new ArgumentException(
                    "ShaderLab program entries must have non-empty names.",
                    nameof(entry));
            }

            return new ShaderProgramEntry(
                entry.EntryName,
                entry.Stage switch
                {
                    EShaderLabShaderStage.ProgramVertex =>
                        ShaderExecutionStage.Vertex,
                    EShaderLabShaderStage.ProgramFragment =>
                        ShaderExecutionStage.Pixel,
                    EShaderLabShaderStage.ProgramMesh =>
                        ShaderExecutionStage.Mesh,
                    EShaderLabShaderStage.ProgramTask =>
                        ShaderExecutionStage.Amplification,
                    EShaderLabShaderStage.ProgramCompute =>
                        ShaderExecutionStage.Compute,
                    EShaderLabShaderStage.ProgramRayGen =>
                        ShaderExecutionStage.RayGeneration,
                    EShaderLabShaderStage.ProgramRayInt =>
                        ShaderExecutionStage.Intersection,
                    EShaderLabShaderStage.ProgramRayAHit =>
                        ShaderExecutionStage.AnyHit,
                    EShaderLabShaderStage.ProgramRayCHit =>
                        ShaderExecutionStage.ClosestHit,
                    EShaderLabShaderStage.ProgramRayMiss =>
                        ShaderExecutionStage.Miss,
                    EShaderLabShaderStage.ProgramRayRcall =>
                        ShaderExecutionStage.Callable,
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(entry),
                        entry.Stage,
                        "ShaderLab program entry stage is not defined."),
                });
        }

        private static ShaderProgramEntry MapEntry(StandaloneShaderEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            return new ShaderProgramEntry(
                entry.EntryName,
                entry.Stage switch
                {
                    StandaloneShaderStage.Compute =>
                        ShaderExecutionStage.Compute,
                    StandaloneShaderStage.RayGeneration =>
                        ShaderExecutionStage.RayGeneration,
                    StandaloneShaderStage.Miss =>
                        ShaderExecutionStage.Miss,
                    StandaloneShaderStage.Callable =>
                        ShaderExecutionStage.Callable,
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(entry),
                        entry.Stage,
                        "Standalone shader entry stage is not defined."),
                });
        }

        public static string GetVariantKey(ShaderVariantKey variant)
        {
            return variant.Keywords.Count == 0
                ? ShaderProgramVariant.Default.Key
                : string.Join(';', variant.Keywords);
        }

        private static string NormalizeSourcePath(string? sourcePath)
        {
            return IsMemorySource(sourcePath)
                ? "<memory>"
                : Path.GetFullPath(sourcePath!);
        }

        private static bool IsMemorySource(string? sourcePath)
        {
            return string.IsNullOrWhiteSpace(sourcePath)
                || string.Equals(sourcePath, "<memory>", StringComparison.Ordinal);
        }

        private static StringComparer GetPathComparer()
        {
            return OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        }

        private static string CreatePassSourceName(
            string sourcePath,
            int passIndex,
            string passName)
        {
            string sourceStem = IsMemorySource(sourcePath)
                ? "shaderlab"
                : Path.GetFileNameWithoutExtension(sourcePath);
            string sourceName = SanitizeSourceName(sourceStem)
                + ".pass-"
                + passIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "-"
                + SanitizeSourceName(passName)
                + ".hlsl";
            if (IsMemorySource(sourcePath))
            {
                return sourceName;
            }

            string? sourceDirectory = Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrWhiteSpace(sourceDirectory))
            {
                throw new ArgumentException(
                    $"Physical ShaderLab source {sourcePath} has no parent directory.",
                    nameof(sourcePath));
            }

            return Path.Combine(sourceDirectory, sourceName);
        }

        private static string SanitizeSourceName(string value)
        {
            char[] characters = value.ToCharArray();
            for (int index = 0; index < characters.Length; ++index)
            {
                char character = characters[index];
                if (!char.IsLetterOrDigit(character)
                    && character is not '.' and not '-' and not '_')
                {
                    characters[index] = '_';
                }
            }

            return characters.Length == 0 ? "shader" : new string(characters);
        }
    }
}
