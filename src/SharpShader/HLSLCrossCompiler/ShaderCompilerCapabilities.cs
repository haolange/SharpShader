using System;
using System.Collections.Generic;
using SharpShader.HLSLCrossCompiler.Internal;

namespace SharpShader.HLSLCrossCompiler;

public sealed class ShaderCompilerCapabilities
{
    public bool NativeDxcAvailable { get; init; }

    public bool IsAnyDxcAvailable => NativeDxcAvailable;

    public IReadOnlyDictionary<string, bool> ProfileSupport { get; init; } =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    public bool IsProfileSupported(ShaderStageKind stage, ShaderModelVersion shaderModel)
    {
        string profile = DxcArgumentBuilder.BuildProfile(stage, shaderModel);
        return ProfileSupport.TryGetValue(profile, out bool supported) && supported;
    }

    public static ShaderCompilerCapabilities Probe()
    {
        return HLSLCrossCompiler.ProbeCapabilities();
    }
}
