namespace SharpShader.CSharp.ShaderLib
{
    public readonly struct ByteAddressBuffer
    {
        [Intrinsic("BYTE_BUFFER_LOAD")]
        public uint Load(uint byteIndex)
        {
            return 0;
        }

        [Intrinsic("BYTE_BUFFER_READ")]
        public T Load<T>(uint byteIndex)
            where T : struct
        {
            return default;
        }
    }
}
