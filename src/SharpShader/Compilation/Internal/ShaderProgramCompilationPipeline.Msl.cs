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
                uint privateDescriptorSet =
                    SpirvBindingRemapper.GetPrivateAttachmentDescriptorSet(
                        variant.BackendLayouts!.Vulkan!);

                foreach (CompileUnitState unit in variant.Units)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ShaderCompileRequest request = CreateCompileRequest(
                        snapshot,
                        variant.Defines,
                        unit.Entries.Select(static entry => entry.Definition).ToArray(),
                        ShaderTargetKind.SpirV,
                        options,
                        cancellationToken,
                        requiresStencilExport:
                            RequiresStencilExport(unit.Entries));
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
                        subsetLayout,
                        unit.Entries
                            .Select(static entry => entry.AttachmentInterface)
                            .ToArray(),
                        privateDescriptorSet);
                    ShaderArtifactReflection spirvReflection =
                        SpirvArtifactReflector.Reflect(
                            request,
                            new ShaderCompileResult
                            {
                                Bytecode = unit.RemappedSpirv,
                            });
                    Dictionary<(string Name, ShaderExecutionStage Stage),
                        ShaderEntryPointReflection> spirvEntries = new();
                    foreach (ShaderEntryPointReflection spirvEntry in
                             spirvReflection.EntryPoints)
                    {
                        if (!spirvEntries.TryAdd(
                                (spirvEntry.Name, spirvEntry.Stage),
                                spirvEntry))
                        {
                            throw Failure(
                                $"SPIR-V reflection contains duplicate entry "
                                + $"{spirvEntry.Name} ({spirvEntry.Stage}).");
                        }
                    }

                    foreach (EntryState entry in unit.Entries)
                    {
                        entry.SpirvEntry = spirvEntries[
                            (entry.Definition.Name, entry.Definition.Stage)];
                        ShaderAttachmentInterfaceValidator.ValidateSpirv(
                            entry.AttachmentInterface,
                            entry.SpirvEntry,
                            privateDescriptorSet,
                            subsetLayout);
                    }

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
                                unit.RemappedSpirv,
                                privateDescriptorSet);
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
            byte[] spirv,
            uint privateDescriptorSet)
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
            ShaderAttachmentInterfaceValidator.ValidateMetalStrategy(
                entry.AttachmentInterface,
                entry.SpirvEntry!,
                vulkanSubset);
            MslTranslationBindingPlan bindingPlan =
                MslTranslationBindingPlan.Create(
                    entry.Definition.Name,
                    entry.Definition.Stage,
                    vulkanSubset.Bindings,
                    metalSubset,
                    entry.AttachmentInterface,
                    entry.SpirvEntry!,
                    privateDescriptorSet);
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
                bindingPlan);
            EnsureArtifactNotEmpty(translated, request, "MSL source");

            string text = translated.Text
                ?? s_StrictUtf8.GetString(translated.Bytecode);
            if (entry.Definition.Stage == ShaderExecutionStage.Pixel)
            {
                MslArtifactReflection mslReflection = MslArtifactReflector.Reflect(
                    text,
                    entry.Definition.Name,
                    entry.Definition.Stage);
                ShaderAttachmentInterfaceValidator.ValidateMsl(
                    entry.AttachmentInterface,
                    mslReflection,
                    bindingPlan);
            }
            entry.Artifacts.Add(CreateArtifact(
                variant.Definition.Key,
                entry.Definition,
                ShaderArtifactKind.MslSource,
                translated.Bytecode,
                text));
        }

}
}
