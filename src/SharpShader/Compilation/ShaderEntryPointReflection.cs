using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public sealed class ShaderEntryPointReflection : IEquatable<ShaderEntryPointReflection>
    {
        private readonly ReadOnlyCollection<ShaderResourceBindingReflection> m_Resources;
        private readonly ReadOnlyCollection<ShaderStageIoReflection> m_StageInputs;
        private readonly ReadOnlyCollection<ShaderStageIoReflection> m_StageOutputs;
        private readonly ReadOnlyCollection<ShaderInputAttachmentReflection> m_InputAttachments;

        public string Name { get; }
        public ShaderExecutionStage Stage { get; }
        public ShaderThreadGroupSize? ThreadGroupSize { get; }
        public IReadOnlyList<ShaderResourceBindingReflection> Resources => m_Resources;
        public IReadOnlyList<ShaderStageIoReflection> StageInputs => m_StageInputs;
        public IReadOnlyList<ShaderStageIoReflection> StageOutputs => m_StageOutputs;
        public IReadOnlyList<ShaderInputAttachmentReflection> InputAttachments =>
            m_InputAttachments;
        public ShaderAttachmentArtifactRequirement AttachmentRequirements { get; }

        public ShaderEntryPointReflection(
            string name,
            ShaderExecutionStage stage,
            IEnumerable<ShaderResourceBindingReflection>? resources = null,
            ShaderThreadGroupSize? threadGroupSize = null,
            IEnumerable<ShaderStageIoReflection>? stageInputs = null,
            IEnumerable<ShaderStageIoReflection>? stageOutputs = null,
            IEnumerable<ShaderInputAttachmentReflection>? inputAttachments = null,
            ShaderAttachmentArtifactRequirement attachmentRequirements =
                ShaderAttachmentArtifactRequirement.None)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Entry-point name must not be empty.", nameof(name));
            }

            ShaderStageMask expectedStage = ShaderStageMaskUtility.FromStage(stage);
            bool supportsThreadGroup = stage is ShaderExecutionStage.Compute
                or ShaderExecutionStage.Amplification
                or ShaderExecutionStage.Mesh
                or ShaderExecutionStage.Node;
            if (threadGroupSize.HasValue && !supportsThreadGroup)
            {
                throw new ArgumentException(
                    $"Thread-group size is not valid for entry-point stage {stage}.",
                    nameof(threadGroupSize));
            }

            ShaderResourceBindingReflection[] copy = resources is null
                ? Array.Empty<ShaderResourceBindingReflection>()
                : new List<ShaderResourceBindingReflection>(resources).ToArray();

            HashSet<ShaderBindingKey> keys = new HashSet<ShaderBindingKey>();
            foreach (ShaderResourceBindingReflection resource in copy)
            {
                ArgumentNullException.ThrowIfNull(resource);
                if ((resource.Stages & expectedStage) == 0)
                {
                    throw new ArgumentException(
                        $"Resource {resource.Name} is not visible to entry-point stage {stage}.",
                        nameof(resources));
                }

                if (!keys.Add(resource.Key))
                {
                    throw new ArgumentException($"Entry point contains duplicate logical binding {resource.Key}.", nameof(resources));
                }
            }

            Array.Sort(copy, CompareResources);
            ShaderStageIoReflection[] inputCopy = MaterializeStageIo(
                stageInputs,
                ShaderStageIoDirection.Input,
                nameof(stageInputs));
            ShaderStageIoReflection[] outputCopy = MaterializeStageIo(
                stageOutputs,
                ShaderStageIoDirection.Output,
                nameof(stageOutputs));
            ShaderInputAttachmentReflection[] inputAttachmentCopy =
                inputAttachments is null
                    ? Array.Empty<ShaderInputAttachmentReflection>()
                    : new List<ShaderInputAttachmentReflection>(
                        inputAttachments).ToArray();
            Array.Sort(
                inputAttachmentCopy,
                static (left, right) =>
                {
                    int index = left.InputAttachmentIndex.CompareTo(
                        right.InputAttachmentIndex);
                    return index != 0
                        ? index
                        : string.CompareOrdinal(left.Name, right.Name);
                });
            for (int index = 0; index < inputAttachmentCopy.Length; ++index)
            {
                ArgumentNullException.ThrowIfNull(inputAttachmentCopy[index]);
                if (index > 0
                    && inputAttachmentCopy[index - 1].InputAttachmentIndex
                        == inputAttachmentCopy[index].InputAttachmentIndex)
                {
                    throw new ArgumentException(
                        $"Entry point contains duplicate input attachment index "
                        + $"{inputAttachmentCopy[index].InputAttachmentIndex}.",
                        nameof(inputAttachments));
                }
            }

            const ShaderAttachmentArtifactRequirement knownRequirements =
                ShaderAttachmentArtifactRequirement.RasterOrderedViews |
                ShaderAttachmentArtifactRequirement.StencilReferenceExport |
                ShaderAttachmentArtifactRequirement.FramebufferLocalRead |
                ShaderAttachmentArtifactRequirement.OrderedPixelFragmentInterlock |
                ShaderAttachmentArtifactRequirement.UnorderedFragmentInterlock |
                ShaderAttachmentArtifactRequirement.SampleOrderedFragmentInterlock |
                ShaderAttachmentArtifactRequirement.ShadingRateOrderedFragmentInterlock;
            if ((attachmentRequirements & ~knownRequirements) != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(attachmentRequirements),
                    attachmentRequirements,
                    "Attachment artifact requirements contain unknown flags.");
            }

            if (stage != ShaderExecutionStage.Pixel
                && (inputAttachmentCopy.Length != 0
                    || attachmentRequirements
                        != ShaderAttachmentArtifactRequirement.None))
            {
                throw new ArgumentException(
                    "Only pixel shader entries may declare attachment-specific reflection.");
            }

            Name = name;
            Stage = stage;
            ThreadGroupSize = threadGroupSize;
            m_Resources = Array.AsReadOnly(copy);
            m_StageInputs = Array.AsReadOnly(inputCopy);
            m_StageOutputs = Array.AsReadOnly(outputCopy);
            m_InputAttachments = Array.AsReadOnly(inputAttachmentCopy);
            AttachmentRequirements = attachmentRequirements;
        }

        private static ShaderStageIoReflection[] MaterializeStageIo(
            IEnumerable<ShaderStageIoReflection>? values,
            ShaderStageIoDirection expectedDirection,
            string parameterName)
        {
            ShaderStageIoReflection[] copy = values is null
                ? Array.Empty<ShaderStageIoReflection>()
                : new List<ShaderStageIoReflection>(values).ToArray();
            Array.Sort(copy, CompareStageIo);
            for (int index = 0; index < copy.Length; ++index)
            {
                ArgumentNullException.ThrowIfNull(copy[index]);
                if (copy[index].Direction != expectedDirection)
                {
                    throw new ArgumentException(
                        $"Stage I/O {copy[index].Name} has direction "
                        + $"{copy[index].Direction}; expected {expectedDirection}.",
                        parameterName);
                }

                if (index > 0
                    && HaveSameStageIoLocation(copy[index - 1], copy[index]))
                {
                    throw new ArgumentException(
                        "Entry point contains duplicate stage I/O locations.",
                        parameterName);
                }
            }

            return copy;
        }

        private static int CompareResources(
            ShaderResourceBindingReflection left,
            ShaderResourceBindingReflection right)
        {
            int comparison = left.Key.Table.CompareTo(right.Key.Table);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.Key.Slot.CompareTo(right.Key.Slot);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.Key.Type.CompareTo(right.Key.Type);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(left.Name, right.Name);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.PhysicalLocation.Backend.CompareTo(right.PhysicalLocation.Backend);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.PhysicalLocation.Group.CompareTo(right.PhysicalLocation.Group);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.PhysicalLocation.Binding.CompareTo(right.PhysicalLocation.Binding);
            return comparison != 0
                ? comparison
                : left.PhysicalLocation.Namespace.CompareTo(right.PhysicalLocation.Namespace);
        }

        private static int CompareStageIo(
            ShaderStageIoReflection left,
            ShaderStageIoReflection right)
        {
            int builtIn = left.BuiltIn.CompareTo(right.BuiltIn);
            if (builtIn != 0)
            {
                return builtIn;
            }

            int location = Nullable.Compare(left.Location, right.Location);
            if (location != 0)
            {
                return location;
            }

            int index = left.Index.CompareTo(right.Index);
            if (index != 0)
            {
                return index;
            }

            int component = left.Component.CompareTo(right.Component);
            return component != 0
                ? component
                : string.CompareOrdinal(left.Name, right.Name);
        }

        private static bool HaveSameStageIoLocation(
            ShaderStageIoReflection left,
            ShaderStageIoReflection right)
        {
            return left.BuiltIn == right.BuiltIn
                && left.Location == right.Location
                && left.Index == right.Index
                && left.Component == right.Component;
        }

        public bool Equals(ShaderEntryPointReflection? other)
        {
            if (other is null
                || !string.Equals(Name, other.Name, StringComparison.Ordinal)
                || Stage != other.Stage
                || ThreadGroupSize != other.ThreadGroupSize
                || AttachmentRequirements != other.AttachmentRequirements
                || m_Resources.Count != other.m_Resources.Count
                || m_StageInputs.Count != other.m_StageInputs.Count
                || m_StageOutputs.Count != other.m_StageOutputs.Count
                || m_InputAttachments.Count != other.m_InputAttachments.Count)
            {
                return false;
            }

            for (int index = 0; index < m_Resources.Count; ++index)
            {
                if (!m_Resources[index].Equals(other.m_Resources[index]))
                {
                    return false;
                }
            }

            for (int index = 0; index < m_StageInputs.Count; ++index)
            {
                if (!m_StageInputs[index].Equals(other.m_StageInputs[index]))
                {
                    return false;
                }
            }

            for (int index = 0; index < m_StageOutputs.Count; ++index)
            {
                if (!m_StageOutputs[index].Equals(other.m_StageOutputs[index]))
                {
                    return false;
                }
            }

            for (int index = 0; index < m_InputAttachments.Count; ++index)
            {
                if (!m_InputAttachments[index].Equals(other.m_InputAttachments[index]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderEntryPointReflection);

        public override int GetHashCode()
        {
            HashCode hash = new HashCode();
            hash.Add(Name, StringComparer.Ordinal);
            hash.Add(Stage);
            hash.Add(ThreadGroupSize);
            hash.Add(AttachmentRequirements);
            foreach (ShaderResourceBindingReflection resource in m_Resources)
            {
                hash.Add(resource);
            }

            foreach (ShaderStageIoReflection stageInput in m_StageInputs)
            {
                hash.Add(stageInput);
            }

            foreach (ShaderStageIoReflection stageOutput in m_StageOutputs)
            {
                hash.Add(stageOutput);
            }

            foreach (ShaderInputAttachmentReflection inputAttachment in m_InputAttachments)
            {
                hash.Add(inputAttachment);
            }

            return hash.ToHashCode();
        }
    }
}
