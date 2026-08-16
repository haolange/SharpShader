using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.Compilation.Internal
{

    internal sealed class MslRasterOrderGroupReflection
    {
        public MslResourceBindingKind BindingKind { get; }
        public uint BindingIndex { get; }
        public uint Group { get; }

        public MslRasterOrderGroupReflection(
            MslResourceBindingKind bindingKind,
            uint bindingIndex,
            uint group)
        {
            if (!Enum.IsDefined(bindingKind))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bindingKind),
                    bindingKind,
                    "MSL raster-order binding kind is not defined.");
            }

            BindingKind = bindingKind;
            BindingIndex = bindingIndex;
            Group = group;
        }
    }
}
