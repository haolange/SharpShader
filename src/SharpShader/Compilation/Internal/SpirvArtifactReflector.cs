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
    internal static unsafe class SpirvArtifactReflector
    {
        private const int MaximumTypeNestingDepth = 64;

        private static readonly ResourceType[] s_SupportedResourceTypes =
        {
            ResourceType.UniformBuffer,
            ResourceType.StorageBuffer,
            ResourceType.SubpassInput,
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

                reflected[index] = new ShaderEntryPointReflection(
                    entryPoint.Name,
                    stage,
                    reflectedResources,
                    threadGroupSize);
            }

            return new ShaderArtifactReflection(
                ShaderArtifactKind.SpirV,
                reflected);
        }

        private static IReadOnlyDictionary<uint, uint> ReflectSpecializationConstants(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            Compiler* compiler)
        {
            NativeSpecializationConstant* constants = null;
            nuint countValue = 0;
            ThrowIfFailed(
                request,
                cross,
                context,
                cross.CompilerGetSpecializationConstants(
                    compiler,
                    &constants,
                    &countValue),
                "Failed to enumerate SPIR-V specialization constants.");

            int count = ToManagedCount(
                request,
                countValue,
                "SPIR-V specialization-constant count");
            Dictionary<uint, uint> result = new Dictionary<uint, uint>(count);
            for (int index = 0; index < count; ++index)
            {
                NativeSpecializationConstant constant = constants[index];
                if (constant.Id == 0)
                {
                    throw ReflectionFailure(
                        request,
                        $"SPIR-V specialization constant {index} has an invalid zero result ID.");
                }

                if (!result.TryAdd(constant.Id, constant.ConstantId))
                {
                    throw ReflectionFailure(
                        request,
                        $"SPIR-V specialization constant result ID {constant.Id} is duplicated.");
                }
            }

            return result;
        }

        private static void ValidateUnsupportedResources(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            Resources* resources,
            string entryPointName)
        {
            foreach (ResourceType resourceType in s_UnsupportedResourceTypes)
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
                    $"Failed to inspect SPIR-V {resourceType} resources.");

                if (countValue != 0)
                {
                    throw ReflectionFailure(
                        request,
                        $"SPIR-V entry point {entryPointName} contains unsupported resource class {resourceType}.");
                }
            }
        }

        private static ShaderResourceBindingReflection[] ReflectResources(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            Compiler* compiler,
            Resources* resources,
            ShaderStageMask stages,
            IReadOnlyDictionary<uint, uint> specializationConstants)
        {
            List<ShaderResourceBindingReflection> reflected = new();
            foreach (ResourceType resourceType in s_SupportedResourceTypes)
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
                    $"Failed to enumerate SPIR-V {resourceType} resources.");

                int count = ToManagedCount(
                    request,
                    countValue,
                    $"SPIR-V {resourceType} resource count");
                for (int index = 0; index < count; ++index)
                {
                    reflected.Add(ReflectResource(
                        request,
                        cross,
                        context,
                        compiler,
                        resourceType,
                        nativeResources[index],
                        stages,
                        specializationConstants));
                }
            }

            return reflected.ToArray();
        }

        private static ShaderResourceBindingReflection ReflectResource(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            Compiler* compiler,
            ResourceType resourceType,
            NativeResource resource,
            ShaderStageMask stages,
            IReadOnlyDictionary<uint, uint> specializationConstants)
        {
            string name = ReadResourceName(cross, compiler, resource);
            if (cross.CompilerHasDecoration(
                    compiler,
                    resource.Id,
                    Decoration.DescriptorSet) == 0)
            {
                throw ReflectionFailure(
                    request,
                    $"SPIR-V resource {name} has no DescriptorSet decoration.");
            }

            if (cross.CompilerHasDecoration(
                    compiler,
                    resource.Id,
                    Decoration.Binding) == 0)
            {
                throw ReflectionFailure(
                    request,
                    $"SPIR-V resource {name} has no Binding decoration.");
            }

            uint descriptorSet = cross.CompilerGetDecoration(
                compiler,
                resource.Id,
                Decoration.DescriptorSet);
            uint binding = cross.CompilerGetDecoration(
                compiler,
                resource.Id,
                Decoration.Binding);
            CrossType* resourceTypeHandle = RequireTypeHandle(
                request,
                cross,
                compiler,
                resource.TypeId,
                $"SPIR-V resource {name}");
            ShaderArrayShape array = ReflectArrayShape(
                request,
                cross,
                resourceTypeHandle,
                specializationConstants,
                $"SPIR-V resource {name}");

            ShaderBindingClass bindingClass;
            ShaderResourceKind kind;
            ShaderResourceDimension dimension;
            ShaderResourceAccess access;
            uint? structureStride = null;
            ShaderSamplerKind? samplerKind = null;
            ShaderConstantBufferLayout? constantBufferLayout = null;

            switch (resourceType)
            {
                case ResourceType.UniformBuffer:
                    bindingClass = ShaderBindingClass.ConstantBuffer;
                    kind = ShaderResourceKind.ConstantBuffer;
                    dimension = ShaderResourceDimension.Buffer;
                    access = ShaderResourceAccess.ReadOnly;
                    constantBufferLayout = ReflectConstantBuffer(
                        request,
                        cross,
                        context,
                        compiler,
                        resource.BaseTypeId,
                        name,
                        specializationConstants);
                    break;

                case ResourceType.StorageBuffer:
                    access = ReflectAccess(
                        request,
                        cross,
                        compiler,
                        resource,
                        resourceTypeHandle,
                        name);
                    bindingClass = access == ShaderResourceAccess.ReadOnly
                        ? ShaderBindingClass.ShaderResource
                        : ShaderBindingClass.UnorderedAccess;
                    kind = ShaderResourceKind.StorageBuffer;
                    dimension = ShaderResourceDimension.Buffer;
                    structureStride = ReflectStorageBufferStride(
                        request,
                        cross,
                        context,
                        compiler,
                        resource.BaseTypeId,
                        name);
                    break;

                case ResourceType.SubpassInput:
                    bindingClass = ShaderBindingClass.ShaderResource;
                    kind = ShaderResourceKind.InputAttachment;
                    dimension = MapImageDimension(
                        request,
                        cross,
                        resourceTypeHandle,
                        name);
                    access = ShaderResourceAccess.ReadOnly;
                    break;

                case ResourceType.StorageImage:
                    access = ReflectImageAccess(
                        request,
                        cross,
                        compiler,
                        resource,
                        resourceTypeHandle,
                        name);
                    bindingClass = access == ShaderResourceAccess.ReadOnly
                        ? ShaderBindingClass.ShaderResource
                        : ShaderBindingClass.UnorderedAccess;
                    dimension = MapImageDimension(
                        request,
                        cross,
                        resourceTypeHandle,
                        name);
                    kind = dimension == ShaderResourceDimension.Buffer
                        ? ShaderResourceKind.TypedBuffer
                        : ShaderResourceKind.Texture;
                    break;

                case ResourceType.AtomicCounter:
                    bindingClass = ShaderBindingClass.UnorderedAccess;
                    kind = ShaderResourceKind.AtomicCounter;
                    dimension = ShaderResourceDimension.Buffer;
                    access = ShaderResourceAccess.ReadWrite;
                    break;

                case ResourceType.SeparateImage:
                    if (cross.TypeGetImageIsStorage(resourceTypeHandle) != 0)
                    {
                        throw ReflectionFailure(
                            request,
                            $"SPIR-V separate image {name} is unexpectedly marked as a storage image.");
                    }

                    bindingClass = ShaderBindingClass.ShaderResource;
                    dimension = MapImageDimension(
                        request,
                        cross,
                        resourceTypeHandle,
                        name);
                    kind = dimension == ShaderResourceDimension.Buffer
                        ? ShaderResourceKind.TypedBuffer
                        : ShaderResourceKind.Texture;
                    access = ShaderResourceAccess.ReadOnly;
                    break;

                case ResourceType.SeparateSamplers:
                    bindingClass = ShaderBindingClass.Sampler;
                    kind = ShaderResourceKind.Sampler;
                    dimension = ShaderResourceDimension.Unknown;
                    access = ShaderResourceAccess.ReadOnly;
                    samplerKind = ShaderSamplerKind.Unknown;
                    break;

                case ResourceType.AccelerationStructure:
                    bindingClass = ShaderBindingClass.ShaderResource;
                    kind = ShaderResourceKind.AccelerationStructure;
                    dimension = ShaderResourceDimension.Unknown;
                    access = ShaderResourceAccess.ReadOnly;
                    break;

                case ResourceType.ShaderRecordBuffer:
                    bindingClass = ShaderBindingClass.ShaderResource;
                    kind = ShaderResourceKind.ShaderRecordBuffer;
                    dimension = ShaderResourceDimension.Buffer;
                    access = ShaderResourceAccess.ReadOnly;
                    break;

                default:
                    throw ReflectionFailure(
                        request,
                        $"SPIR-V resource {name} has unsupported resource class {resourceType}.");
            }

            ShaderResourceShape shape = new(
                kind,
                dimension,
                access,
                array,
                structureStride,
                samplerKind);
            ShaderLogicalBinding logicalBinding = new(
                new ShaderBindingKey(
                    descriptorSet,
                    binding,
                    bindingClass),
                name,
                aliases: null,
                shape,
                stages,
                constantBufferLayout,
                ShaderBindingProvenance.Unknown);

            return new ShaderResourceBindingReflection(
                logicalBinding,
                new ShaderPhysicalBindingLocation(
                    ShaderBackendKind.Vulkan,
                    descriptorSet,
                    binding,
                    ShaderPhysicalBindingNamespace.Unified));
        }

        private static ShaderConstantBufferLayout ReflectConstantBuffer(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            Compiler* compiler,
            uint baseTypeId,
            string name,
            IReadOnlyDictionary<uint, uint> specializationConstants)
        {
            CrossType* type = RequireTypeHandle(
                request,
                cross,
                compiler,
                baseTypeId,
                $"SPIR-V constant buffer {name}");
            if (cross.TypeGetBasetype(type) != Basetype.Struct)
            {
                throw ReflectionFailure(
                    request,
                    $"SPIR-V constant buffer {name} does not reference a struct block type.");
            }

            uint byteSize = GetDeclaredStructSize(
                request,
                cross,
                context,
                compiler,
                type,
                $"SPIR-V constant buffer {name}");
            int memberCount = ToManagedCount(
                request,
                cross.TypeGetNumMemberTypes(type),
                $"SPIR-V constant buffer {name} member count");
            if (memberCount == 0)
            {
                throw ReflectionFailure(
                    request,
                    $"SPIR-V constant buffer {name} has no reflected members.");
            }

            ShaderValueMember[] members = new ShaderValueMember[memberCount];
            for (int memberIndex = 0; memberIndex < memberCount; ++memberIndex)
            {
                string memberName = ReadMemberName(
                    cross,
                    compiler,
                    baseTypeId,
                    (uint)memberIndex);
                uint offset = GetStructMemberOffset(
                    request,
                    cross,
                    context,
                    compiler,
                    type,
                    (uint)memberIndex,
                    $"{name}.{memberName}");
                uint memberSize = GetDeclaredStructMemberSize(
                    request,
                    cross,
                    context,
                    compiler,
                    type,
                    (uint)memberIndex,
                    $"{name}.{memberName}");
                uint memberTypeId = cross.TypeGetMemberType(
                    type,
                    (uint)memberIndex);
                ValueDecorations decorations = ReflectValueDecorations(
                    request,
                    cross,
                    context,
                    compiler,
                    type,
                    baseTypeId,
                    (uint)memberIndex,
                    memberTypeId,
                    $"{name}.{memberName}");
                ShaderValueLayout value = ReflectValueLayout(
                    request,
                    cross,
                    context,
                    compiler,
                    memberTypeId,
                    memberSize,
                    decorations,
                    specializationConstants,
                    $"{name}.{memberName}",
                    depth: 0);
                members[memberIndex] = new ShaderValueMember(
                    memberName,
                    offset,
                    memberSize,
                    value);
            }

            return new ShaderConstantBufferLayout(
                name,
                byteSize,
                members);
        }

        private static ShaderValueLayout ReflectValueLayout(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            Compiler* compiler,
            uint typeId,
            uint byteSize,
            ValueDecorations decorations,
            IReadOnlyDictionary<uint, uint> specializationConstants,
            string path,
            int depth)
        {
            if (depth > MaximumTypeNestingDepth)
            {
                throw ReflectionFailure(
                    request,
                    $"SPIR-V value layout {path} exceeds the maximum nesting depth {MaximumTypeNestingDepth}.");
            }

            CrossType* qualifiedType = RequireTypeHandle(
                request,
                cross,
                compiler,
                typeId,
                path);
            ShaderArrayShape array = ReflectArrayShape(
                request,
                cross,
                qualifiedType,
                specializationConstants,
                path);
            Basetype baseType = cross.TypeGetBasetype(qualifiedType);
            uint vectorSize = cross.TypeGetVectorSize(qualifiedType);
            uint columns = cross.TypeGetColumns(qualifiedType);

            if (baseType == Basetype.Struct)
            {
                uint baseTypeId = cross.TypeGetBaseTypeId(qualifiedType);
                CrossType* structType = RequireTypeHandle(
                    request,
                    cross,
                    compiler,
                    baseTypeId,
                    path);
                int memberCount = ToManagedCount(
                    request,
                    cross.TypeGetNumMemberTypes(structType),
                    $"{path} member count");
                if (memberCount == 0)
                {
                    throw ReflectionFailure(
                        request,
                        $"SPIR-V struct value {path} has no members.");
                }

                ShaderValueMember[] members = new ShaderValueMember[memberCount];
                for (int memberIndex = 0; memberIndex < memberCount; ++memberIndex)
                {
                    string memberName = ReadMemberName(
                        cross,
                        compiler,
                        baseTypeId,
                        (uint)memberIndex);
                    string memberPath = $"{path}.{memberName}";
                    uint offset = GetStructMemberOffset(
                        request,
                        cross,
                        context,
                        compiler,
                        structType,
                        (uint)memberIndex,
                        memberPath);
                    uint memberSize = GetDeclaredStructMemberSize(
                        request,
                        cross,
                        context,
                        compiler,
                        structType,
                        (uint)memberIndex,
                        memberPath);
                    uint memberTypeId = cross.TypeGetMemberType(
                        structType,
                        (uint)memberIndex);
                    ValueDecorations memberDecorations = ReflectValueDecorations(
                        request,
                        cross,
                        context,
                        compiler,
                        structType,
                        baseTypeId,
                        (uint)memberIndex,
                        memberTypeId,
                        memberPath);
                    members[memberIndex] = new ShaderValueMember(
                        memberName,
                        offset,
                        memberSize,
                        ReflectValueLayout(
                            request,
                            cross,
                            context,
                            compiler,
                            memberTypeId,
                            memberSize,
                            memberDecorations,
                            specializationConstants,
                            memberPath,
                            depth + 1));
                }

                return new ShaderValueLayout(
                    ShaderValueKind.Struct,
                    scalarType: null,
                    rows: 0,
                    columns: 0,
                    array,
                    byteSize,
                    decorations.ArrayStride,
                    provenMatrixStride: null,
                    matrixMajorOrder: null,
                    members);
            }

            ShaderScalarType scalarType = MapScalarType(
                request,
                baseType,
                cross.TypeGetBitWidth(qualifiedType),
                path);
            if (columns > 1)
            {
                if (!decorations.MatrixStride.HasValue
                    || !decorations.MatrixMajorOrder.HasValue)
                {
                    throw ReflectionFailure(
                        request,
                        $"SPIR-V matrix value {path} lacks exact MatrixStride or major-order decoration.");
                }

                return new ShaderValueLayout(
                    ShaderValueKind.Matrix,
                    scalarType,
                    vectorSize,
                    columns,
                    array,
                    byteSize,
                    decorations.ArrayStride,
                    decorations.MatrixStride,
                    decorations.MatrixMajorOrder);
            }

            if (vectorSize > 1)
            {
                return new ShaderValueLayout(
                    ShaderValueKind.Vector,
                    scalarType,
                    rows: 1,
                    columns: vectorSize,
                    array,
                    byteSize,
                    decorations.ArrayStride);
            }

            if (vectorSize != 1 || columns != 1)
            {
                throw ReflectionFailure(
                    request,
                    $"SPIR-V value {path} has unsupported scalar/vector dimensions {vectorSize}x{columns}.");
            }

            return new ShaderValueLayout(
                ShaderValueKind.Scalar,
                scalarType,
                rows: 1,
                columns: 1,
                array,
                byteSize,
                decorations.ArrayStride);
        }

        private static ValueDecorations ReflectValueDecorations(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            Compiler* compiler,
            CrossType* parentType,
            uint parentTypeId,
            uint memberIndex,
            uint memberTypeId,
            string path)
        {
            CrossType* memberType = RequireTypeHandle(
                request,
                cross,
                compiler,
                memberTypeId,
                path);
            uint? arrayStride = null;
            if (cross.TypeGetNumArrayDimensions(memberType) != 0)
            {
                uint stride = 0;
                ThrowIfFailed(
                    request,
                    cross,
                    context,
                    cross.CompilerTypeStructMemberArrayStride(
                        compiler,
                        parentType,
                        memberIndex,
                        &stride),
                    $"Failed to read SPIR-V array stride for {path}.");
                if (stride == 0)
                {
                    throw ReflectionFailure(
                        request,
                        $"SPIR-V array value {path} has a zero ArrayStride.");
                }

                arrayStride = stride;
            }

            uint? matrixStride = null;
            ShaderMatrixMajorOrder? matrixMajorOrder = null;
            if (cross.TypeGetColumns(memberType) > 1)
            {
                uint stride = 0;
                ThrowIfFailed(
                    request,
                    cross,
                    context,
                    cross.CompilerTypeStructMemberMatrixStride(
                        compiler,
                        parentType,
                        memberIndex,
                        &stride),
                    $"Failed to read SPIR-V matrix stride for {path}.");
                if (stride == 0)
                {
                    throw ReflectionFailure(
                        request,
                        $"SPIR-V matrix value {path} has a zero MatrixStride.");
                }

                bool rowMajor = cross.CompilerHasMemberDecoration(
                    compiler,
                    parentTypeId,
                    memberIndex,
                    Decoration.RowMajor) != 0;
                bool columnMajor = cross.CompilerHasMemberDecoration(
                    compiler,
                    parentTypeId,
                    memberIndex,
                    Decoration.ColMajor) != 0;
                if (rowMajor == columnMajor)
                {
                    throw ReflectionFailure(
                        request,
                        $"SPIR-V matrix value {path} must have exactly one RowMajor or ColMajor decoration.");
                }

                matrixStride = stride;
                matrixMajorOrder = rowMajor
                    ? ShaderMatrixMajorOrder.RowMajor
                    : ShaderMatrixMajorOrder.ColumnMajor;
            }

            return new ValueDecorations(
                arrayStride,
                matrixStride,
                matrixMajorOrder);
        }

        private static ShaderArrayShape ReflectArrayShape(
            ShaderCompileRequest request,
            Cross cross,
            CrossType* type,
            IReadOnlyDictionary<uint, uint> specializationConstants,
            string path)
        {
            int dimensionCount = ToManagedCount(
                request,
                cross.TypeGetNumArrayDimensions(type),
                $"{path} array dimension count");
            if (dimensionCount == 0)
            {
                return ShaderArrayShape.Scalar;
            }

            ShaderArrayExtent[] extents = new ShaderArrayExtent[dimensionCount];
            for (int dimensionIndex = 0; dimensionIndex < dimensionCount; ++dimensionIndex)
            {
                uint value = cross.TypeGetArrayDimension(
                    type,
                    (uint)dimensionIndex);
                if (cross.TypeArrayDimensionIsLiteral(
                        type,
                        (uint)dimensionIndex) != 0)
                {
                    extents[dimensionIndex] = value == 0
                        ? ShaderArrayExtent.Runtime()
                        : ShaderArrayExtent.Bounded(value);
                    continue;
                }

                if (!specializationConstants.TryGetValue(
                        value,
                        out uint specializationConstantId))
                {
                    throw ReflectionFailure(
                        request,
                        $"SPIR-V array value {path} dimension {dimensionIndex} references unknown specialization result ID {value}.");
                }

                extents[dimensionIndex] =
                    ShaderArrayExtent.SpecializationConstant(
                        specializationConstantId);
            }

            return new ShaderArrayShape(extents);
        }

        private static ShaderThreadGroupSize? ReflectThreadGroupSize(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            Compiler* compiler,
            ShaderExecutionStage stage,
            string entryPointName)
        {
            bool usesThreadGroup = stage is ShaderExecutionStage.Compute
                or ShaderExecutionStage.Amplification
                or ShaderExecutionStage.Mesh
                or ShaderExecutionStage.Node;
            if (!usesThreadGroup)
            {
                return null;
            }

            ExecutionMode* modes = null;
            nuint modeCountValue = 0;
            ThrowIfFailed(
                request,
                cross,
                context,
                cross.CompilerGetExecutionModes(
                    compiler,
                    &modes,
                    &modeCountValue),
                $"Failed to enumerate execution modes for SPIR-V entry point {entryPointName}.");

            int modeCount = ToManagedCount(
                request,
                modeCountValue,
                $"SPIR-V entry point {entryPointName} execution-mode count");
            bool hasLocalSize = false;
            for (int index = 0; index < modeCount; ++index)
            {
                if (modes[index] is ExecutionMode.LocalSize or ExecutionMode.LocalSizeId)
                {
                    hasLocalSize = true;
                    break;
                }
            }

            if (!hasLocalSize)
            {
                throw ReflectionFailure(
                    request,
                    $"SPIR-V entry point {entryPointName} does not declare LocalSize or LocalSizeId.");
            }

            NativeSpecializationConstant xSpecialization = default;
            NativeSpecializationConstant ySpecialization = default;
            NativeSpecializationConstant zSpecialization = default;
            _ = cross.CompilerGetWorkGroupSizeSpecializationConstants(
                compiler,
                &xSpecialization,
                &ySpecialization,
                &zSpecialization);

            return new ShaderThreadGroupSize(
                ReflectThreadGroupDimension(
                    request,
                    cross,
                    compiler,
                    xSpecialization,
                    0,
                    entryPointName),
                ReflectThreadGroupDimension(
                    request,
                    cross,
                    compiler,
                    ySpecialization,
                    1,
                    entryPointName),
                ReflectThreadGroupDimension(
                    request,
                    cross,
                    compiler,
                    zSpecialization,
                    2,
                    entryPointName));
        }

        private static ShaderThreadGroupDimension ReflectThreadGroupDimension(
            ShaderCompileRequest request,
            Cross cross,
            Compiler* compiler,
            NativeSpecializationConstant specialization,
            uint dimensionIndex,
            string entryPointName)
        {
            uint defaultValue = cross.CompilerGetExecutionModeArgumentByIndex(
                compiler,
                ExecutionMode.LocalSize,
                dimensionIndex);
            if (defaultValue == 0)
            {
                throw ReflectionFailure(
                    request,
                    $"SPIR-V entry point {entryPointName} has a zero local-size default in dimension {dimensionIndex}.");
            }

            return specialization.Id == 0
                ? ShaderThreadGroupDimension.Fixed(defaultValue)
                : ShaderThreadGroupDimension.SpecializationConstant(
                    specialization.ConstantId,
                    defaultValue);
        }

        private static uint ReflectStorageBufferStride(
            ShaderCompileRequest request,
            Cross cross,
            Context* context,
            Compiler* compiler,
            uint baseTypeId,
            string name)
        {
            CrossType* blockType = RequireTypeHandle(
                request,
                cross,
                compiler,
                baseTypeId,
                $"SPIR-V storage buffer {name}");
            if (cross.TypeGetBasetype(blockType) != Basetype.Struct)
            {
                throw ReflectionFailure(
                    request,
                    $"SPIR-V storage buffer {name} does not reference a struct block type.");
            }

            int memberCount = ToManagedCount(
                request,
                cross.TypeGetNumMemberTypes(blockType),
                $"SPIR-V storage buffer {name} member count");
            if (memberCount == 0)
            {
                throw ReflectionFailure(
                    request,
                    $"SPIR-V storage buffer {name} has an empty block type.");
            }

            int arrayMember = -1;
            for (int memberIndex = 0; memberIndex < memberCount; ++memberIndex)
            {
                uint memberTypeId = cross.TypeGetMemberType(
                    blockType,
                    (uint)memberIndex);
                CrossType* memberType = RequireTypeHandle(
                    request,
                    cross,
                    compiler,
                    memberTypeId,
                    $"{name}.member{memberIndex}");
                if (cross.TypeGetNumArrayDimensions(memberType) != 0)
                {
                    if (arrayMember >= 0)
                    {
                        throw ReflectionFailure(
                            request,
                            $"SPIR-V storage buffer {name} has multiple array block members and no single provable element stride.");
                    }

                    arrayMember = memberIndex;
                }
            }

            if (arrayMember >= 0)
            {
                uint stride = 0;
                ThrowIfFailed(
                    request,
                    cross,
                    context,
                    cross.CompilerTypeStructMemberArrayStride(
                        compiler,
                        blockType,
                        (uint)arrayMember,
                        &stride),
                    $"Failed to read SPIR-V storage-buffer stride for {name}.");
                if (stride == 0)
                {
                    throw ReflectionFailure(
                        request,
                        $"SPIR-V storage buffer {name} has a zero element stride.");
                }

                return stride;
            }

            return GetDeclaredStructSize(
                request,
                cross,
                context,
                compiler,
                blockType,
                $"SPIR-V storage buffer {name}");
        }

        private static ShaderResourceAccess ReflectImageAccess(
            ShaderCompileRequest request,
            Cross cross,
            Compiler* compiler,
            NativeResource resource,
            CrossType* type,
            string name)
        {
            ShaderResourceAccess decorated = ReflectAccess(
                request,
                cross,
                compiler,
                resource,
                type,
                name);
            AccessQualifier qualifier = cross.TypeGetImageAccessQualifier(type);
            if (qualifier == AccessQualifier.Max)
            {
                return decorated;
            }

            ShaderResourceAccess qualified = qualifier switch
            {
                AccessQualifier.ReadOnly => ShaderResourceAccess.ReadOnly,
                AccessQualifier.WriteOnly => ShaderResourceAccess.WriteOnly,
                AccessQualifier.ReadWrite => ShaderResourceAccess.ReadWrite,
                _ => throw ReflectionFailure(
                    request,
                    $"SPIR-V storage image {name} has unsupported access qualifier {qualifier}."),
            };

            if (decorated != ShaderResourceAccess.ReadWrite
                && decorated != qualified)
            {
                throw ReflectionFailure(
                    request,
                    $"SPIR-V storage image {name} has conflicting access decorations ({decorated}) and qualifier ({qualified}).");
            }

            return qualified;
        }

        private static ShaderResourceAccess ReflectAccess(
            ShaderCompileRequest request,
            Cross cross,
            Compiler* compiler,
            NativeResource resource,
            CrossType* resourceType,
            string name)
        {
            bool nonWritable = HasAccessDecoration(
                cross,
                compiler,
                resource,
                resourceType,
                Decoration.NonWritable);
            bool nonReadable = HasAccessDecoration(
                cross,
                compiler,
                resource,
                resourceType,
                Decoration.NonReadable);
            if (nonWritable && nonReadable)
            {
                throw ReflectionFailure(
                    request,
                    $"SPIR-V resource {name} is both NonWritable and NonReadable.");
            }

            if (nonWritable)
            {
                return ShaderResourceAccess.ReadOnly;
            }

            if (nonReadable)
            {
                return ShaderResourceAccess.WriteOnly;
            }

            return ShaderResourceAccess.ReadWrite;
        }

        private static bool HasAccessDecoration(
            Cross cross,
            Compiler* compiler,
            NativeResource resource,
            CrossType* resourceType,
            Decoration decoration)
        {
            if (cross.CompilerHasDecoration(
                    compiler,
                    resource.Id,
                    decoration) != 0
                || cross.CompilerHasDecoration(
                    compiler,
                    resource.BaseTypeId,
                    decoration) != 0)
            {
                return true;
            }

            uint baseTypeId = cross.TypeGetBaseTypeId(resourceType);
            CrossType* baseType = cross.CompilerGetTypeHandle(
                compiler,
                baseTypeId);
            if (baseType == null)
            {
                return false;
            }

            uint memberCount = cross.TypeGetNumMemberTypes(baseType);
            for (uint memberIndex = 0; memberIndex < memberCount; ++memberIndex)
            {
                if (cross.CompilerHasMemberDecoration(
                        compiler,
                        baseTypeId,
                        memberIndex,
                        decoration) != 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static ShaderResourceDimension MapImageDimension(
            ShaderCompileRequest request,
            Cross cross,
            CrossType* type,
            string name)
        {
            Dim dimension = cross.TypeGetImageDimension(type);
            bool arrayed = cross.TypeGetImageArrayed(type) != 0;
            bool multisampled = cross.TypeGetImageMultisampled(type) != 0;
            return dimension switch
            {
                Dim.Dim1D when !multisampled => arrayed
                    ? ShaderResourceDimension.Texture1DArray
                    : ShaderResourceDimension.Texture1D,
                Dim.Dim2D => (arrayed, multisampled) switch
                {
                    (false, false) => ShaderResourceDimension.Texture2D,
                    (true, false) => ShaderResourceDimension.Texture2DArray,
                    (false, true) => ShaderResourceDimension.Texture2DMultisampled,
                    (true, true) => ShaderResourceDimension.Texture2DMultisampledArray,
                },
                Dim.Dim3D when !arrayed && !multisampled =>
                    ShaderResourceDimension.Texture3D,
                Dim.DimCube when !multisampled => arrayed
                    ? ShaderResourceDimension.TextureCubeArray
                    : ShaderResourceDimension.TextureCube,
                Dim.DimBuffer when !arrayed && !multisampled =>
                    ShaderResourceDimension.Buffer,
                Dim.DimSubpassData when !arrayed => multisampled
                    ? ShaderResourceDimension.Texture2DMultisampled
                    : ShaderResourceDimension.Texture2D,
                _ => throw ReflectionFailure(
                    request,
                    $"SPIR-V image {name} has unsupported dimension {dimension}, arrayed={arrayed}, multisampled={multisampled}."),
            };
        }

        private static ShaderScalarType MapScalarType(
            ShaderCompileRequest request,
            Basetype baseType,
            uint bitWidth,
            string path)
        {
            return baseType switch
            {
                Basetype.Boolean when bitWidth is 1 or 32 =>
                    ShaderScalarType.Boolean,
                Basetype.Int8 when bitWidth == 8 => ShaderScalarType.Int8,
                Basetype.Uint8 when bitWidth == 8 => ShaderScalarType.UInt8,
                Basetype.Int16 when bitWidth == 16 => ShaderScalarType.Int16,
                Basetype.Uint16 when bitWidth == 16 => ShaderScalarType.UInt16,
                Basetype.Int32 when bitWidth == 32 => ShaderScalarType.Int32,
                Basetype.Uint32 when bitWidth == 32 => ShaderScalarType.UInt32,
                Basetype.Int64 when bitWidth == 64 => ShaderScalarType.Int64,
                Basetype.Uint64 when bitWidth == 64 => ShaderScalarType.UInt64,
                Basetype.FP16 when bitWidth == 16 => ShaderScalarType.Float16,
                Basetype.FP32 when bitWidth == 32 => ShaderScalarType.Float32,
                Basetype.FP64 when bitWidth == 64 => ShaderScalarType.Float64,
                _ => throw ReflectionFailure(
                    request,
                    $"SPIR-V value {path} has unsupported scalar type {baseType} with bit width {bitWidth}."),
            };
        }

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
