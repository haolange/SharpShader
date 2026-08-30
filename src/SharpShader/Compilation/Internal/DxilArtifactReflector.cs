using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using SharpShader.HLSLCrossCompiler;
using SharpShader.HLSLCrossCompiler.Internal;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D12;
using DxcBuffer = Silk.NET.Direct3D.Compilers.Buffer;

namespace SharpShader.Compilation.Internal
{
    internal static unsafe partial class DxilArtifactReflector
    {
        private const string DxcLibraryName = "dxcompiler";
        private const uint DxcRuntimeDescriptorArrayBindCount = 0;
        private const uint KnownShaderInputFlags = 0x1F;
        private const int MaximumTypeNestingDepth = 64;
        private const int MaximumUtf8NameByteLength = 4096;
        private const ulong D3DShaderRequiresStencilReference = 0x00000200;
        private const ulong D3DShaderRequiresRasterOrderedViews = 0x00001000;

        private static readonly UTF8Encoding s_StrictUtf8 = new(false, true);

        private static readonly Guid s_ClsidDxcUtils = new("6245D6AF-66E0-48FD-80B4-4D271796748C");

        [DllImport(
            DxcLibraryName,
            EntryPoint = "DxcCreateInstance",
            CallingConvention = CallingConvention.Winapi,
            ExactSpelling = true)]
        private static extern int NativeDxcCreateInstance(ref Guid clsid, ref Guid iid, out nint instance);

        static DxilArtifactReflector()
        {
            SharpShaderNativeLibraryResolver.EnsureResolverRegistered(typeof(DxilArtifactReflector).Assembly);
        }

        internal static ShaderArtifactReflection Reflect(
            ShaderCompileRequest request,
            ShaderCompileResult compiledArtifact)
        {
            if (request is null)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.InvalidRequest,
                    "A DXIL reflection request is required.");
            }

            if (compiledArtifact is null)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.InvalidRequest,
                    "A compiled DXIL artifact is required.");
            }

            if (request.Target != ShaderTargetKind.Dxil)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.InvalidRequest,
                    $"DXIL reflection cannot consume target {request.Target}.",
                    requestedProfile: BuildRequestedProfile(request));
            }

            if (compiledArtifact.ReflectionData.Length == 0)
            {
                throw ReflectionFailure(
                    request,
                    "The compiled DXIL artifact does not contain DXC reflection data.");
            }

            try
            {
                using ComPtr<IDxcUtils> utils = CreateDxcUtils();
                fixed (byte* reflectionPointer = compiledArtifact.ReflectionData)
                {
                    DxcBuffer reflectionBuffer = new()
                    {
                        Ptr = reflectionPointer,
                        Size = (nuint)compiledArtifact.ReflectionData.Length,
                        Encoding = 0,
                    };

                    return request.Stage == ShaderStageKind.Library
                        ? ReflectLibrary(request, utils, &reflectionBuffer)
                        : ReflectShader(request, utils, &reflectionBuffer);
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
                TypeInitializationException or
                InvalidCastException)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.BackendUnavailable,
                    "DXC reflection backend is unavailable in the current process.",
                    ex.Message,
                    BuildRequestedProfile(request),
                    ex);
            }
            catch (Exception ex) when (
                ex is OverflowException or
                ArgumentException or
                InvalidOperationException or
                COMException)
            {
                throw ReflectionFailure(
                    request,
                    $"DXIL reflection data is invalid or cannot be represented: {ex.Message}",
                    ex);
            }
        }

        private static ShaderArtifactReflection ReflectShader(
            ShaderCompileRequest request,
            ComPtr<IDxcUtils> utils,
            DxcBuffer* reflectionBuffer)
        {
            using ComPtr<ID3D12ShaderReflection> reflection =
                CreateReflection<ID3D12ShaderReflection>(
                    request,
                    utils,
                    reflectionBuffer,
                    ID3D12ShaderReflection.Guid,
                    nameof(ID3D12ShaderReflection));

            ShaderDesc shaderDescription = default;
            ThrowIfFailed(
                request,
                reflection.Get().GetDesc(ref shaderDescription),
                "Failed to read the DXIL shader description.");

            ShaderExecutionStage reflectedStage = MapExecutionStage(request, shaderDescription.Version);
            ShaderExecutionStage requestedStage = MapRequestedStage(request);
            if (reflectedStage != requestedStage)
            {
                throw ReflectionFailure(
                    request,
                    $"DXIL reflection reports stage {reflectedStage}, but the compile request declares {requestedStage}.");
            }

            if (string.IsNullOrWhiteSpace(request.EntryPoint))
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.InvalidRequest,
                    "A non-library DXIL reflection request requires an entry-point name.",
                    requestedProfile: BuildRequestedProfile(request));
            }

            ReflectionScope scope = new(reflection.Handle);
            ShaderStageMask stageMask = ShaderStageMaskUtility.FromStage(reflectedStage);
            ShaderResourceBindingReflection[] resources = ReflectResources(
                request,
                scope,
                shaderDescription.BoundResources,
                shaderDescription.ConstantBuffers,
                stageMask);
            ShaderThreadGroupSize? threadGroupSize = ReflectThreadGroupSize(
                request,
                reflection.Handle,
                reflectedStage);
            ShaderStageIoReflection[] stageInputs = ReflectStageIo(
                request,
                reflection.Handle,
                shaderDescription.InputParameters,
                ShaderStageIoDirection.Input);
            ShaderStageIoReflection[] stageOutputs = ReflectStageIo(
                request,
                reflection.Handle,
                shaderDescription.OutputParameters,
                ShaderStageIoDirection.Output);
            ShaderAttachmentArtifactRequirement attachmentRequirements =
                ReflectAttachmentRequirements(reflection.Get().GetRequiresFlags());

            ShaderEntryPointReflection entryPoint = new(
                request.EntryPoint,
                reflectedStage,
                resources,
                threadGroupSize,
                stageInputs,
                stageOutputs,
                inputAttachments: null,
                attachmentRequirements: attachmentRequirements);

            return new ShaderArtifactReflection(
                ShaderArtifactKind.Dxil,
                new[] { entryPoint });
        }

        private static ShaderArtifactReflection ReflectLibrary(
            ShaderCompileRequest request,
            ComPtr<IDxcUtils> utils,
            DxcBuffer* reflectionBuffer)
        {
            using ComPtr<ID3D12LibraryReflection> reflection =
                CreateReflection<ID3D12LibraryReflection>(
                    request,
                    utils,
                    reflectionBuffer,
                    ID3D12LibraryReflection.Guid,
                    nameof(ID3D12LibraryReflection));

            LibraryDesc libraryDescription = default;
            ThrowIfFailed(
                request,
                reflection.Get().GetDesc(ref libraryDescription),
                "Failed to read the DXIL library description.");

            int functionCount = ToManagedCount(
                request,
                libraryDescription.FunctionCount,
                "DXIL library function count");
            if (functionCount == 0)
            {
                throw ReflectionFailure(request, "DXIL library reflection contains no shader entry points.");
            }

            ShaderEntryPointReflection[] entryPoints = new ShaderEntryPointReflection[functionCount];
            for (int functionIndex = 0; functionIndex < functionCount; ++functionIndex)
            {
                ID3D12FunctionReflection* function = reflection.Get().GetFunctionByIndex(functionIndex);
                if (function == null)
                {
                    throw ReflectionFailure(
                        request,
                        $"DXIL library function {functionIndex} has no reflection interface.");
                }

                FunctionDesc functionDescription = default;
                ThrowIfFailed(
                    request,
                    function->GetDesc(ref functionDescription),
                    $"Failed to read DXIL library function {functionIndex}.");

                string reflectedName = ReadRequiredUtf8Name(
                    request,
                    functionDescription.Name,
                    $"DXIL library function {functionIndex}");
                string entryPointName = DecodeLibraryEntryPointName(request, reflectedName);
                ShaderExecutionStage stage = MapExecutionStage(request, functionDescription.Version);
                ShaderStageMask stageMask = ShaderStageMaskUtility.FromStage(stage);
                ReflectionScope scope = new(function);
                ShaderResourceBindingReflection[] resources = ReflectResources(
                    request,
                    scope,
                    functionDescription.BoundResources,
                    functionDescription.ConstantBuffers,
                    stageMask);

                entryPoints[functionIndex] = new ShaderEntryPointReflection(
                    entryPointName,
                    stage,
                    resources);
            }

            return new ShaderArtifactReflection(ShaderArtifactKind.Dxil, entryPoints);
        }

    }
}
