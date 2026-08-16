using System;

namespace SharpShader.Compilation
{

    [Flags]
    public enum ShaderAttachmentArtifactRequirement : byte
    {
        None = 0,
        RasterOrderedViews = 1 << 0,
        StencilReferenceExport = 1 << 1,
        FramebufferLocalRead = 1 << 2,
        OrderedPixelFragmentInterlock = 1 << 3,
        UnorderedFragmentInterlock = 1 << 4,
        SampleOrderedFragmentInterlock = 1 << 5,
        ShadingRateOrderedFragmentInterlock = 1 << 6,
    }
}
