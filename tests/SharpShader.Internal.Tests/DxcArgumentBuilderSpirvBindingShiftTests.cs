using System;
using System.Collections.Generic;
using SharpShader.HLSLCrossCompiler;
using SharpShader.HLSLCrossCompiler.Internal;
using Xunit;

namespace SharpShader.Internal.Tests
{
    public sealed class DxcArgumentBuilderSpirvBindingShiftTests
    {
        [Fact]
        public void BuildArguments_EmitsStructuredShiftsInStableKindAndSpaceOrder()
        {
            SpirvBindingShift[] shifts =
            {
                new(SpirvBindingShiftKind.UnorderedAccess, 9, 400),
                new(SpirvBindingShiftKind.ShaderResource, 9, 20),
                new(SpirvBindingShiftKind.ConstantBuffer, 7, 200),
                new(SpirvBindingShiftKind.Sampler, 3, 100),
                new(SpirvBindingShiftKind.ShaderResource, 2, 10),
            };
            SpirvBindingShift[] snapshot = (SpirvBindingShift[])shifts.Clone();
            ShaderCompileRequest request = CreateRequest(shifts);

            IReadOnlyList<string> arguments =
                DxcArgumentBuilder.BuildArguments(request, "cs_6_6");

            Assert.Equal(snapshot, shifts);
            Assert.Equal(
                new[]
                {
                    "-fvk-t-shift", "10", "2",
                    "-fvk-t-shift", "20", "9",
                    "-fvk-s-shift", "100", "3",
                    "-fvk-b-shift", "200", "7",
                    "-fvk-u-shift", "400", "9",
                },
                ExtractShiftArguments(arguments));
        }

        [Fact]
        public void BuildArguments_RejectsDuplicateKindAndSpaceIdentity()
        {
            ShaderCompileRequest request = CreateRequest(new[]
            {
                new SpirvBindingShift(SpirvBindingShiftKind.ShaderResource, 4, 10),
                new SpirvBindingShift(SpirvBindingShiftKind.ShaderResource, 4, 20),
            });

            ShaderCompilerException exception = Assert.Throws<ShaderCompilerException>(
                () => DxcArgumentBuilder.BuildArguments(request, "cs_6_6"));

            Assert.Equal(ShaderCompilerErrorCode.InvalidRequest, exception.ErrorCode);
            Assert.Contains("duplicate ShaderResource mapping", exception.Message, StringComparison.Ordinal);
            Assert.Contains("register space 4", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildArguments_RejectsNegativeAndUnknownStructuredShifts()
        {
            ShaderCompilerException negative = Assert.Throws<ShaderCompilerException>(
                () => DxcArgumentBuilder.BuildArguments(
                    CreateRequest(new[]
                    {
                        new SpirvBindingShift(SpirvBindingShiftKind.Sampler, 2, -1),
                    }),
                    "cs_6_6"));
            Assert.Equal(ShaderCompilerErrorCode.InvalidRequest, negative.ErrorCode);
            Assert.Contains("must not be negative", negative.Message, StringComparison.Ordinal);

            ShaderCompilerException unknown = Assert.Throws<ShaderCompilerException>(
                () => DxcArgumentBuilder.BuildArguments(
                    CreateRequest(new[]
                    {
                        new SpirvBindingShift((SpirvBindingShiftKind)int.MaxValue, 0, 0),
                    }),
                    "cs_6_6"));
            Assert.Equal(ShaderCompilerErrorCode.InvalidRequest, unknown.ErrorCode);
            Assert.Contains("is not defined", unknown.Message, StringComparison.Ordinal);
        }

        private static ShaderCompileRequest CreateRequest(
            IReadOnlyList<SpirvBindingShift> shifts)
        {
            return new ShaderCompileRequest
            {
                Source = "[numthreads(1, 1, 1)] void CSMain() {}",
                SourceName = "structured-shifts.hlsl",
                EntryPoint = "CSMain",
                Stage = ShaderStageKind.Compute,
                ShaderModel = new ShaderModelVersion(6, 6),
                Target = ShaderTargetKind.SpirV,
                SpirvOptions = new SpirvCompileOptions
                {
                    BindingShifts = shifts,
                },
            };
        }

        private static string[] ExtractShiftArguments(
            IReadOnlyList<string> arguments)
        {
            List<string> result = new();
            for (int index = 0; index < arguments.Count; ++index)
            {
                if (!arguments[index].StartsWith("-fvk-", StringComparison.Ordinal)
                    || !arguments[index].EndsWith("-shift", StringComparison.Ordinal))
                {
                    continue;
                }

                Assert.True(index + 2 < arguments.Count);
                result.Add(arguments[index]);
                result.Add(arguments[index + 1]);
                result.Add(arguments[index + 2]);
                index += 2;
            }

            return result.ToArray();
        }
    }
}
