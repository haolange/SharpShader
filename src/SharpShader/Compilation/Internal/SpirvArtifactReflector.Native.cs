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

        private static uint GetDeclaredStructSize(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            Compiler* compiler,
            CrossType* type,
            string path)
        {
            nuint size = 0;
            ThrowIfFailed(
                request,
                cross,
                context,
                cross.CompilerGetDeclaredStructSize(
                    compiler,
                    type,
                    &size),
                $"Failed to read declared struct size for {path}.");
            return ToPositiveUInt32(request, size, $"{path} byte size");
        }

        private static uint GetDeclaredStructMemberSize(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            Compiler* compiler,
            CrossType* type,
            uint memberIndex,
            string path)
        {
            nuint size = 0;
            ThrowIfFailed(
                request,
                cross,
                context,
                cross.CompilerGetDeclaredStructMemberSize(
                    compiler,
                    type,
                    memberIndex,
                    &size),
                $"Failed to read declared member size for {path}.");
            return ToPositiveUInt32(request, size, $"{path} byte size");
        }

        private static uint GetStructMemberOffset(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            Compiler* compiler,
            CrossType* type,
            uint memberIndex,
            string path)
        {
            uint offset = 0;
            ThrowIfFailed(
                request,
                cross,
                context,
                cross.CompilerTypeStructMemberOffset(
                    compiler,
                    type,
                    memberIndex,
                    &offset),
                $"Failed to read member offset for {path}.");
            return offset;
        }

        private static CrossType* RequireTypeHandle(
            ShaderCompileRequest request,
            Cross cross,
            Compiler* compiler,
            uint typeId,
            string path)
        {
            if (typeId == 0)
            {
                throw ReflectionFailure(
                    request,
                    $"{path} references invalid zero type ID.");
            }

            CrossType* type = cross.CompilerGetTypeHandle(
                compiler,
                typeId);
            if (type == null)
            {
                throw ReflectionFailure(
                    request,
                    $"{path} references missing type ID {typeId}.");
            }

            return type;
        }

        private static string ReadResourceName(
            Cross cross,
            Compiler* compiler,
            NativeResource resource)
        {
            string? name = Marshal.PtrToStringUTF8(
                (IntPtr)resource.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = cross.CompilerGetNameS(
                    compiler,
                    resource.Id);
            }

            return string.IsNullOrWhiteSpace(name)
                ? $"spirv_resource_{resource.Id}"
                : name;
        }

        private static string ReadMemberName(
            Cross cross,
            Compiler* compiler,
            uint parentTypeId,
            uint memberIndex)
        {
            string name = cross.CompilerGetMemberNameS(
                compiler,
                parentTypeId,
                memberIndex);
            return string.IsNullOrWhiteSpace(name)
                ? $"member_{memberIndex}"
                : name;
        }

        private static string ReadRequiredName(
            ShaderCompileRequest request,
            byte* pointer,
            string description)
        {
            string? name = Marshal.PtrToStringUTF8(
                (IntPtr)pointer);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw ReflectionFailure(
                    request,
                    $"{description} has no UTF-8 name.");
            }

            return name;
        }

        private static ShaderExecutionStage MapExecutionStage(
            ShaderCompileRequest request,
            ExecutionModel model)
        {
            if (model == ExecutionModel.Vertex)
            {
                return ShaderExecutionStage.Vertex;
            }

            if (model == ExecutionModel.TessellationControl)
            {
                return ShaderExecutionStage.Hull;
            }

            if (model == ExecutionModel.TessellationEvaluation)
            {
                return ShaderExecutionStage.Domain;
            }

            if (model == ExecutionModel.Geometry)
            {
                return ShaderExecutionStage.Geometry;
            }

            if (model == ExecutionModel.Fragment)
            {
                return ShaderExecutionStage.Pixel;
            }

            if (model == ExecutionModel.GLCompute)
            {
                return ShaderExecutionStage.Compute;
            }

            if (model == ExecutionModel.TaskNV || model == ExecutionModel.TaskExt)
            {
                return ShaderExecutionStage.Amplification;
            }

            if (model == ExecutionModel.MeshNV || model == ExecutionModel.MeshExt)
            {
                return ShaderExecutionStage.Mesh;
            }

            if (model == ExecutionModel.RayGenerationKhr
                || model == ExecutionModel.RayGenerationNV)
            {
                return ShaderExecutionStage.RayGeneration;
            }

            if (model == ExecutionModel.IntersectionKhr
                || model == ExecutionModel.IntersectionNV)
            {
                return ShaderExecutionStage.Intersection;
            }

            if (model == ExecutionModel.AnyHitKhr
                || model == ExecutionModel.AnyHitNV)
            {
                return ShaderExecutionStage.AnyHit;
            }

            if (model == ExecutionModel.ClosestHitKhr
                || model == ExecutionModel.ClosestHitNV)
            {
                return ShaderExecutionStage.ClosestHit;
            }

            if (model == ExecutionModel.MissKhr
                || model == ExecutionModel.MissNV)
            {
                return ShaderExecutionStage.Miss;
            }

            if (model == ExecutionModel.CallableKhr
                || model == ExecutionModel.CallableNV)
            {
                return ShaderExecutionStage.Callable;
            }

            throw ReflectionFailure(
                request,
                $"SPIR-V execution model {model} is unsupported.");
        }

        private static int ToManagedCount(
            ShaderCompileRequest request,
            nuint value,
            string description)
        {
            if (value > int.MaxValue)
            {
                throw ReflectionFailure(
                    request,
                    $"{description} {value} exceeds the managed collection limit {int.MaxValue}.");
            }

            return checked((int)value);
        }

        private static int ToManagedCount(
            ShaderCompileRequest request,
            uint value,
            string description)
        {
            if (value > int.MaxValue)
            {
                throw ReflectionFailure(
                    request,
                    $"{description} {value} exceeds the managed collection limit {int.MaxValue}.");
            }

            return checked((int)value);
        }

        private static uint ToPositiveUInt32(
            ShaderCompileRequest request,
            nuint value,
            string description)
        {
            if (value == 0 || value > uint.MaxValue)
            {
                throw ReflectionFailure(
                    request,
                    $"{description} {value} is outside the supported positive UInt32 range.");
            }

            return checked((uint)value);
        }

        private static void ThrowIfFailed(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            CrossResult result,
            string message)
        {
            if (result == CrossResult.Success)
            {
                return;
            }

            string detail = context == null
                ? string.Empty
                : cross.ContextGetLastErrorStringS(context) ?? string.Empty;
            throw ReflectionFailure(
                request,
                string.IsNullOrWhiteSpace(detail)
                    ? message
                    : $"{message} {detail}");
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

        private static string BuildRequestedProfile(
            ShaderCompileRequest request)
        {
            return $"{request.Stage}:{request.ShaderModel}";
        }

        private readonly record struct ReflectedEntryPoint(
            string Name,
            ExecutionModel ExecutionModel);

        private readonly record struct ValueDecorations(
            uint? ArrayStride,
            uint? MatrixStride,
            ShaderMatrixMajorOrder? MatrixMajorOrder);
}
}
