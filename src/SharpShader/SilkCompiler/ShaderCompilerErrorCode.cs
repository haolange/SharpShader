namespace SharpShader.SilkCompiler;

public enum ShaderCompilerErrorCode
{
    Unknown = 0,
    InvalidRequest,
    ProfileUnsupported,
    BackendUnavailable,
    CompileFailed,
    MslTranslateFailed,
    RosettaHelperUnavailable,
}
