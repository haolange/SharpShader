using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

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
}
