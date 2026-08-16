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

                ShaderAttachmentInterface attachmentInterface =
                    snapshot.Request.GetAttachmentInterface(
                        variantKey,
                        entry.Name,
                        entry.Stage);
                ShaderAttachmentInterfaceValidator.ValidateDxil(
                    attachmentInterface,
                    reflectedEntry);
                ShaderEntryPointReflection publicReflection =
                    ShaderAttachmentInterfaceValidator.CreatePublicDxilReflection(
                        attachmentInterface,
                        reflectedEntry);
                EntryState entryState = new(
                    entry,
                    attachmentInterface,
                    publicReflection,
                    unit);
                unit.Entries.Add(entryState);
            }

            return unit;
        }
}
}
