using System;
using System.Collections.Generic;
using SharpShader.HLSLCrossCompiler.Internal;

namespace SharpShader.HLSLCrossCompiler
{
    public static class HLSLCrossCompiler
    {
        public static ShaderCompileResult Compile(ShaderCompileRequest request)
        {
            return CompileCore(request, null);
        }

        public static ShaderCompileResult CompileDxil(ShaderCompileRequest request)
        {
            return CompileCore(request with { Target = ShaderTargetKind.Dxil }, null);
        }

        public static ShaderCompileResult CompileSpirV(ShaderCompileRequest request)
        {
            return CompileCore(request with { Target = ShaderTargetKind.SpirV }, null);
        }

        public static ShaderCompileResult CompileMsl(ShaderCompileRequest request)
        {
            return CompileCore(request with { Target = ShaderTargetKind.Msl }, null);
        }

        public static ShaderCompileResult CompileMetalLibrary(ShaderCompileRequest request)
        {
            return CompileCore(request with { Target = ShaderTargetKind.MetalLibrary }, null);
        }

        public static ShaderCompilerCapabilities ProbeCapabilities()
        {
            return ProbeCapabilitiesCore(null);
        }

        internal static ShaderCompileResult CompileForTesting(ShaderCompileRequest request, ShaderCompilerExecutionContext context)
        {
            return CompileCore(request, context);
        }

        internal static ShaderCompilerCapabilities ProbeCapabilitiesForTesting(ShaderCompilerExecutionContext context)
        {
            return ProbeCapabilitiesCore(context);
        }

        private static ShaderCompileResult CompileCore(ShaderCompileRequest request, ShaderCompilerExecutionContext? context)
        {
#if INFINITY_TARGET_ANDROID || INFINITY_TARGET_IOS
            request.Validate();
            throw new PlatformNotSupportedException(
                "Runtime shader compilation is disabled for mobile targets. "
                + "Consume a host-produced, target-qualified shader artifact package.");
#else
            request.Validate();

            if (request.Target == ShaderTargetKind.Msl)
            {
                ShaderCompileRequest spirvRequest = request with { Target = ShaderTargetKind.SpirV };
                ShaderCompileResult spirvResult = CompileViaDxc(spirvRequest, context);
                return SpirvToMslTranslator.Translate(request, spirvResult);
            }

            if (request.Target == ShaderTargetKind.MetalLibrary)
            {
                return MetalShaderConverterCompiler.CompileToMetalLibrary(request, context);
            }

            return CompileViaDxc(request, context);
#endif
        }

        private static ShaderCompileResult CompileViaDxc(ShaderCompileRequest request, ShaderCompilerExecutionContext? context)
        {
            return context?.NativeCompileOverride?.Invoke(request) ?? NativeDxcCompiler.Compile(request);
        }

        private static ShaderCompilerCapabilities ProbeCapabilitiesCore(ShaderCompilerExecutionContext? context)
        {
#if INFINITY_TARGET_ANDROID || INFINITY_TARGET_IOS
            return new ShaderCompilerCapabilities
            {
                NativeDxcAvailable = false,
                ProfileSupport = new Dictionary<string, bool>(
                    StringComparer.OrdinalIgnoreCase),
            };
#else
            bool nativeDxcAvailable = context?.IsNativeDxcAvailableOverride?.Invoke() ?? NativeDxcCompiler.IsAvailable();

            Dictionary<string, bool> profileSupport = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            if (nativeDxcAvailable)
            {
                foreach (ShaderStageKind stage in Enum.GetValues<ShaderStageKind>())
                {
                    for (int minor = 0; minor <= 8; minor++)
                    {
                        ShaderModelVersion shaderModel = new ShaderModelVersion(6, minor);
                        string profile = DxcArgumentBuilder.BuildProfile(stage, shaderModel);
                        profileSupport[profile] = ProbeProfileSupport(stage, shaderModel, context);
                    }
                }
            }

            return new ShaderCompilerCapabilities
            {
                NativeDxcAvailable = nativeDxcAvailable,
                ProfileSupport = profileSupport,
            };
#endif
        }

        private static bool ProbeProfileSupport(ShaderStageKind stage, ShaderModelVersion shaderModel, ShaderCompilerExecutionContext? context)
        {
            ShaderCompileRequest request = ProbeShaderSourceFactory.CreateProbeRequest(stage, shaderModel);

            try
            {
                CompileCore(request, context);
                return true;
            }
            catch (ShaderCompilerException ex) when (
                ex.ErrorCode == ShaderCompilerErrorCode.ProfileUnsupported ||
                ex.ErrorCode == ShaderCompilerErrorCode.CompileFailed ||
                ex.ErrorCode == ShaderCompilerErrorCode.BackendUnavailable)
            {
                return false;
            }
        }
    }
}
