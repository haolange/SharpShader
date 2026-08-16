using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.Compilation.Internal
{

    internal sealed class MslTextureBindingReflection
    {
        public uint TextureIndex { get; }
        public ShaderAttachmentNumericClass NumericClass { get; }
        public ShaderAttachmentSampleMode SampleMode { get; }
        public ShaderAttachmentLayerMode LayerMode { get; }
        public bool IsReadWrite { get; }
        public uint? RasterOrderGroup { get; }

        public MslTextureBindingReflection(
            uint textureIndex,
            ShaderAttachmentNumericClass numericClass,
            ShaderAttachmentSampleMode sampleMode,
            ShaderAttachmentLayerMode layerMode,
            bool isReadWrite,
            uint? rasterOrderGroup)
        {
            if (!Enum.IsDefined(numericClass)
                || numericClass == ShaderAttachmentNumericClass.DepthStencil)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(numericClass),
                    numericClass,
                    "MSL textures require a color numeric class.");
            }

            if (!Enum.IsDefined(sampleMode))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sampleMode),
                    sampleMode,
                    "MSL texture sample mode is not defined.");
            }

            if (!Enum.IsDefined(layerMode))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(layerMode),
                    layerMode,
                    "MSL texture layer mode is not defined.");
            }

            TextureIndex = textureIndex;
            NumericClass = numericClass;
            SampleMode = sampleMode;
            LayerMode = layerMode;
            IsReadWrite = isReadWrite;
            RasterOrderGroup = rasterOrderGroup;
        }
    }
}
