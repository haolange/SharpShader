using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SharpShader.HLSLCrossCompiler;
using SharpShader.HLSLCrossCompiler.Internal;
using Silk.NET.SPIRV;
using Silk.NET.SPIRV.Cross;
using CrossResult = Silk.NET.SPIRV.Cross.Result;
using NativeEntryPoint = Silk.NET.SPIRV.Cross.EntryPoint;
using NativeResource = Silk.NET.SPIRV.Cross.ReflectedResource;
using NativeSpecializationConstant = Silk.NET.SPIRV.Cross.SpecializationConstant;

namespace SharpShader.Compilation.Internal
{
    internal static unsafe partial class SpirvArtifactReflector
    {
        private const int MaximumTypeNestingDepth = 64;

        private static readonly ResourceType[] s_SupportedResourceTypes =
        {
            ResourceType.UniformBuffer,
            ResourceType.StorageBuffer,
            ResourceType.StorageImage,
            ResourceType.AtomicCounter,
            ResourceType.SeparateImage,
            ResourceType.SeparateSamplers,
            ResourceType.AccelerationStructure,
            ResourceType.ShaderRecordBuffer,
        };

        private static readonly ResourceType[] s_UnsupportedResourceTypes =
        {
            ResourceType.SampledImage,
            ResourceType.PushConstant,
        };

        internal static ShaderArtifactReflection Reflect(
            ShaderCompileRequest request,
            ShaderCompileResult compiledArtifact)
        {
            if (request is null)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.InvalidRequest,
                    "A SPIR-V reflection request is required.");
            }

            if (compiledArtifact is null)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.InvalidRequest,
                    "A compiled SPIR-V artifact is required.");
            }

            if (request.Target != ShaderTargetKind.SpirV)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.InvalidRequest,
                    $"SPIR-V reflection cannot consume target {request.Target}.",
                    requestedProfile: BuildRequestedProfile(request));
            }

            if (compiledArtifact.Bytecode.Length == 0)
            {
                throw ReflectionFailure(request, "The compiled SPIR-V artifact is empty.");
            }

            if ((compiledArtifact.Bytecode.Length % sizeof(uint)) != 0)
            {
                throw ReflectionFailure(
                    request,
                    "The compiled SPIR-V artifact length is not aligned to 32-bit words.");
            }

            uint[] words = new uint[compiledArtifact.Bytecode.Length / sizeof(uint)];
            Buffer.BlockCopy(compiledArtifact.Bytecode, 0, words, 0, compiledArtifact.Bytecode.Length);

            try
            {
                using Cross cross = SpirvCrossNativeLibraryBootstrap.CreateApi();
                Context* context = null;
                try
                {
                    ThrowIfFailed(
                        request,
                        cross,
                        context,
                        cross.ContextCreate(&context),
                        "Failed to create the SPIRV-Cross reflection context.");

                    ParsedIr* parsedIr = null;
                    fixed (uint* wordPointer = words)
                    {
                        ThrowIfFailed(
                            request,
                            cross,
                            context,
                            cross.ContextParseSpirv(
                                context,
                                wordPointer,
                                checked((nuint)words.Length),
                                &parsedIr),
                            "Failed to parse the SPIR-V artifact.");
                    }

                    Compiler* compiler = null;
                    ThrowIfFailed(
                        request,
                        cross,
                        context,
                        cross.ContextCreateCompiler(
                            context,
                            Backend.None,
                            parsedIr,
                            CaptureMode.TakeOwnership,
                            &compiler),
                        "Failed to create the SPIRV-Cross reflection compiler.");

                    return ReflectCompiler(request, cross, context, compiler);
                }
                finally
                {
                    if (context != null)
                    {
                        cross.ContextDestroy(context);
                    }
                }
            }
            catch (ShaderCompilerException)
            {
                throw;
            }
            catch (Exception ex) when (
                ex is FileNotFoundException or
                DllNotFoundException or
                BadImageFormatException or
                EntryPointNotFoundException or
                TypeInitializationException)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.BackendUnavailable,
                    "The SPIRV-Cross reflection backend is unavailable in the current process.",
                    ex.Message,
                    BuildRequestedProfile(request),
                    ex);
            }
            catch (Exception ex) when (
                ex is ArgumentException or
                InvalidOperationException or
                OverflowException)
            {
                throw ReflectionFailure(
                    request,
                    $"SPIR-V reflection data is invalid or cannot be represented: {ex.Message}",
                    ex);
            }
        }

        private static ShaderArtifactReflection ReflectCompiler(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            Compiler* compiler)
        {
            NativeEntryPoint* nativeEntryPoints = null;
            nuint entryPointCountValue = 0;
            ThrowIfFailed(
                request,
                cross,
                context,
                cross.CompilerGetEntryPoints(
                    compiler,
                    &nativeEntryPoints,
                    &entryPointCountValue),
                "Failed to enumerate SPIR-V entry points.");

            int entryPointCount = ToManagedCount(
                request,
                entryPointCountValue,
                "SPIR-V entry-point count");
            if (entryPointCount == 0)
            {
                throw ReflectionFailure(request, "SPIR-V reflection contains no entry points.");
            }

            ReflectedEntryPoint[] entryPoints = new ReflectedEntryPoint[entryPointCount];
            for (int index = 0; index < entryPointCount; ++index)
            {
                entryPoints[index] = new ReflectedEntryPoint(
                    ReadRequiredName(
                        request,
                        nativeEntryPoints[index].Name,
                        $"SPIR-V entry point {index}"),
                    nativeEntryPoints[index].ExecutionModel);
            }

            IReadOnlyDictionary<uint, uint> specializationConstants =
                ReflectSpecializationConstants(request, cross, context, compiler);
            ShaderEntryPointReflection[] reflected =
                new ShaderEntryPointReflection[entryPointCount];
            for (int index = 0; index < entryPointCount; ++index)
            {
                ReflectedEntryPoint entryPoint = entryPoints[index];
                ThrowIfFailed(
                    request,
                    cross,
                    context,
                    cross.CompilerSetEntryPoint(
                        compiler,
                        entryPoint.Name,
                        entryPoint.ExecutionModel),
                    $"Failed to select SPIR-V entry point {entryPoint.Name}.");

                ShaderExecutionStage stage = MapExecutionStage(
                    request,
                    entryPoint.ExecutionModel);
                ShaderStageMask stages = ShaderStageMaskUtility.FromStage(stage);

                Set* activeVariables = null;
                ThrowIfFailed(
                    request,
                    cross,
                    context,
                    cross.CompilerGetActiveInterfaceVariables(
                        compiler,
                        &activeVariables),
                    $"Failed to enumerate active variables for SPIR-V entry point {entryPoint.Name}.");

                Resources* resources = null;
                ThrowIfFailed(
                    request,
                    cross,
                    context,
                    cross.CompilerCreateShaderResourcesForActiveVariables(
                        compiler,
                        &resources,
                        activeVariables),
                    $"Failed to enumerate active resources for SPIR-V entry point {entryPoint.Name}.");

                Resources* interfaceResources = null;
                ThrowIfFailed(
                    request,
                    cross,
                    context,
                    cross.CompilerCreateShaderResources(
                        compiler,
                        &interfaceResources),
                    $"Failed to enumerate complete stage interfaces for SPIR-V "
                    + $"entry point {entryPoint.Name}.");

                ValidateUnsupportedResources(
                    request,
                    cross,
                    context,
                    resources,
                    entryPoint.Name);
                ShaderResourceBindingReflection[] reflectedResources = ReflectResources(
                    request,
                    cross,
                    context,
                    compiler,
                    resources,
                    stages,
                    specializationConstants);
                ShaderThreadGroupSize? threadGroupSize = ReflectThreadGroupSize(
                    request,
                    cross,
                    context,
                    compiler,
                    stage,
                    entryPoint.Name);
                ShaderStageIoReflection[] stageInputs;
                ShaderStageIoReflection[] stageOutputs;
                if (stage == ShaderExecutionStage.Compute)
                {
                    ValidateComputeStageInterface(
                        request,
                        cross,
                        context,
                        compiler,
                        interfaceResources);
                    stageInputs = Array.Empty<ShaderStageIoReflection>();
                    stageOutputs = Array.Empty<ShaderStageIoReflection>();
                }
                else
                {
                    stageInputs = ReflectStageIo(
                        request,
                        cross,
                        context,
                        compiler,
                        interfaceResources,
                        ResourceType.StageInput,
                        ShaderStageIoDirection.Input);
                    stageOutputs = ReflectStageIo(
                        request,
                        cross,
                        context,
                        compiler,
                        interfaceResources,
                        ResourceType.StageOutput,
                        ShaderStageIoDirection.Output);
                    stageOutputs = NormalizeDepthExportMode(
                        request,
                        cross,
                        context,
                        compiler,
                        entryPoint.Name,
                        stageOutputs);
                }
                ShaderInputAttachmentReflection[] inputAttachments =
                    ReflectInputAttachments(
                        request,
                        cross,
                        context,
                        compiler,
                        resources);
                ShaderAttachmentArtifactRequirement requirements =
                    ReflectAttachmentRequirements(
                        request,
                        cross,
                        context,
                        compiler,
                        entryPoint.Name,
                        stageOutputs,
                        inputAttachments);

                reflected[index] = new ShaderEntryPointReflection(
                    entryPoint.Name,
                    stage,
                    reflectedResources,
                    threadGroupSize,
                    stageInputs,
                    stageOutputs,
                    inputAttachments,
                    requirements);
            }

            return new ShaderArtifactReflection(
                ShaderArtifactKind.SpirV,
                reflected);
        }




    }
}
