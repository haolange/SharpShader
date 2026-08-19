namespace SharpShader.CSharp.ShaderLib
{
    public readonly struct TextureCube<T>
        where T : struct
    {
        [Intrinsic("TEXTURECUBE_SAMPLE")]
        public T Sample<TSampler, TCoord>(TSampler sampler, TCoord direction)
        {
            return default;
        }

        [Intrinsic("TEXTURECUBE_SAMPLE_LEVEL")]
        public T SampleLevel<TSampler, TCoord>(TSampler sampler, TCoord direction, float lod)
        {
            return default;
        }
    }
}
