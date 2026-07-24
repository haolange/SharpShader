using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SharpShader.HLSLCrossCompiler;
using SharpShader.HLSLCrossCompiler.Internal;

namespace SharpShader.Compilation.Internal
{
    internal sealed class ShaderProgramCompilerExecutionContext
    {
        public Func<ShaderCompileRequest, ShaderCompileResult>? CompileOverride { get; init; }
        public Func<
            ShaderCompileRequest,
            DxcDependencyCaptureLimits,
            DxcPreprocessedSource>? PreprocessOverride { get; init; }
        public Func<ShaderProgramCompileRequest, IReadOnlyList<ShaderToolchainComponent>>?
            ToolchainComponentsOverride { get; init; }
    }

    internal sealed class ShaderProgramInputSnapshot
    {
        private static readonly UTF8Encoding s_StrictUtf8 = new(false, true);

        private readonly ShaderProgramCacheLimits m_Limits;
        private readonly bool m_IncludeSourceDirectoryTopology;
        private readonly List<ShaderProgramFrozenCompileUnit> m_FrozenUnits = new();
        private IReadOnlyList<ShaderProgramDirectoryTopology>?
            m_InitialTopologies;
        private bool m_InitialTopologyCaptureComplete;
        private bool m_FreezeCompleted;

        public ShaderProgramCompileRequest Request { get; }
        public IReadOnlyList<string> IncludeDirectories { get; }
        public IReadOnlyList<ShaderToolchainComponent> ToolchainComponents { get; }
        public string ProvisionalKey { get; }

        private ShaderProgramInputSnapshot(
            ShaderProgramCompileRequest request,
            IReadOnlyList<string> includeDirectories,
            IReadOnlyList<ShaderToolchainComponent> toolchainComponents,
            string provisionalKey,
            bool includeSourceDirectoryTopology,
            ShaderProgramCacheLimits limits)
        {
            Request = request;
            IncludeDirectories = includeDirectories;
            ToolchainComponents = toolchainComponents;
            ProvisionalKey = provisionalKey;
            m_IncludeSourceDirectoryTopology = includeSourceDirectoryTopology;
            m_Limits = limits;
        }

        public ShaderCompileRequest FreezeCompileUnit(
            ShaderCompileRequest request,
            ShaderProgramCompileUnitIdentity identity,
            ShaderProgramCompilerExecutionContext? context)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (m_FreezeCompleted)
            {
                throw new InvalidOperationException(
                    "Shader compile units cannot be frozen after finalization.");
            }

            if (m_FrozenUnits.Any(unit => unit.Identity == identity))
            {
                throw new InvalidOperationException(
                    $"Shader compile unit {identity} was frozen more than once.");
            }

            EnsureInitialTopologyCapture();
            DxcDependencyCaptureLimits captureLimits = CreateCaptureLimits();
            DxcPreprocessedSource preprocessed =
                context?.PreprocessOverride?.Invoke(request, captureLimits)
                ?? NativeDxcCompiler.Preprocess(request, captureLimits);
            if (!m_IncludeSourceDirectoryTopology
                && IncludeDirectories.Count == 0
                && preprocessed.Includes.Count != 0)
            {
                throw InvalidRequest(
                    "An in-memory shader with relative SourceName must provide an "
                    + "explicit absolute include directory before it can include files.");
            }

            m_FrozenUnits.Add(new ShaderProgramFrozenCompileUnit(
                identity,
                preprocessed));
            return request with
            {
                Source = preprocessed.Source,
                Defines = Array.Empty<ShaderDefine>(),
                IncludeDirs = Array.Empty<string>(),
            };
        }

        public ShaderProgramFrozenInput CompleteFreeze()
        {
            if (m_FreezeCompleted)
            {
                throw new InvalidOperationException(
                    "A shader input snapshot can only be finalized once.");
            }

            if (m_FrozenUnits.Count == 0)
            {
                throw new InvalidOperationException(
                    "A shader input snapshot cannot be finalized without compile units.");
            }

            ShaderProgramFrozenCompileUnit[] units = m_FrozenUnits.ToArray();
            Array.Sort(units, static (left, right) =>
                CompareCompileUnitIdentities(left.Identity, right.Identity));
            string sourceDigest = ComputeFrozenSourceDigest(units);
            string finalKey = ComputeFinalKey(ProvisionalKey, sourceDigest);
            ShaderProgramDependencySnapshot dependencies =
                ShaderProgramDependencySnapshot.Create(
                    ProvisionalKey,
                    finalKey,
                    Request.SourceName,
                    m_IncludeSourceDirectoryTopology,
                    IncludeDirectories,
                    m_InitialTopologies!,
                    m_InitialTopologyCaptureComplete,
                    units,
                    m_Limits);
            m_FreezeCompleted = true;
            return new ShaderProgramFrozenInput(
                sourceDigest,
                finalKey,
                dependencies);
        }

        public static ShaderProgramInputSnapshot Create(
            ShaderProgramCompileRequest request,
            ShaderProgramCacheLimits limits,
            ShaderProgramCompilerExecutionContext? context)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(limits);
            ValidateRequest(request, limits);

            string[] includeDirectories = NormalizeIncludeDirectories(
                request.IncludeDirectories);
            ShaderSourceOrigin sourceOrigin = NormalizeSourceOrigin(
                request.SourceName,
                includeDirectories);
            ShaderProgramCompileRequest normalizedRequest =
                CopyRequestWithOrigin(
                    request,
                    sourceOrigin.SourceName,
                    includeDirectories);
            IReadOnlyList<ShaderToolchainComponent> toolchainComponents =
                context?.ToolchainComponentsOverride?.Invoke(normalizedRequest)
                ?? DiscoverToolchainComponents(normalizedRequest);
            ShaderToolchainComponent[] toolchainCopy =
                new List<ShaderToolchainComponent>(toolchainComponents).ToArray();
            Array.Sort(toolchainCopy, static (left, right) =>
                string.CompareOrdinal(left?.Name, right?.Name));
            ValidateToolchain(toolchainCopy);

            string provisionalKey = ComputeProvisionalKey(
                normalizedRequest,
                includeDirectories,
                toolchainCopy);
            return new ShaderProgramInputSnapshot(
                normalizedRequest,
                Array.AsReadOnly(includeDirectories),
                Array.AsReadOnly(toolchainCopy),
                provisionalKey,
                sourceOrigin.IncludeDirectoryTopology,
                limits);
        }

        private static void ValidateRequest(
            ShaderProgramCompileRequest request,
            ShaderProgramCacheLimits limits)
        {
            if (string.IsNullOrWhiteSpace(request.Source))
            {
                throw InvalidRequest("Shader program source must not be empty.");
            }

            if (string.IsNullOrWhiteSpace(request.SourceName))
            {
                throw InvalidRequest("Shader program source name must not be empty.");
            }

            int sourceByteCount;
            try
            {
                sourceByteCount = s_StrictUtf8.GetByteCount(request.Source);
            }
            catch (EncoderFallbackException ex)
            {
                throw InvalidRequest("Shader program source is not valid Unicode.", ex);
            }

            if (sourceByteCount > limits.MaximumSourceBytes)
            {
                throw InvalidRequest(
                    $"Shader program source contains {sourceByteCount} UTF-8 bytes, "
                    + $"exceeding the configured limit {limits.MaximumSourceBytes}.");
            }

            if ((request.Targets & ~ShaderProgramTarget.All) != 0
                || request.Targets == ShaderProgramTarget.None)
            {
                throw InvalidRequest(
                    $"Shader program targets {request.Targets} are empty or contain undefined flags.");
            }

            if (!request.ShaderModel.IsInRange)
            {
                throw InvalidRequest(
                    $"Shader model {request.ShaderModel} is outside supported range 6.0-6.8.");
            }

            if (request.OptimizationLevel is < 0 or > 3)
            {
                throw InvalidRequest("OptimizationLevel must be in range 0-3.");
            }

            bool requestsCrossBackend =
                (request.Targets & (ShaderProgramTarget.Vulkan | ShaderProgramTarget.MetalMsl)) != 0;
            if (requestsCrossBackend)
            {
                ShaderProgramEntry[] libraryEntries = request.Entries
                    .Where(static entry =>
                        entry.Stage is ShaderExecutionStage.RayGeneration
                            or ShaderExecutionStage.Intersection
                            or ShaderExecutionStage.AnyHit
                            or ShaderExecutionStage.ClosestHit
                            or ShaderExecutionStage.Miss
                            or ShaderExecutionStage.Callable
                            or ShaderExecutionStage.Node)
                    .ToArray();
                if (libraryEntries.Length != 0)
                {
                    string stages = string.Join(
                        ", ",
                        libraryEntries.Select(static entry =>
                            $"{entry.Name} ({entry.Stage})"));
                    throw InvalidRequest(
                        $"Shader program targets {request.Targets} include unsupported "
                        + $"cross-backend targets for DXIL-library entries: {stages}. "
                        + "The verified high-level SPIR-V/MSL path does not support "
                        + "ray-tracing or work-graph libraries; compile the complete "
                        + "program for DirectX12 only.");
                }
            }

            ValidateCombinedDefines(request);
            ValidateStrings(request.IncludeDirectories, "include directories", allowEmpty: false);
            ValidateReservedArguments(request);
            ValidateSpirvOptions(request.SpirvOptions, requestsCrossBackend);
            ValidateMetalCapacities(request.MetalArrayCapacities);
            ValidateMslOptions(request.MslOptions);
        }

        private static void ValidateCombinedDefines(ShaderProgramCompileRequest request)
        {
            foreach (ShaderProgramVariant variant in request.Variants)
            {
                HashSet<string> names = new(StringComparer.Ordinal);
                foreach (ShaderDefine define in request.GlobalDefines)
                {
                    names.Add(define.Name);
                }

                foreach (ShaderDefine define in variant.Defines)
                {
                    if (!names.Add(define.Name))
                    {
                        throw InvalidRequest(
                            $"Variant {variant.Key} redefines global shader define {define.Name}.");
                    }
                }
            }
        }

        private static void ValidateStrings(
            IReadOnlyList<string> values,
            string description,
            bool allowEmpty)
        {
            ArgumentNullException.ThrowIfNull(values);
            foreach (string value in values)
            {
                if (value is null || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
                {
                    throw InvalidRequest(
                        $"Shader program {description} must not contain null or empty values.");
                }
            }
        }

        private static void ValidateReservedArguments(ShaderProgramCompileRequest request)
        {
            if (request.SpirvOptions.AdditionalArguments.Count != 0)
            {
                throw InvalidRequest(
                    "ShaderProgramCompiler does not accept raw SPIR-V additional "
                    + "arguments. Use typed high-level options, or use raw "
                    + "HLSLCrossCompiler for caller-owned target-specific arguments.");
            }
        }

        private static void ValidateSpirvOptions(
            SpirvCompileOptions options,
            bool requestsCrossBackend)
        {
            ArgumentNullException.ThrowIfNull(options);
            int layoutCount = (options.UseDxLayout ? 1 : 0)
                + (options.UseGlLayout ? 1 : 0)
                + (options.UseScalarLayout ? 1 : 0);
            if (layoutCount > 1)
            {
                throw InvalidRequest(
                    "SPIR-V DX, GL, and scalar memory layout modes are mutually exclusive.");
            }

            if (requestsCrossBackend
                && (!options.UseDxLayout
                    || options.UseGlLayout
                    || options.UseScalarLayout))
            {
                throw InvalidRequest(
                    "High-level Vulkan/Metal compilation requires SPIR-V DX memory layout.");
            }

            if (options.BindingShifts is null)
            {
                throw InvalidRequest("SPIR-V binding shifts must not be null.");
            }

            if (options.BindingShifts.Count != 0)
            {
                throw InvalidRequest(
                    "High-level shader compilation owns all SPIR-V binding shifts. "
                    + "Use raw HLSLCrossCompiler for caller-owned target-specific bindings.");
            }

            ValidateStrings(
                options.AdditionalArguments,
                "SPIR-V additional arguments",
                allowEmpty: false);
        }

        private static void ValidateMslOptions(MslCompileOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (!Enum.IsDefined(options.Platform))
            {
                throw InvalidRequest(
                    $"Metal target platform {options.Platform} is not defined.");
            }

            if (options.EnableArgumentBuffers
                || options.ArgumentBuffersTier != 0
                || options.EnableDecorateArgumentBufferIndex)
            {
                throw InvalidRequest(
                    "High-level Metal compilation owns argument-buffer enablement, tier, "
                    + "and binding-index decoration. Use raw HLSLCrossCompiler for "
                    + "caller-owned target-specific Metal binding options.");
            }
        }

        private static void ValidateMetalCapacities(
            IReadOnlyDictionary<ShaderBindingKey, uint> capacities)
        {
            ArgumentNullException.ThrowIfNull(capacities);
            foreach ((ShaderBindingKey key, uint capacity) in capacities)
            {
                if (capacity == 0)
                {
                    throw InvalidRequest(
                        $"Metal array capacity for {key} must be greater than zero.");
                }
            }
        }

        private static string[] NormalizeIncludeDirectories(
            IReadOnlyList<string> includeDirectories)
        {
            string[] normalized = new string[includeDirectories.Count];
            HashSet<string> identities = new(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);
            for (int index = 0; index < includeDirectories.Count; ++index)
            {
                if (!Path.IsPathFullyQualified(includeDirectories[index]))
                {
                    throw InvalidRequest(
                        $"Include directory {includeDirectories[index]} must be an "
                        + "absolute path. High-level compilation never resolves "
                        + "include roots against the process working directory.");
                }

                string path;
                try
                {
                    path = Path.GetFullPath(includeDirectories[index]);
                }
                catch (Exception ex) when (
                    ex is ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
                {
                    throw InvalidRequest(
                        $"Include directory {includeDirectories[index]} is not a valid path.",
                        ex);
                }

                if (!Directory.Exists(path))
                {
                    throw InvalidRequest($"Include directory {path} does not exist.");
                }

                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw InvalidRequest(
                        $"Include directory {path} is a reparse point. "
                        + "Reparse points are not followed by the content-addressed source graph.");
                }

                if (!identities.Add(path))
                {
                    throw InvalidRequest($"Include directory {path} is duplicated.");
                }

                normalized[index] = path;
            }

            return normalized;
        }

        private static ShaderSourceOrigin NormalizeSourceOrigin(
            string sourceName,
            IReadOnlyList<string> includeDirectories)
        {
            try
            {
                if (Path.IsPathFullyQualified(sourceName))
                {
                    return new ShaderSourceOrigin(
                        Path.GetFullPath(sourceName),
                        IncludeDirectoryTopology: true);
                }

                if (includeDirectories.Count != 0)
                {
                    string root = includeDirectories[0];
                    string relativeName = string.Equals(
                        sourceName,
                        "<memory>",
                        StringComparison.Ordinal)
                        ? CreateLogicalSourceFileName(sourceName)
                        : sourceName;
                    string resolved = Path.GetFullPath(
                        Path.Combine(root, relativeName));
                    if (!IsPathWithinRoot(root, resolved))
                    {
                        throw InvalidRequest(
                            $"Shader source name {sourceName} escapes the explicit "
                            + $"source root {root}.");
                    }

                    return new ShaderSourceOrigin(
                        resolved,
                        IncludeDirectoryTopology: true);
                }

                string logicalRoot = Path.Combine(
                    AppContext.BaseDirectory,
                    ".sharpshader-memory");
                return new ShaderSourceOrigin(
                    Path.Combine(
                        logicalRoot,
                        CreateLogicalSourceFileName(sourceName)),
                    IncludeDirectoryTopology: false);
            }
            catch (ShaderCompilerException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
            {
                throw InvalidRequest(
                    $"Shader source name {sourceName} is not a valid source origin.",
                    exception);
            }
        }

        private static string CreateLogicalSourceFileName(string sourceName)
        {
            string digest = Convert.ToHexStringLower(
                SHA256.HashData(s_StrictUtf8.GetBytes(sourceName)));
            return $"logical-{digest}.hlsl";
        }

        private static bool IsPathWithinRoot(string root, string path)
        {
            string relative = Path.GetRelativePath(root, path);
            return !Path.IsPathFullyQualified(relative)
                && !string.Equals(relative, "..", StringComparison.Ordinal)
                && !relative.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                && !relative.StartsWith(
                    $"..{Path.AltDirectorySeparatorChar}",
                    StringComparison.Ordinal);
        }

        private static ShaderProgramCompileRequest CopyRequestWithOrigin(
            ShaderProgramCompileRequest request,
            string sourceName,
            IReadOnlyList<string> includeDirectories)
        {
            return new ShaderProgramCompileRequest(
                request.Source,
                sourceName,
                request.Entries,
                request.Variants,
                request.Targets,
                request.ShaderModel,
                request.GlobalDefines,
                includeDirectories,
                request.SpirvOptions,
                request.MslOptions,
                request.MetalArrayCapacities,
                request.Enable16BitTypes,
                request.EnableDebugInfo,
                request.DisableOptimizations,
                request.OptimizationLevel,
                request.SkipValidation,
                request.TreatWarningsAsErrors);
        }

        private void EnsureInitialTopologyCapture()
        {
            if (m_InitialTopologies is not null)
            {
                return;
            }

            m_InitialTopologies =
                ShaderProgramDependencySnapshot.CaptureTopologies(
                    Request.SourceName,
                    m_IncludeSourceDirectoryTopology,
                    IncludeDirectories,
                    m_Limits,
                    out bool captureComplete);
            m_InitialTopologyCaptureComplete = captureComplete;
        }

        private DxcDependencyCaptureLimits CreateCaptureLimits()
        {
            return new DxcDependencyCaptureLimits(
                m_Limits.MaximumIncludeFileCount,
                m_Limits.MaximumIncludeFileBytes,
                m_Limits.MaximumIncludeTotalBytes,
                m_Limits.MaximumCachePackageBytes);
        }

        private static int CompareCompileUnitIdentities(
            ShaderProgramCompileUnitIdentity left,
            ShaderProgramCompileUnitIdentity right)
        {
            int variant = string.CompareOrdinal(
                left.VariantKey,
                right.VariantKey);
            if (variant != 0)
            {
                return variant;
            }

            int target = left.Target.CompareTo(right.Target);
            if (target != 0)
            {
                return target;
            }

            int stage = left.Stage.CompareTo(right.Stage);
            return stage != 0
                ? stage
                : string.CompareOrdinal(
                    left.EntryIdentity,
                    right.EntryIdentity);
        }

        private static string ComputeFrozenSourceDigest(
            IReadOnlyList<ShaderProgramFrozenCompileUnit> units)
        {
            using IncrementalHash hash =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendString(hash, "SharpShader.FrozenSource.v1");
            AppendUInt64(hash, checked((ulong)units.Count));
            foreach (ShaderProgramFrozenCompileUnit unit in units)
            {
                AppendString(hash, unit.Identity.VariantKey);
                AppendInt32(hash, (int)unit.Identity.Target);
                AppendInt32(hash, (int)unit.Identity.Stage);
                AppendString(hash, unit.Identity.EntryIdentity);
                AppendContent(hash, unit.PreprocessedSource.CopyContent());
                AppendUInt64(
                    hash,
                    checked((ulong)unit.PreprocessedSource.Includes.Count));
                foreach (DxcCapturedInclude include in
                         unit.PreprocessedSource.Includes)
                {
                    AppendString(
                        hash,
                        NormalizeHashPath(include.RequestedPath));
                    AppendContent(hash, include.CopyContent());
                }
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        private static string ComputeFinalKey(
            string provisionalKey,
            string sourceDigest)
        {
            using IncrementalHash hash =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendString(hash, "SharpShader.ProgramCache.v2.Final");
            AppendString(hash, provisionalKey);
            AppendString(hash, sourceDigest);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        private static void AppendContent(
            IncrementalHash hash,
            byte[] content)
        {
            AppendUInt64(hash, checked((ulong)content.LongLength));
            hash.AppendData(content);
        }

        private static string NormalizeHashPath(string path)
        {
            return path
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
        }

        private readonly record struct ShaderSourceOrigin(
            string SourceName,
            bool IncludeDirectoryTopology);

        private static IReadOnlyList<ShaderToolchainComponent> DiscoverToolchainComponents(
            ShaderProgramCompileRequest request)
        {
            Assembly assembly = typeof(ShaderProgramCompiler).Assembly;
            List<ShaderToolchainComponent> components = new()
            {
                CreateManagedComponent(assembly),
            };
            components.AddRange(
                SharpShaderNativeLibraryResolver
                    .ResolveDxcToolchain(assembly)
                    .Components);

            if ((request.Targets & (ShaderProgramTarget.Vulkan | ShaderProgramTarget.MetalMsl)) != 0)
            {
                components.Add(
                    SpirvCrossNativeLibraryBootstrap
                        .ResolveToolchainComponent());
            }

            return components;
        }

        private static ShaderToolchainComponent CreateManagedComponent(Assembly assembly)
        {
            string version = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "unknown";
            string? digest = File.Exists(assembly.Location)
                ? ComputeFileDigest(assembly.Location)
                : null;
            return new ShaderToolchainComponent("SharpShader", version, digest);
        }

        private static string ComputeFileDigest(string path)
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        private static void ValidateToolchain(ShaderToolchainComponent[] components)
        {
            if (components.Length == 0)
            {
                throw InvalidRequest("Shader toolchain identity must not be empty.");
            }

            for (int index = 0; index < components.Length; ++index)
            {
                ArgumentNullException.ThrowIfNull(components[index]);
                if (index > 0
                    && string.Equals(
                        components[index - 1].Name,
                        components[index].Name,
                        StringComparison.Ordinal))
                {
                    throw InvalidRequest(
                        $"Shader toolchain component {components[index].Name} is duplicated.");
                }
            }
        }

        private static string ComputeProvisionalKey(
            ShaderProgramCompileRequest request,
            IReadOnlyList<string> includeDirectories,
            IReadOnlyList<ShaderToolchainComponent> components)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendString(hash, "SharpShader.ProgramCache.v2.Provisional");
            AppendString(hash, request.Source);
            AppendString(hash, request.SourceName);
            AppendUInt64(hash, (ulong)request.Targets);
            AppendInt32(hash, request.ShaderModel.Major);
            AppendInt32(hash, request.ShaderModel.Minor);
            AppendBoolean(hash, request.Enable16BitTypes);
            AppendBoolean(hash, request.EnableDebugInfo);
            AppendBoolean(hash, request.DisableOptimizations);
            AppendInt32(hash, request.OptimizationLevel);
            AppendBoolean(hash, request.SkipValidation);
            AppendBoolean(hash, request.TreatWarningsAsErrors);

            AppendEntries(hash, request.Entries);
            AppendVariants(hash, request.Variants);
            AppendDefines(hash, request.GlobalDefines);
            AppendStrings(hash, includeDirectories);
            AppendSpirvOptions(hash, request.SpirvOptions);
            AppendMslOptions(hash, request.MslOptions);
            AppendMetalCapacities(hash, request.MetalArrayCapacities);
            AppendToolchain(hash, components);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        private static void AppendEntries(
            IncrementalHash hash,
            IReadOnlyList<ShaderProgramEntry> entries)
        {
            AppendUInt64(hash, checked((ulong)entries.Count));
            foreach (ShaderProgramEntry entry in entries)
            {
                AppendString(hash, entry.Name);
                AppendInt32(hash, (int)entry.Stage);
            }
        }

        private static void AppendVariants(
            IncrementalHash hash,
            IReadOnlyList<ShaderProgramVariant> variants)
        {
            AppendUInt64(hash, checked((ulong)variants.Count));
            foreach (ShaderProgramVariant variant in variants)
            {
                AppendString(hash, variant.Key);
                AppendDefines(hash, variant.Defines);
            }
        }

        private static void AppendDefines(
            IncrementalHash hash,
            IReadOnlyList<ShaderDefine> defines)
        {
            AppendUInt64(hash, checked((ulong)defines.Count));
            foreach (ShaderDefine define in defines)
            {
                AppendString(hash, define.Name);
                AppendNullableString(hash, define.Value);
            }
        }

        private static void AppendStrings(
            IncrementalHash hash,
            IReadOnlyList<string> values)
        {
            AppendUInt64(hash, checked((ulong)values.Count));
            foreach (string value in values)
            {
                AppendString(hash, value);
            }
        }

        private static void AppendSpirvOptions(
            IncrementalHash hash,
            SpirvCompileOptions options)
        {
            AppendBoolean(hash, options.UseDxLayout);
            AppendBoolean(hash, options.UseGlLayout);
            AppendBoolean(hash, options.UseScalarLayout);
            AppendBoolean(hash, options.InvertY);
            AppendNullableString(hash, options.TargetEnvironment);
            AppendStrings(hash, options.AdditionalArguments);
        }

        private static void AppendMslOptions(
            IncrementalHash hash,
            MslCompileOptions options)
        {
            AppendInt32(hash, (int)options.Platform);
            AppendUInt64(hash, options.MslVersion);
            AppendBoolean(hash, options.EnableArgumentBuffers);
            AppendUInt64(hash, options.ArgumentBuffersTier);
            AppendBoolean(hash, options.ForceNativeArrays);
            AppendBoolean(hash, options.PadFragmentOutputComponents);
            AppendBoolean(hash, options.CaptureOutputToBuffer);
            AppendBoolean(hash, options.EnablePointSizeBuiltin);
            AppendBoolean(hash, options.EnableDecorateArgumentBufferIndex);
        }

        private static void AppendMetalCapacities(
            IncrementalHash hash,
            IReadOnlyDictionary<ShaderBindingKey, uint> capacities)
        {
            KeyValuePair<ShaderBindingKey, uint>[] ordered =
                capacities.ToArray();
            Array.Sort(ordered, static (left, right) =>
            {
                int table = left.Key.Table.CompareTo(right.Key.Table);
                if (table != 0)
                {
                    return table;
                }

                int slot = left.Key.Slot.CompareTo(right.Key.Slot);
                return slot != 0
                    ? slot
                    : left.Key.Type.CompareTo(right.Key.Type);
            });

            AppendUInt64(hash, checked((ulong)ordered.Length));
            foreach ((ShaderBindingKey key, uint capacity) in ordered)
            {
                AppendUInt64(hash, key.Table);
                AppendUInt64(hash, key.Slot);
                AppendInt32(hash, (int)key.Type);
                AppendUInt64(hash, capacity);
            }
        }

        private static void AppendToolchain(
            IncrementalHash hash,
            IReadOnlyList<ShaderToolchainComponent> components)
        {
            AppendUInt64(hash, checked((ulong)components.Count));
            foreach (ShaderToolchainComponent component in components)
            {
                AppendString(hash, component.Name);
                AppendString(hash, component.Version);
                AppendNullableString(hash, component.ContentDigest);
            }
        }

        private static void AppendNullableString(
            IncrementalHash hash,
            string? value)
        {
            AppendBoolean(hash, value is not null);
            if (value is not null)
            {
                AppendString(hash, value);
            }
        }

        private static void AppendString(IncrementalHash hash, string value)
        {
            byte[] bytes = s_StrictUtf8.GetBytes(value);
            AppendUInt64(hash, checked((ulong)bytes.Length));
            hash.AppendData(bytes);
        }

        private static void AppendBoolean(IncrementalHash hash, bool value)
        {
            AppendByte(hash, value ? (byte)1 : (byte)0);
        }

        private static void AppendByte(IncrementalHash hash, byte value)
        {
            Span<byte> bytes = stackalloc byte[1];
            bytes[0] = value;
            hash.AppendData(bytes);
        }

        private static void AppendInt32(IncrementalHash hash, int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            hash.AppendData(bytes);
        }

        private static void AppendUInt64(IncrementalHash hash, ulong value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            hash.AppendData(bytes);
        }

        private static ShaderCompilerException InvalidRequest(
            string message,
            Exception? innerException = null)
        {
            return new ShaderCompilerException(
                ShaderCompilerErrorCode.InvalidRequest,
                message,
                requestedProfile: "shader-program",
                innerException: innerException);
        }
    }
}
