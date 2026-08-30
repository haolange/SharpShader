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
                        entry.AttachmentInterface,
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
                backendLayouts.Values,
                snapshot.Request.Targets);
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

        private static bool RequiresStencilExport(
            IReadOnlyList<EntryState> entries)
        {
            foreach (EntryState entry in entries)
            {
                if (entry.AttachmentInterface.Phase?.StencilExport
                    == ShaderStencilExport.StencilReference)
                {
                    return true;
                }
            }

            return false;
        }
}
}
