namespace SharpShader.CSharp.ShaderLib
{
    public readonly struct RWTexture2D<T>
        where T : struct
    {
        public T this[int x]
        {
            [Intrinsic("TEXTURE2D_LOAD")]
            get => default;
            [Intrinsic("TEXTURE2D_STORE")]
            set { }
        }

        [Intrinsic("TEXTURE2D_LOAD")]
        public T Load<TCoord>(TCoord location)
        {
            return default;
        }

        [Intrinsic("TEXTURE2D_STORE")]
        public void Store<TCoord>(TCoord location, T value)
        {
        }
    }
}
