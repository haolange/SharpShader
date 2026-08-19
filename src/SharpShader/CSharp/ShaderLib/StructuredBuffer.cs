namespace SharpShader.CSharp.ShaderLib
{
    public readonly struct StructuredBuffer<T>
        where T : struct
    {
        public T this[uint index]
        {
            [Intrinsic("BUFFER_READ")]
            get => default;
        }

        [Intrinsic("BUFFER_READ")]
        public T Load(uint index)
        {
            return default;
        }
    }
}
