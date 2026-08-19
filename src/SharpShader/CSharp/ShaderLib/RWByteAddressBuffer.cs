namespace SharpShader.CSharp.ShaderLib
{
    public readonly struct RWByteAddressBuffer
    {
        [Intrinsic("BYTE_BUFFER_LOAD")]
        public uint Load(uint byteIndex)
        {
            return 0;
        }

        [Intrinsic("BYTE_BUFFER_STORE")]
        public void Store(uint byteIndex, uint value)
        {
        }

        [Intrinsic("BYTE_BUFFER_READ")]
        public T Load<T>(uint byteIndex)
            where T : struct
        {
            return default;
        }

        [Intrinsic("BYTE_BUFFER_WRITE")]
        public void Store<T>(uint byteIndex, T value)
            where T : struct
        {
        }
    }
}
