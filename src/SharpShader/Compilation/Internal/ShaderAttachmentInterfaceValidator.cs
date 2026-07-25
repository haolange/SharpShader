using System;
using System.Collections.Generic;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.Compilation.Internal
{
    internal static partial class ShaderAttachmentInterfaceValidator
    {
        internal static void ValidateDxil(
            ShaderAttachmentInterface attachmentInterface,
            ShaderEntryPointReflection reflection)
        {
            ValidateIdentity(attachmentInterface, reflection, "DXIL");
            if (attachmentInterface.Stage != ShaderExecutionStage.Pixel)
            {
                return;
            }

            ShaderAttachmentPhase phase = attachmentInterface.Phase
                ?? throw Failure("A pixel attachment interface has no raster phase.");
            ValidateDxilPrivateResources(phase, reflection);
            ValidateSampledFeedbackBindings(phase, reflection, "DXIL");
            List<ShaderAttachmentDeclaration> expectedOutputs = new();
            bool requiresRasterOrdering = false;
            foreach (ShaderAttachmentDeclaration attachment in phase.Attachments)
            {
                if (attachment.OutputLocation.HasValue)
                {
                    expectedOutputs.Add(attachment);
                }

                requiresRasterOrdering |=
                    attachment.Ordering == ShaderAttachmentOrdering.RasterOrdered;
            }

            List<ShaderStageIoReflection> reflectedColorOutputs = new();
            foreach (ShaderStageIoReflection output in reflection.StageOutputs)
            {
                if (output.BuiltIn == ShaderStageIoBuiltIn.Color)
                {
                    reflectedColorOutputs.Add(output);
                }
            }

            if (reflectedColorOutputs.Count != expectedOutputs.Count)
            {
                throw Failure(
                    $"DXIL color output count {reflectedColorOutputs.Count} does not "
                    + $"match explicit attachment output count {expectedOutputs.Count} "
                    + $"for {attachmentInterface.EntryPoint}.");
            }

            HashSet<ShaderStageIoReflection> matchedOutputs = new();
            foreach (ShaderAttachmentDeclaration expected in expectedOutputs)
            {
                uint dxilTarget = expected.OutputIndex == 0
                    ? expected.OutputLocation!.Value
                    : expected.OutputIndex;
                ShaderStageIoReflection? match = null;
                foreach (ShaderStageIoReflection candidate in reflectedColorOutputs)
                {
                    if (!matchedOutputs.Contains(candidate)
                        && candidate.Location == dxilTarget
                        && candidate.NumericClass == expected.NumericClass
                        && candidate.Component == expected.OutputComponent)
                    {
                        match = candidate;
                        break;
                    }
                }

                if (match is null)
                {
                    throw Failure(
                        $"DXIL does not contain the declared color output "
                        + $"location/index/component {expected.OutputLocation}/"
                        + $"{expected.OutputIndex}/{expected.OutputComponent} "
                        + $"with numeric class {expected.NumericClass}.");
                }

                matchedOutputs.Add(match);
            }

            ValidateDepthStencilExports(phase, reflection.StageOutputs, "DXIL");
            bool reportsRasterOrdering =
                (reflection.AttachmentRequirements
                    & ShaderAttachmentArtifactRequirement.RasterOrderedViews) != 0;
            if (reportsRasterOrdering != requiresRasterOrdering)
            {
                throw Failure(
                    $"DXIL ROV requirement ({reportsRasterOrdering}) does not match "
                    + $"the explicit raster ordering contract ({requiresRasterOrdering}).");
            }

            bool reportsStencilExport =
                (reflection.AttachmentRequirements
                    & ShaderAttachmentArtifactRequirement.StencilReferenceExport) != 0;
            if (reportsStencilExport
                != (phase.StencilExport == ShaderStencilExport.StencilReference))
            {
                throw Failure(
                    "DXIL stencil-export requires flags do not match the explicit contract.");
            }
        }

        internal static ShaderEntryPointReflection CreatePublicDxilReflection(
            ShaderAttachmentInterface attachmentInterface,
            ShaderEntryPointReflection reflection)
        {
            ValidateIdentity(attachmentInterface, reflection, "DXIL");
            List<ShaderResourceBindingReflection> resources = new();
            foreach (ShaderResourceBindingReflection resource in reflection.Resources)
            {
                if (attachmentInterface.Stage != ShaderExecutionStage.Pixel
                    && resource.Key.Table
                        == ShaderAttachmentDeclaration.ReservedAttachmentBindingTable)
                {
                    throw Failure(
                        $"Non-pixel entry {reflection.Name} uses the reserved attachment "
                        + $"binding {resource.Key}.");
                }

                if (resource.Key.Table
                    != ShaderAttachmentDeclaration.ReservedAttachmentBindingTable)
                {
                    resources.Add(resource);
                }
            }

            return new ShaderEntryPointReflection(
                reflection.Name,
                reflection.Stage,
                resources,
                reflection.ThreadGroupSize,
                reflection.StageInputs,
                reflection.StageOutputs,
                reflection.InputAttachments,
                reflection.AttachmentRequirements);
        }

        internal static void ValidateSpirv(
            ShaderAttachmentInterface attachmentInterface,
            ShaderEntryPointReflection reflection,
            uint privateDescriptorSet,
            VulkanShaderBackendLayout vulkanLayout)
        {
            ValidateIdentity(attachmentInterface, reflection, "SPIR-V");
            if (attachmentInterface.Stage != ShaderExecutionStage.Pixel)
            {
                return;
            }

            ShaderAttachmentPhase phase = attachmentInterface.Phase
                ?? throw Failure("A pixel attachment interface has no raster phase.");
            Dictionary<uint, ShaderAttachmentDeclaration> expectedInputs = new();
            Dictionary<uint, ShaderAttachmentDeclaration> expectedRasterOrdered = new();
            Dictionary<(uint Location, uint Index), ShaderAttachmentDeclaration>
                expectedOutputs = new();
            foreach (ShaderAttachmentDeclaration attachment in phase.Attachments)
            {
                if (attachment.InputIndex.HasValue
                    && attachment.Ordering != ShaderAttachmentOrdering.RasterOrdered)
                {
                    if (!expectedInputs.TryAdd(attachment.InputIndex.Value, attachment)
                        && expectedInputs[attachment.InputIndex.Value].LogicalAttachmentId
                            != attachment.LogicalAttachmentId)
                    {
                        throw Failure(
                            $"Attachment input index {attachment.InputIndex.Value} maps "
                            + "to multiple logical attachments.");
                    }
                }

                if (attachment.Ordering == ShaderAttachmentOrdering.RasterOrdered)
                {
                    expectedRasterOrdered.TryAdd(
                        attachment.LogicalAttachmentId,
                        attachment);
                }

                if (attachment.OutputLocation.HasValue)
                {
                    expectedOutputs.Add(
                        (attachment.OutputLocation.Value, attachment.OutputIndex),
                        attachment);
                }
            }

            if (reflection.InputAttachments.Count != expectedInputs.Count)
            {
                throw Failure(
                    $"SPIR-V input attachment count {reflection.InputAttachments.Count} "
                    + $"does not match explicit count {expectedInputs.Count}.");
            }

            foreach (ShaderInputAttachmentReflection input in reflection.InputAttachments)
            {
                if (!expectedInputs.TryGetValue(
                        input.InputAttachmentIndex,
                        out ShaderAttachmentDeclaration? expected)
                    || input.NumericClass != expected.NumericClass
                    || input.SampleMode != expected.SampleMode
                    || input.PhysicalLocation.Group != privateDescriptorSet
                    || input.PhysicalLocation.Binding != input.InputAttachmentIndex)
                {
                    throw Failure(
                        $"SPIR-V input attachment {input.InputAttachmentIndex} does not "
                        + "match its explicit logical, numeric, sample, or private "
                        + "descriptor mapping.");
                }
            }

            ValidateSpirvRasterOrderedResources(
                expectedRasterOrdered, reflection, privateDescriptorSet);

            ValidateSampledFeedbackBindings(
                phase, reflection, "SPIR-V", vulkanLayout.Bindings);

            List<ShaderStageIoReflection> colorOutputs = new();
            foreach (ShaderStageIoReflection output in reflection.StageOutputs)
            {
                if (output.BuiltIn == ShaderStageIoBuiltIn.None)
                {
                    colorOutputs.Add(output);
                }
            }

            if (colorOutputs.Count != expectedOutputs.Count)
            {
                throw Failure(
                    $"SPIR-V color output count {colorOutputs.Count} does not match "
                    + $"explicit count {expectedOutputs.Count}.");
            }

            foreach (ShaderStageIoReflection output in colorOutputs)
            {
                if (!output.Location.HasValue
                    || !expectedOutputs.TryGetValue(
                        (output.Location.Value, output.Index),
                        out ShaderAttachmentDeclaration? expected)
                    || output.Component != expected.OutputComponent
                    || output.NumericClass != expected.NumericClass)
                {
                    throw Failure(
                        $"SPIR-V output {output.Name} at "
                        + $"{output.Location}/{output.Index}/{output.Component} "
                        + "does not match the explicit attachment contract.");
                }
            }

            ValidateDepthStencilExports(phase, reflection.StageOutputs, "SPIR-V");
            bool reportsLocalRead =
                (reflection.AttachmentRequirements
                    & ShaderAttachmentArtifactRequirement.FramebufferLocalRead) != 0;
            if (reportsLocalRead != (expectedInputs.Count != 0))
            {
                throw Failure(
                    "SPIR-V framebuffer-local-read reflection does not match the "
                    + "explicit attachment contract.");
            }

            bool requiresRasterOrdering = false;
            foreach (ShaderAttachmentDeclaration attachment in phase.Attachments)
            {
                requiresRasterOrdering |=
                    attachment.Ordering == ShaderAttachmentOrdering.RasterOrdered;
            }

            bool reportsRasterOrdering =
                (reflection.AttachmentRequirements
                    & ShaderAttachmentArtifactRequirement.OrderedPixelFragmentInterlock) != 0;
            bool reportsUnorderedInterlock =
                (reflection.AttachmentRequirements
                    & ShaderAttachmentArtifactRequirement.UnorderedFragmentInterlock) != 0;
            bool reportsNonPixelOrderedInterlock =
                (reflection.AttachmentRequirements
                    & (ShaderAttachmentArtifactRequirement.SampleOrderedFragmentInterlock
                        | ShaderAttachmentArtifactRequirement
                            .ShadingRateOrderedFragmentInterlock)) != 0;
            if (reportsUnorderedInterlock
                || reportsNonPixelOrderedInterlock
                || reportsRasterOrdering != requiresRasterOrdering)
            {
                throw Failure(
                    $"SPIR-V ordered fragment-interlock requirement "
                    + $"({reportsRasterOrdering}) does not match explicit raster "
                    + $"ordering ({requiresRasterOrdering}); unordered interlock="
                    + $"{reportsUnorderedInterlock}.");
            }
        }

        private static void ValidateSpirvRasterOrderedResources(
            IReadOnlyDictionary<uint, ShaderAttachmentDeclaration> expected,
            ShaderEntryPointReflection reflection,
            uint privateDescriptorSet)
        {
            HashSet<uint> matched = new();
            foreach (ShaderResourceBindingReflection resource in reflection.Resources)
            {
                if (resource.PhysicalLocation.Backend != ShaderBackendKind.Vulkan
                    || resource.PhysicalLocation.Namespace
                        != ShaderPhysicalBindingNamespace.Unified
                    || resource.PhysicalLocation.Group != privateDescriptorSet
                    || resource.PhysicalLocation.Binding
                        < SpirvBindingRemapper.RasterOrderedBindingBase
                    || resource.PhysicalLocation.Binding >= checked(
                        SpirvBindingRemapper.RasterOrderedBindingBase
                        + ShaderAttachmentDeclaration.MaximumColorAttachments))
                {
                    continue;
                }

                uint logicalAttachmentId = checked(
                    resource.PhysicalLocation.Binding
                    - SpirvBindingRemapper.RasterOrderedBindingBase);
                if (!expected.TryGetValue(
                        logicalAttachmentId,
                        out ShaderAttachmentDeclaration? attachment)
                    || resource.Key.Type != ShaderBindingClass.UnorderedAccess
                    || resource.Shape.Kind != ShaderResourceKind.Texture
                    || resource.Shape.Dimension
                        != GetExpectedTextureDimension(attachment)
                    || resource.Shape.Access != ShaderResourceAccess.ReadWrite
                    || resource.Shape.Array.IsArray
                    || !matched.Add(logicalAttachmentId))
                {
                    throw Failure(
                        $"SPIR-V private raster-ordered resource {resource.Name} at "
                        + $"set={resource.PhysicalLocation.Group}, binding="
                        + $"{resource.PhysicalLocation.Binding} does not match the "
                        + "explicit attachment contract.");
                }
            }

            if (matched.Count != expected.Count)
            {
                foreach (uint logicalAttachmentId in expected.Keys)
                {
                    if (!matched.Contains(logicalAttachmentId))
                    {
                        throw Failure(
                            $"SPIR-V is missing private raster-ordered resource for "
                            + $"logical attachment {logicalAttachmentId}.");
                    }
                }
            }
        }
        internal static void ValidateMetalStrategy(
            ShaderAttachmentInterface attachmentInterface,
            ShaderEntryPointReflection postRemapSpirvReflection,
            VulkanShaderBackendLayout vulkanLayout)
        {
            ValidateIdentity(attachmentInterface, postRemapSpirvReflection, "Metal input");
            if (attachmentInterface.Stage != ShaderExecutionStage.Pixel)
            {
                return;
            }

            ShaderAttachmentPhase phase = attachmentInterface.Phase
                ?? throw Failure("A pixel attachment interface has no raster phase.");
            Dictionary<uint, uint> inputLogicalIds = new();
            bool requiresRasterOrderGroups = false;
            foreach (ShaderAttachmentDeclaration attachment in phase.Attachments)
            {
                if (attachment.OutputLocation.HasValue
                    && attachment.OutputComponent != 0)
                {
                    throw Failure(
                        $"Metal attachment strategy for {attachmentInterface.EntryPoint} "
                        + "cannot structurally remap a non-zero output component "
                        + "through the SPIRV-Cross C API.");
                }

                if (attachment.OutputIndex != 0)
                {
                    throw Failure(
                        $"Metal attachment strategy for {attachmentInterface.EntryPoint} "
                        + "cannot structurally verify dual-source output index 1 "
                        + "through the SPIRV-Cross C API. Metal dual-source output "
                        + "mapping is therefore unavailable on this toolchain.");
                }

                if (attachment.InputIndex.HasValue
                    && attachment.Ordering != ShaderAttachmentOrdering.RasterOrdered)
                {
                    if (inputLogicalIds.TryGetValue(
                            attachment.InputIndex.Value,
                            out uint existingInputLogicalId)
                        && existingInputLogicalId != attachment.LogicalAttachmentId)
                    {
                        throw Failure(
                            $"Metal input index {attachment.InputIndex.Value} maps to "
                            + "multiple logical attachments.");
                    }

                    inputLogicalIds[attachment.InputIndex.Value] =
                        attachment.LogicalAttachmentId;
                }


                requiresRasterOrderGroups |=
                    attachment.Ordering == ShaderAttachmentOrdering.RasterOrdered;
            }

            ValidateSampledFeedbackBindings(
                phase,
                postRemapSpirvReflection,
                "Metal SPIR-V input",
                vulkanLayout.Bindings);
            bool reportsRasterOrderGroups =
                (postRemapSpirvReflection.AttachmentRequirements
                    & ShaderAttachmentArtifactRequirement.OrderedPixelFragmentInterlock) != 0;
            bool reportsUnorderedInterlock =
                (postRemapSpirvReflection.AttachmentRequirements
                    & ShaderAttachmentArtifactRequirement.UnorderedFragmentInterlock) != 0;
            bool reportsNonPixelOrderedInterlock =
                (postRemapSpirvReflection.AttachmentRequirements
                    & (ShaderAttachmentArtifactRequirement.SampleOrderedFragmentInterlock
                        | ShaderAttachmentArtifactRequirement
                            .ShadingRateOrderedFragmentInterlock)) != 0;
            if (reportsUnorderedInterlock
                || reportsNonPixelOrderedInterlock
                || reportsRasterOrderGroups != requiresRasterOrderGroups)
            {
                throw Failure(
                    $"Metal ROG strategy requires ordered SPIR-V interlock="
                    + $"{requiresRasterOrderGroups}, reflected ordered="
                    + $"{reportsRasterOrderGroups}, unordered="
                    + $"{reportsUnorderedInterlock}.");
            }


            if (postRemapSpirvReflection.InputAttachments.Count
                != inputLogicalIds.Count)
            {
                throw Failure(
                    "Metal strategy input facts do not match post-remap SPIR-V "
                    + "input-attachment reflection.");
            }
        }

        private static void ValidateDxilPrivateResources(
            ShaderAttachmentPhase phase,
            ShaderEntryPointReflection reflection)
        {
            Dictionary<ShaderBindingKey, ShaderAttachmentDeclaration> expected = new();
            foreach (ShaderAttachmentDeclaration attachment in phase.Attachments)
            {
                ShaderBindingKey? key = null;
                if (attachment.Ordering == ShaderAttachmentOrdering.RasterOrdered)
                {
                    key = new ShaderBindingKey(
                        ShaderAttachmentDeclaration.ReservedAttachmentBindingTable,
                        attachment.LogicalAttachmentId,
                        ShaderBindingClass.UnorderedAccess);
                }
                else if (attachment.InputIndex.HasValue)
                {
                    key = new ShaderBindingKey(
                        ShaderAttachmentDeclaration.ReservedAttachmentBindingTable,
                        attachment.InputIndex.Value,
                        ShaderBindingClass.ShaderResource);
                }

                if (key.HasValue
                    && !expected.TryAdd(key.Value, attachment)
                    && expected[key.Value].LogicalAttachmentId
                        != attachment.LogicalAttachmentId)
                {
                    throw Failure(
                        $"DXIL private attachment binding {key.Value} is assigned to "
                        + "multiple logical attachments.");
                }
            }

            foreach (ShaderResourceBindingReflection resource in reflection.Resources)
            {
                if (resource.Key.Table
                    != ShaderAttachmentDeclaration.ReservedAttachmentBindingTable)
                {
                    continue;
                }

                if (!expected.Remove(
                        resource.Key,
                        out ShaderAttachmentDeclaration? attachment))
                {
                    throw Failure(
                        $"DXIL exposes undeclared backend-private attachment resource "
                        + $"{resource.Key} ({resource.Name}).");
                }

                ShaderResourceDimension expectedDimension =
                    GetExpectedTextureDimension(attachment);
                ShaderResourceAccess expectedAccess = resource.Key.Type
                    == ShaderBindingClass.UnorderedAccess
                        ? ShaderResourceAccess.ReadWrite
                        : ShaderResourceAccess.ReadOnly;
                if (resource.Shape.Kind != ShaderResourceKind.Texture
                    || resource.Shape.Dimension != expectedDimension
                    || resource.Shape.Access != expectedAccess
                    || resource.Shape.Array.IsArray)
                {
                    throw Failure(
                        $"DXIL private attachment resource {resource.Key} has shape "
                        + $"{resource.Shape.Kind}/{resource.Shape.Dimension}/"
                        + $"{resource.Shape.Access}, expected Texture/{expectedDimension}/"
                        + $"{expectedAccess}.");
                }
            }

            if (expected.Count != 0)
            {
                foreach (ShaderBindingKey missing in expected.Keys)
                {
                    throw Failure(
                        $"DXIL does not expose declared backend-private attachment "
                        + $"resource {missing}.");
                }
            }
        }

        private static void ValidateSampledFeedbackBindings(
            ShaderAttachmentPhase phase,
            ShaderEntryPointReflection reflection,
            string artifact,
            IReadOnlyList<VulkanShaderBindingMapping>? vulkanBindings = null)
        {
            HashSet<ShaderBindingKey> validated = new();
            foreach (ShaderAttachmentDeclaration attachment in phase.Attachments)
            {
                if (attachment.Feedback != ShaderAttachmentFeedback.Sampled)
                {
                    continue;
                }

                ShaderBindingKey logicalKey = attachment.SampledFeedbackBinding
                    ?? throw Failure(
                        $"Sampled-feedback attachment {attachment.LogicalAttachmentId} "
                        + "has no canonical binding.");
                if (!validated.Add(logicalKey))
                {
                    continue;
                }

                ShaderBindingKey artifactKey = logicalKey;
                if (vulkanBindings is not null)
                {
                    bool foundMapping = false;
                    foreach (VulkanShaderBindingMapping mapping in vulkanBindings)
                    {
                        if (mapping.LogicalBinding != logicalKey)
                        {
                            continue;
                        }

                        artifactKey = new ShaderBindingKey(
                            mapping.DescriptorSet,
                            mapping.Binding,
                            logicalKey.Type);
                        foundMapping = true;
                        break;
                    }

                    if (!foundMapping)
                    {
                        throw Failure(
                            $"Sampled-feedback binding {logicalKey} has no Vulkan "
                            + "physical mapping.");
                    }
                }

                ShaderResourceBindingReflection? match = null;
                foreach (ShaderResourceBindingReflection resource in reflection.Resources)
                {
                    if (resource.Key == artifactKey)
                    {
                        match = resource;
                        break;
                    }
                }

                ShaderResourceDimension expectedDimension =
                    GetExpectedTextureDimension(attachment);
                if (match is null
                    || match.Shape.Kind != ShaderResourceKind.Texture
                    || match.Shape.Dimension != expectedDimension
                    || match.Shape.Access != ShaderResourceAccess.ReadOnly
                    || match.Shape.Array.IsArray)
                {
                    throw Failure(
                        $"Sampled-feedback binding {logicalKey} is missing from {artifact} or is "
                        + $"not a read-only {expectedDimension} texture.");
                }
            }
        }

        internal static ShaderResourceDimension GetExpectedTextureDimension(
            ShaderAttachmentDeclaration attachment)
        {
            bool multisampled = attachment.SampleMode
                == ShaderAttachmentSampleMode.Multisampled;
            bool layered = attachment.LayerMode
                == ShaderAttachmentLayerMode.Layered;
            return (multisampled, layered) switch
            {
                (false, false) => ShaderResourceDimension.Texture2D,
                (true, false) => ShaderResourceDimension.Texture2DMultisampled,
                (false, true) => ShaderResourceDimension.Texture2DArray,
                (true, true) =>
                    ShaderResourceDimension.Texture2DMultisampledArray,
            };
        }

        private static void ValidateIdentity(
            ShaderAttachmentInterface attachmentInterface,
            ShaderEntryPointReflection reflection,
            string artifact)
        {
            ArgumentNullException.ThrowIfNull(attachmentInterface);
            ArgumentNullException.ThrowIfNull(reflection);
            if (!string.Equals(
                    attachmentInterface.EntryPoint,
                    reflection.Name,
                    StringComparison.Ordinal)
                || attachmentInterface.Stage != reflection.Stage)
            {
                throw Failure(
                    $"{artifact} entry {reflection.Name} ({reflection.Stage}) does not "
                    + $"match attachment contract {attachmentInterface.EntryPoint} "
                    + $"({attachmentInterface.Stage}).");
            }
        }

        private static void ValidateDepthStencilExports(
            ShaderAttachmentPhase phase,
            IReadOnlyList<ShaderStageIoReflection> outputs,
            string artifact)
        {
            ShaderStageIoBuiltIn expectedDepth = phase.DepthExport switch
            {
                ShaderDepthExport.None => ShaderStageIoBuiltIn.None,
                ShaderDepthExport.Depth => ShaderStageIoBuiltIn.Depth,
                ShaderDepthExport.DepthGreaterEqual =>
                    ShaderStageIoBuiltIn.DepthGreaterEqual,
                ShaderDepthExport.DepthLessEqual =>
                    ShaderStageIoBuiltIn.DepthLessEqual,
                _ => throw Failure("The depth export contract is not defined."),
            };
            int depthExports = 0;
            int stencilExports = 0;
            foreach (ShaderStageIoReflection output in outputs)
            {
                if (output.BuiltIn is ShaderStageIoBuiltIn.Depth
                    or ShaderStageIoBuiltIn.DepthGreaterEqual
                    or ShaderStageIoBuiltIn.DepthLessEqual)
                {
                    ++depthExports;
                    if (output.BuiltIn != expectedDepth)
                    {
                        throw Failure(
                            $"{artifact} depth export {output.BuiltIn} does not "
                            + $"match explicit export {phase.DepthExport}.");
                    }
                }

                if (output.BuiltIn == ShaderStageIoBuiltIn.StencilReference)
                {
                    ++stencilExports;
                }
            }

            int expectedDepthCount = phase.DepthExport == ShaderDepthExport.None ? 0 : 1;
            int expectedStencilCount =
                phase.StencilExport == ShaderStencilExport.None ? 0 : 1;
            if (depthExports != expectedDepthCount
                || stencilExports != expectedStencilCount)
            {
                throw Failure(
                    $"{artifact} depth/stencil exports ({depthExports}/"
                    + $"{stencilExports}) do not match explicit counts "
                    + $"({expectedDepthCount}/{expectedStencilCount}).");
            }
        }

        private static ShaderCompilerException Failure(string message)
        {
            return new ShaderCompilerException(
                ShaderCompilerErrorCode.CompileFailed,
                message,
                requestedProfile: "attachment-abi-validation");
        }
    }
}
