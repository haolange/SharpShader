namespace SharpShader.CSharp.ShaderLib
{
    public readonly struct RWStructuredBuffer<T>
        where T : struct
    {
        public T this[uint index]
        {
            [Intrinsic("BUFFER_READ")]
            get => default;
            [Intrinsic("BUFFER_WRITE")]
            set { }
        }

        [Intrinsic("BUFFER_READ")]
        public T Load(uint index)
        {
            return default;
        }

        [Intrinsic("BUFFER_WRITE")]
        public void Store(uint index, T value)
        {
        }
    }
}
