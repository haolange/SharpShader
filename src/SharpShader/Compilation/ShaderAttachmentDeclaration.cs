using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    /// <summary>
    /// Declares one logical color attachment used by a raster phase.
    /// This contract is independent from ordinary descriptor bindings.
    /// </summary>
    public sealed class ShaderAttachmentDeclaration : IEquatable<ShaderAttachmentDeclaration>
    {
        public const uint MaximumColorAttachments = 8;
        public uint LogicalAttachmentId { get; }
        public uint? InputIndex { get; }
        public uint? OutputLocation { get; }
        public uint OutputIndex { get; }
        public uint OutputComponent { get; }
        public ShaderAttachmentAspect Aspect { get; }
        public ShaderAttachmentNumericClass NumericClass { get; }
        public ShaderAttachmentSampleMode SampleMode { get; }
        public ShaderAttachmentLayerMode LayerMode { get; }

        public ShaderAttachmentDeclaration(
            uint logicalAttachmentId,
            uint? inputIndex,
            uint? outputLocation,
            ShaderAttachmentAspect aspect,
            ShaderAttachmentNumericClass numericClass,
            ShaderAttachmentSampleMode sampleMode,
            ShaderAttachmentLayerMode layerMode,
            uint outputIndex = 0,
            uint outputComponent = 0)
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
                && !outputLocation.HasValue)
            {
                throw new ArgumentException(
                    "A color attachment declaration must be an input, an output, or both.");
            }

            if (isDepthStencil
                && (inputIndex.HasValue
                    || outputLocation.HasValue
                    || outputIndex != 0
                    || outputComponent != 0))
            {
                throw new ArgumentException(
                    "Depth/stencil attachment access and exports are declared on the raster phase.");
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

            LogicalAttachmentId = logicalAttachmentId;
            InputIndex = inputIndex;
            OutputLocation = outputLocation;
            OutputIndex = outputIndex;
            OutputComponent = outputComponent;
            Aspect = aspect;
            NumericClass = numericClass;
            SampleMode = sampleMode;
            LayerMode = layerMode;
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
                && LayerMode == other.LayerMode;
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
            return hash.ToHashCode();
        }
    }
}
