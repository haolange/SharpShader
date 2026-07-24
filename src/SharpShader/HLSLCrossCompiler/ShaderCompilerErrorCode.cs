namespace SharpShader.HLSLCrossCompiler
{
    public enum ShaderCompilerErrorCode
    {
        Unknown = 0,
        InvalidRequest,
        ProfileUnsupported,
        BackendUnavailable,
        CompileFailed,
        ToolLaunchFailed,
        ToolTimedOut,
        MslTranslateFailed,
    }
}
