using System;
using System.Linq;
using System.Reflection;
using SharpGPU;
using SharpShader.Compilation;
using SharpShader.CSharp.ShaderLib;
using SharpShader.HLSLCrossCompiler;
using SharpShader.SharpGPU;
using Xunit;

namespace SharpShader.Tests
{
    public sealed class SharpGpuWaveAndCooperativeMatrixAdapterTests
    {
        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public void BindingTablePlan_ShouldAllowFeedbackTextureOnDx12AndRejectVulkanMetal()
        {
            ShaderBindingKey key = new(
                0,
                0,
                ShaderBindingClass.UnorderedAccess);
            ShaderInterfaceLayout layout = new(new[]
            {
                new ShaderLogicalBinding(
                    key,
                    "SamplerFeedback",
                    aliases: null,
                    new ShaderResourceShape(
                        ShaderResourceKind.FeedbackTexture,
                        ShaderResourceDimension.Texture2D,
                        ShaderResourceAccess.ReadWrite),
                    ShaderStageMask.Pixel),
            });
            ShaderBackendLayouts backends = ShaderBackendLayoutPlanner.Plan(layout);

            SharpGpuBindingTableLayoutPlan dx12 =
                SharpGpuShaderInterfaceAdapter.CreateBindingTableLayoutPlan(
                    layout,
                    backends,
                    ERHIBackend.DirectX12);
            RHIBindingTableLayoutDescriptor[] tables =
                dx12.CreateBindingTableLayoutDescriptors();
            Assert.Equal(ERHIBindType.StorageTexture2D, tables[0].Elements.Span[0].Type);

            NotSupportedException vulkan = Assert.Throws<NotSupportedException>(() =>
                SharpGpuShaderInterfaceAdapter.CreateBindingTableLayoutPlan(
                    layout,
                    backends,
                    ERHIBackend.Vulkan));
            NotSupportedException metal = Assert.Throws<NotSupportedException>(() =>
                SharpGpuShaderInterfaceAdapter.CreateBindingTableLayoutPlan(
                    layout,
                    backends,
                    ERHIBackend.Metal));

            Assert.Contains("sampler-feedback", vulkan.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("sampler-feedback", metal.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public void ShaderSurface_ShouldKeepExistingWaveIntrinsicAndRejectInventedHalTypes()
        {
            Assert.NotNull(
                typeof(Hlsl).GetMethod(
                    nameof(Hlsl.WaveActiveSum),
                    BindingFlags.Public | BindingFlags.Static));
            Assert.Null(
                typeof(Hlsl).GetMethod(
                    "WaveMMA",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance));
            Assert.Null(
                typeof(Hlsl).GetMethod(
                    "CooperativeMatrixMultiply",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance));

            foreach (Type type in typeof(Hlsl).Assembly.GetExportedTypes())
            {
                Assert.DoesNotContain("CooperativeMatrix", type.Name, StringComparison.Ordinal);
                Assert.DoesNotContain("WaveMMA", type.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("FeedbackTexture", type.Name, StringComparison.Ordinal);
                Assert.DoesNotContain("RHI", type.Name, StringComparison.Ordinal);
            }

            string[] shaderReferences = typeof(ShaderInterfaceLayout)
                .Assembly
                .GetReferencedAssemblies()
                .Select(reference => reference.Name!)
                .ToArray();
            string[] gpuReferences = typeof(RHIComputeCapabilities)
                .Assembly
                .GetReferencedAssemblies()
                .Select(reference => reference.Name!)
                .ToArray();
            Assert.DoesNotContain("SharpGPU", shaderReferences);
            Assert.DoesNotContain("SharpShader", gpuReferences);

            foreach (Type type in typeof(SharpGpuWaveAndCooperativeMatrixContract).Assembly.GetExportedTypes())
            {
                Assert.NotEqual("RHIComputeCapabilities", type.Name);
                Assert.NotEqual("RHICooperativeMatrixConfig", type.Name);
                Assert.DoesNotContain("WaveMMA", type.Name, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Null(
                typeof(RHIMachineLearningCapabilities).GetProperty(
                    "CooperativeMatrix",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
            Assert.NotNull(
                typeof(RHIComputeCapabilities).GetProperty(
                    nameof(RHIComputeCapabilities.CooperativeMatrix),
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        }

        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public void CompileTimeContract_ShouldAllowWaveOpsAndFailClosedOnDx12WaveMma()
        {
            ShaderModelVersion sm60 = new(6, 0);
            Assert.True(
                ShaderBackendWaveContract.CanLowerWaveOperations(ShaderBackendKind.DirectX12, sm60));
            Assert.True(
                ShaderBackendWaveContract.CanLowerWaveOperations(ShaderBackendKind.Vulkan, sm60));
            Assert.True(
                ShaderBackendWaveContract.CanLowerWaveOperations(ShaderBackendKind.Metal, sm60));
            Assert.True(
                ShaderBackendWaveContract.AllRequestedTargetsSupportWaveOperations(
                    ShaderProgramTarget.All,
                    sm60));
            Assert.True(
                SharpGpuWaveAndCooperativeMatrixContract.CanLowerWaveOperations(
                    ERHIBackend.DirectX12,
                    sm60));
            Assert.True(
                SharpGpuWaveAndCooperativeMatrixContract.CanLowerWaveOperations(
                    ERHIBackend.Vulkan,
                    sm60));
            Assert.True(
                SharpGpuWaveAndCooperativeMatrixContract.CanLowerWaveOperations(
                    ERHIBackend.Metal,
                    sm60));

            Assert.False(
                SharpGpuWaveAndCooperativeMatrixContract.CanLowerCooperativeMatrix(
                    ERHIBackend.DirectX12));
            Assert.True(
                SharpGpuWaveAndCooperativeMatrixContract.CanLowerCooperativeMatrix(
                    ERHIBackend.Vulkan));
            Assert.True(
                SharpGpuWaveAndCooperativeMatrixContract.CanLowerCooperativeMatrix(
                    ERHIBackend.Metal));

            NotSupportedException dx12 = Assert.Throws<NotSupportedException>(() =>
                SharpGpuWaveAndCooperativeMatrixContract.RequireCooperativeMatrixLowering(
                    ERHIBackend.DirectX12));
            Assert.Contains("WaveMMA", dx12.Message, StringComparison.Ordinal);
            Assert.Contains("Unavailable", dx12.Message, StringComparison.Ordinal);
            SharpGpuWaveAndCooperativeMatrixContract.RequireCooperativeMatrixLowering(
                ERHIBackend.Vulkan);
            SharpGpuWaveAndCooperativeMatrixContract.RequireCooperativeMatrixLowering(
                ERHIBackend.Metal);
        }
    }
}
