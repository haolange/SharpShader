using System;
using System.Collections.Generic;
using SharpGPU;
using SharpShader.Compilation;
using SharpShader.SharpGPU;
using Xunit;

namespace Infinity.Rendering.Tests
{
    public sealed class SharpGpuShaderInterfaceManifestAdapterTests
    {
        private const string Digest =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        [Fact]
        public void ManifestOverload_ShouldResolveUniqueVariantEntryLayoutAndBackend()
        {
            ShaderInterfaceLayout layout = CreateLayout();
            ShaderInterfaceManifest manifest = CreateManifest(
                layout,
                ShaderBackendLayoutPlanner.Plan(layout));

            SharpGpuArgumentTableLayoutPlan plan =
                SharpGpuShaderInterfaceAdapter.CreateArgumentTableLayoutPlan(
                    manifest,
                    "default",
                    "MainCS",
                    ShaderExecutionStage.Compute,
                    ERHIBackend.Vulkan);

            Assert.Equal(ERHIBackend.Vulkan, plan.Backend);
            Assert.Equal(layout.Signature, plan.LogicalLayoutSignature);
            Assert.Single(plan.Bindings);
        }

        [Fact]
        public void ManifestOverload_ShouldFailClosedForMissingVariantEntryOrBackend()
        {
            ShaderInterfaceLayout layout = CreateLayout();
            ShaderBackendLayouts planned = ShaderBackendLayoutPlanner.Plan(layout);
            ShaderInterfaceManifest complete = CreateManifest(layout, planned);

            KeyNotFoundException missingVariant = Assert.Throws<KeyNotFoundException>(() =>
                SharpGpuShaderInterfaceAdapter.CreateArgumentTableLayoutPlan(
                    complete,
                    "missing",
                    "MainCS",
                    ShaderExecutionStage.Compute,
                    ERHIBackend.DirectX12));
            Assert.Contains("variant 'missing'", missingVariant.Message, StringComparison.Ordinal);

            KeyNotFoundException missingEntry = Assert.Throws<KeyNotFoundException>(() =>
                SharpGpuShaderInterfaceAdapter.CreateArgumentTableLayoutPlan(
                    complete,
                    "default",
                    "MainCS",
                    ShaderExecutionStage.Pixel,
                    ERHIBackend.DirectX12));
            Assert.Contains("MainCS", missingEntry.Message, StringComparison.Ordinal);
            Assert.Contains("Pixel", missingEntry.Message, StringComparison.Ordinal);

            ShaderInterfaceManifest dx12Only = CreateManifest(
                layout,
                new ShaderBackendLayouts(layout.Signature, dx12: planned.Dx12));
            ArgumentException missingBackend = Assert.Throws<ArgumentException>(() =>
                SharpGpuShaderInterfaceAdapter.CreateArgumentTableLayoutPlan(
                    dx12Only,
                    "default",
                    "MainCS",
                    ShaderExecutionStage.Compute,
                    ERHIBackend.Vulkan));
            Assert.Contains("Vulkan", missingBackend.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ManifestOverload_ShouldValidateLookupArguments()
        {
            ShaderInterfaceLayout layout = CreateLayout();
            ShaderInterfaceManifest manifest = CreateManifest(
                layout,
                ShaderBackendLayoutPlanner.Plan(layout));

            Assert.Throws<ArgumentException>(() =>
                SharpGpuShaderInterfaceAdapter.CreateArgumentTableLayoutPlan(
                    manifest,
                    " ",
                    "MainCS",
                    ShaderExecutionStage.Compute,
                    ERHIBackend.DirectX12));
            Assert.Throws<ArgumentException>(() =>
                SharpGpuShaderInterfaceAdapter.CreateArgumentTableLayoutPlan(
                    manifest,
                    "default",
                    string.Empty,
                    ShaderExecutionStage.Compute,
                    ERHIBackend.DirectX12));
        }

        private static ShaderInterfaceManifest CreateManifest(
            ShaderInterfaceLayout layout,
            ShaderBackendLayouts backends)
        {
            ShaderInterfaceEntry entry = new(
                "MainCS",
                ShaderExecutionStage.Compute,
                layout.Signature,
                new[]
                {
                    new ShaderArtifactIdentity(
                        ShaderArtifactKind.Dxil,
                        Digest,
                        byteLength: 16,
                        artifactName: "main.dxil"),
                });
            ShaderInterfaceVariant variant = new(
                "default",
                defines: null,
                new[] { entry });

            return new ShaderInterfaceManifest(
                Digest,
                new[] { new ShaderToolchainComponent("SharpShader", "1.0.0") },
                new[] { layout },
                new[] { variant },
                new[] { backends });
        }

        private static ShaderInterfaceLayout CreateLayout()
        {
            return new ShaderInterfaceLayout(new[]
            {
                new ShaderLogicalBinding(
                    new ShaderBindingKey(3, 4, ShaderBindingClass.ShaderResource),
                    "Input",
                    aliases: null,
                    new ShaderResourceShape(
                        ShaderResourceKind.Texture,
                        ShaderResourceDimension.Texture2D,
                        ShaderResourceAccess.ReadOnly),
                    ShaderStageMask.Compute),
            });
        }
    }
}
