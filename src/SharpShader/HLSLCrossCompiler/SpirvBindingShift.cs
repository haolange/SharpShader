namespace SharpShader.HLSLCrossCompiler
{
    public enum SpirvBindingShiftKind
    {
        ShaderResource,
        Sampler,
        ConstantBuffer,
        UnorderedAccess,
    }

    public readonly record struct SpirvBindingShift(
        SpirvBindingShiftKind Kind,
        uint RegisterSpace,
        int Shift);
}
