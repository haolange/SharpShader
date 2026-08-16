namespace SharpShader.HLSLCrossCompiler
{
    public readonly record struct SpirvBindingShift(
        SpirvBindingShiftKind Kind,
        uint RegisterSpace,
        int Shift);
}
