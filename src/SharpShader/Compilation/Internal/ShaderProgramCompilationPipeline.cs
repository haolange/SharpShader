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
    internal static partial class ShaderProgramCompilationPipeline
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



        private static ShaderCompileRequest CreateCompileRequest(
            ShaderProgramInputSnapshot snapshot,
            IReadOnlyList<ShaderDefine> defines,
            IReadOnlyList<ShaderProgramEntry> entries,
            ShaderTargetKind target,
            SpirvCompileOptions spirvOptions,
            CancellationToken cancellationToken,
            bool requiresOrderedFragmentInterlock = false,
            bool requiresStencilExport = false)
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

            if (requiresOrderedFragmentInterlock
                && target != ShaderTargetKind.SpirV)
            {
                throw new InvalidOperationException(
                    "Ordered fragment interlock can be requested only for a SPIR-V compile unit.");
            }

            if (requiresOrderedFragmentInterlock)
            {
                ValidateOrderedFragmentInterlockTargetEnvironment(spirvOptions);
            }

            if (requiresStencilExport && target != ShaderTargetKind.SpirV)
            {
                throw new InvalidOperationException(
                    "Stencil export can be requested only for a SPIR-V compile unit.");
            }

            List<string> extraArguments = new();
            if (target == ShaderTargetKind.SpirV)
            {
                extraArguments.Add("-fvk-auto-shift-bindings");
                if (requiresOrderedFragmentInterlock)
                {
                    extraArguments.Add(
                        "-fspv-extension=SPV_EXT_fragment_shader_interlock");
                }

                if (requiresStencilExport)
                {
                    extraArguments.Add(
                        "-fspv-extension=SPV_EXT_shader_stencil_export");
                }
            }
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
            public ShaderAttachmentInterface AttachmentInterface { get; }
            public ShaderEntryPointReflection DxilEntry { get; }
            public CompileUnitState Unit { get; }
            public ShaderEntryPointReflection? LogicalEntry { get; set; }
            public ShaderEntryPointReflection? SpirvEntry { get; set; }
            public List<ShaderProgramArtifact> Artifacts { get; } = new();

            public EntryState(
                ShaderProgramEntry definition,
                ShaderAttachmentInterface attachmentInterface,
                ShaderEntryPointReflection dxilEntry,
                CompileUnitState unit)
            {
                Definition = definition;
                AttachmentInterface = attachmentInterface;
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
