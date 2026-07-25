using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{
    [Flags]
    public enum ShaderAttachmentAspect : byte
    {
        None = 0,
        Color = 1 << 0,
        Depth = 1 << 1,
        Stencil = 1 << 2,
    }

    public enum ShaderAttachmentNumericClass : byte
    {
        FloatingPoint,
        SignedInteger,
        UnsignedInteger,
        DepthStencil,
    }

    public enum ShaderAttachmentSampleMode : byte
    {
        SingleSample,
        Multisampled,
    }

    public enum ShaderAttachmentLayerMode : byte
    {
        SingleLayer,
        Layered,
    }

    public enum ShaderAttachmentOrdering : byte
    {
        None,
        RasterOrdered,
    }

    public enum ShaderAttachmentFeedback : byte
    {
        None,
        Sampled,
    }

    public enum ShaderDepthStencilAccess : byte
    {
        None,
        ReadOnly,
        ReadWrite,
    }

    public enum ShaderDepthExport : byte
    {
        None,
        Depth,
        DepthGreaterEqual,
        DepthLessEqual,
    }

    public enum ShaderStencilExport : byte
    {
        None,
        StencilReference,
    }

    /// <summary>
    /// Declares one logical color attachment used by a raster phase.
    /// This contract is independent from ordinary descriptor bindings.
    /// </summary>
    public sealed class ShaderAttachmentDeclaration : IEquatable<ShaderAttachmentDeclaration>
    {
        public const uint MaximumColorAttachments = 8;
        public const uint ReservedAttachmentBindingTable = ushort.MaxValue;

        public uint LogicalAttachmentId { get; }
        public uint? InputIndex { get; }
        public uint? OutputLocation { get; }
        public uint OutputIndex { get; }
        public uint OutputComponent { get; }
        public ShaderAttachmentAspect Aspect { get; }
        public ShaderAttachmentNumericClass NumericClass { get; }
        public ShaderAttachmentSampleMode SampleMode { get; }
        public ShaderAttachmentLayerMode LayerMode { get; }
        public ShaderAttachmentOrdering Ordering { get; }
        public ShaderAttachmentFeedback Feedback { get; }
        public ShaderBindingKey? SampledFeedbackBinding { get; }

        public ShaderAttachmentDeclaration(
            uint logicalAttachmentId,
            uint? inputIndex,
            uint? outputLocation,
            ShaderAttachmentAspect aspect,
            ShaderAttachmentNumericClass numericClass,
            ShaderAttachmentSampleMode sampleMode,
            ShaderAttachmentLayerMode layerMode,
            uint outputIndex = 0,
            uint outputComponent = 0,
            ShaderAttachmentOrdering ordering = ShaderAttachmentOrdering.None,
            ShaderAttachmentFeedback feedback = ShaderAttachmentFeedback.None,
            ShaderBindingKey? sampledFeedbackBinding = null)
        {
            const ShaderAttachmentAspect knownAspects =
                ShaderAttachmentAspect.Color |
                ShaderAttachmentAspect.Depth |
                ShaderAttachmentAspect.Stencil;
            if (aspect == ShaderAttachmentAspect.None
                || (aspect & ~knownAspects) != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(aspect),
                    aspect,
                    "Attachment aspect is empty or contains unknown flags.");
            }

            if (!Enum.IsDefined(numericClass))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(numericClass),
                    numericClass,
                    "Attachment numeric class is not defined.");
            }

            if (!Enum.IsDefined(sampleMode))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sampleMode),
                    sampleMode,
                    "Attachment sample mode is not defined.");
            }

            if (!Enum.IsDefined(layerMode))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(layerMode),
                    layerMode,
                    "Attachment layer mode is not defined.");
            }

            if (!Enum.IsDefined(ordering))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ordering),
                    ordering,
                    "Attachment ordering requirement is not defined.");
            }

            if (!Enum.IsDefined(feedback))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(feedback),
                    feedback,
                    "Attachment feedback requirement is not defined.");
            }

            bool isColor = aspect == ShaderAttachmentAspect.Color;
            bool isDepthStencil = (aspect & ShaderAttachmentAspect.Color) == 0;
            if (!isColor && !isDepthStencil)
            {
                throw new ArgumentException(
                    "Color cannot be combined with depth or stencil in one attachment declaration.",
                    nameof(aspect));
            }

            if (isColor && numericClass == ShaderAttachmentNumericClass.DepthStencil)
            {
                throw new ArgumentException(
                    "Color attachments require a color numeric class.",
                    nameof(numericClass));
            }

            if (isDepthStencil
                && numericClass != ShaderAttachmentNumericClass.DepthStencil)
            {
                throw new ArgumentException(
                    "Depth/stencil attachments require the DepthStencil numeric class.",
                    nameof(numericClass));
            }

            if (isColor
                && (logicalAttachmentId >= MaximumColorAttachments
                    || (inputIndex.HasValue
                        && inputIndex.Value >= MaximumColorAttachments)
                    || (outputLocation.HasValue
                        && outputLocation.Value >= MaximumColorAttachments)))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(logicalAttachmentId),
                    "Color logical IDs, input indices, and output locations must be in [0, 7].");
            }

            if (outputIndex == 1
                && (!outputLocation.HasValue || outputLocation.Value != 0))
            {
                throw new ArgumentException(
                    "Dual-source output index 1 is valid only at output location 0.",
                    nameof(outputIndex));
            }

            if (isColor
                && !inputIndex.HasValue
                && !outputLocation.HasValue
                && feedback == ShaderAttachmentFeedback.None)
            {
                throw new ArgumentException(
                    "An attachment declaration must be a local input, an output, "
                    + "or explicit sampled feedback.");
            }

            if (isDepthStencil
                && (inputIndex.HasValue
                    || outputLocation.HasValue
                    || outputIndex != 0
                    || outputComponent != 0
                    || ordering != ShaderAttachmentOrdering.None
                    || feedback != ShaderAttachmentFeedback.None))
            {
                throw new ArgumentException(
                    "Depth/stencil attachment access and exports are declared on the raster phase.");
            }

            if ((feedback == ShaderAttachmentFeedback.Sampled)
                != sampledFeedbackBinding.HasValue)
            {
                throw new ArgumentException(
                    "Sampled feedback and its canonical shader binding must be declared together.",
                    nameof(sampledFeedbackBinding));
            }

            if (inputIndex.HasValue
                && feedback == ShaderAttachmentFeedback.Sampled)
            {
                throw new ArgumentException(
                    "One attachment declaration cannot be both a framebuffer-local "
                    + "input and ordinary sampled feedback.",
                    nameof(feedback));
            }

            if (sampledFeedbackBinding.HasValue
                && sampledFeedbackBinding.Value.Type
                    != ShaderBindingClass.ShaderResource)
            {
                throw new ArgumentException(
                    "Sampled feedback requires a canonical ShaderResource binding.",
                    nameof(sampledFeedbackBinding));
            }

            if (sampledFeedbackBinding.HasValue
                && sampledFeedbackBinding.Value.Table
                    == ReservedAttachmentBindingTable)
            {
                throw new ArgumentException(
                    "Sampled feedback cannot use the backend-private attachment table.",
                    nameof(sampledFeedbackBinding));
            }

            if (isDepthStencil && sampledFeedbackBinding.HasValue)
            {
                throw new ArgumentException(
                    "Depth/stencil sampled feedback is not part of the color attachment ABI.",
                    nameof(sampledFeedbackBinding));
            }

            if (!outputLocation.HasValue
                && (outputIndex != 0 || outputComponent != 0))
            {
                throw new ArgumentException(
                    "Output index and component require an output location.");
            }

            if (outputIndex > 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(outputIndex),
                    outputIndex,
                    "Only output indices 0 and 1 are defined.");
            }

            if (outputComponent > 3)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(outputComponent),
                    outputComponent,
                    "Output component must be in [0, 3].");
            }

            if (ordering == ShaderAttachmentOrdering.RasterOrdered
                && (!inputIndex.HasValue || !outputLocation.HasValue))
            {
                throw new ArgumentException(
                    "Raster-ordered access requires the attachment to be both an input and an output.",
                    nameof(ordering));
            }

            LogicalAttachmentId = logicalAttachmentId;
            InputIndex = inputIndex;
            OutputLocation = outputLocation;
            OutputIndex = outputIndex;
            OutputComponent = outputComponent;
            Aspect = aspect;
            NumericClass = numericClass;
            SampleMode = sampleMode;
            LayerMode = layerMode;
            Ordering = ordering;
            Feedback = feedback;
            SampledFeedbackBinding = sampledFeedbackBinding;
        }

        public bool Equals(ShaderAttachmentDeclaration? other)
        {
            return other is not null
                && LogicalAttachmentId == other.LogicalAttachmentId
                && InputIndex == other.InputIndex
                && OutputLocation == other.OutputLocation
                && OutputIndex == other.OutputIndex
                && OutputComponent == other.OutputComponent
                && Aspect == other.Aspect
                && NumericClass == other.NumericClass
                && SampleMode == other.SampleMode
                && LayerMode == other.LayerMode
                && Ordering == other.Ordering
                && Feedback == other.Feedback
                && SampledFeedbackBinding == other.SampledFeedbackBinding;
        }

        public override bool Equals(object? obj) =>
            Equals(obj as ShaderAttachmentDeclaration);

        public override int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(LogicalAttachmentId);
            hash.Add(InputIndex);
            hash.Add(OutputLocation);
            hash.Add(OutputIndex);
            hash.Add(OutputComponent);
            hash.Add(Aspect);
            hash.Add(NumericClass);
            hash.Add(SampleMode);
            hash.Add(LayerMode);
            hash.Add(Ordering);
            hash.Add(Feedback);
            hash.Add(SampledFeedbackBinding);
            return hash.ToHashCode();
        }
    }

    public sealed class ShaderAttachmentPhase : IEquatable<ShaderAttachmentPhase>
    {
        private readonly ReadOnlyCollection<ShaderAttachmentDeclaration> m_Attachments;

        public uint Phase { get; }
        public ShaderDepthStencilAccess DepthStencilAccess { get; }
        public ShaderDepthExport DepthExport { get; }
        public ShaderStencilExport StencilExport { get; }
        public IReadOnlyList<ShaderAttachmentDeclaration> Attachments => m_Attachments;

        public ShaderAttachmentPhase(
            uint phase,
            IEnumerable<ShaderAttachmentDeclaration>? attachments = null,
            ShaderDepthStencilAccess depthStencilAccess = ShaderDepthStencilAccess.None,
            ShaderDepthExport depthExport = ShaderDepthExport.None,
            ShaderStencilExport stencilExport = ShaderStencilExport.None)
        {
            if (!Enum.IsDefined(depthStencilAccess))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(depthStencilAccess),
                    depthStencilAccess,
                    "Depth/stencil access is not defined.");
            }

            if (!Enum.IsDefined(depthExport))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(depthExport),
                    depthExport,
                    "Depth export is not defined.");
            }

            if (!Enum.IsDefined(stencilExport))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stencilExport),
                    stencilExport,
                    "Stencil export is not defined.");
            }

            if ((depthExport != ShaderDepthExport.None
                    || stencilExport != ShaderStencilExport.None)
                && depthStencilAccess != ShaderDepthStencilAccess.ReadWrite)
            {
                throw new ArgumentException(
                    "Depth or stencil export requires ReadWrite depth/stencil access.");
            }

            ShaderAttachmentDeclaration[] copy = attachments is null
                ? Array.Empty<ShaderAttachmentDeclaration>()
                : new List<ShaderAttachmentDeclaration>(attachments).ToArray();
            Array.Sort(copy, CompareAttachments);
            ValidateAttachments(
                copy,
                depthStencilAccess,
                depthExport,
                stencilExport);

            Phase = phase;
            DepthStencilAccess = depthStencilAccess;
            DepthExport = depthExport;
            StencilExport = stencilExport;
            m_Attachments = Array.AsReadOnly(copy);
        }

        public bool Equals(ShaderAttachmentPhase? other)
        {
            if (other is null
                || Phase != other.Phase
                || DepthStencilAccess != other.DepthStencilAccess
                || DepthExport != other.DepthExport
                || StencilExport != other.StencilExport
                || m_Attachments.Count != other.m_Attachments.Count)
            {
                return false;
            }

            for (int index = 0; index < m_Attachments.Count; ++index)
            {
                if (!m_Attachments[index].Equals(other.m_Attachments[index]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) =>
            Equals(obj as ShaderAttachmentPhase);

        public override int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(Phase);
            hash.Add(DepthStencilAccess);
            hash.Add(DepthExport);
            hash.Add(StencilExport);
            foreach (ShaderAttachmentDeclaration attachment in m_Attachments)
            {
                hash.Add(attachment);
            }

            return hash.ToHashCode();
        }

        private static int CompareAttachments(
            ShaderAttachmentDeclaration? left,
            ShaderAttachmentDeclaration? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            int logicalId = left.LogicalAttachmentId.CompareTo(right.LogicalAttachmentId);
            if (logicalId != 0)
            {
                return logicalId;
            }

            int output = Nullable.Compare(left.OutputLocation, right.OutputLocation);
            if (output != 0)
            {
                return output;
            }

            int index = left.OutputIndex.CompareTo(right.OutputIndex);
            if (index != 0)
            {
                return index;
            }

            int component = left.OutputComponent.CompareTo(right.OutputComponent);
            return component != 0
                ? component
                : Nullable.Compare(left.InputIndex, right.InputIndex);
        }

        private static void ValidateAttachments(
            ShaderAttachmentDeclaration[] attachments,
            ShaderDepthStencilAccess depthStencilAccess,
            ShaderDepthExport depthExport,
            ShaderStencilExport stencilExport)
        {
            Dictionary<uint, ShaderAttachmentDeclaration> logicalAttachments = new();
            HashSet<uint> dualSourceAttachments = new();
            HashSet<uint> inputIndices = new();
            HashSet<(uint Location, uint Index)> outputs = new();
            HashSet<ShaderBindingKey> sampledFeedbackBindings = new();
            ShaderAttachmentDeclaration? depthStencilAttachment = null;
            foreach (ShaderAttachmentDeclaration attachment in attachments)
            {
                ArgumentNullException.ThrowIfNull(attachment);
                bool isDualSourceSecondary = false;
                if (logicalAttachments.TryGetValue(
                        attachment.LogicalAttachmentId,
                        out ShaderAttachmentDeclaration? primary))
                {
                    if (!dualSourceAttachments.Add(attachment.LogicalAttachmentId)
                        || !IsDualSourcePair(primary, attachment))
                    {
                        throw new ArgumentException(
                            $"Raster phase contains duplicate logical attachment ID "
                            + $"{attachment.LogicalAttachmentId} outside the exact "
                            + "location 0, output indices 0/1 dual-source form.",
                            nameof(attachments));
                    }

                    isDualSourceSecondary = true;
                }
                else
                {
                    logicalAttachments.Add(attachment.LogicalAttachmentId, attachment);
                }

                if ((attachment.Aspect & ShaderAttachmentAspect.Color) == 0)
                {
                    if (depthStencilAttachment is not null)
                    {
                        throw new ArgumentException(
                            "A raster phase may declare only one depth/stencil attachment.",
                            nameof(attachments));
                    }

                    depthStencilAttachment = attachment;
                }

                if (attachment.InputIndex.HasValue
                    && !inputIndices.Add(attachment.InputIndex.Value)
                    && !(isDualSourceSecondary
                        && primary!.InputIndex == attachment.InputIndex))
                {
                    throw new ArgumentException(
                        $"Raster phase contains duplicate input index "
                        + $"{attachment.InputIndex.Value}.",
                        nameof(attachments));
                }

                if (attachment.OutputLocation.HasValue
                    && !outputs.Add((
                        attachment.OutputLocation.Value,
                        attachment.OutputIndex)))
                {
                    throw new ArgumentException(
                        $"Raster phase contains duplicate output location/index "
                        + $"{attachment.OutputLocation.Value}/{attachment.OutputIndex}.",
                        nameof(attachments));
                }

                if (attachment.SampledFeedbackBinding.HasValue
                    && !sampledFeedbackBindings.Add(
                        attachment.SampledFeedbackBinding.Value)
                    && !(isDualSourceSecondary
                        && primary!.SampledFeedbackBinding
                            == attachment.SampledFeedbackBinding))
                {
                    throw new ArgumentException(
                        $"Raster phase contains duplicate sampled-feedback binding "
                        + $"{attachment.SampledFeedbackBinding.Value}.",
                        nameof(attachments));
                }
            }

            foreach (ShaderAttachmentDeclaration attachment in attachments)
            {
                if (attachment.OutputIndex == 1
                    && !dualSourceAttachments.Contains(attachment.LogicalAttachmentId))
                {
                    throw new ArgumentException(
                        $"Raster phase logical attachment {attachment.LogicalAttachmentId} "
                        + "declares an output-index 1 value without its matching "
                        + "location 0, output-index 0 primary value.",
                        nameof(attachments));
                }
            }

            if (dualSourceAttachments.Count != 0
                && (outputs.Count != 2
                    || !outputs.Contains((0, 0))
                    || !outputs.Contains((0, 1))))
            {
                throw new ArgumentException(
                    "A raster phase that declares dual-source color output may "
                    + "contain only the location 0, output-index 0/1 pair for "
                    + "one logical color attachment.",
                    nameof(attachments));
            }

            if ((depthStencilAccess == ShaderDepthStencilAccess.None)
                != (depthStencilAttachment is null))
            {
                throw new ArgumentException(
                    "Depth/stencil access and the logical depth/stencil attachment "
                    + "declaration must either both be present or both be absent.",
                    nameof(attachments));
            }

            if (depthStencilAttachment is not null
                && depthExport != ShaderDepthExport.None
                && (depthStencilAttachment.Aspect
                    & ShaderAttachmentAspect.Depth) == 0)
            {
                throw new ArgumentException(
                    "A depth export requires the declared depth/stencil attachment "
                    + "to include the Depth aspect.",
                    nameof(attachments));
            }

            if (depthStencilAttachment is not null
                && stencilExport != ShaderStencilExport.None
                && (depthStencilAttachment.Aspect
                    & ShaderAttachmentAspect.Stencil) == 0)
            {
                throw new ArgumentException(
                    "A stencil-reference export requires the declared "
                    + "depth/stencil attachment to include the Stencil aspect.",
                    nameof(attachments));
            }
        }

        private static bool IsDualSourcePair(
            ShaderAttachmentDeclaration primary,
            ShaderAttachmentDeclaration secondary)
        {
            if (primary.Aspect != ShaderAttachmentAspect.Color
                || secondary.Aspect != ShaderAttachmentAspect.Color
                || primary.OutputLocation != 0
                || secondary.OutputLocation != 0
                || primary.OutputIndex != 0
                || secondary.OutputIndex != 1
                || primary.OutputComponent != secondary.OutputComponent
                || primary.NumericClass != secondary.NumericClass
                || primary.SampleMode != secondary.SampleMode
                || primary.LayerMode != secondary.LayerMode)
            {
                return false;
            }

            bool identicalAccess = primary.InputIndex == secondary.InputIndex
                && primary.Ordering == secondary.Ordering
                && primary.Feedback == secondary.Feedback
                && primary.SampledFeedbackBinding == secondary.SampledFeedbackBinding;
            bool secondaryIsPureOutput = !secondary.InputIndex.HasValue
                && secondary.Ordering == ShaderAttachmentOrdering.None
                && secondary.Feedback == ShaderAttachmentFeedback.None
                && !secondary.SampledFeedbackBinding.HasValue;
            return identicalAccess || secondaryIsPureOutput;
        }
    }

    /// <summary>
    /// Explicit, variant-qualified attachment ABI supplied to shader compilation.
    /// It is intentionally separate from ordinary descriptor-resource reflection.
    /// </summary>
    public sealed class ShaderAttachmentInterface : IEquatable<ShaderAttachmentInterface>
    {
        public const uint CurrentAbiRevision = 1;

        public uint AbiRevision { get; }
        public string VariantKey { get; }
        public string EntryPoint { get; }
        public ShaderExecutionStage Stage { get; }
        public ShaderAttachmentPhase? Phase { get; }

        public ShaderAttachmentInterface(
            string variantKey,
            string entryPoint,
            ShaderExecutionStage stage,
            ShaderAttachmentPhase? phase = null,
            uint abiRevision = CurrentAbiRevision)
        {
            if (abiRevision != CurrentAbiRevision)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(abiRevision),
                    abiRevision,
                    $"Attachment ABI revision must be {CurrentAbiRevision}.");
            }

            if (string.IsNullOrWhiteSpace(variantKey))
            {
                throw new ArgumentException(
                    "Attachment interface variant key must not be empty.",
                    nameof(variantKey));
            }

            if (string.IsNullOrWhiteSpace(entryPoint))
            {
                throw new ArgumentException(
                    "Attachment interface entry point must not be empty.",
                    nameof(entryPoint));
            }

            _ = ShaderStageMaskUtility.FromStage(stage);
            if (stage == ShaderExecutionStage.Pixel && phase is null)
            {
                throw new ArgumentNullException(
                    nameof(phase),
                    "A pixel shader entry requires one explicit raster phase.");
            }

            if (stage != ShaderExecutionStage.Pixel && phase is not null)
            {
                throw new ArgumentException(
                    "Only pixel shader entries may declare a raster attachment phase.",
                    nameof(phase));
            }

            AbiRevision = abiRevision;
            VariantKey = variantKey;
            EntryPoint = entryPoint;
            Stage = stage;
            Phase = phase;
        }

        public bool Equals(ShaderAttachmentInterface? other)
        {
            return other is not null
                && AbiRevision == other.AbiRevision
                && string.Equals(VariantKey, other.VariantKey, StringComparison.Ordinal)
                && string.Equals(EntryPoint, other.EntryPoint, StringComparison.Ordinal)
                && Stage == other.Stage
                && Equals(Phase, other.Phase);
        }

        public override bool Equals(object? obj) =>
            Equals(obj as ShaderAttachmentInterface);

        public override int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(AbiRevision);
            hash.Add(VariantKey, StringComparer.Ordinal);
            hash.Add(EntryPoint, StringComparer.Ordinal);
            hash.Add(Stage);
            hash.Add(Phase);
            return hash.ToHashCode();
        }
    }
}
