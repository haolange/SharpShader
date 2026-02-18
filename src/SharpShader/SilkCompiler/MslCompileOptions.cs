namespace SharpShader.SilkCompiler;

public enum MslTargetPlatform
{
    MacOS,
    IOS,
}

public sealed record MslCompileOptions
{
    public static MslCompileOptions Default { get; } = new();

    public MslTargetPlatform Platform { get; init; } = MslTargetPlatform.MacOS;

    public uint MslVersion { get; init; }

    public bool EnableArgumentBuffers { get; init; }

    public bool ForceNativeArrays { get; init; }

    public bool PadFragmentOutputComponents { get; init; }

    public bool CaptureOutputToBuffer { get; init; }

    public bool EnablePointSizeBuiltin { get; init; }

    // Placeholder for future Metal IR backends (MetalShaderConverter / MDT).
    public string? MetalIrBackendHint { get; init; }
}
