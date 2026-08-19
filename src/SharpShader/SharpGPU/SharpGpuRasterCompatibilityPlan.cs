using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.Compilation;
using Rhi = global::SharpGPU;

namespace SharpShader.SharpGPU
{

    public sealed class SharpGpuRasterCompatibilityPlan
    {
        private readonly ReadOnlyCollection<SharpGpuAttachmentCompatibilityFact>
            m_Attachments;

        public uint AttachmentAbiRevision { get; }
        public string VariantKey { get; }
        public string EntryPoint { get; }
        public ShaderExecutionStage Stage { get; }
        public uint? Phase { get; }
        public ShaderDepthStencilAccess DepthStencilAccess { get; }
        public ShaderDepthExport DepthExport { get; }
        public ShaderStencilExport StencilExport { get; }
        public Rhi.RHIAttachmentInterfaceSignature AttachmentInterfaceSignature { get; }
        public IReadOnlyList<SharpGpuAttachmentCompatibilityFact> Attachments =>
            m_Attachments;

        internal SharpGpuRasterCompatibilityPlan(
            ShaderAttachmentInterface attachmentInterface,
            Rhi.RHIAttachmentInterfaceSignature attachmentInterfaceSignature,
            SharpGpuAttachmentCompatibilityFact[] attachments)
        {
            ArgumentNullException.ThrowIfNull(attachmentInterface);
            ArgumentNullException.ThrowIfNull(attachments);
            AttachmentAbiRevision = attachmentInterface.AbiRevision;
            VariantKey = attachmentInterface.VariantKey;
            EntryPoint = attachmentInterface.EntryPoint;
            Stage = attachmentInterface.Stage;
            Phase = attachmentInterface.Phase?.Phase;
            DepthStencilAccess = attachmentInterface.Phase?.DepthStencilAccess
                ?? ShaderDepthStencilAccess.None;
            DepthExport = attachmentInterface.Phase?.DepthExport
                ?? ShaderDepthExport.None;
            StencilExport = attachmentInterface.Phase?.StencilExport
                ?? ShaderStencilExport.None;
            AttachmentInterfaceSignature = attachmentInterfaceSignature;
            m_Attachments = Array.AsReadOnly(attachments);
        }

        public void ValidateRasterPipelineDescriptor(
            in Rhi.RHIRasterPipelineDescriptor descriptor)
        {
            if (Stage != ShaderExecutionStage.Pixel || !Phase.HasValue)
            {
                throw new InvalidOperationException(
                    $"Shader entry {EntryPoint} ({Stage}) has no raster attachment "
                    + "phase and cannot validate a raster pipeline.");
            }

            Rhi.ERHIPixelFormat[] colorFormats = descriptor.ColorFormats
                ?? throw new ArgumentException(
                    "Raster pipeline ColorFormats cannot be null.",
                    nameof(descriptor));
            if (colorFormats.Length != AttachmentInterfaceSignature.ColorAttachmentCount)
            {
                throw new ArgumentException(
                    $"Raster pipeline color format count {colorFormats.Length} does "
                    + $"not match attachment count "
                    + $"{AttachmentInterfaceSignature.ColorAttachmentCount}.",
                    nameof(descriptor));
            }

            if (descriptor.AttachmentInterface != AttachmentInterfaceSignature)
            {
                throw new ArgumentException(
                    "Raster pipeline attachment signature does not match the "
                    + "compiled shader attachment ABI.",
                    nameof(descriptor));
            }

            bool usesDualSourceBlend = UsesDualSourceBlend(
                descriptor.RenderState.BlendState,
                colorFormats.Length);
            if (usesDualSourceBlend
                != AttachmentInterfaceSignature.UsesDualSourceColor)
            {
                throw new ArgumentException(
                    "Raster pipeline dual-source blend state does not match the "
                    + "compiled shader attachment ABI.",
                    nameof(descriptor));
            }

            ValidateSampleCount(descriptor.SampleCount, nameof(descriptor));
            HashSet<uint> validatedColorAttachments = new();
            foreach (SharpGpuAttachmentCompatibilityFact declaredFact in
                     m_Attachments)
            {
                if (declaredFact.Aspect != ShaderAttachmentAspect.Color
                    || !validatedColorAttachments.Add(
                        declaredFact.LogicalAttachmentId))
                {
                    continue;
                }

                SharpGpuAttachmentCompatibilityFact fact =
                    GetColorAttachmentFact(declaredFact.LogicalAttachmentId);
                ShaderAttachmentNumericClass actual = ClassifyColorFormat(
                    colorFormats[checked((int)fact.LogicalAttachmentId)],
                    checked((int)fact.LogicalAttachmentId));
                if (actual != fact.NumericClass)
                {
                    throw new ArgumentException(
                        $"Raster pipeline color format "
                        + $"{colorFormats[checked((int)fact.LogicalAttachmentId)]} "
                        + $"for logical attachment {fact.LogicalAttachmentId} is {actual}, "
                        + $"but the shader ABI requires {fact.NumericClass}.",
                        nameof(descriptor));
                }

                ValidateSampleMode(
                    fact.SampleMode,
                    descriptor.SampleCount,
                    $"logical color attachment {fact.LogicalAttachmentId}",
                    nameof(descriptor));
            }

            SharpGpuAttachmentCompatibilityFact? depthStencilFact = null;
            foreach (SharpGpuAttachmentCompatibilityFact fact in m_Attachments)
            {
                if ((fact.Aspect & ShaderAttachmentAspect.Color) == 0)
                {
                    depthStencilFact = fact;
                    break;
                }
            }

            if (depthStencilFact.HasValue)
            {
                if (descriptor.DepthFormat == Rhi.ERHIPixelFormat.Unknown)
                {
                    throw new ArgumentException(
                        "The shader attachment ABI requires a depth/stencil "
                        + "attachment, but the raster pipeline has no DepthFormat.",
                        nameof(descriptor));
                }

                ShaderAttachmentAspect actualAspect = GetDepthStencilAspect(
                    descriptor.DepthFormat);
                ShaderAttachmentAspect requiredAspect = depthStencilFact.Value.Aspect;
                if ((actualAspect & requiredAspect) != requiredAspect)
                {
                    throw new ArgumentException(
                        $"Raster pipeline depth format {descriptor.DepthFormat} has "
                        + $"aspect {actualAspect}, but the shader ABI requires "
                        + $"{requiredAspect}.",
                        nameof(descriptor));
                }

                ValidateSampleMode(
                    depthStencilFact.Value.SampleMode,
                    descriptor.SampleCount,
                    "depth/stencil attachment",
                    nameof(descriptor));
            }
            else if (DepthStencilAccess != ShaderDepthStencilAccess.None
                || descriptor.DepthFormat != Rhi.ERHIPixelFormat.Unknown)
            {
                throw new ArgumentException(
                    "Raster pipeline depth/stencil presence does not match the "
                    + "compiled shader attachment ABI.",
                    nameof(descriptor));
            }
        }

        private SharpGpuAttachmentCompatibilityFact GetColorAttachmentFact(
            uint logicalAttachmentId)
        {
            SharpGpuAttachmentCompatibilityFact? selected = null;
            foreach (SharpGpuAttachmentCompatibilityFact fact in m_Attachments)
            {
                if (fact.Aspect != ShaderAttachmentAspect.Color
                    || fact.LogicalAttachmentId != logicalAttachmentId)
                {
                    continue;
                }

                if (!selected.HasValue)
                {
                    selected = fact;
                    continue;
                }

                if (selected.Value.NumericClass != fact.NumericClass
                    || selected.Value.SampleMode != fact.SampleMode
                    || selected.Value.LayerMode != fact.LayerMode)
                {
                    throw new InvalidOperationException(
                        $"Logical attachment {logicalAttachmentId} has inconsistent "
                        + "numeric, sample, or layer facts in the compiled shader ABI.");
                }
            }

            return selected
                ?? throw new InvalidOperationException(
                    $"Compiled shader ABI has no facts for logical color "
                    + $"attachment {logicalAttachmentId}.");
        }

        private static void ValidateSampleCount(
            Rhi.ERHISampleCount sampleCount,
            string parameterName)
        {
            if (sampleCount is not Rhi.ERHISampleCount.None
                and not Rhi.ERHISampleCount.Count2
                and not Rhi.ERHISampleCount.Count4
                and not Rhi.ERHISampleCount.Count8)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    sampleCount,
                    "Raster sample count must be 1, 2, 4, or 8.");
            }
        }

        private static void ValidateSampleMode(
            ShaderAttachmentSampleMode expected,
            Rhi.ERHISampleCount actual,
            string attachment,
            string parameterName)
        {
            bool isMultisampled = actual != Rhi.ERHISampleCount.None;
            if (isMultisampled
                != (expected == ShaderAttachmentSampleMode.Multisampled))
            {
                throw new ArgumentException(
                    $"Raster pipeline sample count {actual} does not match "
                    + $"{expected} for {attachment}.",
                    parameterName);
            }
        }

        private static ShaderAttachmentNumericClass ClassifyColorFormat(
            Rhi.ERHIPixelFormat format,
            int logicalAttachment)
        {
            return format switch
            {
                Rhi.ERHIPixelFormat.R8_UInt
                    or Rhi.ERHIPixelFormat.R16_UInt
                    or Rhi.ERHIPixelFormat.R8G8_UInt
                    or Rhi.ERHIPixelFormat.R32_UInt
                    or Rhi.ERHIPixelFormat.R16G16_UInt
                    or Rhi.ERHIPixelFormat.R8G8B8A8_UInt
                    or Rhi.ERHIPixelFormat.R10G10B10A2_UInt
                    or Rhi.ERHIPixelFormat.RG32_UInt
                    or Rhi.ERHIPixelFormat.R16G16B16A16_UInt
                    or Rhi.ERHIPixelFormat.R32G32B32A32_UInt =>
                        ShaderAttachmentNumericClass.UnsignedInteger,
                Rhi.ERHIPixelFormat.R8_SInt
                    or Rhi.ERHIPixelFormat.R16_SInt
                    or Rhi.ERHIPixelFormat.R8G8_SInt
                    or Rhi.ERHIPixelFormat.R32_SInt
                    or Rhi.ERHIPixelFormat.R16G16_SInt
                    or Rhi.ERHIPixelFormat.R8G8B8A8_SInt
                    or Rhi.ERHIPixelFormat.RG32_SInt
                    or Rhi.ERHIPixelFormat.R16G16B16A16_SInt
                    or Rhi.ERHIPixelFormat.R32G32B32A32_SInt =>
                        ShaderAttachmentNumericClass.SignedInteger,
                Rhi.ERHIPixelFormat.R8_UNorm
                    or Rhi.ERHIPixelFormat.R8_SNorm
                    or Rhi.ERHIPixelFormat.R16_Float
                    or Rhi.ERHIPixelFormat.R8G8_UNorm
                    or Rhi.ERHIPixelFormat.R8G8_SNorm
                    or Rhi.ERHIPixelFormat.R32_Float
                    or Rhi.ERHIPixelFormat.R16G16_Float
                    or Rhi.ERHIPixelFormat.R8G8B8A8_UNorm
                    or Rhi.ERHIPixelFormat.R8G8B8A8_UNorm_Srgb
                    or Rhi.ERHIPixelFormat.R8G8B8A8_SNorm
                    or Rhi.ERHIPixelFormat.B8G8R8A8_UNorm
                    or Rhi.ERHIPixelFormat.B8G8R8A8_UNorm_Srgb
                    or Rhi.ERHIPixelFormat.R99GB99_E5_Float
                    or Rhi.ERHIPixelFormat.R10G10B10A2_UNorm
                    or Rhi.ERHIPixelFormat.R11G11B10_Float
                    or Rhi.ERHIPixelFormat.RG32_Float
                    or Rhi.ERHIPixelFormat.R16G16B16A16_Float
                    or Rhi.ERHIPixelFormat.R32G32B32A32_Float =>
                        ShaderAttachmentNumericClass.FloatingPoint,
                _ => throw new ArgumentException(
                    $"Raster pipeline format {format} for logical attachment "
                    + $"{logicalAttachment} is not a color-renderable numeric "
                    + "format.",
                    nameof(format)),
            };
        }

        private static ShaderAttachmentAspect GetDepthStencilAspect(
            Rhi.ERHIPixelFormat format)
        {
            return format switch
            {
                Rhi.ERHIPixelFormat.D16_UNorm
                    or Rhi.ERHIPixelFormat.D32_Float =>
                        ShaderAttachmentAspect.Depth,
                Rhi.ERHIPixelFormat.D24_UNorm_S8_UInt
                    or Rhi.ERHIPixelFormat.D32_Float_S8_UInt =>
                        ShaderAttachmentAspect.Depth
                        | ShaderAttachmentAspect.Stencil,
                _ => throw new ArgumentException(
                    $"Raster pipeline format {format} is not a depth/stencil "
                    + "format.",
                    nameof(format)),
            };
        }

        private static bool UsesDualSourceBlend(
            in Rhi.RHIBlendStateDescriptor blendState,
            int colorAttachmentCount)
        {
            int descriptorCount = blendState.IndependentBlend
                ? colorAttachmentCount
                : Math.Min(colorAttachmentCount, 1);
            for (int index = 0; index < descriptorCount; ++index)
            {
                Rhi.RHIBlendDescriptor blend = GetBlendDescriptor(
                    blendState,
                    index);
                bool usesSecondarySource = blend.BlendEnable
                    && (IsSecondarySource(blend.SrcBlendColor)
                        || IsSecondarySource(blend.DstBlendColor)
                        || IsSecondarySource(blend.SrcBlendAlpha)
                        || IsSecondarySource(blend.DstBlendAlpha));
                if (usesSecondarySource && index != 0)
                {
                    throw new ArgumentException(
                        "Dual-source blending is valid only for color target 0.",
                        nameof(blendState));
                }

                if (usesSecondarySource)
                {
                    return true;
                }
            }

            return false;
        }

        private static Rhi.RHIBlendDescriptor GetBlendDescriptor(
            in Rhi.RHIBlendStateDescriptor blendState,
            int index)
        {
            return index switch
            {
                0 => blendState.BlendDescriptor0,
                1 => blendState.BlendDescriptor1,
                2 => blendState.BlendDescriptor2,
                3 => blendState.BlendDescriptor3,
                4 => blendState.BlendDescriptor4,
                5 => blendState.BlendDescriptor5,
                6 => blendState.BlendDescriptor6,
                7 => blendState.BlendDescriptor7,
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };
        }

        private static bool IsSecondarySource(Rhi.ERHIBlendMode mode)
        {
            return mode == Rhi.ERHIBlendMode.SecondarySourceColor
                || mode == Rhi.ERHIBlendMode.InverseSecondarySourceColor
                || mode == Rhi.ERHIBlendMode.SecondarySourceAlpha
                || mode == Rhi.ERHIBlendMode.InverseSecondarySourceAlpha;
        }
    }
}
