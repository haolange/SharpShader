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
}
}
