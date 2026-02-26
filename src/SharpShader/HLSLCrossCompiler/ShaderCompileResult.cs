using System;

namespace SharpShader.HLSLCrossCompiler;

public sealed record ShaderCompileResult
{
    public byte[] Bytecode { get; init; } = Array.Empty<byte>();

    public string? Text { get; init; }

    public string Diagnostics { get; init; } = string.Empty;

    public string Warnings { get; init; } = string.Empty;
}
