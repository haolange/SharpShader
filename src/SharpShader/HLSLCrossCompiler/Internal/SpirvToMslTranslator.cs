using System;
using System.Runtime.InteropServices;
using Silk.NET.SPIRV.Cross;
using CrossResult = Silk.NET.SPIRV.Cross.Result;

namespace SharpShader.HLSLCrossCompiler.Internal;

internal static unsafe class SpirvToMslTranslator
{
    public static ShaderCompileResult Translate(ShaderCompileRequest request, ShaderCompileResult spirvResult)
    {
        if (spirvResult.Bytecode.Length == 0)
        {
            throw new ShaderCompilerException(
                ShaderCompilerErrorCode.MslTranslateFailed,
                "SPIR-V output is empty and cannot be translated to MSL.",
                spirvResult.Diagnostics);
        }

        if ((spirvResult.Bytecode.Length % sizeof(uint)) != 0)
        {
            throw new ShaderCompilerException(
                ShaderCompilerErrorCode.MslTranslateFailed,
                "SPIR-V bytecode size is not aligned to 32-bit words.",
                spirvResult.Diagnostics);
        }

        uint[] spirvWords = new uint[spirvResult.Bytecode.Length / sizeof(uint)];
        Buffer.BlockCopy(spirvResult.Bytecode, 0, spirvWords, 0, spirvResult.Bytecode.Length);

        Cross cross = Cross.GetApi();
        Context* context = null;

        ThrowIfFailed(cross.ContextCreate(&context), cross, context, "Failed to create SPIRV-Cross context.");
        try
        {
            ParsedIr* ir = null;
            fixed (uint* spirvPtr = spirvWords)
            {
                ThrowIfFailed(
                    cross.ContextParseSpirv(context, spirvPtr, (nuint)spirvWords.Length, &ir),
                    cross,
                    context,
                    "Failed to parse SPIR-V for MSL translation.");
            }

            Compiler* compiler = null;
            ThrowIfFailed(
                cross.ContextCreateCompiler(context, Backend.Msl, ir, CaptureMode.TakeOwnership, &compiler),
                cross,
                context,
                "Failed to create MSL compiler.");

            CompilerOptions* options = null;
            ThrowIfFailed(cross.CompilerCreateCompilerOptions(compiler, &options), cross, context, "Failed to create MSL compiler options.");

            MslCompileOptions requestedOptions = request.MslOptions;
            uint effectiveMslVersion = requestedOptions.MslVersion;
            if (effectiveMslVersion == 0)
            {
                // SPIR-V emitted by modern DXC frequently requires MSL 2.x features.
                effectiveMslVersion = requestedOptions.Platform == MslTargetPlatform.IOS ? 21000u : 23000u;
            }

            ThrowIfFailed(
                cross.CompilerOptionsSetUint(options, CompilerOption.MslVersion, effectiveMslVersion),
                cross,
                context,
                "Failed to set MSL version.");

            uint platform = requestedOptions.Platform switch
            {
                MslTargetPlatform.IOS => (uint)MslPlatform.Ios,
                _ => (uint)MslPlatform.Macos,
            };

            ThrowIfFailed(
                cross.CompilerOptionsSetUint(options, CompilerOption.MslPlatform, platform),
                cross,
                context,
                "Failed to set MSL target platform.");

            ThrowIfFailed(
                cross.CompilerOptionsSetBool(options, CompilerOption.MslArgumentBuffers, requestedOptions.EnableArgumentBuffers ? (byte)1 : (byte)0),
                cross,
                context,
                "Failed to set MSL argument buffer option.");

            if (requestedOptions.EnableArgumentBuffers)
            {
                ThrowIfFailed(
                    cross.CompilerOptionsSetUint(options, CompilerOption.MslArgumentBuffersTier, requestedOptions.ArgumentBuffersTier),
                    cross,
                    context,
                    "Failed to set MSL argument buffers tier option.");
            }

            ThrowIfFailed(
                cross.CompilerOptionsSetBool(options, CompilerOption.MslForceNativeArrays, requestedOptions.ForceNativeArrays ? (byte)1 : (byte)0),
                cross,
                context,
                "Failed to set MSL native arrays option.");

            ThrowIfFailed(
                cross.CompilerOptionsSetBool(options, CompilerOption.MslPadFragmentOutputComponents, requestedOptions.PadFragmentOutputComponents ? (byte)1 : (byte)0),
                cross,
                context,
                "Failed to set MSL fragment output padding option.");

            ThrowIfFailed(
                cross.CompilerOptionsSetBool(options, CompilerOption.MslCaptureOutputToBuffer, requestedOptions.CaptureOutputToBuffer ? (byte)1 : (byte)0),
                cross,
                context,
                "Failed to set MSL capture output option.");

            ThrowIfFailed(
                cross.CompilerOptionsSetBool(options, CompilerOption.MslEnablePointSizeBuiltin, requestedOptions.EnablePointSizeBuiltin ? (byte)1 : (byte)0),
                cross,
                context,
                "Failed to set MSL point size built-in option.");

            ThrowIfFailed(cross.CompilerInstallCompilerOptions(compiler, options), cross, context, "Failed to install MSL compiler options.");

            byte* mslPtr = null;
            ThrowIfFailed(cross.CompilerCompile(compiler, &mslPtr), cross, context, "SPIRV-Cross failed to compile MSL text.");

            string mslText = Marshal.PtrToStringUTF8((IntPtr)mslPtr) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(mslText))
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.MslTranslateFailed,
                    "MSL translation returned empty text.",
                    spirvResult.Diagnostics);
            }

            return new ShaderCompileResult
            {
                Bytecode = System.Text.Encoding.UTF8.GetBytes(mslText),
                Text = mslText,
                Diagnostics = spirvResult.Diagnostics,
                Warnings = spirvResult.Warnings,
            };
        }
        finally
        {
            if (context != null)
            {
                cross.ContextDestroy(context);
            }
        }
    }

    private static void ThrowIfFailed(CrossResult result, Cross cross, Context* context, string message)
    {
        if (result == CrossResult.Success)
        {
            return;
        }

        string detail = context == null ? string.Empty : (cross.ContextGetLastErrorStringS(context) ?? string.Empty);
        throw new ShaderCompilerException(
            ShaderCompilerErrorCode.MslTranslateFailed,
            string.IsNullOrWhiteSpace(detail) ? message : $"{message} {detail}",
            detail);
    }
}
