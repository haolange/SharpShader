using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using SharpShader.HLSLCrossCompiler;
using Silk.NET.SPIRV;

namespace SharpShader.Compilation.Internal
{
    internal static class SpirvBindingRemapper
    {
        private const uint SpirvMagic = 0x07230203;
        private const uint MinimumSpirvVersion = 0x00010000;
        private const uint MaximumSpirvVersion = 0x00010600;
        private const uint OpName = 5;
        private const uint OpDecorate = 71;
        private const uint OpDecorationGroup = 73;
        private const uint OpGroupDecorate = 74;
        private const uint OpGroupMemberDecorate = 75;
        private const uint OpDecorateId = 332;
        private const int HeaderWordCount = 5;

        private static readonly UTF8Encoding s_StrictUtf8 = new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        internal static byte[] Remap(
            byte[] spirvBytecode,
            ShaderArtifactReflection logicalReflection,
            VulkanShaderBackendLayout targetLayout)
        {
            if (spirvBytecode is null)
            {
                throw InvalidRequest("A SPIR-V artifact is required for binding remapping.");
            }

            if (logicalReflection is null)
            {
                throw InvalidRequest("Logical shader reflection is required for binding remapping.");
            }

            if (targetLayout is null)
            {
                throw InvalidRequest("A Vulkan backend layout is required for binding remapping.");
            }

            SpirvModule module = ParseModule(spirvBytecode);
            ShaderArtifactReflection intermediateReflection = Reflect(module.ToByteArray());
            IReadOnlyDictionary<ShaderBindingKey, SpirvDescriptor> matches =
                MatchLogicalResources(logicalReflection, intermediateReflection, module);
            Dictionary<ShaderBindingKey, VulkanShaderBindingMapping> mappings =
                ValidateTargetLayout(matches, targetLayout, logicalReflection);

            uint[] remappedWords = (uint[])module.Words.Clone();
            foreach ((ShaderBindingKey key, SpirvDescriptor descriptor) in matches)
            {
                VulkanShaderBindingMapping mapping = mappings[key];
                remappedWords[descriptor.DescriptorSetValueWord] = mapping.DescriptorSet;
                remappedWords[descriptor.BindingValueWord] = mapping.Binding;
            }

            byte[] remapped = ToByteArray(remappedWords);
            SpirvModule verifiedModule = ParseModule(remapped);
            ShaderArtifactReflection verifiedReflection = Reflect(remapped);
            IReadOnlyDictionary<ShaderBindingKey, SpirvDescriptor> verifiedMatches =
                MatchLogicalResources(logicalReflection, verifiedReflection, verifiedModule);

            foreach ((ShaderBindingKey key, SpirvDescriptor descriptor) in verifiedMatches)
            {
                VulkanShaderBindingMapping mapping = mappings[key];
                if (descriptor.DescriptorSet != mapping.DescriptorSet
                    || descriptor.Binding != mapping.Binding)
                {
                    throw Failure(
                        $"SPIR-V binding remap verification failed for {key}: "
                        + $"expected set={mapping.DescriptorSet}, binding={mapping.Binding}, "
                        + $"observed set={descriptor.DescriptorSet}, binding={descriptor.Binding}.");
                }
            }

            return remapped;
        }

        private static SpirvModule ParseModule(byte[] bytecode)
        {
            if (bytecode.Length < HeaderWordCount * sizeof(uint))
            {
                throw Failure("The SPIR-V artifact is shorter than the five-word module header.");
            }

            if ((bytecode.Length % sizeof(uint)) != 0)
            {
                throw Failure("The SPIR-V artifact length is not aligned to 32-bit words.");
            }

            uint[] words = new uint[bytecode.Length / sizeof(uint)];
            for (int index = 0; index < words.Length; ++index)
            {
                words[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytecode.AsSpan(index * sizeof(uint), sizeof(uint)));
            }

            if (words[0] != SpirvMagic)
            {
                throw Failure(
                    words[0] == BinaryPrimitives.ReverseEndianness(SpirvMagic)
                        ? "Big-endian SPIR-V artifacts are not supported."
                        : "The SPIR-V artifact has an invalid magic number.");
            }

            uint version = words[1];
            if (version < MinimumSpirvVersion
                || version > MaximumSpirvVersion
                || (version & 0xFFu) != 0)
            {
                throw Failure($"The SPIR-V artifact version 0x{version:X8} is not supported.");
            }

            uint idBound = words[3];
            if (idBound == 0)
            {
                throw Failure("The SPIR-V artifact has an invalid zero ID bound.");
            }

            if (words[4] != 0)
            {
                throw Failure("The SPIR-V artifact reserved header word must be zero.");
            }

            Dictionary<uint, List<string>> names = new();
            Dictionary<uint, DecorationRecord> decorations = new();
            for (int offset = HeaderWordCount; offset < words.Length;)
            {
                uint instruction = words[offset];
                int wordCount = checked((int)(instruction >> 16));
                uint opcode = instruction & 0xFFFFu;
                if (wordCount == 0)
                {
                    throw Failure($"SPIR-V instruction at word {offset} has a zero word count.");
                }

                if (wordCount > words.Length - offset)
                {
                    throw Failure(
                        $"SPIR-V instruction at word {offset} extends beyond the artifact boundary.");
                }

                switch (opcode)
                {
                    case OpName:
                        ParseName(bytecode, words, offset, wordCount, idBound, names);
                        break;
                    case OpDecorate:
                        ParseDecoration(words, offset, wordCount, idBound, decorations);
                        break;
                    case OpDecorationGroup:
                    case OpGroupDecorate:
                    case OpGroupMemberDecorate:
                    case OpDecorateId:
                        throw Failure(
                            $"SPIR-V opcode {opcode} uses an indirect decoration form that cannot be remapped safely.");
                }

                offset += wordCount;
            }

            Dictionary<(uint Set, uint Binding), SpirvDescriptor> locations = new();
            Dictionary<string, SpirvDescriptor> descriptorNames =
                new(StringComparer.Ordinal);
            Dictionary<uint, SpirvDescriptor> descriptors = new();
            foreach ((uint id, DecorationRecord decoration) in decorations)
            {
                if (decoration.DescriptorSetValueWord.HasValue
                    != decoration.BindingValueWord.HasValue)
                {
                    throw Failure(
                        $"SPIR-V ID {id} must have both DescriptorSet and Binding decorations.");
                }

                if (!decoration.DescriptorSetValueWord.HasValue)
                {
                    continue;
                }

                if (!names.TryGetValue(id, out List<string>? idNames)
                    || idNames.Count != 1
                    || string.IsNullOrWhiteSpace(idNames[0]))
                {
                    throw Failure(
                        $"SPIR-V descriptor ID {id} must have exactly one non-empty OpName for stable remapping.");
                }

                int descriptorSetValueWord = decoration.DescriptorSetValueWord.Value;
                int bindingValueWord = decoration.BindingValueWord!.Value;
                SpirvDescriptor descriptor = new(
                    id,
                    idNames[0],
                    words[descriptorSetValueWord],
                    words[bindingValueWord],
                    descriptorSetValueWord,
                    bindingValueWord);

                if (!locations.TryAdd(
                        (descriptor.DescriptorSet, descriptor.Binding),
                        descriptor))
                {
                    throw Failure(
                        $"SPIR-V contains duplicate physical descriptor location "
                        + $"set={descriptor.DescriptorSet}, binding={descriptor.Binding}.");
                }

                if (!descriptorNames.TryAdd(descriptor.Name, descriptor))
                {
                    throw Failure(
                        $"SPIR-V descriptor name {descriptor.Name} is not unique.");
                }

                descriptors.Add(id, descriptor);
            }

            return new SpirvModule(words, descriptors, locations);
        }

        private static void ParseName(
            byte[] bytecode,
            uint[] words,
            int offset,
            int wordCount,
            uint idBound,
            Dictionary<uint, List<string>> names)
        {
            if (wordCount < 3)
            {
                throw Failure($"OpName at word {offset} has an invalid word count {wordCount}.");
            }

            uint targetId = words[offset + 1];
            ValidateId(targetId, idBound, $"OpName at word {offset}");
            int byteOffset = checked((offset + 2) * sizeof(uint));
            int byteCount = checked((wordCount - 2) * sizeof(uint));
            ReadOnlySpan<byte> encoded = bytecode.AsSpan(byteOffset, byteCount);
            int terminator = encoded.IndexOf((byte)0);
            if (terminator <= 0)
            {
                throw Failure($"OpName for SPIR-V ID {targetId} has no non-empty null-terminated name.");
            }

            for (int index = terminator + 1; index < encoded.Length; ++index)
            {
                if (encoded[index] != 0)
                {
                    throw Failure($"OpName for SPIR-V ID {targetId} has non-zero bytes after its terminator.");
                }
            }

            string name;
            try
            {
                name = s_StrictUtf8.GetString(encoded[..terminator]);
            }
            catch (DecoderFallbackException exception)
            {
                throw Failure($"OpName for SPIR-V ID {targetId} is not valid UTF-8.", exception);
            }

            if (!names.TryGetValue(targetId, out List<string>? idNames))
            {
                idNames = new List<string>();
                names.Add(targetId, idNames);
            }

            idNames.Add(name);
        }

        private static void ParseDecoration(
            uint[] words,
            int offset,
            int wordCount,
            uint idBound,
            Dictionary<uint, DecorationRecord> decorations)
        {
            if (wordCount < 3)
            {
                throw Failure($"OpDecorate at word {offset} has an invalid word count {wordCount}.");
            }

            uint targetId = words[offset + 1];
            ValidateId(targetId, idBound, $"OpDecorate at word {offset}");
            uint decoration = words[offset + 2];
            bool isDescriptorSet = decoration == (uint)Decoration.DescriptorSet;
            bool isBinding = decoration == (uint)Decoration.Binding;
            if (!isDescriptorSet && !isBinding)
            {
                return;
            }

            if (wordCount != 4)
            {
                throw Failure(
                    $"Descriptor decoration at word {offset} must contain exactly one literal operand.");
            }

            if (!decorations.TryGetValue(targetId, out DecorationRecord? record))
            {
                record = new DecorationRecord();
                decorations.Add(targetId, record);
            }

            if (isDescriptorSet)
            {
                if (record.DescriptorSetValueWord.HasValue)
                {
                    throw Failure($"SPIR-V ID {targetId} has duplicate DescriptorSet decorations.");
                }

                record.DescriptorSetValueWord = offset + 3;
            }
            else
            {
                if (record.BindingValueWord.HasValue)
                {
                    throw Failure($"SPIR-V ID {targetId} has duplicate Binding decorations.");
                }

                record.BindingValueWord = offset + 3;
            }
        }

        private static void ValidateId(uint id, uint idBound, string context)
        {
            if (id == 0 || id >= idBound)
            {
                throw Failure($"{context} targets invalid SPIR-V ID {id} for bound {idBound}.");
            }
        }

        private static ShaderArtifactReflection Reflect(byte[] bytecode)
        {
            ShaderCompileRequest request = new()
            {
                Source = "// Artifact-only SPIR-V binding remap.",
                SourceName = "binding-remap.spv",
                EntryPoint = string.Empty,
                Stage = ShaderStageKind.Library,
                ShaderModel = new ShaderModelVersion(6, 6),
                Target = ShaderTargetKind.SpirV,
            };

            return SpirvArtifactReflector.Reflect(
                request,
                new ShaderCompileResult { Bytecode = bytecode });
        }

        private static IReadOnlyDictionary<ShaderBindingKey, SpirvDescriptor> MatchLogicalResources(
            ShaderArtifactReflection logicalReflection,
            ShaderArtifactReflection spirvReflection,
            SpirvModule module)
        {
            Dictionary<ShaderBindingKey, ShaderLogicalBinding> logicalBindings =
                CollectLogicalBindings(logicalReflection);
            Dictionary<string, ShaderBindingKey> logicalNames =
                CollectLogicalNames(logicalBindings);
            Dictionary<(string Name, ShaderExecutionStage Stage), ShaderEntryPointReflection> spirvEntries =
                CollectEntries(spirvReflection, "SPIR-V");

            if (logicalReflection.EntryPoints.Count != spirvReflection.EntryPoints.Count)
            {
                throw Failure(
                    $"Logical reflection has {logicalReflection.EntryPoints.Count} entry points, "
                    + $"but SPIR-V has {spirvReflection.EntryPoints.Count}.");
            }

            Dictionary<ShaderBindingKey, SpirvDescriptor> matches = new();
            HashSet<(uint Set, uint Binding)> reflectedLocations = new();
            foreach (ShaderEntryPointReflection logicalEntry in logicalReflection.EntryPoints)
            {
                (string Name, ShaderExecutionStage Stage) identity =
                    (logicalEntry.Name, logicalEntry.Stage);
                if (!spirvEntries.TryGetValue(identity, out ShaderEntryPointReflection? spirvEntry))
                {
                    throw Failure(
                        $"SPIR-V does not contain logical entry point {logicalEntry.Name} ({logicalEntry.Stage}).");
                }

                if (logicalEntry.ThreadGroupSize != spirvEntry.ThreadGroupSize)
                {
                    throw Failure(
                        $"SPIR-V thread-group metadata does not match logical entry point "
                        + $"{logicalEntry.Name} ({logicalEntry.Stage}).");
                }

                Dictionary<ShaderBindingKey, ShaderResourceBindingReflection> logicalActive = new();
                foreach (ShaderResourceBindingReflection resource in logicalEntry.Resources)
                {
                    logicalActive.Add(resource.Key, resource);
                }

                HashSet<ShaderBindingKey> matchedActive = new();
                foreach (ShaderResourceBindingReflection spirvResource in spirvEntry.Resources)
                {
                    string stableName = GetStableSpirvResourceName(spirvResource);
                    if (!logicalNames.TryGetValue(
                            stableName,
                            out ShaderBindingKey logicalKey)
                        || !logicalActive.TryGetValue(logicalKey, out ShaderResourceBindingReflection? logicalResource))
                    {
                        throw Failure(
                            $"SPIR-V resource {spirvResource.Name} is not active in matching logical entry point "
                            + $"{logicalEntry.Name} ({logicalEntry.Stage}).");
                    }

                    ValidateResourceCompatibility(logicalResource, spirvResource);
                    if (!matchedActive.Add(logicalKey))
                    {
                        throw Failure(
                            $"SPIR-V entry point {spirvEntry.Name} maps more than one resource to {logicalKey}.");
                    }

                    (uint Set, uint Binding) location =
                        (spirvResource.PhysicalLocation.Group, spirvResource.PhysicalLocation.Binding);
                    reflectedLocations.Add(location);
                    if (!module.Locations.TryGetValue(location, out SpirvDescriptor? descriptor))
                    {
                        throw Failure(
                            $"SPIR-V reflected resource {spirvResource.Name} has no direct descriptor decoration record.");
                    }

                    if (!string.Equals(descriptor.Name, stableName, StringComparison.Ordinal))
                    {
                        throw Failure(
                            $"SPIR-V descriptor ID {descriptor.Id} OpName {descriptor.Name} "
                            + $"does not match reflected resource name {spirvResource.Name}.");
                    }

                    if (matches.TryGetValue(logicalKey, out SpirvDescriptor? existing))
                    {
                        if (existing.Id != descriptor.Id)
                        {
                            throw Failure(
                                $"Logical resource {logicalKey} maps to multiple SPIR-V IDs.");
                        }
                    }
                    else
                    {
                        matches.Add(logicalKey, descriptor);
                    }
                }

                if (matchedActive.Count != logicalActive.Count)
                {
                    foreach (ShaderBindingKey logicalKey in logicalActive.Keys)
                    {
                        if (!matchedActive.Contains(logicalKey))
                        {
                            throw Failure(
                                $"Logical resource {logicalKey} is absent from SPIR-V entry point "
                                + $"{spirvEntry.Name} ({spirvEntry.Stage}).");
                        }
                    }
                }
            }

            if (matches.Count != logicalBindings.Count)
            {
                throw Failure(
                    $"SPIR-V matched {matches.Count} resources, but logical reflection contains {logicalBindings.Count}.");
            }

            if (reflectedLocations.Count != module.Descriptors.Count)
            {
                foreach (SpirvDescriptor descriptor in module.Descriptors.Values)
                {
                    if (!reflectedLocations.Contains((descriptor.DescriptorSet, descriptor.Binding)))
                    {
                        throw Failure(
                            $"SPIR-V descriptor {descriptor.Name} (ID {descriptor.Id}) is not represented "
                            + "by active artifact reflection.");
                    }
                }
            }

            return matches;
        }

        private static string GetStableSpirvResourceName(
            ShaderResourceBindingReflection resource)
        {
            const string DxcCBufferTypePrefix = "type.";
            if (resource.Shape.Kind == ShaderResourceKind.ConstantBuffer
                && resource.Name.StartsWith(DxcCBufferTypePrefix, StringComparison.Ordinal))
            {
                return resource.Name[DxcCBufferTypePrefix.Length..];
            }

            return resource.Name;
        }

        private static Dictionary<ShaderBindingKey, ShaderLogicalBinding> CollectLogicalBindings(
            ShaderArtifactReflection reflection)
        {
            Dictionary<ShaderBindingKey, ShaderLogicalBinding> result = new();
            foreach (ShaderEntryPointReflection entryPoint in reflection.EntryPoints)
            {
                foreach (ShaderResourceBindingReflection resource in entryPoint.Resources)
                {
                    if (result.TryGetValue(resource.Key, out ShaderLogicalBinding? existing))
                    {
                        if (!HaveSameLogicalIdentity(existing, resource.LogicalBinding))
                        {
                            throw Failure(
                                $"Logical binding {resource.Key} has inconsistent declarations across entry points.");
                        }
                    }
                    else
                    {
                        result.Add(resource.Key, resource.LogicalBinding);
                    }
                }
            }

            return result;
        }

        private static Dictionary<string, ShaderBindingKey> CollectLogicalNames(
            IReadOnlyDictionary<ShaderBindingKey, ShaderLogicalBinding> bindings)
        {
            Dictionary<string, ShaderBindingKey> result = new(StringComparer.Ordinal);
            foreach ((ShaderBindingKey key, ShaderLogicalBinding binding) in bindings)
            {
                AddLogicalName(result, binding.CanonicalName, key);
                foreach (string alias in binding.Aliases)
                {
                    AddLogicalName(result, alias, key);
                }
            }

            return result;
        }

        private static void AddLogicalName(
            Dictionary<string, ShaderBindingKey> names,
            string name,
            ShaderBindingKey key)
        {
            if (!names.TryAdd(name, key))
            {
                throw Failure($"Logical resource name or alias {name} is not unique.");
            }
        }

        private static Dictionary<(string Name, ShaderExecutionStage Stage), ShaderEntryPointReflection> CollectEntries(
            ShaderArtifactReflection reflection,
            string description)
        {
            Dictionary<(string Name, ShaderExecutionStage Stage), ShaderEntryPointReflection> result = new();
            foreach (ShaderEntryPointReflection entryPoint in reflection.EntryPoints)
            {
                if (!result.TryAdd((entryPoint.Name, entryPoint.Stage), entryPoint))
                {
                    throw Failure(
                        $"{description} reflection contains duplicate entry point "
                        + $"{entryPoint.Name} ({entryPoint.Stage}).");
                }
            }

            return result;
        }

        private static bool HaveSameLogicalIdentity(
            ShaderLogicalBinding left,
            ShaderLogicalBinding right)
        {
            if (left.Key != right.Key
                || !string.Equals(left.CanonicalName, right.CanonicalName, StringComparison.Ordinal)
                || !left.Shape.Equals(right.Shape)
                || left.Aliases.Count != right.Aliases.Count)
            {
                return false;
            }

            for (int index = 0; index < left.Aliases.Count; ++index)
            {
                if (!string.Equals(left.Aliases[index], right.Aliases[index], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static void ValidateResourceCompatibility(
            ShaderResourceBindingReflection logical,
            ShaderResourceBindingReflection spirv)
        {
            if (logical.Key.Type != spirv.Key.Type)
            {
                throw Failure(
                    $"SPIR-V resource {spirv.Name} has binding class {spirv.Key.Type}, "
                    + $"but logical resource {logical.Key} requires {logical.Key.Type}.");
            }

            ShaderResourceShape expected = logical.Shape;
            ShaderResourceShape actual = spirv.Shape;
            if (!KindsAreCompatible(expected.Kind, actual.Kind)
                || expected.Dimension != actual.Dimension
                || !AccessIsCompatible(expected.Access, actual.Access)
                || !expected.Array.Equals(actual.Array)
                || !SamplerKindsAreCompatible(expected.SamplerKind, actual.SamplerKind)
                || expected.CounterKind != actual.CounterKind
                || !StructureStridesAreCompatible(expected, actual))
            {
                throw Failure(
                    $"SPIR-V resource {spirv.Name} shape is incompatible with logical resource {logical.Key}.");
            }

            string? constantBufferMismatch = FindConstantBufferLayoutMismatch(
                logical.ConstantBufferLayout,
                spirv.ConstantBufferLayout);
            if (constantBufferMismatch is not null)
            {
                throw Failure(
                    $"SPIR-V constant-buffer layout for {spirv.Name} does not match "
                    + $"DXIL logical layout {logical.Key}: {constantBufferMismatch}");
            }
        }

        private static string? FindConstantBufferLayoutMismatch(
            ShaderConstantBufferLayout? logical,
            ShaderConstantBufferLayout? spirv)
        {
            if (ReferenceEquals(logical, spirv))
            {
                return null;
            }

            if (logical is null || spirv is null)
            {
                return "one backend did not reflect a constant-buffer layout.";
            }

            long alignedSpirvByteSize = (checked((long)spirv.ByteSize) + 15L) & ~15L;
            if (logical.ByteSize != alignedSpirvByteSize)
            {
                return $"declared byte size is {spirv.ByteSize} "
                    + $"({alignedSpirvByteSize} after HLSL cbuffer tail alignment), "
                    + $"expected {logical.ByteSize}.";
            }

            if (logical.Variables.Count != spirv.Variables.Count)
            {
                return $"variable count is {spirv.Variables.Count}, "
                    + $"expected {logical.Variables.Count}.";
            }

            for (int index = 0; index < logical.Variables.Count; ++index)
            {
                ShaderValueMember expected = logical.Variables[index];
                ShaderValueMember actual = spirv.Variables[index];
                string path = $"{logical.Name}.{expected.Name}";
                string? mismatch = FindValueMemberMismatch(path, expected, actual);
                if (mismatch is not null)
                {
                    return mismatch;
                }
            }

            return null;
        }

        private static string? FindValueMemberMismatch(
            string path,
            ShaderValueMember logical,
            ShaderValueMember spirv)
        {
            if (!string.Equals(logical.Name, spirv.Name, StringComparison.Ordinal))
            {
                return $"{path} is named {spirv.Name} in SPIR-V.";
            }

            if (logical.ByteOffset != spirv.ByteOffset)
            {
                return $"{path} byte offset is {spirv.ByteOffset}, "
                    + $"expected {logical.ByteOffset}.";
            }

            if (logical.ByteSize != spirv.ByteSize)
            {
                return $"{path} byte size is {spirv.ByteSize}, "
                    + $"expected {logical.ByteSize}.";
            }

            return FindValueLayoutMismatch(path, logical.Value, spirv.Value);
        }

        private static string? FindValueLayoutMismatch(
            string path,
            ShaderValueLayout logical,
            ShaderValueLayout spirv)
        {
            bool shapeMatches = logical.Kind == ShaderValueKind.Matrix
                && spirv.Kind == ShaderValueKind.Matrix
                    ? MatrixStorageShapesMatch(logical, spirv)
                    : logical.Rows == spirv.Rows
                        && logical.Columns == spirv.Columns
                        && logical.MatrixMajorOrder == spirv.MatrixMajorOrder;
            if (logical.Kind != spirv.Kind
                || logical.ScalarType != spirv.ScalarType
                || !shapeMatches
                || !logical.Array.Equals(spirv.Array)
                || logical.ByteSize != spirv.ByteSize)
            {
                return $"{path} value layout differs: SPIR-V is "
                    + $"{spirv.Kind}/{spirv.ScalarType}/"
                    + $"{spirv.Rows}x{spirv.Columns}, byteSize={spirv.ByteSize}, "
                    + $"major={spirv.MatrixMajorOrder}; DXIL is "
                    + $"{logical.Kind}/{logical.ScalarType}/"
                    + $"{logical.Rows}x{logical.Columns}, byteSize={logical.ByteSize}, "
                    + $"major={logical.MatrixMajorOrder}.";
            }

            if (logical.ProvenArrayStride.HasValue
                && logical.ProvenArrayStride != spirv.ProvenArrayStride)
            {
                return $"{path} array stride is {spirv.ProvenArrayStride}, "
                    + $"expected {logical.ProvenArrayStride}.";
            }

            if (logical.ProvenMatrixStride.HasValue
                && logical.ProvenMatrixStride != spirv.ProvenMatrixStride)
            {
                return $"{path} matrix stride is {spirv.ProvenMatrixStride}, "
                    + $"expected {logical.ProvenMatrixStride}.";
            }

            if (logical.Members.Count != spirv.Members.Count)
            {
                return $"{path} member count is {spirv.Members.Count}, "
                    + $"expected {logical.Members.Count}.";
            }

            for (int index = 0; index < logical.Members.Count; ++index)
            {
                ShaderValueMember expected = logical.Members[index];
                ShaderValueMember actual = spirv.Members[index];
                string? mismatch = FindValueMemberMismatch(
                    $"{path}.{expected.Name}",
                    expected,
                    actual);
                if (mismatch is not null)
                {
                    return mismatch;
                }
            }

            return null;
        }

        private static bool MatrixStorageShapesMatch(
            ShaderValueLayout logical,
            ShaderValueLayout spirv)
        {
            if (!logical.MatrixMajorOrder.HasValue
                || !spirv.MatrixMajorOrder.HasValue)
            {
                return false;
            }

            (uint VectorCount, uint ComponentCount) logicalStorage =
                logical.MatrixMajorOrder == ShaderMatrixMajorOrder.RowMajor
                    ? (logical.Rows, logical.Columns)
                    : (logical.Columns, logical.Rows);
            (uint VectorCount, uint ComponentCount) spirvStorage =
                spirv.MatrixMajorOrder == ShaderMatrixMajorOrder.RowMajor
                    ? (spirv.Rows, spirv.Columns)
                    : (spirv.Columns, spirv.Rows);
            return logicalStorage == spirvStorage;
        }

        private static bool KindsAreCompatible(
            ShaderResourceKind logical,
            ShaderResourceKind spirv)
        {
            return logical == spirv
                || (spirv == ShaderResourceKind.StorageBuffer
                    && logical is ShaderResourceKind.StructuredBuffer
                        or ShaderResourceKind.ByteAddressBuffer);
        }

        private static bool AccessIsCompatible(
            ShaderResourceAccess logical,
            ShaderResourceAccess spirv)
        {
            return logical == spirv
                || (logical == ShaderResourceAccess.ReadWrite
                    && spirv == ShaderResourceAccess.WriteOnly);
        }

        private static bool SamplerKindsAreCompatible(
            ShaderSamplerKind? logical,
            ShaderSamplerKind? spirv)
        {
            return logical == spirv
                || logical == ShaderSamplerKind.Unknown
                || spirv == ShaderSamplerKind.Unknown;
        }

        private static bool StructureStridesAreCompatible(
            ShaderResourceShape logical,
            ShaderResourceShape spirv)
        {
            if (logical.Kind == ShaderResourceKind.ByteAddressBuffer
                && spirv.Kind == ShaderResourceKind.StorageBuffer)
            {
                return spirv.StructureStride == sizeof(uint);
            }

            return logical.StructureStride == spirv.StructureStride;
        }

        private static Dictionary<ShaderBindingKey, VulkanShaderBindingMapping> ValidateTargetLayout(
            IReadOnlyDictionary<ShaderBindingKey, SpirvDescriptor> matches,
            VulkanShaderBackendLayout targetLayout,
            ShaderArtifactReflection logicalReflection)
        {
            Dictionary<ShaderBindingKey, ShaderLogicalBinding> logicalBindings =
                CollectLogicalBindings(logicalReflection);
            Dictionary<ShaderBindingKey, VulkanShaderBindingMapping> mappings = new();
            foreach (VulkanShaderBindingMapping mapping in targetLayout.Bindings)
            {
                if (!matches.ContainsKey(mapping.LogicalBinding))
                {
                    throw Failure(
                        $"Vulkan layout contains unknown logical binding {mapping.LogicalBinding}.");
                }

                VulkanDescriptorKind expectedKind =
                    ShaderBackendLayoutSemantics.GetVulkanDescriptorKind(
                        logicalBindings[mapping.LogicalBinding]);
                if (mapping.DescriptorKind != expectedKind)
                {
                    throw Failure(
                        $"Vulkan layout maps {mapping.LogicalBinding} as {mapping.DescriptorKind}, "
                        + $"but its logical resource shape requires {expectedKind}.");
                }

                mappings.Add(mapping.LogicalBinding, mapping);
            }

            if (mappings.Count != matches.Count)
            {
                foreach (ShaderBindingKey key in matches.Keys)
                {
                    if (!mappings.ContainsKey(key))
                    {
                        throw Failure($"Vulkan layout is missing logical binding {key}.");
                    }
                }
            }

            return mappings;
        }

        private static byte[] ToByteArray(uint[] words)
        {
            byte[] result = new byte[checked(words.Length * sizeof(uint))];
            for (int index = 0; index < words.Length; ++index)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    result.AsSpan(index * sizeof(uint), sizeof(uint)),
                    words[index]);
            }

            return result;
        }

        private static ShaderCompilerException InvalidRequest(string message)
        {
            return new ShaderCompilerException(
                ShaderCompilerErrorCode.InvalidRequest,
                message,
                requestedProfile: "spirv-binding-remap");
        }

        private static ShaderCompilerException Failure(
            string message,
            Exception? innerException = null)
        {
            return new ShaderCompilerException(
                ShaderCompilerErrorCode.CompileFailed,
                message,
                requestedProfile: "spirv-binding-remap",
                innerException: innerException);
        }

        private sealed class DecorationRecord
        {
            public int? DescriptorSetValueWord { get; set; }
            public int? BindingValueWord { get; set; }
        }

        private sealed class SpirvDescriptor
        {
            public uint Id { get; }
            public string Name { get; }
            public uint DescriptorSet { get; }
            public uint Binding { get; }
            public int DescriptorSetValueWord { get; }
            public int BindingValueWord { get; }

            public SpirvDescriptor(
                uint id,
                string name,
                uint descriptorSet,
                uint binding,
                int descriptorSetValueWord,
                int bindingValueWord)
            {
                Id = id;
                Name = name;
                DescriptorSet = descriptorSet;
                Binding = binding;
                DescriptorSetValueWord = descriptorSetValueWord;
                BindingValueWord = bindingValueWord;
            }
        }

        private sealed class SpirvModule
        {
            public uint[] Words { get; }
            public IReadOnlyDictionary<uint, SpirvDescriptor> Descriptors { get; }
            public IReadOnlyDictionary<(uint Set, uint Binding), SpirvDescriptor> Locations { get; }

            public SpirvModule(
                uint[] words,
                IReadOnlyDictionary<uint, SpirvDescriptor> descriptors,
                IReadOnlyDictionary<(uint Set, uint Binding), SpirvDescriptor> locations)
            {
                Words = words;
                Descriptors = descriptors;
                Locations = locations;
            }

            public byte[] ToByteArray()
            {
                return SpirvBindingRemapper.ToByteArray(Words);
            }
        }
    }
}
