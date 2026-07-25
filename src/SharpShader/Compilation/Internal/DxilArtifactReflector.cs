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
    internal static unsafe class DxilArtifactReflector
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

        private static ShaderResourceBindingReflection[] ReflectResources(
            ShaderCompileRequest request,
            ReflectionScope scope,
            uint resourceCountValue,
            uint constantBufferCountValue,
            ShaderStageMask stages)
        {
            int resourceCount = ToManagedCount(request, resourceCountValue, "DXIL resource count");
            int constantBufferCount = ToManagedCount(
                request,
                constantBufferCountValue,
                "DXIL constant-buffer count");
            ReflectedConstantBuffer[] constantBuffers = ReflectConstantBuffers(
                request,
                scope,
                constantBufferCount);

            ShaderResourceBindingReflection[] resources =
                new ShaderResourceBindingReflection[resourceCount];
            for (int resourceIndex = 0; resourceIndex < resourceCount; ++resourceIndex)
            {
                ShaderInputBindDesc bindingDescription = default;
                ThrowIfFailed(
                    request,
                    scope.GetResourceBindingDescription((uint)resourceIndex, ref bindingDescription),
                    $"Failed to read DXIL resource {resourceIndex}.");

                resources[resourceIndex] = ReflectResource(
                    request,
                    bindingDescription,
                    stages,
                    constantBuffers);
            }

            return resources;
        }

        private static ShaderResourceBindingReflection ReflectResource(
            ShaderCompileRequest request,
            ShaderInputBindDesc binding,
            ShaderStageMask stages,
            ReflectedConstantBuffer[] constantBuffers)
        {
            string name = ReadRequiredUtf8Name(request, binding.Name, "DXIL resource");
            ValidateResourceFlags(request, name, binding);
            ShaderArrayShape array = MapDescriptorArray(request, name, binding.BindPoint, binding.BindCount);

            ShaderBindingClass bindingClass;
            ShaderPhysicalBindingNamespace bindingNamespace;
            ShaderResourceKind resourceKind;
            ShaderResourceDimension dimension;
            ShaderResourceAccess access;
            uint? structureStride = null;
            ShaderSamplerKind? samplerKind = null;
            ShaderCounterKind counterKind = ShaderCounterKind.None;

            switch (binding.Type)
            {
                case D3DShaderInputType.D3DSitCbuffer:
                    ValidateDimension(
                        request,
                        name,
                        binding.Dimension,
                        D3DSrvDimension.D3D11SrvDimensionUnknown,
                        D3DSrvDimension.D3D11SrvDimensionBuffer);
                    bindingClass = ShaderBindingClass.ConstantBuffer;
                    bindingNamespace = ShaderPhysicalBindingNamespace.ConstantBuffer;
                    resourceKind = ShaderResourceKind.ConstantBuffer;
                    dimension = ShaderResourceDimension.Buffer;
                    access = ShaderResourceAccess.ReadOnly;
                    break;

                case D3DShaderInputType.D3DSitTbuffer:
                    ValidateBufferDimension(request, name, binding.Dimension);
                    bindingClass = ShaderBindingClass.ShaderResource;
                    bindingNamespace = ShaderPhysicalBindingNamespace.ShaderResource;
                    resourceKind = ShaderResourceKind.TypedBuffer;
                    dimension = ShaderResourceDimension.Buffer;
                    access = ShaderResourceAccess.ReadOnly;
                    break;

                case D3DShaderInputType.D3DSitTexture:
                    bindingClass = ShaderBindingClass.ShaderResource;
                    bindingNamespace = ShaderPhysicalBindingNamespace.ShaderResource;
                    dimension = MapDimension(request, name, binding.Dimension);
                    resourceKind = dimension == ShaderResourceDimension.Buffer
                        ? ShaderResourceKind.TypedBuffer
                        : ShaderResourceKind.Texture;
                    access = ShaderResourceAccess.ReadOnly;
                    break;

                case D3DShaderInputType.D3DSitSampler:
                    ValidateDimension(
                        request,
                        name,
                        binding.Dimension,
                        D3DSrvDimension.D3D11SrvDimensionUnknown);
                    bindingClass = ShaderBindingClass.Sampler;
                    bindingNamespace = ShaderPhysicalBindingNamespace.Sampler;
                    resourceKind = ShaderResourceKind.Sampler;
                    dimension = ShaderResourceDimension.Unknown;
                    access = ShaderResourceAccess.ReadOnly;
                    samplerKind = (binding.UFlags & (uint)D3DShaderInputFlags.D3D10SifComparisonSampler) != 0
                        ? ShaderSamplerKind.Comparison
                        : ShaderSamplerKind.Regular;
                    break;

                case D3DShaderInputType.D3DSitUavRwtyped:
                    bindingClass = ShaderBindingClass.UnorderedAccess;
                    bindingNamespace = ShaderPhysicalBindingNamespace.UnorderedAccess;
                    dimension = MapDimension(request, name, binding.Dimension);
                    resourceKind = dimension == ShaderResourceDimension.Buffer
                        ? ShaderResourceKind.TypedBuffer
                        : ShaderResourceKind.Texture;
                    access = ShaderResourceAccess.ReadWrite;
                    break;

                case D3DShaderInputType.D3DSitStructured:
                    ValidateBufferDimension(request, name, binding.Dimension);
                    bindingClass = ShaderBindingClass.ShaderResource;
                    bindingNamespace = ShaderPhysicalBindingNamespace.ShaderResource;
                    resourceKind = ShaderResourceKind.StructuredBuffer;
                    dimension = ShaderResourceDimension.Buffer;
                    access = ShaderResourceAccess.ReadOnly;
                    structureStride = ValidateStructureStride(request, name, binding.NumSamples);
                    break;

                case D3DShaderInputType.D3DSitUavRwstructured:
                    ValidateBufferDimension(request, name, binding.Dimension);
                    bindingClass = ShaderBindingClass.UnorderedAccess;
                    bindingNamespace = ShaderPhysicalBindingNamespace.UnorderedAccess;
                    resourceKind = ShaderResourceKind.StructuredBuffer;
                    dimension = ShaderResourceDimension.Buffer;
                    access = ShaderResourceAccess.ReadWrite;
                    structureStride = ValidateStructureStride(request, name, binding.NumSamples);
                    break;

                case D3DShaderInputType.D3D11SitByteaddress:
                    ValidateBufferDimension(request, name, binding.Dimension);
                    bindingClass = ShaderBindingClass.ShaderResource;
                    bindingNamespace = ShaderPhysicalBindingNamespace.ShaderResource;
                    resourceKind = ShaderResourceKind.ByteAddressBuffer;
                    dimension = ShaderResourceDimension.Buffer;
                    access = ShaderResourceAccess.ReadOnly;
                    break;

                case D3DShaderInputType.D3DSitUavRwbyteaddress:
                    ValidateBufferDimension(request, name, binding.Dimension);
                    bindingClass = ShaderBindingClass.UnorderedAccess;
                    bindingNamespace = ShaderPhysicalBindingNamespace.UnorderedAccess;
                    resourceKind = ShaderResourceKind.ByteAddressBuffer;
                    dimension = ShaderResourceDimension.Buffer;
                    access = ShaderResourceAccess.ReadWrite;
                    break;

                case D3DShaderInputType.D3D11SitUavAppendStructured:
                    ValidateBufferDimension(request, name, binding.Dimension);
                    bindingClass = ShaderBindingClass.UnorderedAccess;
                    bindingNamespace = ShaderPhysicalBindingNamespace.UnorderedAccess;
                    resourceKind = ShaderResourceKind.StructuredBuffer;
                    dimension = ShaderResourceDimension.Buffer;
                    access = ShaderResourceAccess.ReadWrite;
                    structureStride = ValidateStructureStride(request, name, binding.NumSamples);
                    counterKind = ShaderCounterKind.Append;
                    break;

                case D3DShaderInputType.D3D11SitUavConsumeStructured:
                    ValidateBufferDimension(request, name, binding.Dimension);
                    bindingClass = ShaderBindingClass.UnorderedAccess;
                    bindingNamespace = ShaderPhysicalBindingNamespace.UnorderedAccess;
                    resourceKind = ShaderResourceKind.StructuredBuffer;
                    dimension = ShaderResourceDimension.Buffer;
                    access = ShaderResourceAccess.ReadWrite;
                    structureStride = ValidateStructureStride(request, name, binding.NumSamples);
                    counterKind = ShaderCounterKind.Consume;
                    break;

                case D3DShaderInputType.D3D11SitUavRwstructuredWithCounter:
                    ValidateBufferDimension(request, name, binding.Dimension);
                    bindingClass = ShaderBindingClass.UnorderedAccess;
                    bindingNamespace = ShaderPhysicalBindingNamespace.UnorderedAccess;
                    resourceKind = ShaderResourceKind.StructuredBuffer;
                    dimension = ShaderResourceDimension.Buffer;
                    access = ShaderResourceAccess.ReadWrite;
                    structureStride = ValidateStructureStride(request, name, binding.NumSamples);
                    counterKind = ShaderCounterKind.Counter;
                    break;

                case D3DShaderInputType.D3DSitRtaccelerationstructure:
                    ValidateDimension(
                        request,
                        name,
                        binding.Dimension,
                        D3DSrvDimension.D3D11SrvDimensionUnknown);
                    bindingClass = ShaderBindingClass.ShaderResource;
                    bindingNamespace = ShaderPhysicalBindingNamespace.ShaderResource;
                    resourceKind = ShaderResourceKind.AccelerationStructure;
                    dimension = ShaderResourceDimension.Unknown;
                    access = ShaderResourceAccess.ReadOnly;
                    break;

                case D3DShaderInputType.D3DSitUavFeedbacktexture:
                    bindingClass = ShaderBindingClass.UnorderedAccess;
                    bindingNamespace = ShaderPhysicalBindingNamespace.UnorderedAccess;
                    resourceKind = ShaderResourceKind.FeedbackTexture;
                    dimension = MapTextureDimension(request, name, binding.Dimension);
                    access = ShaderResourceAccess.WriteOnly;
                    break;

                default:
                    throw ReflectionFailure(
                        request,
                        $"DXIL resource {name} has unsupported input type value {(int)binding.Type}.");
            }

            ShaderResourceShape shape = new(
                resourceKind,
                dimension,
                access,
                array,
                structureStride,
                samplerKind,
                counterKind);
            ShaderBindingKey key = new(binding.Space, binding.BindPoint, bindingClass);
            ShaderPhysicalBindingLocation physicalLocation = new(
                ShaderBackendKind.DirectX12,
                binding.Space,
                binding.BindPoint,
                bindingNamespace);
            ShaderConstantBufferLayout? constantBufferLayout =
                binding.Type == D3DShaderInputType.D3DSitCbuffer
                    ? FindConstantBufferLayout(request, name, constantBuffers)
                    : null;
            ShaderBindingProvenance provenance =
                (binding.UFlags & (uint)D3DShaderInputFlags.D3D10SifUserpacked) != 0
                    ? ShaderBindingProvenance.ExplicitSource
                    : ShaderBindingProvenance.Unknown;
            ShaderLogicalBinding logicalBinding = new(
                key,
                name,
                aliases: null,
                shape,
                stages,
                constantBufferLayout,
                provenance);

            return new ShaderResourceBindingReflection(
                logicalBinding,
                physicalLocation);
        }

        private static ShaderConstantBufferLayout FindConstantBufferLayout(
            ShaderCompileRequest request,
            string resourceName,
            ReflectedConstantBuffer[] constantBuffers)
        {
            ShaderConstantBufferLayout? matched = null;
            foreach (ReflectedConstantBuffer buffer in constantBuffers)
            {
                if (!string.Equals(buffer.Name, resourceName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (buffer.Type != D3DCBufferType.D3DCTCbuffer || matched is not null)
                {
                    throw ReflectionFailure(
                        request,
                        $"DXIL resource {resourceName} has an ambiguous or incompatible constant-buffer layout.");
                }

                matched = buffer.Layout;
            }

            return matched ?? throw ReflectionFailure(
                request,
                $"DXIL resource {resourceName} does not expose a matching constant-buffer value layout.");
        }

        private static ShaderThreadGroupSize? ReflectThreadGroupSize(
            ShaderCompileRequest request,
            ID3D12ShaderReflection* reflection,
            ShaderExecutionStage stage)
        {
            if (stage is not ShaderExecutionStage.Compute
                and not ShaderExecutionStage.Amplification
                and not ShaderExecutionStage.Mesh)
            {
                return null;
            }

            uint x = 0;
            uint y = 0;
            uint z = 0;
            uint reflectedTotal = reflection->GetThreadGroupSize(&x, &y, &z);
            uint computedTotal;
            try
            {
                computedTotal = checked(checked(x * y) * z);
            }
            catch (OverflowException ex)
            {
                throw ReflectionFailure(
                    request,
                    $"DXIL entry point has overflowing thread-group dimensions {x}x{y}x{z}.",
                    ex);
            }

            if (x == 0 || y == 0 || z == 0 || reflectedTotal != computedTotal)
            {
                throw ReflectionFailure(
                    request,
                    $"DXIL entry point reports inconsistent thread-group dimensions {x}x{y}x{z} with total {reflectedTotal}.");
            }

            return ShaderThreadGroupSize.Fixed(x, y, z);
        }

        private static ShaderStageIoReflection[] ReflectStageIo(
            ShaderCompileRequest request,
            ID3D12ShaderReflection* reflection,
            uint parameterCountValue,
            ShaderStageIoDirection direction)
        {
            int parameterCount = ToManagedCount(
                request,
                parameterCountValue,
                $"DXIL {direction} parameter count");
            ShaderStageIoReflection[] parameters =
                new ShaderStageIoReflection[parameterCount];
            for (int parameterIndex = 0; parameterIndex < parameterCount; ++parameterIndex)
            {
                SignatureParameterDesc parameter = default;
                int result = direction == ShaderStageIoDirection.Input
                    ? reflection->GetInputParameterDesc((uint)parameterIndex, ref parameter)
                    : reflection->GetOutputParameterDesc((uint)parameterIndex, ref parameter);
                ThrowIfFailed(
                    request,
                    result,
                    $"Failed to read DXIL {direction} parameter {parameterIndex}.");

                string semanticName = ReadRequiredUtf8Name(
                    request,
                    parameter.SemanticName,
                    $"DXIL {direction} parameter {parameterIndex}");
                ShaderStageIoBuiltIn builtIn = MapStageIoBuiltIn(
                    request,
                    semanticName,
                    parameter.SystemValueType);
                uint component = FindFirstSetComponent(
                    request,
                    semanticName,
                    parameter.Mask);
                uint componentCount = CountContiguousComponents(
                    request,
                    semanticName,
                    parameter.Mask,
                    component);
                uint? location = builtIn is ShaderStageIoBuiltIn.None
                    or ShaderStageIoBuiltIn.Color
                    ? parameter.Register
                    : null;

                parameters[parameterIndex] = new ShaderStageIoReflection(
                    semanticName,
                    direction,
                    location,
                    parameter.SemanticIndex,
                    component,
                    builtIn,
                    MapStageIoNumericClass(
                        request,
                        semanticName,
                        parameter.ComponentType),
                    componentCount);
            }

            return parameters;
        }

        private static ShaderAttachmentArtifactRequirement ReflectAttachmentRequirements(
            ulong requiresFlags)
        {
            ShaderAttachmentArtifactRequirement requirements =
                ShaderAttachmentArtifactRequirement.None;
            if ((requiresFlags & D3DShaderRequiresRasterOrderedViews) != 0)
            {
                requirements |= ShaderAttachmentArtifactRequirement.RasterOrderedViews;
            }

            if ((requiresFlags & D3DShaderRequiresStencilReference) != 0)
            {
                requirements |= ShaderAttachmentArtifactRequirement.StencilReferenceExport;
            }

            return requirements;
        }

        private static ShaderStageIoBuiltIn MapStageIoBuiltIn(
            ShaderCompileRequest request,
            string semanticName,
            D3DName systemValue)
        {
            return systemValue switch
            {
                D3DName.D3DNameUndefined => ShaderStageIoBuiltIn.None,
                D3DName.D3DNamePosition => ShaderStageIoBuiltIn.Position,
                D3DName.D3DNameClipDistance => ShaderStageIoBuiltIn.ClipDistance,
                D3DName.D3DNameCullDistance => ShaderStageIoBuiltIn.CullDistance,
                D3DName.D3DNameRenderTargetArrayIndex =>
                    ShaderStageIoBuiltIn.RenderTargetArrayIndex,
                D3DName.D3DNameViewportArrayIndex =>
                    ShaderStageIoBuiltIn.ViewportArrayIndex,
                D3DName.D3DNameVertexID => ShaderStageIoBuiltIn.VertexId,
                D3DName.D3DNamePrimitiveID => ShaderStageIoBuiltIn.PrimitiveId,
                D3DName.D3DNameInstanceID => ShaderStageIoBuiltIn.InstanceId,
                D3DName.D3DNameIsFrontFace => ShaderStageIoBuiltIn.FrontFace,
                D3DName.D3DNameSampleIndex => ShaderStageIoBuiltIn.SampleIndex,
                D3DName.D3DNameFinalQuadEdgeTessfactor
                    or D3DName.D3DNameFinalTriEdgeTessfactor
                    or D3DName.D3DNameFinalLineDetailTessfactor =>
                        ShaderStageIoBuiltIn.TessellationFactor,
                D3DName.D3DNameFinalQuadInsideTessfactor
                    or D3DName.D3DNameFinalTriInsideTessfactor
                    or D3DName.D3DNameFinalLineDensityTessfactor =>
                        ShaderStageIoBuiltIn.InsideTessellationFactor,
                D3DName.D3DNameBarycentrics => ShaderStageIoBuiltIn.Barycentrics,
                D3DName.D3DNameShadingrate => ShaderStageIoBuiltIn.ShadingRate,
                D3DName.D3DNameCullprimitive => ShaderStageIoBuiltIn.CullPrimitive,
                D3DName.D3DNameTarget => ShaderStageIoBuiltIn.Color,
                D3DName.D3DNameDepth => ShaderStageIoBuiltIn.Depth,
                D3DName.D3DNameCoverage => ShaderStageIoBuiltIn.Coverage,
                D3DName.D3DNameDepthGreaterEqual =>
                    ShaderStageIoBuiltIn.DepthGreaterEqual,
                D3DName.D3DNameDepthLessEqual =>
                    ShaderStageIoBuiltIn.DepthLessEqual,
                D3DName.D3DNameStencilRef =>
                    ShaderStageIoBuiltIn.StencilReference,
                D3DName.D3DNameInnerCoverage =>
                    ShaderStageIoBuiltIn.InnerCoverage,
                _ => throw ReflectionFailure(
                    request,
                    $"DXIL stage I/O {semanticName} has unsupported system-value "
                    + $"semantic value {(int)systemValue}."),
            };
        }

        private static ShaderAttachmentNumericClass MapStageIoNumericClass(
            ShaderCompileRequest request,
            string semanticName,
            D3DRegisterComponentType componentType)
        {
            return componentType switch
            {
                D3DRegisterComponentType.D3DRegisterComponentFloat16
                    or D3DRegisterComponentType.D3DRegisterComponentFloat32
                    or D3DRegisterComponentType.D3DRegisterComponentFloat64 =>
                        ShaderAttachmentNumericClass.FloatingPoint,
                D3DRegisterComponentType.D3DRegisterComponentSint16
                    or D3DRegisterComponentType.D3DRegisterComponentSint32
                    or D3DRegisterComponentType.D3DRegisterComponentSint64 =>
                        ShaderAttachmentNumericClass.SignedInteger,
                D3DRegisterComponentType.D3DRegisterComponentUint16
                    or D3DRegisterComponentType.D3DRegisterComponentUint32
                    or D3DRegisterComponentType.D3DRegisterComponentUint64 =>
                        ShaderAttachmentNumericClass.UnsignedInteger,
                _ => throw ReflectionFailure(
                    request,
                    $"DXIL stage I/O {semanticName} has unsupported component type "
                    + $"{componentType}."),
            };
        }

        private static uint FindFirstSetComponent(
            ShaderCompileRequest request,
            string semanticName,
            byte mask)
        {
            uint component = 0;
            while (component < 4 && (mask & (1 << (int)component)) == 0)
            {
                ++component;
            }

            if (component == 4)
            {
                throw ReflectionFailure(
                    request,
                    $"DXIL stage I/O {semanticName} has an empty component mask.");
            }

            return component;
        }

        private static uint CountContiguousComponents(
            ShaderCompileRequest request,
            string semanticName,
            byte mask,
            uint firstComponent)
        {
            uint componentCount = 0;
            uint component = firstComponent;
            while (component < 4 && (mask & (1 << (int)component)) != 0)
            {
                ++componentCount;
                ++component;
            }

            byte expectedMask = (byte)(((1u << (int)componentCount) - 1u)
                << (int)firstComponent);
            if ((mask & 0x0F) != expectedMask || (mask & 0xF0) != 0)
            {
                throw ReflectionFailure(
                    request,
                    $"DXIL stage I/O {semanticName} has non-contiguous component mask "
                    + $"0x{mask:X2}.");
            }

            return componentCount;
        }

        private static ReflectedConstantBuffer[] ReflectConstantBuffers(
            ShaderCompileRequest request,
            ReflectionScope scope,
            int constantBufferCount)
        {
            List<ReflectedConstantBuffer> buffers = new(constantBufferCount);
            for (int bufferIndex = 0; bufferIndex < constantBufferCount; ++bufferIndex)
            {
                ID3D12ShaderReflectionConstantBuffer* buffer =
                    scope.GetConstantBufferByIndex((uint)bufferIndex);
                if (buffer == null)
                {
                    throw ReflectionFailure(
                        request,
                        $"DXIL constant buffer {bufferIndex} has no reflection interface.");
                }

                ShaderBufferDesc bufferDescription = default;
                ThrowIfFailed(
                    request,
                    buffer->GetDesc(ref bufferDescription),
                    $"Failed to read DXIL constant buffer {bufferIndex}.");

                string name = ReadRequiredUtf8Name(
                    request,
                    bufferDescription.Name,
                    $"DXIL constant buffer {bufferIndex}");
                if (bufferDescription.Type is D3DCBufferType.D3D11CTInterfacePointers
                    or D3DCBufferType.D3D11CTResourceBindInfo)
                {
                    continue;
                }

                if (bufferDescription.Type is not D3DCBufferType.D3DCTCbuffer
                    and not D3DCBufferType.D3D11CTTbuffer)
                {
                    throw ReflectionFailure(
                        request,
                        $"DXIL buffer {name} has unsupported buffer type value {(int)bufferDescription.Type}.");
                }

                int variableCount = ToManagedCount(
                    request,
                    bufferDescription.Variables,
                    $"DXIL buffer {name} variable count");
                if (bufferDescription.Size == 0 || variableCount == 0)
                {
                    throw ReflectionFailure(
                        request,
                        $"DXIL buffer {name} does not expose a positive size and at least one variable.");
                }

                ShaderValueMember[] variables = new ShaderValueMember[variableCount];
                for (int variableIndex = 0; variableIndex < variableCount; ++variableIndex)
                {
                    ID3D12ShaderReflectionVariable* variable =
                        buffer->GetVariableByIndex((uint)variableIndex);
                    if (variable == null)
                    {
                        throw ReflectionFailure(
                            request,
                            $"DXIL buffer {name} variable {variableIndex} has no reflection interface.");
                    }

                    ShaderVariableDesc variableDescription = default;
                    ThrowIfFailed(
                        request,
                        variable->GetDesc(ref variableDescription),
                        $"Failed to read DXIL buffer {name} variable {variableIndex}.");
                    string variableName = ReadRequiredUtf8Name(
                        request,
                        variableDescription.Name,
                        $"DXIL buffer {name} variable {variableIndex}");
                    if (variableDescription.Size == 0)
                    {
                        throw ReflectionFailure(
                            request,
                            $"DXIL buffer {name} variable {variableName} has zero size.");
                    }

                    ValidateRange(
                        request,
                        $"DXIL buffer {name} variable {variableName}",
                        variableDescription.StartOffset,
                        variableDescription.Size,
                        bufferDescription.Size);

                    ID3D12ShaderReflectionType* variableType = variable->GetType();
                    if (variableType == null)
                    {
                        throw ReflectionFailure(
                            request,
                            $"DXIL buffer {name} variable {variableName} has no type reflection.");
                    }

                    ShaderValueLayout valueLayout = ReflectValueLayout(
                        request,
                        variableType,
                        variableDescription.Size,
                        byteSizeIsExact: true,
                        $"{name}.{variableName}",
                        depth: 0);
                    variables[variableIndex] = new ShaderValueMember(
                        variableName,
                        variableDescription.StartOffset,
                        variableDescription.Size,
                        valueLayout);
                }

                ShaderConstantBufferLayout layout = new(
                    name,
                    bufferDescription.Size,
                    variables);
                buffers.Add(new ReflectedConstantBuffer(
                    name,
                    bufferDescription.Type,
                    layout));
            }

            return buffers.ToArray();
        }

        private static ShaderValueLayout ReflectValueLayout(
            ShaderCompileRequest request,
            ID3D12ShaderReflectionType* reflectedType,
            uint availableByteSize,
            bool byteSizeIsExact,
            string path,
            int depth)
        {
            if (depth > MaximumTypeNestingDepth)
            {
                throw ReflectionFailure(
                    request,
                    $"DXIL value layout {path} exceeds the maximum supported nesting depth.");
            }

            ShaderTypeDesc typeDescription = default;
            ThrowIfFailed(
                request,
                reflectedType->GetDesc(ref typeDescription),
                $"Failed to read DXIL value type {path}.");

            ShaderArrayShape array = typeDescription.Elements == 0
                ? ShaderArrayShape.Scalar
                : new ShaderArrayShape(new[]
                {
                    ShaderArrayExtent.Bounded(typeDescription.Elements),
                });

            uint elementAvailableByteSize = availableByteSize;
            if (typeDescription.Elements != 0)
            {
                if (availableByteSize % typeDescription.Elements != 0)
                {
                    throw ReflectionFailure(
                        request,
                        $"DXIL value layout {path} size {availableByteSize} is not divisible by its array count {typeDescription.Elements}.");
                }

                elementAvailableByteSize = availableByteSize / typeDescription.Elements;
                if (elementAvailableByteSize == 0)
                {
                    throw ReflectionFailure(
                        request,
                        $"DXIL value layout {path} has a zero-sized array element.");
                }
            }

            switch (typeDescription.Class)
            {
                case D3DShaderVariableClass.D3DSvcScalar:
                    ValidateNumericShape(request, path, typeDescription, 1, 1);
                    return new ShaderValueLayout(
                        ShaderValueKind.Scalar,
                        MapScalarType(request, path, typeDescription.Type),
                        1,
                        1,
                        array,
                        byteSizeIsExact ? availableByteSize : null);

                case D3DShaderVariableClass.D3D10SvcVector:
                    if (typeDescription.Rows != 1 || typeDescription.Columns is < 2 or > 4)
                    {
                        throw ReflectionFailure(
                            request,
                            $"DXIL vector {path} has invalid shape {typeDescription.Rows}x{typeDescription.Columns}.");
                    }

                    return new ShaderValueLayout(
                        ShaderValueKind.Vector,
                        MapScalarType(request, path, typeDescription.Type),
                        typeDescription.Rows,
                        typeDescription.Columns,
                        array,
                        byteSizeIsExact ? availableByteSize : null);

                case D3DShaderVariableClass.D3DSvcMatrixRows:
                case D3DShaderVariableClass.D3DSvcMatrixColumns:
                    if (typeDescription.Rows is < 1 or > 4 || typeDescription.Columns is < 1 or > 4)
                    {
                        throw ReflectionFailure(
                            request,
                            $"DXIL matrix {path} has invalid shape {typeDescription.Rows}x{typeDescription.Columns}.");
                    }

                    return new ShaderValueLayout(
                        ShaderValueKind.Matrix,
                        MapScalarType(request, path, typeDescription.Type),
                        typeDescription.Rows,
                        typeDescription.Columns,
                        array,
                        byteSizeIsExact ? availableByteSize : null,
                        provenArrayStride: null,
                        provenMatrixStride: null,
                        matrixMajorOrder: typeDescription.Class == D3DShaderVariableClass.D3DSvcMatrixRows
                            ? ShaderMatrixMajorOrder.RowMajor
                            : ShaderMatrixMajorOrder.ColumnMajor);

                case D3DShaderVariableClass.D3DSvcStruct:
                    return ReflectStructValueLayout(
                        request,
                        reflectedType,
                        typeDescription,
                        availableByteSize,
                        elementAvailableByteSize,
                        byteSizeIsExact,
                        array,
                        path,
                        depth);

                case D3DShaderVariableClass.D3DSvcObject:
                case D3DShaderVariableClass.D3DSvcInterfaceClass:
                case D3DShaderVariableClass.D3DSvcInterfacePointer:
                    throw ReflectionFailure(
                        request,
                        $"DXIL constant-buffer value {path} has unsupported class {typeDescription.Class}.");

                default:
                    throw ReflectionFailure(
                        request,
                        $"DXIL constant-buffer value {path} has unknown class value {(int)typeDescription.Class}.");
            }
        }

        private static ShaderValueLayout ReflectStructValueLayout(
            ShaderCompileRequest request,
            ID3D12ShaderReflectionType* reflectedType,
            ShaderTypeDesc typeDescription,
            uint availableByteSize,
            uint elementAvailableByteSize,
            bool byteSizeIsExact,
            ShaderArrayShape array,
            string path,
            int depth)
        {
            int memberCount = ToManagedCount(
                request,
                typeDescription.Members,
                $"DXIL struct {path} member count");
            if (memberCount == 0)
            {
                throw ReflectionFailure(request, $"DXIL struct {path} contains no members.");
            }

            ReflectedTypeMember[] reflectedMembers = new ReflectedTypeMember[memberCount];
            for (int memberIndex = 0; memberIndex < memberCount; ++memberIndex)
            {
                ID3D12ShaderReflectionType* memberType =
                    reflectedType->GetMemberTypeByIndex((uint)memberIndex);
                if (memberType == null)
                {
                    throw ReflectionFailure(
                        request,
                        $"DXIL struct {path} member {memberIndex} has no type reflection.");
                }

                string memberName = ReadRequiredUtf8Name(
                    request,
                    reflectedType->GetMemberTypeName((uint)memberIndex),
                    $"DXIL struct {path} member {memberIndex}");
                ShaderTypeDesc memberDescription = default;
                ThrowIfFailed(
                    request,
                    memberType->GetDesc(ref memberDescription),
                    $"Failed to read DXIL struct member {path}.{memberName}.");

                reflectedMembers[memberIndex] = new ReflectedTypeMember(
                    memberName,
                    memberDescription.Offset,
                    memberType);
            }

            System.Array.Sort(reflectedMembers, static (left, right) =>
            {
                int offset = left.Offset.CompareTo(right.Offset);
                return offset != 0 ? offset : string.CompareOrdinal(left.Name, right.Name);
            });

            ShaderValueMember[] members = new ShaderValueMember[memberCount];
            for (int memberIndex = 0; memberIndex < memberCount; ++memberIndex)
            {
                ReflectedTypeMember member = reflectedMembers[memberIndex];
                uint end = memberIndex + 1 < memberCount
                    ? reflectedMembers[memberIndex + 1].Offset
                    : elementAvailableByteSize;
                if (member.Offset >= end || end > elementAvailableByteSize)
                {
                    throw ReflectionFailure(
                        request,
                        $"DXIL struct member {path}.{member.Name} has invalid byte range [{member.Offset}, {end}) within {elementAvailableByteSize} bytes.");
                }

                uint memberSpan = end - member.Offset;
                ShaderValueLayout memberValue = ReflectValueLayout(
                    request,
                    member.Type,
                    memberSpan,
                    byteSizeIsExact: false,
                    $"{path}.{member.Name}",
                    depth + 1);
                members[memberIndex] = new ShaderValueMember(
                    member.Name,
                    member.Offset,
                    memberSpan,
                    memberValue);
            }

            return new ShaderValueLayout(
                ShaderValueKind.Struct,
                scalarType: null,
                rows: 0,
                columns: 0,
                array,
                byteSizeIsExact ? availableByteSize : null,
                provenArrayStride: null,
                provenMatrixStride: null,
                matrixMajorOrder: null,
                members);
        }

        private static ShaderScalarType MapScalarType(
            ShaderCompileRequest request,
            string path,
            D3DShaderVariableType type)
        {
            return type switch
            {
                D3DShaderVariableType.D3DSvtBool => ShaderScalarType.Boolean,
                D3DShaderVariableType.D3DSvtInt => ShaderScalarType.Int32,
                D3DShaderVariableType.D3DSvtFloat => ShaderScalarType.Float32,
                D3DShaderVariableType.D3D10SvtUint => ShaderScalarType.UInt32,
                D3DShaderVariableType.D3D10SvtUint8 => ShaderScalarType.UInt8,
                D3DShaderVariableType.D3DSvtDouble => ShaderScalarType.Float64,
                D3DShaderVariableType.D3DSvtInt16 => ShaderScalarType.Int16,
                D3DShaderVariableType.D3DSvtUint16 => ShaderScalarType.UInt16,
                D3DShaderVariableType.D3DSvtFloat16 => ShaderScalarType.Float16,
                D3DShaderVariableType.D3DSvtInt64 => ShaderScalarType.Int64,
                D3DShaderVariableType.D3DSvtUint64 => ShaderScalarType.UInt64,
                D3DShaderVariableType.D3DSvtMin8float or
                D3DShaderVariableType.D3DSvtMin10float or
                D3DShaderVariableType.D3DSvtMin16float or
                D3DShaderVariableType.D3DSvtMin12int or
                D3DShaderVariableType.D3DSvtMin16int or
                D3DShaderVariableType.D3DSvtMin16Uint =>
                    throw ReflectionFailure(
                        request,
                        $"DXIL value {path} uses minimum-precision scalar type {type}, whose physical width is not proven by D3D12 reflection."),
                _ => throw ReflectionFailure(
                    request,
                    $"DXIL value {path} has unsupported scalar type value {(int)type}."),
            };
        }

        private static ShaderArrayShape MapDescriptorArray(
            ShaderCompileRequest request,
            string name,
            uint bindPoint,
            uint bindCount)
        {
            if (bindCount == DxcRuntimeDescriptorArrayBindCount)
            {
                return new ShaderArrayShape(new[] { ShaderArrayExtent.Runtime() });
            }

            if (bindCount == 1)
            {
                return ShaderArrayShape.Scalar;
            }

            try
            {
                _ = checked(bindPoint + bindCount - 1);
            }
            catch (OverflowException ex)
            {
                throw ReflectionFailure(
                    request,
                    $"DXIL resource {name} range [{bindPoint}, +{bindCount}) overflows the register namespace.",
                    ex);
            }

            return new ShaderArrayShape(new[] { ShaderArrayExtent.Bounded(bindCount) });
        }

        private static ShaderResourceDimension MapDimension(
            ShaderCompileRequest request,
            string resourceName,
            D3DSrvDimension dimension)
        {
            return dimension switch
            {
                D3DSrvDimension.D3D11SrvDimensionBuffer or
                D3DSrvDimension.D3DSrvDimensionBufferex =>
                    ShaderResourceDimension.Buffer,
                D3DSrvDimension.D3DSrvDimensionTexture1D =>
                    ShaderResourceDimension.Texture1D,
                D3DSrvDimension.D3D101SrvDimensionTexture1Darray =>
                    ShaderResourceDimension.Texture1DArray,
                D3DSrvDimension.D3D11SrvDimensionTexture2D =>
                    ShaderResourceDimension.Texture2D,
                D3DSrvDimension.D3D11SrvDimensionTexture2Darray =>
                    ShaderResourceDimension.Texture2DArray,
                D3DSrvDimension.D3D11SrvDimensionTexture2Dms =>
                    ShaderResourceDimension.Texture2DMultisampled,
                D3DSrvDimension.D3D101SrvDimensionTexture2Dmsarray =>
                    ShaderResourceDimension.Texture2DMultisampledArray,
                D3DSrvDimension.D3D11SrvDimensionTexture3D =>
                    ShaderResourceDimension.Texture3D,
                D3DSrvDimension.D3DSrvDimensionTexturecube =>
                    ShaderResourceDimension.TextureCube,
                D3DSrvDimension.D3D11SrvDimensionTexturecubearray =>
                    ShaderResourceDimension.TextureCubeArray,
                D3DSrvDimension.D3D11SrvDimensionUnknown =>
                    throw ReflectionFailure(
                        request,
                        $"DXIL resource {resourceName} has an unknown resource dimension."),
                _ => throw ReflectionFailure(
                    request,
                    $"DXIL resource {resourceName} has unsupported dimension value {(int)dimension}."),
            };
        }

        private static ShaderResourceDimension MapTextureDimension(
            ShaderCompileRequest request,
            string resourceName,
            D3DSrvDimension dimension)
        {
            ShaderResourceDimension mapped = MapDimension(request, resourceName, dimension);
            if (mapped == ShaderResourceDimension.Buffer)
            {
                throw ReflectionFailure(
                    request,
                    $"DXIL resource {resourceName} requires a texture dimension, but reflection reports a buffer.");
            }

            return mapped;
        }

        private static ShaderExecutionStage MapExecutionStage(
            ShaderCompileRequest request,
            uint encodedVersion)
        {
            uint shaderKind = encodedVersion >> 16;
            return shaderKind switch
            {
                0 => ShaderExecutionStage.Pixel,
                1 => ShaderExecutionStage.Vertex,
                2 => ShaderExecutionStage.Geometry,
                3 => ShaderExecutionStage.Hull,
                4 => ShaderExecutionStage.Domain,
                5 => ShaderExecutionStage.Compute,
                7 => ShaderExecutionStage.RayGeneration,
                8 => ShaderExecutionStage.Intersection,
                9 => ShaderExecutionStage.AnyHit,
                10 => ShaderExecutionStage.ClosestHit,
                11 => ShaderExecutionStage.Miss,
                12 => ShaderExecutionStage.Callable,
                13 => ShaderExecutionStage.Mesh,
                14 => ShaderExecutionStage.Amplification,
                15 => ShaderExecutionStage.Node,
                6 => throw ReflectionFailure(
                    request,
                    "DXIL library reflection exposed a plain library function without a concrete shader stage."),
                _ => throw ReflectionFailure(
                    request,
                    $"DXIL reflection contains unknown shader-kind value {shaderKind}."),
            };
        }

        private static ShaderExecutionStage MapRequestedStage(ShaderCompileRequest request)
        {
            return request.Stage switch
            {
                ShaderStageKind.Vertex => ShaderExecutionStage.Vertex,
                ShaderStageKind.Hull => ShaderExecutionStage.Hull,
                ShaderStageKind.Domain => ShaderExecutionStage.Domain,
                ShaderStageKind.Geometry => ShaderExecutionStage.Geometry,
                ShaderStageKind.Pixel => ShaderExecutionStage.Pixel,
                ShaderStageKind.Compute => ShaderExecutionStage.Compute,
                ShaderStageKind.Amplification => ShaderExecutionStage.Amplification,
                ShaderStageKind.Mesh => ShaderExecutionStage.Mesh,
                ShaderStageKind.Library => throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.InvalidRequest,
                    "Library reflection must use the DXIL library reflection path.",
                    requestedProfile: BuildRequestedProfile(request)),
                _ => throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.InvalidRequest,
                    $"Shader stage {request.Stage} is not supported for DXIL reflection.",
                    requestedProfile: BuildRequestedProfile(request)),
            };
        }

        private static string DecodeLibraryEntryPointName(
            ShaderCompileRequest request,
            string reflectedName)
        {
            if (IsHlslIdentifier(reflectedName))
            {
                return reflectedName;
            }

            if (reflectedName.Length >= 5
                && reflectedName[0] == '\u0001'
                && reflectedName[1] == '?')
            {
                int terminator = reflectedName.IndexOf("@@", 2, StringComparison.Ordinal);
                if (terminator > 2)
                {
                    string candidate = reflectedName.Substring(2, terminator - 2);
                    if (IsHlslIdentifier(candidate))
                    {
                        return candidate;
                    }
                }
            }

            throw ReflectionFailure(
                request,
                $"DXIL library function name '{EscapeForDiagnostic(reflectedName)}' cannot be decoded into a proven HLSL entry-point identifier.");
        }

        private static bool IsHlslIdentifier(string value)
        {
            if (value.Length == 0 || !(value[0] == '_' || IsAsciiLetter(value[0])))
            {
                return false;
            }

            for (int index = 1; index < value.Length; ++index)
            {
                char character = value[index];
                if (!(character == '_' || IsAsciiLetter(character) || (character >= '0' && character <= '9')))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsAsciiLetter(char value)
        {
            return (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');
        }

        private static string EscapeForDiagnostic(string value)
        {
            return value
                .Replace("\0", "\\0", StringComparison.Ordinal)
                .Replace("\u0001", "\\u0001", StringComparison.Ordinal);
        }

        private static void ValidateResourceFlags(
            ShaderCompileRequest request,
            string name,
            ShaderInputBindDesc binding)
        {
            if ((binding.UFlags & ~KnownShaderInputFlags) != 0)
            {
                throw ReflectionFailure(
                    request,
                    $"DXIL resource {name} contains unsupported input flags 0x{binding.UFlags:X8}.");
            }

            bool comparisonSampler =
                (binding.UFlags & (uint)D3DShaderInputFlags.D3D10SifComparisonSampler) != 0;
            if (comparisonSampler && binding.Type != D3DShaderInputType.D3DSitSampler)
            {
                throw ReflectionFailure(
                    request,
                    $"DXIL resource {name} sets the comparison-sampler flag for input type {binding.Type}.");
            }
        }

        private static uint ValidateStructureStride(
            ShaderCompileRequest request,
            string resourceName,
            uint reflectedStride)
        {
            if (reflectedStride == 0 || reflectedStride == uint.MaxValue)
            {
                throw ReflectionFailure(
                    request,
                    $"DXIL structured buffer {resourceName} has invalid reflected stride {reflectedStride}.");
            }

            return reflectedStride;
        }

        private static void ValidateBufferDimension(
            ShaderCompileRequest request,
            string resourceName,
            D3DSrvDimension dimension)
        {
            ValidateDimension(
                request,
                resourceName,
                dimension,
                D3DSrvDimension.D3D11SrvDimensionBuffer,
                D3DSrvDimension.D3DSrvDimensionBufferex);
        }

        private static void ValidateDimension(
            ShaderCompileRequest request,
            string resourceName,
            D3DSrvDimension actual,
            params D3DSrvDimension[] expected)
        {
            foreach (D3DSrvDimension allowed in expected)
            {
                if (actual == allowed)
                {
                    return;
                }
            }

            throw ReflectionFailure(
                request,
                $"DXIL resource {resourceName} reports incompatible dimension {actual}.");
        }

        private static void ValidateNumericShape(
            ShaderCompileRequest request,
            string path,
            ShaderTypeDesc type,
            uint expectedRows,
            uint expectedColumns)
        {
            if (type.Rows != expectedRows || type.Columns != expectedColumns)
            {
                throw ReflectionFailure(
                    request,
                    $"DXIL value {path} has invalid scalar shape {type.Rows}x{type.Columns}.");
            }
        }

        private static void ValidateRange(
            ShaderCompileRequest request,
            string context,
            uint offset,
            uint size,
            uint containingSize)
        {
            uint end;
            try
            {
                end = checked(offset + size);
            }
            catch (OverflowException ex)
            {
                throw ReflectionFailure(
                    request,
                    $"{context} byte range overflows UInt32.",
                    ex);
            }

            if (size == 0 || end > containingSize)
            {
                throw ReflectionFailure(
                    request,
                    $"{context} byte range [{offset}, {end}) exceeds its {containingSize}-byte container.");
            }
        }

        private static string ReadRequiredUtf8Name(
            ShaderCompileRequest request,
            byte* pointer,
            string context)
        {
            if (pointer == null)
            {
                throw ReflectionFailure(request, $"{context} has a null name.");
            }

            int byteLength = 0;
            while (byteLength < MaximumUtf8NameByteLength && pointer[byteLength] != 0)
            {
                ++byteLength;
            }

            if (byteLength == MaximumUtf8NameByteLength)
            {
                throw ReflectionFailure(
                    request,
                    $"{context} exceeds the maximum supported UTF-8 name length of {MaximumUtf8NameByteLength - 1} bytes.");
            }

            string value;
            try
            {
                value = s_StrictUtf8.GetString(new ReadOnlySpan<byte>(pointer, byteLength));
            }
            catch (DecoderFallbackException ex)
            {
                throw ReflectionFailure(request, $"{context} is not valid UTF-8.", ex);
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                throw ReflectionFailure(request, $"{context} has an empty name.");
            }

            return value;
        }

        private static int ToManagedCount(
            ShaderCompileRequest request,
            uint value,
            string context)
        {
            if (value > int.MaxValue)
            {
                throw ReflectionFailure(
                    request,
                    $"{context} {value} exceeds managed collection limits.");
            }

            return (int)value;
        }

        private static void ThrowIfFailed(
            ShaderCompileRequest request,
            int hresult,
            string message)
        {
            if (hresult < 0)
            {
                throw ReflectionFailure(
                    request,
                    $"{message} HRESULT=0x{hresult:X8}");
            }
        }

        private static ComPtr<IDxcUtils> CreateDxcUtils()
        {
            Guid clsid = s_ClsidDxcUtils;
            Guid iid = IDxcUtils.Guid;
            int hresult = NativeDxcCreateInstance(ref clsid, ref iid, out nint instance);
            if (hresult < 0 || instance == 0)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.BackendUnavailable,
                    $"DXC reflection backend initialization failed. HRESULT=0x{hresult:X8}");
            }

            return new ComPtr<IDxcUtils>((IDxcUtils*)instance);
        }

        private static ComPtr<T> CreateReflection<T>(
            ShaderCompileRequest request,
            ComPtr<IDxcUtils> utils,
            DxcBuffer* reflectionBuffer,
            Guid iid,
            string interfaceName)
            where T : unmanaged, IComVtbl<T>
        {
            ComPtr<T> reflection = default;
            int hresult = utils.Get().CreateReflection(
                reflectionBuffer,
                ref iid,
                (void**)reflection.GetAddressOf());
            if (hresult < 0 || reflection.Handle == null)
            {
                reflection.Dispose();
                throw ReflectionFailure(
                    request,
                    $"DXC could not create {interfaceName} from the artifact reflection data. HRESULT=0x{hresult:X8}");
            }

            return reflection;
        }

        private static ShaderCompilerException ReflectionFailure(
            ShaderCompileRequest request,
            string message,
            Exception? innerException = null)
        {
            return new ShaderCompilerException(
                ShaderCompilerErrorCode.CompileFailed,
                message,
                requestedProfile: BuildRequestedProfile(request),
                innerException: innerException);
        }

        private static string BuildRequestedProfile(ShaderCompileRequest request)
        {
            string stagePrefix = request.Stage switch
            {
                ShaderStageKind.Vertex => "vs",
                ShaderStageKind.Hull => "hs",
                ShaderStageKind.Domain => "ds",
                ShaderStageKind.Geometry => "gs",
                ShaderStageKind.Pixel => "ps",
                ShaderStageKind.Compute => "cs",
                ShaderStageKind.Amplification => "as",
                ShaderStageKind.Mesh => "ms",
                ShaderStageKind.Library => "lib",
                _ => "unknown",
            };

            return $"{stagePrefix}_{request.ShaderModel.Major}_{request.ShaderModel.Minor}";
        }

        private readonly struct ReflectionScope
        {
            private readonly ID3D12ShaderReflection* m_Shader;
            private readonly ID3D12FunctionReflection* m_Function;

            public ReflectionScope(ID3D12ShaderReflection* shader)
            {
                m_Shader = shader;
                m_Function = null;
            }

            public ReflectionScope(ID3D12FunctionReflection* function)
            {
                m_Shader = null;
                m_Function = function;
            }

            public int GetResourceBindingDescription(
                uint index,
                ref ShaderInputBindDesc description)
            {
                return m_Shader != null
                    ? m_Shader->GetResourceBindingDesc(index, ref description)
                    : m_Function->GetResourceBindingDesc(index, ref description);
            }

            public ID3D12ShaderReflectionConstantBuffer* GetConstantBufferByIndex(uint index)
            {
                return m_Shader != null
                    ? m_Shader->GetConstantBufferByIndex(index)
                    : m_Function->GetConstantBufferByIndex(index);
            }
        }

        private readonly struct ReflectedConstantBuffer
        {
            public string Name { get; }
            public D3DCBufferType Type { get; }
            public ShaderConstantBufferLayout Layout { get; }

            public ReflectedConstantBuffer(
                string name,
                D3DCBufferType type,
                ShaderConstantBufferLayout layout)
            {
                Name = name;
                Type = type;
                Layout = layout;
            }
        }

        private readonly struct ReflectedTypeMember
        {
            public string Name { get; }
            public uint Offset { get; }
            public ID3D12ShaderReflectionType* Type { get; }

            public ReflectedTypeMember(
                string name,
                uint offset,
                ID3D12ShaderReflectionType* type)
            {
                Name = name;
                Offset = offset;
                Type = type;
            }
        }
    }
}
