namespace SharpShader.CSharp.ShaderLib
{
    public readonly struct Texture2D<T>
        where T : struct
    {
        [Intrinsic("TEXTURE2D_SAMPLE")]
        public T Sample<TSampler, TCoord>(TSampler sampler, TCoord uv)
        {
            return default;
        }

        [Intrinsic("TEXTURE2D_SAMPLE_LEVEL")]
        public T SampleLevel<TSampler, TCoord>(TSampler sampler, TCoord uv, float lod)
        {
            return default;
        }

        [Intrinsic("TEXTURE2D_LOAD")]
        public T Load<TCoord>(TCoord location)
        {
            return default;
        }
    }
}
