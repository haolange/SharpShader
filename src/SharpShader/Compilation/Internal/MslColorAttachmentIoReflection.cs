using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.Compilation.Internal
{

    internal sealed class MslColorAttachmentIoReflection
    {
        public ShaderStageIoDirection Direction { get; }
        public uint Location { get; }
        public uint Index { get; }
        public ShaderAttachmentNumericClass NumericClass { get; }
        public uint ComponentCount { get; }

        public MslColorAttachmentIoReflection(
            ShaderStageIoDirection direction,
            uint location,
            uint index,
            ShaderAttachmentNumericClass numericClass,
            uint componentCount)
        {
            if (!Enum.IsDefined(direction))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(direction),
                    direction,
                    "MSL attachment I/O direction is not defined.");
            }

            if (!Enum.IsDefined(numericClass)
                || numericClass == ShaderAttachmentNumericClass.DepthStencil)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(numericClass),
                    numericClass,
                    "MSL color attachment I/O requires a color numeric class.");
            }

            if (componentCount is < 1 or > 4)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(componentCount),
                    componentCount,
                    "MSL color attachment component count must be in [1, 4].");
            }

            Direction = direction;
            Location = location;
            Index = index;
            NumericClass = numericClass;
            ComponentCount = componentCount;
        }
    }
}
