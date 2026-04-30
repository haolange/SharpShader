using System;
using System.Collections.Generic;

namespace SharpShader.HLSLCrossCompiler;

public sealed record ShaderCompileRequest
{
    public string Source { get; init; } = string.Empty;

    public string SourceName { get; init; } = "inline.hlsl";

    public string EntryPoint { get; init; } = "main";

    public ShaderStageKind Stage { get; init; } = ShaderStageKind.Vertex;

    public ShaderModelVersion ShaderModel { get; init; } = new ShaderModelVersion(6, 0);

    public ShaderTargetKind Target { get; init; } = ShaderTargetKind.Dxil;

    public IReadOnlyList<ShaderDefine> Defines { get; init; } = Array.Empty<ShaderDefine>();

    public IReadOnlyList<string> IncludeDirs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Exports { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ExtraArguments { get; init; } = Array.Empty<string>();

    public MslCompileOptions MslOptions { get; init; } = MslCompileOptions.Default;

    public SpirvCompileOptions SpirvOptions { get; init; } = SpirvCompileOptions.Default;

    public AppleMetalCompileStrategy AppleMetalStrategy { get; init; } = AppleMetalCompileStrategy.LegacySpirvCrossMsl;

    public bool Enable16BitTypes { get; init; } = true;

    public bool EnableDebugInfo { get; init; }

    public bool DisableOptimizations { get; init; }

    public int OptimizationLevel { get; init; } = 3;

    public bool SkipValidation { get; init; }

    public bool TreatWarningsAsErrors { get; init; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Source))
        {
            throw new ShaderCompilerException(ShaderCompilerErrorCode.InvalidRequest, "Shader source must not be empty.");
        }

        if (!ShaderModel.IsInRange)
        {
            throw new ShaderCompilerException(ShaderCompilerErrorCode.InvalidRequest, $"Shader model {ShaderModel} is outside supported range 6.0-6.8.");
        }

        if (OptimizationLevel is < 0 or > 3)
        {
            throw new ShaderCompilerException(ShaderCompilerErrorCode.InvalidRequest, "OptimizationLevel must be in range 0-3.");
        }

        if (Stage != ShaderStageKind.Library && string.IsNullOrWhiteSpace(EntryPoint))
        {
            throw new ShaderCompilerException(ShaderCompilerErrorCode.InvalidRequest, "EntryPoint is required for non-library shader stages.");
        }

        if (Stage == ShaderStageKind.Library && Exports.Count > 0)
        {
            foreach (string exportName in Exports)
            {
                if (string.IsNullOrWhiteSpace(exportName))
                {
                    throw new ShaderCompilerException(ShaderCompilerErrorCode.InvalidRequest, "Exports cannot contain empty function names.");
                }
            }
        }
    }
}
