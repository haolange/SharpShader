using System;

namespace SharpShader.HLSLCrossCompiler;

public sealed class ShaderCompilerException : Exception
{
    public ShaderCompilerException(
        ShaderCompilerErrorCode errorCode,
        string message,
        string diagnostics = "",
        string? requestedProfile = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        Diagnostics = diagnostics;
        RequestedProfile = requestedProfile;
    }

    public ShaderCompilerErrorCode ErrorCode { get; }

    public string Diagnostics { get; }

    public string? RequestedProfile { get; }
}
