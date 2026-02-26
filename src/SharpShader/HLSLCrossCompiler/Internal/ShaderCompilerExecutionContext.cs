using System;

namespace SharpShader.HLSLCrossCompiler.Internal;

internal sealed class ShaderCompilerExecutionContext
{
    public Func<ShaderCompileRequest, ShaderCompileResult>? NativeCompileOverride { get; init; }

    public Func<bool>? IsNativeDxcAvailableOverride { get; init; }
}
