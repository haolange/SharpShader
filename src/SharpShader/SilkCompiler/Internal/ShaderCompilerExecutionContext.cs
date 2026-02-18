using System;

namespace SharpShader.SilkCompiler.Internal;

internal sealed class ShaderCompilerExecutionContext
{
    public Func<ShaderCompileRequest, ShaderCompileResult>? NativeCompileOverride { get; init; }

    public Func<bool>? IsNativeDxcAvailableOverride { get; init; }
}
