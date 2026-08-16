using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.Compilation.Internal
{

    internal sealed class MslArtifactReflection
    {
        private readonly ReadOnlyCollection<MslColorAttachmentIoReflection>
            m_ColorInputs;
        private readonly ReadOnlyCollection<MslColorAttachmentIoReflection>
            m_ColorOutputs;
        private readonly ReadOnlyCollection<MslTextureBindingReflection>
            m_TextureBindings;
        private readonly ReadOnlyCollection<MslRasterOrderGroupReflection>
            m_RasterOrderGroups;

        public string EntryPoint { get; }
        public ShaderExecutionStage Stage { get; }
        public IReadOnlyList<MslColorAttachmentIoReflection> ColorInputs =>
            m_ColorInputs;
        public IReadOnlyList<MslColorAttachmentIoReflection> ColorOutputs =>
            m_ColorOutputs;
        public IReadOnlyList<MslTextureBindingReflection> TextureBindings =>
            m_TextureBindings;
        public IReadOnlyList<MslRasterOrderGroupReflection> RasterOrderGroups =>
            m_RasterOrderGroups;
        public ShaderDepthExport DepthExport { get; }
        public ShaderStencilExport StencilExport { get; }
        public ShaderAttachmentArtifactRequirement AttachmentRequirements { get; }

        public MslArtifactReflection(
            string entryPoint,
            ShaderExecutionStage stage,
            MslColorAttachmentIoReflection[] colorInputs,
            MslColorAttachmentIoReflection[] colorOutputs,
            MslTextureBindingReflection[] textureBindings,
            MslRasterOrderGroupReflection[] rasterOrderGroups,
            ShaderDepthExport depthExport,
            ShaderStencilExport stencilExport)
        {
            if (string.IsNullOrWhiteSpace(entryPoint))
            {
                throw new ArgumentException(
                    "MSL entry-point name must not be empty.",
                    nameof(entryPoint));
            }

            if (!Enum.IsDefined(stage))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stage),
                    stage,
                    "MSL entry-point stage is not defined.");
            }

            ArgumentNullException.ThrowIfNull(colorInputs);
            ArgumentNullException.ThrowIfNull(colorOutputs);
            ArgumentNullException.ThrowIfNull(textureBindings);
            ArgumentNullException.ThrowIfNull(rasterOrderGroups);
            if (!Enum.IsDefined(depthExport))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(depthExport),
                    depthExport,
                    "MSL depth export is not defined.");
            }

            if (!Enum.IsDefined(stencilExport))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stencilExport),
                    stencilExport,
                    "MSL stencil export is not defined.");
            }

            Array.Sort(colorInputs, CompareColorIo);
            Array.Sort(colorOutputs, CompareColorIo);
            Array.Sort(
                textureBindings,
                static (left, right) =>
                    left.TextureIndex.CompareTo(right.TextureIndex));
            Array.Sort(
                rasterOrderGroups,
                static (left, right) =>
                {
                    int kind = left.BindingKind.CompareTo(right.BindingKind);
                    return kind != 0
                        ? kind
                        : left.BindingIndex.CompareTo(right.BindingIndex);
                });

            EntryPoint = entryPoint;
            Stage = stage;
            m_ColorInputs = Array.AsReadOnly(colorInputs);
            m_ColorOutputs = Array.AsReadOnly(colorOutputs);
            m_TextureBindings = Array.AsReadOnly(textureBindings);
            m_RasterOrderGroups = Array.AsReadOnly(rasterOrderGroups);
            DepthExport = depthExport;
            StencilExport = stencilExport;

            ShaderAttachmentArtifactRequirement requirements =
                ShaderAttachmentArtifactRequirement.None;
            if (colorInputs.Length != 0)
            {
                requirements |=
                    ShaderAttachmentArtifactRequirement.FramebufferLocalRead;
            }

            if (rasterOrderGroups.Length != 0)
            {
                requirements |= ShaderAttachmentArtifactRequirement
                    .OrderedPixelFragmentInterlock;
            }

            if (stencilExport == ShaderStencilExport.StencilReference)
            {
                requirements |=
                    ShaderAttachmentArtifactRequirement.StencilReferenceExport;
            }

            AttachmentRequirements = requirements;
        }

        private static int CompareColorIo(
            MslColorAttachmentIoReflection left,
            MslColorAttachmentIoReflection right)
        {
            int location = left.Location.CompareTo(right.Location);
            return location != 0
                ? location
                : left.Index.CompareTo(right.Index);
        }
    }
}
