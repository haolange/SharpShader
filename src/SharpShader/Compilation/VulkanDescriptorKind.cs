using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{
    public enum VulkanDescriptorKind
    {
        Sampler,
        SampledImage,
        StorageImage,
        UniformBuffer,
        StorageBuffer,
        UniformTexelBuffer,
        StorageTexelBuffer,
        AccelerationStructure,
        InputAttachment,
    }
}
