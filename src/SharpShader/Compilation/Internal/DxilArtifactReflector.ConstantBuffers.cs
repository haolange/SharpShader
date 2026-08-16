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
}
}
