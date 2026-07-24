using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SharpShader.HLSLCrossCompiler;
using SharpShader.HLSLCrossCompiler.Internal;

namespace SharpShader.Compilation.Internal
{
    internal static class ShaderProgramCompilationPipeline
    {
        private static readonly UTF8Encoding s_StrictUtf8 = new(false, true);

        public static ShaderProgramCompilationOutput Compile(
            ShaderProgramInputSnapshot snapshot,
            ShaderProgramCompilerExecutionContext? context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            cancellationToken.ThrowIfCancellationRequested();

            List<VariantState> variants = new(snapshot.Request.Variants.Count);
            foreach (ShaderProgramVariant variant in snapshot.Request.Variants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                variants.Add(CompileDxilVariant(snapshot, variant, context, cancellationToken));
            }

            InternLayouts(variants);
            ValidateMetalCapacityCoverage(snapshot.Request, variants);

            List<ShaderProgramArtifact> artifacts = new();
            foreach (VariantState variant in variants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PlanVariant(snapshot.Request, variant);
                CompileRequestedArtifacts(
                    snapshot,
                    variant,
                    artifacts,
                    context,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ShaderProgramFrozenInput frozenInput = snapshot.CompleteFreeze();
            ShaderInterfaceManifest manifest = BuildManifest(
                snapshot,
                variants,
                artifacts,
                frozenInput.SourceDigest);
            return new ShaderProgramCompilationOutput(
                new ShaderProgramCompilation(
                    frozenInput.FinalKey,
                    manifest,
                    artifacts),
                frozenInput.Dependencies);
        }

        private static VariantState CompileDxilVariant(
            ShaderProgramInputSnapshot snapshot,
            ShaderProgramVariant variant,
            ShaderProgramCompilerExecutionContext? context,
            CancellationToken cancellationToken)
        {
            ShaderDefine[] defines = CombineDefines(
                snapshot.Request.GlobalDefines,
                variant.Defines);
            VariantState state = new(variant, defines);

            List<ShaderProgramEntry> libraryEntries = new();
            foreach (ShaderProgramEntry entry in snapshot.Request.Entries)
            {
                if (IsLibraryStage(entry.Stage))
                {
                    libraryEntries.Add(entry);
                    continue;
                }

                CompileUnitState unit = CompileDxilUnit(
                    snapshot,
                    variant.Key,
                    defines,
                    new[] { entry },
                    context,
                    cancellationToken);
                state.Units.Add(unit);
                state.Entries.AddRange(unit.Entries);
            }

            if (libraryEntries.Count != 0)
            {
                CompileUnitState unit = CompileDxilUnit(
                    snapshot,
                    variant.Key,
                    defines,
                    libraryEntries,
                    context,
                    cancellationToken);
                state.Units.Add(unit);
                state.Entries.AddRange(unit.Entries);
            }

            state.Entries.Sort(static (left, right) =>
                ShaderProgramModelValidation.CompareEntries(
                    left.Definition,
                    right.Definition));
            state.InitialLayout = BuildUnionLayout(state.Entries);
            state.Layout = state.InitialLayout;
            return state;
        }

        private static CompileUnitState CompileDxilUnit(
            ShaderProgramInputSnapshot snapshot,
            string variantKey,
            IReadOnlyList<ShaderDefine> defines,
            IReadOnlyList<ShaderProgramEntry> entries,
            ShaderProgramCompilerExecutionContext? context,
            CancellationToken cancellationToken)
        {
            ShaderCompileRequest request = CreateCompileRequest(
                snapshot,
                defines,
                entries,
                ShaderTargetKind.Dxil,
                SpirvCompileOptions.Default,
                cancellationToken);
            request = FreezeCompileRequest(
                snapshot,
                request,
                variantKey,
                entries,
                context);
            ShaderCompileResult result = CompileNative(request, context);
            EnsureArtifactNotEmpty(result, request, "DXIL");
            ShaderArtifactReflection reflection =
                DxilArtifactReflector.Reflect(request, result);

            Dictionary<(string Name, ShaderExecutionStage Stage), ShaderEntryPointReflection>
                reflectedEntries = new();
            foreach (ShaderEntryPointReflection reflectedEntry in reflection.EntryPoints)
            {
                if (!reflectedEntries.TryAdd(
                        (reflectedEntry.Name, reflectedEntry.Stage),
                        reflectedEntry))
                {
                    throw Failure(
                        $"DXIL reflection contains duplicate entry {reflectedEntry.Name} "
                        + $"({reflectedEntry.Stage}) for variant {variantKey}.");
                }
            }

            CompileUnitState unit = new(request, result);
            foreach (ShaderProgramEntry entry in entries)
            {
                if (!reflectedEntries.TryGetValue(
                        (entry.Name, entry.Stage),
                        out ShaderEntryPointReflection? reflectedEntry))
                {
                    throw Failure(
                        $"DXIL reflection for variant {variantKey} does not contain requested "
                        + $"entry {entry.Name} ({entry.Stage}).");
                }

                EntryState entryState = new(entry, reflectedEntry, unit);
                unit.Entries.Add(entryState);
            }

            return unit;
        }

        private static ShaderInterfaceLayout BuildUnionLayout(
            IReadOnlyList<EntryState> entries)
        {
            Dictionary<ShaderBindingKey, BindingAccumulator> bindings = new();
            foreach (EntryState entry in entries)
            {
                foreach (ShaderResourceBindingReflection resource in entry.DxilEntry.Resources)
                {
                    if (!bindings.TryGetValue(
                            resource.Key,
                            out BindingAccumulator? accumulator))
                    {
                        accumulator = new BindingAccumulator(resource.LogicalBinding);
                        bindings.Add(resource.Key, accumulator);
                    }
                    else
                    {
                        accumulator.Merge(
                            resource.LogicalBinding,
                            $"entry {entry.Definition.Name} ({entry.Definition.Stage})");
                    }
                }
            }

            return BuildLayout(bindings.Values);
        }

        private static ShaderInterfaceLayout BuildLayout(
            IEnumerable<BindingAccumulator> accumulators)
        {
            BindingAccumulator[] ordered = accumulators.ToArray();
            Array.Sort(ordered, static (left, right) =>
                CompareKeys(left.Key, right.Key));

            Dictionary<string, ShaderBindingKey> names = new(StringComparer.Ordinal);
            ShaderLogicalBinding[] bindings = new ShaderLogicalBinding[ordered.Length];
            for (int index = 0; index < ordered.Length; ++index)
            {
                bindings[index] = ordered[index].Build();
                AddBindingName(names, bindings[index].CanonicalName, bindings[index].Key);
                foreach (string alias in bindings[index].Aliases)
                {
                    AddBindingName(names, alias, bindings[index].Key);
                }
            }

            return new ShaderInterfaceLayout(bindings);
        }

        private static void AddBindingName(
            Dictionary<string, ShaderBindingKey> names,
            string name,
            ShaderBindingKey key)
        {
            if (names.TryGetValue(name, out ShaderBindingKey existing)
                && existing != key)
            {
                throw Failure(
                    $"Reflected resource name {name} identifies both {existing} and {key}. "
                    + "Logical binding names and aliases must be globally unambiguous.");
            }

            names[name] = key;
        }

        private static void InternLayouts(List<VariantState> variants)
        {
            Dictionary<ShaderLayoutSignature, List<VariantState>> groups = new();
            foreach (VariantState variant in variants)
            {
                if (!groups.TryGetValue(
                        variant.InitialLayout.Signature,
                        out List<VariantState>? group))
                {
                    group = new List<VariantState>();
                    groups.Add(variant.InitialLayout.Signature, group);
                }

                group.Add(variant);
            }

            foreach ((ShaderLayoutSignature signature, List<VariantState> group) in groups)
            {
                ShaderInterfaceLayout first = group[0].InitialLayout;
                Dictionary<ShaderBindingKey, BindingAccumulator> merged = new();
                foreach (VariantState variant in group)
                {
                    if (!first.AbiEquals(variant.InitialLayout))
                    {
                        throw new InvalidOperationException(
                            $"Shader layout signature collision detected for {signature}; "
                            + "structural equality rejected interning.");
                    }

                    foreach (ShaderLogicalBinding binding in variant.InitialLayout.Bindings)
                    {
                        if (!merged.TryGetValue(
                                binding.Key,
                                out BindingAccumulator? accumulator))
                        {
                            accumulator = new BindingAccumulator(binding);
                            merged.Add(binding.Key, accumulator);
                        }
                        else
                        {
                            accumulator.Merge(
                                binding,
                                $"variant {variant.Definition.Key}");
                        }
                    }
                }

                ShaderInterfaceLayout interned = BuildLayout(merged.Values);
                if (interned.Signature != signature || !first.AbiEquals(interned))
                {
                    throw new InvalidOperationException(
                        $"Interning reflection metadata changed logical shader ABI {signature}.");
                }

                foreach (VariantState variant in group)
                {
                    variant.Layout = interned;
                }
            }
        }

        private static void ValidateMetalCapacityCoverage(
            ShaderProgramCompileRequest request,
            IReadOnlyList<VariantState> variants)
        {
            if (request.MetalArrayCapacities.Count == 0)
            {
                return;
            }

            if ((request.Targets & ShaderProgramTarget.MetalMsl) == 0)
            {
                throw InvalidRequest(
                    "Metal array capacities were provided, but MetalMsl is not a requested target.");
            }

            HashSet<ShaderBindingKey> knownBindings = new();
            foreach (VariantState variant in variants)
            {
                foreach (ShaderLogicalBinding binding in variant.Layout.Bindings)
                {
                    knownBindings.Add(binding.Key);
                }
            }

            foreach (ShaderBindingKey key in request.MetalArrayCapacities.Keys)
            {
                if (!knownBindings.Contains(key))
                {
                    throw InvalidRequest(
                        $"Metal array capacity references unknown logical binding {key}.");
                }
            }
        }

        private static void PlanVariant(
            ShaderProgramCompileRequest request,
            VariantState variant)
        {
            Dictionary<ShaderBindingKey, uint> capacities = new();
            HashSet<ShaderBindingKey> layoutKeys = new(
                variant.Layout.Bindings.Select(static binding => binding.Key));
            foreach ((ShaderBindingKey key, uint capacity) in request.MetalArrayCapacities)
            {
                if (layoutKeys.Contains(key))
                {
                    capacities.Add(key, capacity);
                }
            }

            variant.BackendLayouts = ShaderBackendLayoutPlanner.Plan(
                variant.Layout,
                capacities.Count == 0 ? null : capacities);
            RebuildLogicalReflections(variant);
        }

        private static void RebuildLogicalReflections(VariantState variant)
        {
            Dictionary<ShaderBindingKey, ShaderLogicalBinding> bindingsByKey =
                variant.Layout.Bindings.ToDictionary(
                    static binding => binding.Key,
                    static binding => binding);

            foreach (CompileUnitState unit in variant.Units)
            {
                List<ShaderEntryPointReflection> rebuiltEntries = new(unit.Entries.Count);
                foreach (EntryState entry in unit.Entries)
                {
                    List<ShaderResourceBindingReflection> resources =
                        new(entry.DxilEntry.Resources.Count);
                    foreach (ShaderResourceBindingReflection original in entry.DxilEntry.Resources)
                    {
                        ShaderLogicalBinding binding = bindingsByKey[original.Key];
                        resources.Add(new ShaderResourceBindingReflection(
                            binding,
                            CreateDx12PhysicalLocation(binding.Key)));
                    }

                    ShaderEntryPointReflection rebuilt = new(
                        entry.Definition.Name,
                        entry.Definition.Stage,
                        resources,
                        entry.DxilEntry.ThreadGroupSize);
                    entry.LogicalEntry = rebuilt;
                    rebuiltEntries.Add(rebuilt);
                }

                unit.LogicalReflection = new ShaderArtifactReflection(
                    ShaderArtifactKind.Dxil,
                    rebuiltEntries);
            }
        }

        private static void CompileRequestedArtifacts(
            ShaderProgramInputSnapshot snapshot,
            VariantState variant,
            List<ShaderProgramArtifact> destination,
            ShaderProgramCompilerExecutionContext? context,
            CancellationToken cancellationToken)
        {
            ShaderProgramTarget targets = snapshot.Request.Targets;
            if ((targets & ShaderProgramTarget.DirectX12) != 0)
            {
                foreach (CompileUnitState unit in variant.Units)
                {
                    foreach (EntryState entry in unit.Entries)
                    {
                        entry.Artifacts.Add(CreateArtifact(
                            variant.Definition.Key,
                            entry.Definition,
                            ShaderArtifactKind.Dxil,
                            unit.DxilResult.Bytecode,
                            text: null));
                    }
                }
            }

            bool needsSpirv =
                (targets & (ShaderProgramTarget.Vulkan | ShaderProgramTarget.MetalMsl)) != 0;
            if (needsSpirv)
            {
                IReadOnlyList<SpirvBindingShift> shifts =
                    BuildCollisionFreeBindingShifts(variant.Layout);
                SpirvCompileOptions options = CopySpirvOptions(
                    snapshot.Request.SpirvOptions,
                    shifts);

                foreach (CompileUnitState unit in variant.Units)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ShaderCompileRequest request = CreateCompileRequest(
                        snapshot,
                        variant.Defines,
                        unit.Entries.Select(static entry => entry.Definition).ToArray(),
                        ShaderTargetKind.SpirV,
                        options,
                        cancellationToken);
                    request = FreezeCompileRequest(
                        snapshot,
                        request,
                        variant.Definition.Key,
                        unit.Entries.Select(static entry => entry.Definition).ToArray(),
                        context);
                    ShaderCompileResult intermediate = CompileNative(request, context);
                    EnsureArtifactNotEmpty(intermediate, request, "intermediate SPIR-V");

                    VulkanShaderBackendLayout subsetLayout = CreateVulkanSubset(
                        variant.BackendLayouts!.Vulkan!,
                        unit.LogicalReflection!);
                    unit.RemappedSpirv = SpirvBindingRemapper.Remap(
                        intermediate.Bytecode,
                        unit.LogicalReflection!,
                        subsetLayout);

                    if ((targets & ShaderProgramTarget.Vulkan) != 0)
                    {
                        foreach (EntryState entry in unit.Entries)
                        {
                            entry.Artifacts.Add(CreateArtifact(
                                variant.Definition.Key,
                                entry.Definition,
                                ShaderArtifactKind.SpirV,
                                unit.RemappedSpirv,
                                text: null));
                        }
                    }

                    if ((targets & ShaderProgramTarget.MetalMsl) != 0)
                    {
                        foreach (EntryState entry in unit.Entries)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            TranslateMsl(
                                snapshot,
                                variant,
                                entry,
                                unit.RemappedSpirv);
                        }
                    }
                }
            }

            foreach (EntryState entry in variant.Entries)
            {
                destination.AddRange(entry.Artifacts);
            }
        }

        private static void TranslateMsl(
            ShaderProgramInputSnapshot snapshot,
            VariantState variant,
            EntryState entry,
            byte[] spirv)
        {
            ShaderArtifactReflection entryReflection = new(
                ShaderArtifactKind.Dxil,
                new[] { entry.LogicalEntry! });
            VulkanShaderBackendLayout vulkanSubset = CreateVulkanSubset(
                variant.BackendLayouts!.Vulkan!,
                entryReflection);
            MetalShaderBackendLayout metalSubset = CreateMetalSubset(
                variant.BackendLayouts.Metal!,
                entryReflection);
            ShaderCompileRequest request = CreateCompileRequest(
                snapshot,
                variant.Defines,
                new[] { entry.Definition },
                ShaderTargetKind.Msl,
                snapshot.Request.SpirvOptions,
                CancellationToken.None);
            ShaderCompileResult translated = SpirvToMslTranslator.Translate(
                request,
                new ShaderCompileResult { Bytecode = spirv },
                entry.Definition.Name,
                entry.Definition.Stage,
                vulkanSubset.Bindings,
                metalSubset);
            EnsureArtifactNotEmpty(translated, request, "MSL source");

            string text = translated.Text
                ?? s_StrictUtf8.GetString(translated.Bytecode);
            entry.Artifacts.Add(CreateArtifact(
                variant.Definition.Key,
                entry.Definition,
                ShaderArtifactKind.MslSource,
                translated.Bytecode,
                text));
        }

        private static ShaderInterfaceManifest BuildManifest(
            ShaderProgramInputSnapshot snapshot,
            IReadOnlyList<VariantState> variants,
            IReadOnlyList<ShaderProgramArtifact> artifacts,
            string sourceDigest)
        {
            Dictionary<ShaderLayoutSignature, ShaderInterfaceLayout> layouts = new();
            Dictionary<ShaderLayoutSignature, ShaderBackendLayouts> backendLayouts = new();
            foreach (VariantState variant in variants)
            {
                layouts.TryAdd(variant.Layout.Signature, variant.Layout);
                backendLayouts.TryAdd(
                    variant.BackendLayouts!.LogicalLayoutSignature,
                    variant.BackendLayouts);
            }

            Dictionary<(string Variant, string Entry, ShaderExecutionStage Stage),
                List<ShaderArtifactIdentity>> identities = new();
            foreach (ShaderProgramArtifact artifact in artifacts)
            {
                (string, string, ShaderExecutionStage) key =
                    (artifact.VariantKey, artifact.EntryPoint, artifact.Stage);
                if (!identities.TryGetValue(
                        key,
                        out List<ShaderArtifactIdentity>? entryArtifacts))
                {
                    entryArtifacts = new List<ShaderArtifactIdentity>();
                    identities.Add(key, entryArtifacts);
                }

                entryArtifacts.Add(artifact.Identity);
            }

            List<ShaderInterfaceVariant> manifestVariants = new(variants.Count);
            foreach (VariantState variant in variants)
            {
                List<ShaderInterfaceEntry> entries = new(variant.Entries.Count);
                foreach (EntryState entry in variant.Entries)
                {
                    List<ShaderArtifactIdentity> entryArtifacts =
                        identities[(
                            variant.Definition.Key,
                            entry.Definition.Name,
                            entry.Definition.Stage)];
                    entries.Add(new ShaderInterfaceEntry(
                        entry.Definition.Name,
                        entry.Definition.Stage,
                        variant.Layout.Signature,
                        entryArtifacts));
                }

                manifestVariants.Add(new ShaderInterfaceVariant(
                    variant.Definition.Key,
                    variant.Defines.Select(FormatDefine),
                    entries));
            }

            return new ShaderInterfaceManifest(
                sourceDigest,
                snapshot.ToolchainComponents,
                layouts.Values,
                manifestVariants,
                backendLayouts.Values);
        }

        private static ShaderProgramArtifact CreateArtifact(
            string variantKey,
            ShaderProgramEntry entry,
            ShaderArtifactKind kind,
            byte[] content,
            string? text)
        {
            if (content.Length == 0)
            {
                throw Failure(
                    $"Cannot create empty {kind} artifact for {variantKey}/{entry.Name}.");
            }

            string digest = Convert.ToHexStringLower(SHA256.HashData(content));
            string extension = kind switch
            {
                ShaderArtifactKind.Dxil => "dxil",
                ShaderArtifactKind.SpirV => "spv",
                ShaderArtifactKind.MslSource => "metal",
                ShaderArtifactKind.MetalLibrary => "metallib",
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
            };
            ShaderArtifactIdentity identity = new(
                kind,
                digest,
                checked((ulong)content.Length),
                $"{variantKey}:{entry.Stage}:{entry.Name}.{extension}");
            return new ShaderProgramArtifact(
                variantKey,
                entry.Name,
                entry.Stage,
                identity,
                content,
                text);
        }

        private static ShaderCompileRequest CreateCompileRequest(
            ShaderProgramInputSnapshot snapshot,
            IReadOnlyList<ShaderDefine> defines,
            IReadOnlyList<ShaderProgramEntry> entries,
            ShaderTargetKind target,
            SpirvCompileOptions spirvOptions,
            CancellationToken cancellationToken)
        {
            bool library = entries.Count != 0 && IsLibraryStage(entries[0].Stage);
            foreach (ShaderProgramEntry entry in entries)
            {
                if (IsLibraryStage(entry.Stage) != library)
                {
                    throw new InvalidOperationException(
                        "A native shader compile unit cannot mix library and non-library entries.");
                }
            }

            string[] extraArguments = target == ShaderTargetKind.SpirV
                ? new[] { "-fvk-auto-shift-bindings" }
                : Array.Empty<string>();
            return new ShaderCompileRequest
            {
                Source = snapshot.Request.Source,
                SourceName = snapshot.Request.SourceName,
                EntryPoint = library ? string.Empty : entries[0].Name,
                Stage = library
                    ? ShaderStageKind.Library
                    : MapStage(entries[0].Stage),
                ShaderModel = snapshot.Request.ShaderModel,
                Target = target,
                Defines = defines,
                IncludeDirs = snapshot.IncludeDirectories,
                Exports = library
                    ? entries.Select(static entry => entry.Name).ToArray()
                    : Array.Empty<string>(),
                ExtraArguments = extraArguments,
                SpirvOptions = spirvOptions,
                MslOptions = snapshot.Request.MslOptions,
                CancellationToken = cancellationToken,
                Enable16BitTypes = snapshot.Request.Enable16BitTypes,
                EnableDebugInfo = snapshot.Request.EnableDebugInfo,
                DisableOptimizations = snapshot.Request.DisableOptimizations,
                OptimizationLevel = snapshot.Request.OptimizationLevel,
                SkipValidation = snapshot.Request.SkipValidation,
                TreatWarningsAsErrors = snapshot.Request.TreatWarningsAsErrors,
            };
        }

        private static ShaderCompileRequest FreezeCompileRequest(
            ShaderProgramInputSnapshot snapshot,
            ShaderCompileRequest request,
            string variantKey,
            IReadOnlyList<ShaderProgramEntry> entries,
            ShaderProgramCompilerExecutionContext? context)
        {
            StringBuilder entryIdentity = new();
            for (int index = 0; index < entries.Count; ++index)
            {
                if (index != 0)
                {
                    entryIdentity.Append(';');
                }

                entryIdentity
                    .Append((int)entries[index].Stage)
                    .Append(':')
                    .Append(entries[index].Name);
            }

            return snapshot.FreezeCompileUnit(
                request,
                new ShaderProgramCompileUnitIdentity(
                    variantKey,
                    request.Target,
                    request.Stage,
                    entryIdentity.ToString()),
                context);
        }

        private static ShaderCompileResult CompileNative(
            ShaderCompileRequest request,
            ShaderProgramCompilerExecutionContext? context)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            return context?.CompileOverride?.Invoke(request)
                ?? SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(request);
        }

        private static void EnsureArtifactNotEmpty(
            ShaderCompileResult result,
            ShaderCompileRequest request,
            string description)
        {
            if (result is null || result.Bytecode.Length == 0)
            {
                throw Failure(
                    $"Native compiler returned an empty {description} artifact for "
                    + $"{request.SourceName}/{request.EntryPoint}.");
            }
        }

        private static ShaderDefine[] CombineDefines(
            IReadOnlyList<ShaderDefine> globalDefines,
            IReadOnlyList<ShaderDefine> variantDefines)
        {
            ShaderDefine[] combined =
                new ShaderDefine[globalDefines.Count + variantDefines.Count];
            for (int index = 0; index < globalDefines.Count; ++index)
            {
                combined[index] = globalDefines[index];
            }

            for (int index = 0; index < variantDefines.Count; ++index)
            {
                combined[globalDefines.Count + index] = variantDefines[index];
            }

            Array.Sort(combined, ShaderProgramModelValidation.CompareDefines);
            return combined;
        }

        private static IReadOnlyList<SpirvBindingShift> BuildCollisionFreeBindingShifts(
            ShaderInterfaceLayout layout)
        {
            List<SpirvBindingShift> shifts = new();
            foreach (IGrouping<uint, ShaderLogicalBinding> table in
                     layout.Bindings.GroupBy(static binding => binding.Key.Table))
            {
                long nextBinding = 0;
                foreach (ShaderBindingClass bindingClass in
                         Enum.GetValues<ShaderBindingClass>())
                {
                    ShaderLogicalBinding[] classBindings = table
                        .Where(binding => binding.Key.Type == bindingClass)
                        .ToArray();
                    if (classBindings.Length == 0)
                    {
                        continue;
                    }

                    uint maximumSlot = classBindings.Max(
                        static binding => binding.Key.Slot);
                    if (nextBinding > int.MaxValue
                        || checked(nextBinding + maximumSlot) > int.MaxValue)
                    {
                        throw InvalidRequest(
                            $"Logical table {table.Key} requires an intermediate SPIR-V "
                            + "binding shift outside DXC's Int32 range.");
                    }

                    shifts.Add(new SpirvBindingShift(
                        MapShiftKind(bindingClass),
                        table.Key,
                        checked((int)nextBinding)));
                    nextBinding = checked(nextBinding + maximumSlot + 1L);
                }
            }

            return new ReadOnlyCollection<SpirvBindingShift>(shifts);
        }

        private static SpirvCompileOptions CopySpirvOptions(
            SpirvCompileOptions source,
            IReadOnlyList<SpirvBindingShift> shifts)
        {
            return new SpirvCompileOptions
            {
                UseDxLayout = source.UseDxLayout,
                UseGlLayout = source.UseGlLayout,
                UseScalarLayout = source.UseScalarLayout,
                InvertY = source.InvertY,
                BindingShifts = shifts,
                TargetEnvironment = source.TargetEnvironment,
                AdditionalArguments = source.AdditionalArguments.ToArray(),
            };
        }

        private static VulkanShaderBackendLayout CreateVulkanSubset(
            VulkanShaderBackendLayout fullLayout,
            ShaderArtifactReflection reflection)
        {
            HashSet<ShaderBindingKey> keys = CollectKeys(reflection);
            return new VulkanShaderBackendLayout(
                fullLayout.Bindings.Where(mapping =>
                    keys.Contains(mapping.LogicalBinding)));
        }

        private static MetalShaderBackendLayout CreateMetalSubset(
            MetalShaderBackendLayout fullLayout,
            ShaderArtifactReflection reflection)
        {
            HashSet<ShaderBindingKey> keys = CollectKeys(reflection);
            if (fullLayout.DirectBindings.Count != 0)
            {
                return new MetalShaderBackendLayout(
                    directBindings: fullLayout.DirectBindings.Where(mapping =>
                        keys.Contains(mapping.LogicalBinding)));
            }

            return new MetalShaderBackendLayout(
                referenceBufferBindings:
                    fullLayout.ReferenceBufferBindings.Where(mapping =>
                        keys.Contains(mapping.LogicalBinding)));
        }

        private static HashSet<ShaderBindingKey> CollectKeys(
            ShaderArtifactReflection reflection)
        {
            HashSet<ShaderBindingKey> keys = new();
            foreach (ShaderEntryPointReflection entry in reflection.EntryPoints)
            {
                foreach (ShaderResourceBindingReflection resource in entry.Resources)
                {
                    keys.Add(resource.Key);
                }
            }

            return keys;
        }

        private static ShaderPhysicalBindingLocation CreateDx12PhysicalLocation(
            ShaderBindingKey key)
        {
            ShaderPhysicalBindingNamespace bindingNamespace = key.Type switch
            {
                ShaderBindingClass.ShaderResource =>
                    ShaderPhysicalBindingNamespace.ShaderResource,
                ShaderBindingClass.Sampler =>
                    ShaderPhysicalBindingNamespace.Sampler,
                ShaderBindingClass.ConstantBuffer =>
                    ShaderPhysicalBindingNamespace.ConstantBuffer,
                ShaderBindingClass.UnorderedAccess =>
                    ShaderPhysicalBindingNamespace.UnorderedAccess,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(key),
                    key.Type,
                    "Shader binding class is not defined."),
            };
            return new ShaderPhysicalBindingLocation(
                ShaderBackendKind.DirectX12,
                key.Table,
                key.Slot,
                bindingNamespace);
        }

        private static SpirvBindingShiftKind MapShiftKind(
            ShaderBindingClass bindingClass)
        {
            return bindingClass switch
            {
                ShaderBindingClass.ShaderResource =>
                    SpirvBindingShiftKind.ShaderResource,
                ShaderBindingClass.Sampler =>
                    SpirvBindingShiftKind.Sampler,
                ShaderBindingClass.ConstantBuffer =>
                    SpirvBindingShiftKind.ConstantBuffer,
                ShaderBindingClass.UnorderedAccess =>
                    SpirvBindingShiftKind.UnorderedAccess,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(bindingClass),
                    bindingClass,
                    "Shader binding class is not defined."),
            };
        }

        private static ShaderStageKind MapStage(ShaderExecutionStage stage)
        {
            return stage switch
            {
                ShaderExecutionStage.Vertex => ShaderStageKind.Vertex,
                ShaderExecutionStage.Hull => ShaderStageKind.Hull,
                ShaderExecutionStage.Domain => ShaderStageKind.Domain,
                ShaderExecutionStage.Geometry => ShaderStageKind.Geometry,
                ShaderExecutionStage.Pixel => ShaderStageKind.Pixel,
                ShaderExecutionStage.Compute => ShaderStageKind.Compute,
                ShaderExecutionStage.Amplification => ShaderStageKind.Amplification,
                ShaderExecutionStage.Mesh => ShaderStageKind.Mesh,
                _ when IsLibraryStage(stage) => ShaderStageKind.Library,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(stage),
                    stage,
                    "Shader execution stage is not defined."),
            };
        }

        private static bool IsLibraryStage(ShaderExecutionStage stage)
        {
            return stage is ShaderExecutionStage.RayGeneration
                or ShaderExecutionStage.Intersection
                or ShaderExecutionStage.AnyHit
                or ShaderExecutionStage.ClosestHit
                or ShaderExecutionStage.Miss
                or ShaderExecutionStage.Callable
                or ShaderExecutionStage.Node;
        }

        private static int CompareKeys(ShaderBindingKey left, ShaderBindingKey right)
        {
            int table = left.Table.CompareTo(right.Table);
            if (table != 0)
            {
                return table;
            }

            int slot = left.Slot.CompareTo(right.Slot);
            return slot != 0 ? slot : left.Type.CompareTo(right.Type);
        }

        private static string FormatDefine(ShaderDefine define)
        {
            return string.IsNullOrEmpty(define.Value)
                ? define.Name
                : $"{define.Name}={define.Value}";
        }

        private static ShaderCompilerException InvalidRequest(string message)
        {
            return new ShaderCompilerException(
                ShaderCompilerErrorCode.InvalidRequest,
                message,
                requestedProfile: "shader-program");
        }

        private static ShaderCompilerException Failure(string message)
        {
            return new ShaderCompilerException(
                ShaderCompilerErrorCode.CompileFailed,
                message,
                requestedProfile: "shader-program");
        }

        private sealed class BindingAccumulator
        {
            private readonly HashSet<string> m_Names = new(StringComparer.Ordinal);
            private ShaderBindingProvenance? m_Provenance;

            public ShaderBindingKey Key { get; }
            public ShaderResourceShape Shape { get; }
            public ShaderConstantBufferLayout? ConstantBufferLayout { get; }
            public ShaderStageMask Stages { get; private set; }

            public BindingAccumulator(ShaderLogicalBinding binding)
            {
                Key = binding.Key;
                Shape = binding.Shape;
                ConstantBufferLayout = binding.ConstantBufferLayout;
                Stages = binding.StageMask;
                m_Provenance = binding.Provenance;
                AddNames(binding);
            }

            public void Merge(ShaderLogicalBinding binding, string context)
            {
                if (binding.Key != Key)
                {
                    throw new InvalidOperationException(
                        "Binding accumulator received a mismatched logical key.");
                }

                if (!Shape.Equals(binding.Shape)
                    || !Equals(ConstantBufferLayout, binding.ConstantBufferLayout))
                {
                    throw Failure(
                        $"Logical binding {Key} has conflicting resource shape or "
                        + $"constant-buffer layout in {context}.");
                }

                Stages |= binding.StageMask;
                if (m_Provenance != binding.Provenance)
                {
                    m_Provenance = ShaderBindingProvenance.Unknown;
                }

                AddNames(binding);
            }

            public ShaderLogicalBinding Build()
            {
                string[] names = m_Names.ToArray();
                Array.Sort(names, StringComparer.Ordinal);
                if (names.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Logical binding {Key} has no reflected names.");
                }

                return new ShaderLogicalBinding(
                    Key,
                    names[0],
                    names.Skip(1),
                    Shape,
                    Stages,
                    ConstantBufferLayout,
                    m_Provenance ?? ShaderBindingProvenance.Unknown);
            }

            private void AddNames(ShaderLogicalBinding binding)
            {
                m_Names.Add(binding.CanonicalName);
                foreach (string alias in binding.Aliases)
                {
                    m_Names.Add(alias);
                }
            }
        }

        private sealed class EntryState
        {
            public ShaderProgramEntry Definition { get; }
            public ShaderEntryPointReflection DxilEntry { get; }
            public CompileUnitState Unit { get; }
            public ShaderEntryPointReflection? LogicalEntry { get; set; }
            public List<ShaderProgramArtifact> Artifacts { get; } = new();

            public EntryState(
                ShaderProgramEntry definition,
                ShaderEntryPointReflection dxilEntry,
                CompileUnitState unit)
            {
                Definition = definition;
                DxilEntry = dxilEntry;
                Unit = unit;
            }
        }

        private sealed class CompileUnitState
        {
            public ShaderCompileRequest DxilRequest { get; }
            public ShaderCompileResult DxilResult { get; }
            public List<EntryState> Entries { get; } = new();
            public ShaderArtifactReflection? LogicalReflection { get; set; }
            public byte[] RemappedSpirv { get; set; } = Array.Empty<byte>();

            public CompileUnitState(
                ShaderCompileRequest dxilRequest,
                ShaderCompileResult dxilResult)
            {
                DxilRequest = dxilRequest;
                DxilResult = dxilResult;
            }
        }

        private sealed class VariantState
        {
            public ShaderProgramVariant Definition { get; }
            public ShaderDefine[] Defines { get; }
            public List<CompileUnitState> Units { get; } = new();
            public List<EntryState> Entries { get; } = new();
            public ShaderInterfaceLayout InitialLayout { get; set; } = null!;
            public ShaderInterfaceLayout Layout { get; set; } = null!;
            public ShaderBackendLayouts? BackendLayouts { get; set; }

            public VariantState(
                ShaderProgramVariant definition,
                ShaderDefine[] defines)
            {
                Definition = definition;
                Defines = defines;
            }
        }
    }
}
