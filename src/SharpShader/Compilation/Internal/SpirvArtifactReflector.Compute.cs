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

        private static void ValidateComputeStageInterface(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            Compiler* compiler,
            Resources* resources)
        {
            ValidateComputeStageIoResources(
                request,
                cross,
                context,
                compiler,
                resources,
                ResourceType.StageInput);
            ValidateComputeStageIoResources(
                request,
                cross,
                context,
                compiler,
                resources,
                ResourceType.StageOutput);
            ValidateComputeBuiltInResources(
                request,
                cross,
                context,
                compiler,
                resources,
                BuiltinResourceType.StageInput);
            ValidateComputeBuiltInResources(
                request,
                cross,
                context,
                compiler,
                resources,
                BuiltinResourceType.StageOutput);
        }

        private static void ValidateComputeStageIoResources(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            Compiler* compiler,
            Resources* resources,
            ResourceType resourceType)
        {
            NativeResource* nativeResources = null;
            nuint countValue = 0;
            ThrowIfFailed(
                request,
                cross,
                context,
                cross.ResourcesGetResourceListForType(
                    resources,
                    resourceType,
                    &nativeResources,
                    &countValue),
                $"Failed to enumerate SPIR-V {resourceType} compute stage I/O.");
            int count = ToManagedCount(
                request,
                countValue,
                $"SPIR-V {resourceType} compute stage-I/O count");
            for (int index = 0; index < count; ++index)
            {
                NativeResource resource = nativeResources[index];
                string name = ReadResourceName(cross, compiler, resource);
                if (cross.CompilerHasDecoration(
                        compiler,
                        resource.Id,
                        Decoration.Location) != 0)
                {
                    throw ReflectionFailure(
                        request,
                        $"SPIR-V compute stage I/O {name} has a Location "
                        + "decoration; compute execution values must be BuiltIn-only.");
                }

                bool hasBuiltIn = cross.CompilerHasDecoration(
                    compiler,
                    resource.Id,
                    Decoration.BuiltIn) != 0;
                if (!hasBuiltIn)
                {
                    throw ReflectionFailure(
                        request,
                        $"SPIR-V compute stage I/O {name} is not a BuiltIn "
                        + "execution value.");
                }

                BuiltIn builtIn = (BuiltIn)cross.CompilerGetDecoration(
                    compiler,
                    resource.Id,
                    Decoration.BuiltIn);
                ValidateComputeExecutionBuiltIn(request, name, builtIn);
            }
        }

        private static void ValidateComputeBuiltInResources(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            Compiler* compiler,
            Resources* resources,
            BuiltinResourceType resourceType)
        {
            ReflectedBuiltinResource* nativeResources = null;
            nuint countValue = 0;
            ThrowIfFailed(
                request,
                cross,
                context,
                cross.ResourcesGetBuiltinResourceListForType(
                    resources,
                    resourceType,
                    &nativeResources,
                    &countValue),
                $"Failed to enumerate SPIR-V {resourceType} compute built-in variables.");
            int count = ToManagedCount(
                request,
                countValue,
                $"SPIR-V {resourceType} compute built-in variable count");
            for (int index = 0; index < count; ++index)
            {
                ReflectedBuiltinResource resource = nativeResources[index];
                string name = resource.Resource.Name is null
                    || resource.Resource.Name[0] == 0
                        ? $"BuiltIn.{resource.Builtin}"
                        : ReadRequiredName(
                            request,
                            resource.Resource.Name,
                            $"SPIR-V {resourceType} built-in {resource.Builtin}");
                if (cross.CompilerHasDecoration(
                        compiler,
                        resource.Resource.Id,
                        Decoration.Location) != 0)
                {
                    throw ReflectionFailure(
                        request,
                        $"SPIR-V compute stage I/O {name} has a Location "
                        + "decoration; compute execution values must be BuiltIn-only.");
                }

                ValidateComputeExecutionBuiltIn(
                    request,
                    name,
                    resource.Builtin);
            }
        }

        private static void ValidateComputeExecutionBuiltIn(
            ShaderCompileRequest request,
            string name,
            BuiltIn builtIn)
        {
            if (IsComputeExecutionBuiltIn(builtIn))
            {
                return;
            }

            throw ReflectionFailure(
                request,
                $"SPIR-V compute stage I/O {name} has BuiltIn {builtIn}, which "
                + "is not a recognized compute execution built-in.");
        }

        private static bool IsComputeExecutionBuiltIn(BuiltIn builtIn)
        {
            return builtIn is BuiltIn.NumWorkgroups
                or BuiltIn.WorkgroupSize
                or BuiltIn.WorkgroupId
                or BuiltIn.LocalInvocationId
                or BuiltIn.GlobalInvocationId
                or BuiltIn.LocalInvocationIndex;
        }
}
}
