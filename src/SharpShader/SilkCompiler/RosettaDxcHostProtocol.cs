namespace SharpShader.SilkCompiler;

public sealed record RosettaDxcHostCompileRequest
{
    public ShaderCompileRequest Request { get; init; } = new();
}

public sealed record RosettaDxcHostCompileResponse
{
    public bool Success { get; init; }

    public ShaderCompileResult? Result { get; init; }

    public ShaderCompilerErrorCode ErrorCode { get; init; } = ShaderCompilerErrorCode.Unknown;

    public string Message { get; init; } = string.Empty;

    public string Diagnostics { get; init; } = string.Empty;
}
