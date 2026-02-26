using System;
using System.Collections.Generic;

namespace SharpShader.HLSLCrossCompiler;

public static class ShaderCompilerCompat
{
    public static ShaderCompileResult Compile(
        string source,
        string entryPoint,
        ShaderStageKind stage,
        ShaderModelVersion shaderModel,
        ShaderTargetKind target,
        IReadOnlyList<ShaderDefine>? defines = null,
        IReadOnlyList<string>? includeDirs = null,
        IReadOnlyList<string>? exports = null,
        IReadOnlyList<string>? extraArguments = null,
        SpirvCompileOptions? spirvOptions = null,
        MslCompileOptions? mslOptions = null)
    {
        ShaderCompileRequest request = new ShaderCompileRequest
        {
            Source = source,
            EntryPoint = entryPoint,
            Stage = stage,
            ShaderModel = shaderModel,
            Target = target,
            Defines = defines ?? Array.Empty<ShaderDefine>(),
            IncludeDirs = includeDirs ?? Array.Empty<string>(),
            Exports = exports ?? Array.Empty<string>(),
            ExtraArguments = extraArguments ?? Array.Empty<string>(),
            SpirvOptions = spirvOptions ?? SpirvCompileOptions.Default,
            MslOptions = mslOptions ?? MslCompileOptions.Default,
        };

        return HLSLCrossCompiler.Compile(request);
    }

    public static byte[] CompileDxil(
        string source,
        string entryPoint,
        ShaderStageKind stage,
        ShaderModelVersion shaderModel,
        IReadOnlyList<ShaderDefine>? defines = null,
        IReadOnlyList<string>? includeDirs = null,
        IReadOnlyList<string>? exports = null,
        IReadOnlyList<string>? extraArguments = null)
    {
        return Compile(source, entryPoint, stage, shaderModel, ShaderTargetKind.Dxil, defines, includeDirs, exports, extraArguments).Bytecode;
    }

    public static byte[] CompileSpirV(
        string source,
        string entryPoint,
        ShaderStageKind stage,
        ShaderModelVersion shaderModel,
        IReadOnlyList<ShaderDefine>? defines = null,
        IReadOnlyList<string>? includeDirs = null,
        IReadOnlyList<string>? exports = null,
        IReadOnlyList<string>? extraArguments = null,
        SpirvCompileOptions? spirvOptions = null)
    {
        return Compile(source, entryPoint, stage, shaderModel, ShaderTargetKind.SpirV, defines, includeDirs, exports, extraArguments, spirvOptions).Bytecode;
    }

    public static string CompileMsl(
        string source,
        string entryPoint,
        ShaderStageKind stage,
        ShaderModelVersion shaderModel,
        IReadOnlyList<ShaderDefine>? defines = null,
        IReadOnlyList<string>? includeDirs = null,
        IReadOnlyList<string>? exports = null,
        IReadOnlyList<string>? extraArguments = null,
        SpirvCompileOptions? spirvOptions = null,
        MslCompileOptions? mslOptions = null)
    {
        ShaderCompileResult result = Compile(source, entryPoint, stage, shaderModel, ShaderTargetKind.Msl, defines, includeDirs, exports, extraArguments, spirvOptions, mslOptions);
        return result.Text ?? string.Empty;
    }
}
