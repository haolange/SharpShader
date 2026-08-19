namespace SharpShader.CSharp.Frontend
{
    public enum CSharpShaderResourceKind
    {
        ConstantBuffer = 0,
        StructuredBuffer = 1,
        RWStructuredBuffer = 2,
        ByteAddressBuffer = 3,
        RWByteAddressBuffer = 4,
        Texture2D = 5,
        RWTexture2D = 6,
        TextureCube = 7,
        SamplerState = 8,
        AccelerationStructure = 9,
    }
}
