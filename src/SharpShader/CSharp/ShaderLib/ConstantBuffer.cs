namespace SharpShader.CSharp.ShaderLib
{
    public readonly struct ConstantBuffer<T>
        where T : struct
    {
        public T Value { get; }
    }
}
