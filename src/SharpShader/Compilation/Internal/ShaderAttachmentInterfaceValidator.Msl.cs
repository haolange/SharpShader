using System;
using System.Collections.Generic;
using SharpShader.HLSLCrossCompiler;
using SharpShader.HLSLCrossCompiler.Internal;

namespace SharpShader.Compilation.Internal
{
    internal static partial class ShaderAttachmentInterfaceValidator
    {
        internal static void ValidateMsl(
            ShaderAttachmentInterface attachmentInterface,
            MslArtifactReflection reflection,
            MslTranslationBindingPlan bindingPlan)
        {
            ArgumentNullException.ThrowIfNull(attachmentInterface);
            ArgumentNullException.ThrowIfNull(reflection);
            ArgumentNullException.ThrowIfNull(bindingPlan);
            if (!string.Equals(
                    attachmentInterface.EntryPoint,
                    reflection.EntryPoint,
                    StringComparison.Ordinal)
                || attachmentInterface.Stage != reflection.Stage
                || !string.Equals(
                    bindingPlan.EntryPoint,
                    reflection.EntryPoint,
                    StringComparison.Ordinal)
                || bindingPlan.Stage != reflection.Stage)
            {
                throw MslFailure(
                    $"MSL entry {reflection.EntryPoint} ({reflection.Stage}) does not "
                    + $"match attachment contract {attachmentInterface.EntryPoint} "
                    + $"({attachmentInterface.Stage}) and frozen translation plan "
                    + $"{bindingPlan.EntryPoint} ({bindingPlan.Stage}).");
            }

            if (attachmentInterface.Stage != ShaderExecutionStage.Pixel)
            {
                if (reflection.ColorInputs.Count != 0
                    || reflection.ColorOutputs.Count != 0
                    || reflection.RasterOrderGroups.Count != 0
                    || reflection.DepthExport != ShaderDepthExport.None
                    || reflection.StencilExport != ShaderStencilExport.None)
                {
                    throw MslFailure(
                        "A non-pixel MSL entry exposes raster attachment facts.");
                }

                return;
            }

            ShaderAttachmentPhase phase = attachmentInterface.Phase
                ?? throw MslFailure(
                    "A pixel attachment interface has no raster phase.");
            ValidateMslOutputs(reflection, bindingPlan);
            ValidateMslLocalInputs(phase, reflection, bindingPlan);
            if (reflection.RasterOrderGroups.Count != 0)
            {
                throw MslFailure(
                    "Compiler-generated attachment I/O must not lower to a private raster-order resource.");
            }

            if (reflection.DepthExport != phase.DepthExport
                || reflection.DepthExport != bindingPlan.DepthExport
                || reflection.StencilExport != phase.StencilExport
                || reflection.StencilExport != bindingPlan.StencilExport)
            {
                throw MslFailure(
                    $"MSL depth/stencil exports {reflection.DepthExport}/"
                    + $"{reflection.StencilExport} do not match contract "
                    + $"{phase.DepthExport}/{phase.StencilExport} and frozen plan "
                    + $"{bindingPlan.DepthExport}/{bindingPlan.StencilExport}.");
            }

            bool reportsFramebufferFetch =
                (reflection.AttachmentRequirements
                    & ShaderAttachmentArtifactRequirement.FramebufferLocalRead) != 0;
            bool reportsRasterOrdering =
                (reflection.AttachmentRequirements
                    & ShaderAttachmentArtifactRequirement.OrderedPixelFragmentInterlock)
                != 0;
            bool reportsStencilExport =
                (reflection.AttachmentRequirements
                    & ShaderAttachmentArtifactRequirement.StencilReferenceExport) != 0;
            if (reportsFramebufferFetch != bindingPlan.UsesFramebufferFetch
                || reportsRasterOrdering
                || reportsStencilExport
                    != (bindingPlan.StencilExport
                        == ShaderStencilExport.StencilReference))
            {
                throw MslFailure(
                    "MSL attachment requirements do not match the frozen "
                    + "framebuffer-fetch or stencil strategy.");
            }
        }

        private static void ValidateMslOutputs(
            MslArtifactReflection reflection,
            MslTranslationBindingPlan bindingPlan)
        {
            if (reflection.ColorOutputs.Count
                != bindingPlan.ShaderOutputs.Count)
            {
                throw MslFailure(
                    $"MSL color output count {reflection.ColorOutputs.Count} does "
                    + $"not match frozen plan count "
                    + $"{bindingPlan.ShaderOutputs.Count}.");
            }

            Dictionary<(uint Location, uint Index), MslColorAttachmentIoReflection>
                actual = new();
            foreach (MslColorAttachmentIoReflection output in
                     reflection.ColorOutputs)
            {
                if (output.Direction != ShaderStageIoDirection.Output
                    || !actual.TryAdd((output.Location, output.Index), output))
                {
                    throw MslFailure(
                        $"MSL contains an invalid or duplicate color output at "
                        + $"{output.Location}/{output.Index}.");
                }
            }

            foreach (MslTranslationShaderOutput expected in
                     bindingPlan.ShaderOutputs)
            {
                if (!actual.Remove(
                        (expected.Location, 0),
                        out MslColorAttachmentIoReflection? output)
                    || output.NumericClass != expected.NumericClass
                    || output.ComponentCount != expected.ComponentCount)
                {
                    throw MslFailure(
                        $"MSL output location/index {expected.Location}/0 does not "
                        + $"match frozen numeric/component facts "
                        + $"{expected.NumericClass}/{expected.ComponentCount}.");
                }
            }

            if (actual.Count != 0)
            {
                throw MslFailure(
                    "MSL contains color outputs not present in the frozen plan.");
            }
        }

        private static void ValidateMslLocalInputs(
            ShaderAttachmentPhase phase,
            MslArtifactReflection reflection,
            MslTranslationBindingPlan bindingPlan)
        {
            Dictionary<uint, MslTranslationResourceBinding> expected = new();
            foreach (MslTranslationResourceBinding resource in
                     bindingPlan.Resources)
            {
                if (!resource.IsPrivateAttachment
                    || resource.DescriptorKind
                        != VulkanDescriptorKind.InputAttachment)
                {
                    continue;
                }

                if (resource.MetalNamespace
                        != ShaderPhysicalBindingNamespace.Texture
                    || resource.ResourceCount != 1
                    || !expected.TryAdd(resource.MetalIndex, resource))
                {
                    throw MslFailure(
                        "The frozen MSL local-input plan is ambiguous or does "
                        + "not target one Metal texture namespace element.");
                }
            }

            if (reflection.ColorInputs.Count != expected.Count)
            {
                throw MslFailure(
                    $"MSL local-input count {reflection.ColorInputs.Count} does "
                    + $"not match frozen plan count {expected.Count}.");
            }

            foreach (MslColorAttachmentIoReflection input in
                     reflection.ColorInputs)
            {
                if (input.Direction != ShaderStageIoDirection.Input
                    || input.Index != 0
                    || !expected.Remove(
                        input.Location,
                        out MslTranslationResourceBinding resource))
                {
                    throw MslFailure(
                        $"MSL local input at logical color/index "
                        + $"{input.Location}/{input.Index} is not in the frozen "
                        + "attachment mapping.");
                }

                ShaderAttachmentDeclaration attachment =
                    FindLocalInputAttachment(
                        phase,
                        resource.Binding);
                uint expectedColorLocation =
                    attachment.OutputLocation ??
                    attachment.InputIndex!.Value;
                if (resource.MetalIndex != expectedColorLocation)
                {
                    throw MslFailure(
                        $"MSL local input slot {resource.Binding} maps to "
                        + $"color {resource.MetalIndex}, expected "
                        + $"{expectedColorLocation} from the input/output "
                        + "interface.");
                }
                if (input.NumericClass != attachment.NumericClass)
                {
                    throw MslFailure(
                        $"MSL local input {input.Location} has numeric class "
                        + $"{input.NumericClass}, expected "
                        + $"{attachment.NumericClass}.");
                }
            }

            if (expected.Count != 0)
            {
                throw MslFailure(
                    "MSL is missing one or more frozen local-input mappings.");
            }
        }

        private static ShaderAttachmentDeclaration FindLocalInputAttachment(
            ShaderAttachmentPhase phase,
            uint inputIndex)
        {
            ShaderAttachmentDeclaration? match = null;
            foreach (ShaderAttachmentDeclaration attachment in phase.Attachments)
            {
                if (attachment.InputIndex != inputIndex)
                {
                    continue;
                }

                if (match is not null)
                {
                    throw MslFailure(
                        $"Attachment contract contains duplicate local input "
                        + $"{inputIndex}.");
                }

                match = attachment;
            }

            return match
                ?? throw MslFailure(
                    $"Frozen MSL local input {inputIndex} has no explicit "
                    + "attachment.");
        }

        private static ShaderCompilerException MslFailure(string message)
        {
            return new ShaderCompilerException(
                ShaderCompilerErrorCode.MslTranslateFailed,
                message,
                requestedProfile: "attachment-abi-msl-validation");
        }
    }
}
