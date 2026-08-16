using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public enum ShaderResourceKind
    {
        ConstantBuffer,
        Texture,
        Sampler,
        TypedBuffer,
        StructuredBuffer,
        StorageBuffer,
        ByteAddressBuffer,
        AccelerationStructure,
        InputAttachment,
        AtomicCounter,
        ShaderRecordBuffer,
        FeedbackTexture,
    }
}
