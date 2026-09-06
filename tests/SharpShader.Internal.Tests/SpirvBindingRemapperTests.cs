using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SharpShader.Compilation;
using SharpShader.Compilation.Internal;
using SharpShader.HLSLCrossCompiler;
using Silk.NET.SPIRV;
using Xunit;

namespace Infinity.Rendering.Tests
{
    public sealed class SpirvBindingRemapperTests
    {
        private const uint OpName = 5;
        private const uint OpDecorate = 71;
        private const uint OpDecorateId = 332;

        [Fact]
        public void Remap_MapsExplicitImplicitAndMixedT0S0B0U0ToUnifiedBindings()
        {
            const string source = """
                cbuffer Constants : register(b0)
                {
                    uint PixelIndex;
                };

                Texture2D<float4> ExplicitTexture : register(t0);
                SamplerState ImplicitSampler;
                RWTexture2D<float4> ImplicitOutput;

                [numthreads(1, 1, 1)]
                void CSMain()
                {
                    ImplicitOutput[uint2(0, 0)] =
                        ExplicitTexture.SampleLevel(ImplicitSampler, float2(PixelIndex, 0), 0);
                }
                """;

            ShaderArtifactReflection logical = CompileDxil(source, "mixed-logical-bindings.hlsl");
            ShaderCompileResult temporary = CompileSpirv(
                source,
                "mixed-temporary-bindings.hlsl",
                CreateShiftedOptions(includeAutoShift: true));
            VulkanShaderBackendLayout target = CreateDenseTargetLayout(logical);
            byte[] inputSnapshot = (byte[])temporary.Bytecode.Clone();

            byte[] remapped = RemapForTest(
                temporary.Bytecode,
                logical,
                target);

            Assert.Equal(inputSnapshot, temporary.Bytecode);
            Assert.NotSame(temporary.Bytecode, remapped);
            Assert.Equal(new uint[] { 0, 1, 2, 3 }, target.Bindings.Select(mapping => mapping.Binding));

            ShaderEntryPointReflection entry = Assert.Single(
                ReflectSpirv(remapped, source, "mixed-remapped-bindings.spv").EntryPoints);
            Assert.Equal(
                new uint[] { 0, 1, 2, 3 },
                entry.Resources.Select(resource => resource.PhysicalLocation.Binding));
            Assert.Equal(
                new[]
                {
                    ShaderBindingClass.ShaderResource,
                    ShaderBindingClass.Sampler,
                    ShaderBindingClass.ConstantBuffer,
                    ShaderBindingClass.UnorderedAccess,
                },
                entry.Resources.Select(resource => resource.Key.Type));
        }

        [Fact]
        public void Remap_DensifiesMultipleLogicalTables()
        {
            const string source = """
                Texture2D<float4> TableFourTexture : register(t0, space4);
                Texture2D<float4> TableNineTexture : register(t0, space9);
                RWStructuredBuffer<float4> TableNineOutput : register(u10, space9);

                [numthreads(1, 1, 1)]
                void CSMain()
                {
                    TableNineOutput[0] =
                        TableFourTexture.Load(int3(0, 0, 0))
                        + TableNineTexture.Load(int3(0, 0, 0));
                }
                """;

            ShaderArtifactReflection logical = CompileDxil(source, "multiple-logical-tables.hlsl");
            ShaderCompileResult temporary = CompileSpirv(
                source,
                "multiple-temporary-tables.hlsl",
                new SpirvCompileOptions
                {
                    BindingShifts = new[]
                    {
                        new SpirvBindingShift(SpirvBindingShiftKind.ShaderResource, 4, 10),
                        new SpirvBindingShift(SpirvBindingShiftKind.ShaderResource, 9, 20),
                        new SpirvBindingShift(SpirvBindingShiftKind.UnorderedAccess, 9, 30),
                    },
                });
            VulkanShaderBackendLayout target = CreateDenseTargetLayout(logical);

            byte[] remapped = RemapForTest(
                temporary.Bytecode,
                logical,
                target);
            ShaderEntryPointReflection entry = Assert.Single(
                ReflectSpirv(remapped, source, "multiple-remapped-tables.spv").EntryPoints);

            Assert.Equal(new uint[] { 0, 1, 1 }, target.Bindings.Select(mapping => mapping.DescriptorSet));
            Assert.Equal(new uint[] { 0, 0, 1 }, target.Bindings.Select(mapping => mapping.Binding));
            Assert.Equal(
                new[] { (0u, 0u), (1u, 0u), (1u, 1u) },
                entry.Resources.Select(
                    resource => (
                        resource.PhysicalLocation.Group,
                        resource.PhysicalLocation.Binding)));
        }

        [Fact]
        public void Remap_PreservesBoundedDescriptorArrayShape()
        {
            const string source = """
                Texture2D<float4> Textures[3] : register(t0);
                SamplerState LinearSampler : register(s0);
                RWStructuredBuffer<float4> Output : register(u0);

                [numthreads(1, 1, 1)]
                void CSMain()
                {
                    Output[0] = Textures[2].SampleLevel(LinearSampler, 0.5.xx, 0);
                }
                """;

            ShaderArtifactReflection logical = CompileDxil(source, "array-logical-bindings.hlsl");
            ShaderCompileResult temporary = CompileSpirv(
                source,
                "array-temporary-bindings.hlsl",
                CreateShiftedOptions(includeAutoShift: false));
            VulkanShaderBackendLayout target = CreateDenseTargetLayout(logical);

            byte[] remapped = RemapForTest(
                temporary.Bytecode,
                logical,
                target);
            ShaderResourceBindingReflection textures = Assert.Single(
                Assert.Single(
                    ReflectSpirv(remapped, source, "array-remapped-bindings.spv").EntryPoints)
                    .Resources,
                resource => resource.Name == "Textures");

            Assert.Equal(3u, textures.Shape.Array.BoundedElementCount);
            Assert.Equal(
                target.Bindings.Single(mapping => mapping.LogicalBinding.Type == ShaderBindingClass.ShaderResource).Binding,
                textures.PhysicalLocation.Binding);
        }

        [Fact]
        public void Remap_RejectsDuplicateStableResourceNames()
        {
            const string source = """
                Texture2D<float4> TextureA : register(t0);
                Texture2D<float4> TextureB : register(t1);
                RWStructuredBuffer<float4> Output : register(u0);

                [numthreads(1, 1, 1)]
                void CSMain()
                {
                    Output[0] = TextureA.Load(int3(0, 0, 0)) + TextureB.Load(int3(0, 0, 0));
                }
                """;

            ShaderArtifactReflection logical = CompileDxil(source, "duplicate-name-logical.hlsl");
            ShaderCompileResult temporary = CompileSpirv(
                source,
                "duplicate-name-temporary.hlsl",
                CreateShiftedOptions(includeAutoShift: false));
            byte[] duplicateNames = RenameResource(
                temporary.Bytecode,
                "TextureB",
                "TextureA");

            ShaderCompilerException exception = Assert.Throws<ShaderCompilerException>(
                () => RemapForTest(
                    duplicateNames,
                    logical,
                    CreateDenseTargetLayout(logical)));

            Assert.Equal(ShaderCompilerErrorCode.CompileFailed, exception.ErrorCode);
            Assert.Contains("name TextureA is not unique", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Remap_RejectsMissingDuplicateAndConflictingDirectDecorations()
        {
            const string source = """
                Texture2D<float4> InputA : register(t0);
                Texture2D<float4> InputB : register(t1);
                RWStructuredBuffer<float4> Output : register(u0);

                [numthreads(1, 1, 1)]
                void CSMain()
                {
                    Output[0] = InputA.Load(int3(0, 0, 0)) + InputB.Load(int3(0, 0, 0));
                }
                """;

            ShaderArtifactReflection logical = CompileDxil(source, "decoration-negative-logical.hlsl");
            ShaderCompileResult temporary = CompileSpirv(
                source,
                "decoration-negative-temporary.hlsl",
                CreateShiftedOptions(includeAutoShift: false));
            VulkanShaderBackendLayout target = CreateDenseTargetLayout(logical);

            ShaderCompilerException missing = Assert.Throws<ShaderCompilerException>(
                () => RemapForTest(
                    RemoveFirstDecoration(temporary.Bytecode, Decoration.Binding),
                    logical,
                    target));
            Assert.Contains("must have both DescriptorSet and Binding", missing.Message, StringComparison.Ordinal);

            ShaderCompilerException duplicate = Assert.Throws<ShaderCompilerException>(
                () => RemapForTest(
                    DuplicateFirstDecoration(temporary.Bytecode, Decoration.Binding),
                    logical,
                    target));
            Assert.Contains("duplicate Binding decorations", duplicate.Message, StringComparison.Ordinal);

            ShaderCompilerException conflict = Assert.Throws<ShaderCompilerException>(
                () => RemapForTest(
                    CollideFirstTwoBindings(temporary.Bytecode),
                    logical,
                    target));
            Assert.Contains("duplicate physical descriptor location", conflict.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Remap_RejectsIndirectDecorationFormsBeforeReflection()
        {
            const string source = """
                Texture2D<float4> Input : register(t0);
                RWStructuredBuffer<float4> Output : register(u0);

                [numthreads(1, 1, 1)]
                void CSMain()
                {
                    Output[0] = Input.Load(int3(0, 0, 0));
                }
                """;

            ShaderArtifactReflection logical = CompileDxil(source, "indirect-decoration-logical.hlsl");
            ShaderCompileResult temporary = CompileSpirv(
                source,
                "indirect-decoration-temporary.hlsl",
                CreateShiftedOptions(includeAutoShift: false));
            byte[] indirect = ReplaceFirstDecorationOpcode(
                temporary.Bytecode,
                Decoration.Binding,
                OpDecorateId);

            ShaderCompilerException exception = Assert.Throws<ShaderCompilerException>(
                () => RemapForTest(
                    indirect,
                    logical,
                    CreateDenseTargetLayout(logical)));

            Assert.Contains("indirect decoration form", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Remap_RejectsMissingOrTypeIncompatibleTargetPlan()
        {
            const string source = """
                Texture2D<float4> Input : register(t0);
                RWStructuredBuffer<float4> Output : register(u0);

                [numthreads(1, 1, 1)]
                void CSMain()
                {
                    Output[0] = Input.Load(int3(0, 0, 0));
                }
                """;

            ShaderArtifactReflection logical = CompileDxil(source, "plan-negative-logical.hlsl");
            ShaderCompileResult temporary = CompileSpirv(
                source,
                "plan-negative-temporary.hlsl",
                CreateShiftedOptions(includeAutoShift: false));
            VulkanShaderBackendLayout valid = CreateDenseTargetLayout(logical);
            VulkanShaderBindingMapping first = valid.Bindings[0];
            VulkanShaderBackendLayout missing = new(valid.Bindings.Skip(1));
            VulkanShaderBackendLayout wrongKind = new(
                valid.Bindings.Select(mapping => mapping.LogicalBinding == first.LogicalBinding
                    ? new VulkanShaderBindingMapping(
                        mapping.LogicalBinding,
                        mapping.DescriptorSet,
                        mapping.Binding,
                        VulkanDescriptorKind.Sampler)
                    : mapping));

            ShaderCompilerException missingException = Assert.Throws<ShaderCompilerException>(
                () => RemapForTest(temporary.Bytecode, logical, missing));
            Assert.Contains("missing logical binding", missingException.Message, StringComparison.Ordinal);

            ShaderCompilerException kindException = Assert.Throws<ShaderCompilerException>(
                () => RemapForTest(temporary.Bytecode, logical, wrongKind));
            Assert.Contains("logical resource shape requires", kindException.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Remap_RejectsUnsupportedHeaderAndMalformedInstructionBounds()
        {
            const string source = """
                Texture2D<float4> Input : register(t0);
                RWStructuredBuffer<float4> Output : register(u0);

                [numthreads(1, 1, 1)]
                void CSMain()
                {
                    Output[0] = Input.Load(int3(0, 0, 0));
                }
                """;

            ShaderArtifactReflection logical = CompileDxil(source, "module-validation-logical.hlsl");
            ShaderCompileResult temporary = CompileSpirv(
                source,
                "module-validation-temporary.hlsl",
                CreateShiftedOptions(includeAutoShift: false));
            VulkanShaderBackendLayout target = CreateDenseTargetLayout(logical);

            byte[] badVersion = (byte[])temporary.Bytecode.Clone();
            WriteWord(badVersion, 1, 0x00010700);
            ShaderCompilerException versionException = Assert.Throws<ShaderCompilerException>(
                () => RemapForTest(badVersion, logical, target));
            Assert.Contains("version 0x00010700 is not supported", versionException.Message, StringComparison.Ordinal);

            byte[] badInstruction = (byte[])temporary.Bytecode.Clone();
            uint firstInstruction = ReadWord(badInstruction, 5);
            WriteWord(
                badInstruction,
                5,
                (uint.MaxValue & 0xFFFF0000u) | (firstInstruction & 0xFFFFu));
            ShaderCompilerException instructionException = Assert.Throws<ShaderCompilerException>(
                () => RemapForTest(badInstruction, logical, target));
            Assert.Contains("extends beyond the artifact boundary", instructionException.Message, StringComparison.Ordinal);
        }

        private static ShaderArtifactReflection CompileDxil(string source, string sourceName)
        {
            ShaderCompileRequest request = CreateRequest(
                source,
                sourceName,
                ShaderTargetKind.Dxil,
                SpirvCompileOptions.Default);
            ShaderCompileResult result = Compile(request);
            return DxilArtifactReflector.Reflect(request, result);
        }

        private static ShaderCompileResult CompileSpirv(
            string source,
            string sourceName,
            SpirvCompileOptions options)
        {
            return Compile(CreateRequest(
                source,
                sourceName,
                ShaderTargetKind.SpirV,
                options));
        }

        private static ShaderArtifactReflection ReflectSpirv(
            byte[] bytecode,
            string source,
            string sourceName)
        {
            ShaderCompileRequest request = CreateRequest(
                source,
                sourceName,
                ShaderTargetKind.SpirV,
                SpirvCompileOptions.Default);
            return SpirvArtifactReflector.Reflect(
                request,
                new ShaderCompileResult { Bytecode = bytecode });
        }

        private static ShaderCompileRequest CreateRequest(
            string source,
            string sourceName,
            ShaderTargetKind target,
            SpirvCompileOptions options)
        {
            return new ShaderCompileRequest
            {
                Source = source,
                SourceName = sourceName,
                EntryPoint = "CSMain",
                Stage = ShaderStageKind.Compute,
                ShaderModel = new ShaderModelVersion(6, 6),
                Target = target,
                SpirvOptions = options,
            };
        }

        private static ShaderCompileResult Compile(ShaderCompileRequest request)
        {
            try
            {
                return HLSLCrossCompiler.Compile(request);
            }
            catch (ShaderCompilerException exception)
            {
                throw new Xunit.Sdk.XunitException(
                    $"{exception.Message}{Environment.NewLine}{exception.Diagnostics}");
            }
        }

        private static SpirvCompileOptions CreateShiftedOptions(bool includeAutoShift)
        {
            return new SpirvCompileOptions
            {
                TargetEnvironment = "vulkan1.2",
                BindingShifts = new[]
                {
                    new SpirvBindingShift(SpirvBindingShiftKind.ShaderResource, 0, 0),
                    new SpirvBindingShift(SpirvBindingShiftKind.Sampler, 0, 100),
                    new SpirvBindingShift(SpirvBindingShiftKind.ConstantBuffer, 0, 200),
                    new SpirvBindingShift(SpirvBindingShiftKind.UnorderedAccess, 0, 300),
                },
                AdditionalArguments = includeAutoShift
                    ? new[] { "-fvk-auto-shift-bindings" }
                    : Array.Empty<string>(),
            };
        }

        private static VulkanShaderBackendLayout CreateDenseTargetLayout(
            ShaderArtifactReflection logical)
        {
            ShaderLogicalBinding[] bindings = logical.EntryPoints
                .SelectMany(entryPoint => entryPoint.Resources)
                .GroupBy(resource => resource.Key)
                .Select(group => group.First().LogicalBinding)
                .OrderBy(binding => binding.Key.Table)
                .ThenBy(binding => binding.Key.Slot)
                .ThenBy(binding => binding.Key.Type)
                .ToArray();
            Dictionary<uint, uint> physicalSets = bindings
                .Select(binding => binding.Key.Table)
                .Distinct()
                .OrderBy(table => table)
                .Select((table, index) => (table, index))
                .ToDictionary(pair => pair.table, pair => checked((uint)pair.index));
            List<VulkanShaderBindingMapping> mappings = new();
            foreach (IGrouping<uint, ShaderLogicalBinding> table in
                bindings.GroupBy(binding => binding.Key.Table))
            {
                uint bindingIndex = 0;
                foreach (ShaderLogicalBinding binding in table)
                {
                    mappings.Add(new VulkanShaderBindingMapping(
                        binding.Key,
                        physicalSets[table.Key],
                        bindingIndex++,
                        ShaderBackendLayoutSemantics.GetVulkanDescriptorKind(binding)));
                }
            }

            return new VulkanShaderBackendLayout(mappings);
        }

        private static byte[] RenameResource(
            byte[] bytecode,
            string oldName,
            string newName)
        {
            byte[] oldBytes = Encoding.UTF8.GetBytes(oldName);
            byte[] newBytes = Encoding.UTF8.GetBytes(newName);
            Assert.Equal(oldBytes.Length, newBytes.Length);
            byte[] result = (byte[])bytecode.Clone();
            foreach (Instruction instruction in EnumerateInstructions(result))
            {
                if (instruction.Opcode != OpName)
                {
                    continue;
                }

                int nameOffset = checked((instruction.WordOffset + 2) * sizeof(uint));
                ReadOnlySpan<byte> candidate = result.AsSpan(nameOffset, oldBytes.Length);
                if (candidate.SequenceEqual(oldBytes)
                    && result[nameOffset + oldBytes.Length] == 0)
                {
                    newBytes.CopyTo(result, nameOffset);
                    return result;
                }
            }

            throw new Xunit.Sdk.XunitException($"SPIR-V OpName {oldName} was not found.");
        }

        private static byte[] RemoveFirstDecoration(
            byte[] bytecode,
            Decoration decoration)
        {
            byte[] result = (byte[])bytecode.Clone();
            Instruction instruction = FindFirstDecoration(result, decoration);
            for (int index = 0; index < instruction.WordCount; ++index)
            {
                WriteWord(result, instruction.WordOffset + index, 1u << 16);
            }

            return result;
        }

        private static byte[] DuplicateFirstDecoration(
            byte[] bytecode,
            Decoration decoration)
        {
            Instruction instruction = FindFirstDecoration(bytecode, decoration);
            int instructionByteOffset = checked(instruction.WordOffset * sizeof(uint));
            int instructionByteCount = checked(instruction.WordCount * sizeof(uint));
            byte[] result = new byte[checked(bytecode.Length + instructionByteCount)];
            Buffer.BlockCopy(bytecode, 0, result, 0, instructionByteOffset);
            Buffer.BlockCopy(
                bytecode,
                instructionByteOffset,
                result,
                instructionByteOffset,
                instructionByteCount);
            Buffer.BlockCopy(
                bytecode,
                instructionByteOffset,
                result,
                instructionByteOffset + instructionByteCount,
                bytecode.Length - instructionByteOffset);
            return result;
        }

        private static byte[] CollideFirstTwoBindings(byte[] bytecode)
        {
            byte[] result = (byte[])bytecode.Clone();
            Instruction[] bindings = EnumerateInstructions(result)
                .Where(instruction => IsDecoration(result, instruction, Decoration.Binding))
                .Take(2)
                .ToArray();
            Assert.Equal(2, bindings.Length);
            uint firstBinding = ReadWord(result, bindings[0].WordOffset + 3);
            WriteWord(result, bindings[1].WordOffset + 3, firstBinding);

            Instruction[] descriptorSets = EnumerateInstructions(result)
                .Where(instruction => IsDecoration(result, instruction, Decoration.DescriptorSet))
                .ToArray();
            uint firstId = ReadWord(result, bindings[0].WordOffset + 1);
            uint secondId = ReadWord(result, bindings[1].WordOffset + 1);
            uint firstSet = ReadDecorationValue(result, descriptorSets, firstId);
            Instruction secondSet = descriptorSets.Single(
                instruction => ReadWord(result, instruction.WordOffset + 1) == secondId);
            WriteWord(result, secondSet.WordOffset + 3, firstSet);
            return result;
        }

        private static byte[] ReplaceFirstDecorationOpcode(
            byte[] bytecode,
            Decoration decoration,
            uint opcode)
        {
            byte[] result = (byte[])bytecode.Clone();
            Instruction instruction = FindFirstDecoration(result, decoration);
            WriteWord(
                result,
                instruction.WordOffset,
                checked(((uint)instruction.WordCount << 16) | opcode));
            return result;
        }

        private static Instruction FindFirstDecoration(
            byte[] bytecode,
            Decoration decoration)
        {
            return EnumerateInstructions(bytecode).First(
                instruction => IsDecoration(bytecode, instruction, decoration));
        }

        private static bool IsDecoration(
            byte[] bytecode,
            Instruction instruction,
            Decoration decoration)
        {
            return instruction.Opcode == OpDecorate
                && instruction.WordCount == 4
                && ReadWord(bytecode, instruction.WordOffset + 2) == (uint)decoration;
        }

        private static uint ReadDecorationValue(
            byte[] bytecode,
            IEnumerable<Instruction> decorations,
            uint targetId)
        {
            Instruction instruction = decorations.Single(
                candidate => ReadWord(bytecode, candidate.WordOffset + 1) == targetId);
            return ReadWord(bytecode, instruction.WordOffset + 3);
        }

        private static IEnumerable<Instruction> EnumerateInstructions(byte[] bytecode)
        {
            Assert.Equal(0, bytecode.Length % sizeof(uint));
            int wordLength = bytecode.Length / sizeof(uint);
            for (int offset = 5; offset < wordLength;)
            {
                uint instructionWord = ReadWord(bytecode, offset);
                int wordCount = checked((int)(instructionWord >> 16));
                Assert.True(wordCount > 0, $"Invalid instruction at word {offset}.");
                Assert.True(wordCount <= wordLength - offset);
                yield return new Instruction(
                    offset,
                    wordCount,
                    instructionWord & 0xFFFFu);
                offset += wordCount;
            }
        }

        private static byte[] RemapForTest(
            byte[] bytecode,
            ShaderArtifactReflection reflection,
            VulkanShaderBackendLayout targetLayout)
        {
            return SpirvBindingRemapper.Remap(
                bytecode,
                reflection,
                targetLayout,
                Array.Empty<ShaderAttachmentInterface>(),
                SpirvBindingRemapper.GetPrivateAttachmentDescriptorSet(
                    targetLayout));
        }
        private static uint ReadWord(byte[] bytecode, int wordOffset)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(
                bytecode.AsSpan(wordOffset * sizeof(uint), sizeof(uint)));
        }

        private static void WriteWord(byte[] bytecode, int wordOffset, uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytecode.AsSpan(wordOffset * sizeof(uint), sizeof(uint)),
                value);
        }

        private readonly record struct Instruction(
            int WordOffset,
            int WordCount,
            uint Opcode);
    }
}
