using System;
using System.Collections.Generic;
using SharpShader.Compilation;
using Silk.NET.SPIRV;
using Silk.NET.SPIRV.Cross;
using System.Runtime.InteropServices;
using CrossResult = Silk.NET.SPIRV.Cross.Result;

namespace SharpShader.HLSLCrossCompiler.Internal
{
    internal static unsafe class SpirvToMslTranslator
    {
        public static ShaderCompileResult Translate(ShaderCompileRequest request, ShaderCompileResult spirvResult)
        {
            return TranslateCore(
                request,
                spirvResult,
                bindingPlan: null);
        }

        internal static ShaderCompileResult Translate(
            ShaderCompileRequest request,
            ShaderCompileResult spirvResult,
            string entryPoint,
            ShaderExecutionStage stage,
            IReadOnlyList<VulkanShaderBindingMapping> vulkanBindings,
            MetalShaderBackendLayout metalLayout,
            ShaderAttachmentInterface attachmentInterface,
            ShaderEntryPointReflection postRemapSpirvReflection,
            uint privateAttachmentDescriptorSet,
            uint privateMetalTextureBase)
        {
            MslTranslationBindingPlan bindingPlan =
                MslTranslationBindingPlan.Create(
                    entryPoint,
                    stage,
                    vulkanBindings,
                    metalLayout,
                    attachmentInterface,
                    postRemapSpirvReflection,
                    privateAttachmentDescriptorSet,
                    privateMetalTextureBase);
            return Translate(
                request,
                spirvResult,
                bindingPlan);
        }

        internal static ShaderCompileResult Translate(
            ShaderCompileRequest request,
            ShaderCompileResult spirvResult,
            MslTranslationBindingPlan bindingPlan)
        {
            ArgumentNullException.ThrowIfNull(bindingPlan);
            return TranslateCore(request, spirvResult, bindingPlan);
        }

        private static ShaderCompileResult TranslateCore(
            ShaderCompileRequest request,
            ShaderCompileResult spirvResult,
            MslTranslationBindingPlan? bindingPlan)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(spirvResult);
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

            using Cross cross = SpirvCrossNativeLibraryBootstrap.CreateApi();
            Context* context = null;

            try
            {
                ThrowIfFailed(cross.ContextCreate(&context), cross, context, "Failed to create SPIRV-Cross context.");

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

                ExecutionModel? selectedExecutionModel = bindingPlan is null
                    ? null
                    : SpirvToMslBindingApplier.SelectEntryPoint(
                        cross,
                        context,
                        compiler,
                        bindingPlan);

                CompilerOptions* options = null;
                ThrowIfFailed(cross.CompilerCreateCompilerOptions(compiler, &options), cross, context, "Failed to create MSL compiler options.");

                MslCompileOptions requestedOptions = request.MslOptions;
                uint effectiveMslVersion = requestedOptions.MslVersion;
                if (effectiveMslVersion == 0)
                {
                    // SPIR-V emitted by modern DXC frequently requires MSL 2.x features.
                    effectiveMslVersion = requestedOptions.Platform == MslTargetPlatform.IOS ? 21000u : 23000u;
                }

                if (bindingPlan?.StencilExport
                        == ShaderStencilExport.StencilReference
                    && effectiveMslVersion < 20100)
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.MslTranslateFailed,
                        "Metal stencil-reference export requires MSL 2.1 or "
                        + $"newer, but version {effectiveMslVersion} was "
                        + "requested.",
                        spirvResult.Diagnostics);
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

                bool enableArgumentBuffers = bindingPlan is null
                    ? requestedOptions.EnableArgumentBuffers
                    : bindingPlan.Mode == MslTranslationBindingMode.ReferenceBuffer;
                ThrowIfFailed(
                    cross.CompilerOptionsSetBool(options, CompilerOption.MslArgumentBuffers, enableArgumentBuffers ? (byte)1 : (byte)0),
                    cross,
                    context,
                    "Failed to set MSL argument buffer option.");

                if (enableArgumentBuffers)
                {
                    uint argumentBuffersTier = bindingPlan is null
                        ? requestedOptions.ArgumentBuffersTier
                        : 1u;
                    ThrowIfFailed(
                        cross.CompilerOptionsSetUint(options, CompilerOption.MslArgumentBuffersTier, argumentBuffersTier),
                        cross,
                        context,
                        "Failed to set MSL argument buffers tier option.");
                }

                bool enableDecorationBinding =
                    requestedOptions.EnableDecorateArgumentBufferIndex
                    || bindingPlan?.Mode == MslTranslationBindingMode.ReferenceBuffer;
                ThrowIfFailed(
                    cross.CompilerOptionsSetBool(options, CompilerOption.MslEnableDecorationBinding, enableDecorationBinding ? (byte)1 : (byte)0),
                    cross,
                    context,
                    "Failed to set MSL argument-buffer id decoration option.");

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

                if (bindingPlan is not null)
                {
                    ThrowIfFailed(
                        cross.CompilerOptionsSetBool(
                            options,
                            CompilerOption.MslEnableFragDepthBuiltin,
                            bindingPlan.DepthExport == ShaderDepthExport.None
                                ? (byte)0
                                : (byte)1),
                        cross,
                        context,
                        "Failed to set Metal fragment-depth output option.");
                    ThrowIfFailed(
                        cross.CompilerOptionsSetBool(
                            options,
                            CompilerOption.MslEnableFragStencilRefBuiltin,
                            bindingPlan.StencilExport == ShaderStencilExport.None
                                ? (byte)0
                                : (byte)1),
                        cross,
                        context,
                        "Failed to set Metal stencil-reference output option.");
                }

                if (bindingPlan?.UsesFramebufferFetch == true)
                {
                    ThrowIfFailed(
                        cross.CompilerOptionsSetBool(
                            options,
                            CompilerOption.MslFramebufferFetchSubpass,
                            1),
                        cross,
                        context,
                        "Failed to enable Metal framebuffer-fetch subpass lowering.");
                }

                ThrowIfFailed(cross.CompilerInstallCompilerOptions(compiler, options), cross, context, "Failed to install MSL compiler options.");

                if (bindingPlan is not null)
                {
                    if (!selectedExecutionModel.HasValue)
                    {
                        throw new ShaderCompilerException(
                            ShaderCompilerErrorCode.MslTranslateFailed,
                            "Planned MSL translation did not select a SPIR-V execution model.");
                    }

                    SpirvToMslBindingApplier.Apply(
                        cross,
                        context,
                        compiler,
                        selectedExecutionModel.Value,
                        bindingPlan);
                }

                byte* mslPtr = null;
                ThrowIfFailed(cross.CompilerCompile(compiler, &mslPtr), cross, context, "SPIRV-Cross failed to compile MSL text.");

                if (bindingPlan is not null)
                {
                    SpirvToMslBindingApplier.ValidateAdopted(
                        cross,
                        compiler,
                        selectedExecutionModel!.Value,
                        bindingPlan);
                }

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
}
