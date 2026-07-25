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

        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public void RasterPlan_ShouldPreserveSparseLayeredAndSpecialAccessFacts()
        {
            ShaderBindingKey sampledBinding = new(
                1,
                4,
                ShaderBindingClass.ShaderResource);
            ShaderInterfaceLayout layout = new(new[]
            {
                new ShaderLogicalBinding(
                    sampledBinding,
                    "History",
                    aliases: null,
                    new ShaderResourceShape(
                        ShaderResourceKind.Texture,
                        ShaderResourceDimension.Texture2DArray,
                        ShaderResourceAccess.ReadOnly),
                    ShaderStageMask.Pixel),
            });
            ShaderAttachmentPhase phase = new(
                0,
                new[]
                {
                    ColorAttachment(
                        3,
                        inputIndex: 0,
                        outputLocation: null,
                        layerMode: ShaderAttachmentLayerMode.Layered),
                    ColorAttachment(
                        2,
                        inputIndex: 1,
                        outputLocation: 0,
                        layerMode: ShaderAttachmentLayerMode.Layered,
                        ordering: ShaderAttachmentOrdering.RasterOrdered),
                    ColorAttachment(
                        5,
                        inputIndex: null,
                        outputLocation: null,
                        layerMode: ShaderAttachmentLayerMode.Layered,
                        feedback: ShaderAttachmentFeedback.Sampled,
                        sampledFeedbackBinding: sampledBinding),
                    ColorAttachment(
                        7,
                        inputIndex: null,
                        outputLocation: 2,
                        layerMode: ShaderAttachmentLayerMode.Layered),
                });
            SharpGpuRasterCompatibilityPlan plan =
                SharpGpuShaderInterfaceAdapter.CreateRasterCompatibilityPlan(
                    CreateRasterManifest(layout, phase),
                    "default",
                    "MainPS",
                    ShaderExecutionStage.Pixel);
            RHIAttachmentInterfaceSignature signature =
                plan.AttachmentInterfaceSignature;

            Assert.Equal(8, signature.ColorAttachmentCount);
            Assert.Equal(3, signature.GetColorInputLogicalAttachment(0));
            Assert.Equal(2, signature.GetColorInputLogicalAttachment(1));
            Assert.Equal(2, signature.GetColorOutputLogicalAttachment(0));
            Assert.Equal(
                RHIAttachmentInterfaceSignature.UnboundLogicalAttachment,
                signature.GetColorOutputLogicalAttachment(1));
            Assert.Equal(7, signature.GetColorOutputLogicalAttachment(2));
            Assert.Equal(5, signature.GetSampledFeedbackLogicalAttachment(0));
            Assert.Equal((byte)0x04, signature.RasterOrderedReadWriteMask);
            Assert.Equal((byte)0x2c, signature.LayeredAccessMask);
            Assert.All(plan.Attachments, fact =>
                Assert.Equal(ShaderAttachmentLayerMode.Layered, fact.LayerMode));
        }

        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public void RasterPlan_ShouldValidateMultisampleFormatAndDualSourceSignature()
        {
            ShaderInterfaceLayout layout =
                new(Array.Empty<ShaderLogicalBinding>());
            ShaderAttachmentPhase phase = new(
                0,
                new[]
                {
                    ColorAttachment(
                        0,
                        inputIndex: null,
                        outputLocation: 0,
                        sampleMode: ShaderAttachmentSampleMode.Multisampled),
                    ColorAttachment(
                        0,
                        inputIndex: null,
                        outputLocation: 0,
                        sampleMode: ShaderAttachmentSampleMode.Multisampled,
                        outputIndex: 1),
                });
            SharpGpuRasterCompatibilityPlan plan =
                SharpGpuShaderInterfaceAdapter.CreateRasterCompatibilityPlan(
                    CreateRasterManifest(layout, phase),
                    "default",
                    "MainPS",
                    ShaderExecutionStage.Pixel);
            Assert.True(plan.AttachmentInterfaceSignature.UsesDualSourceColor);
            Assert.Equal(
                0,
                plan.AttachmentInterfaceSignature.GetColorOutputLogicalAttachment(
                    0,
                    outputIndex: 1));

            RHIRasterPipelineDescriptor descriptor = new()
            {
                SampleCount = ERHISampleCount.Count4,
                DepthFormat = ERHIPixelFormat.Unknown,
                ColorFormats = new[] { ERHIPixelFormat.R8G8B8A8_UNorm },
                AttachmentInterface = plan.AttachmentInterfaceSignature,
                RenderState = new RHIRenderStateDescriptor
                {
                    BlendState = new RHIBlendStateDescriptor
                    {
                        BlendDescriptor0 = new RHIBlendDescriptor
                        {
                            BlendEnable = true,
                            SrcBlendColor = ERHIBlendMode.SecondarySourceColor,
                        },
                    },
                },
            };
            plan.ValidateRasterPipelineDescriptor(descriptor);

            RHIRasterPipelineDescriptor sampleMismatch = descriptor;
            sampleMismatch.SampleCount = ERHISampleCount.None;
            Assert.Throws<ArgumentException>(() =>
                plan.ValidateRasterPipelineDescriptor(sampleMismatch));
            RHIRasterPipelineDescriptor formatMismatch = descriptor;
            formatMismatch.ColorFormats = new[] { ERHIPixelFormat.R8G8B8A8_UInt };
            Assert.Throws<ArgumentException>(() =>
                plan.ValidateRasterPipelineDescriptor(formatMismatch));
        }

        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public void RasterPlan_ShouldRejectSecondarySourceOnIndependentTargetOne()
        {
            ShaderInterfaceLayout layout =
                new(Array.Empty<ShaderLogicalBinding>());
            ShaderAttachmentPhase phase = new(
                0,
                new[]
                {
                    ColorAttachment(0, null, 0),
                    ColorAttachment(1, null, 1),
                });
            SharpGpuRasterCompatibilityPlan plan =
                SharpGpuShaderInterfaceAdapter.CreateRasterCompatibilityPlan(
                    CreateRasterManifest(layout, phase),
                    "default",
                    "MainPS",
                    ShaderExecutionStage.Pixel);
            RHIRasterPipelineDescriptor descriptor = new()
            {
                SampleCount = ERHISampleCount.None,
                DepthFormat = ERHIPixelFormat.Unknown,
                ColorFormats = new[]
                {
                    ERHIPixelFormat.R8G8B8A8_UNorm,
                    ERHIPixelFormat.R8G8B8A8_UNorm,
                },
                AttachmentInterface = plan.AttachmentInterfaceSignature,
                RenderState = new RHIRenderStateDescriptor
                {
                    BlendState = new RHIBlendStateDescriptor
                    {
                        IndependentBlend = true,
                        BlendDescriptor1 = new RHIBlendDescriptor
                        {
                            BlendEnable = true,
                            SrcBlendColor = ERHIBlendMode.SecondarySourceColor,
                        },
                    },
                },
            };

            Assert.Throws<ArgumentException>(() =>
                plan.ValidateRasterPipelineDescriptor(descriptor));
        }

        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public void ArgumentTablePlan_ShouldRejectInputAttachmentResources()
        {
            ShaderBindingKey key = new(
                0,
                0,
                ShaderBindingClass.ShaderResource);
            ShaderInterfaceLayout layout = new(new[]
            {
                new ShaderLogicalBinding(
                    key,
                    "LocalInput",
                    aliases: null,
                    new ShaderResourceShape(
                        ShaderResourceKind.InputAttachment,
                        ShaderResourceDimension.Texture2D,
                        ShaderResourceAccess.ReadOnly),
                    ShaderStageMask.Pixel),
            });
            ShaderBackendLayouts backends = new(
                layout.Signature,
                dx12: new Dx12ShaderBackendLayout(new[]
                {
                    new Dx12ShaderBindingMapping(
                        key,
                        registerSpace: 0,
                        shaderRegister: 0,
                        ShaderBindingClass.ShaderResource),
                }));

            Assert.Throws<NotSupportedException>(() =>
                SharpGpuShaderInterfaceAdapter.CreateArgumentTableLayoutPlan(
                    layout,
                    backends,
                    ERHIBackend.DirectX12));
        }

        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public void RasterPlan_ShouldMapDepthOnlyReadAccessPrecisely()
        {
            ShaderInterfaceLayout layout =
                new(Array.Empty<ShaderLogicalBinding>());
            ShaderAttachmentDeclaration depth = new(
                logicalAttachmentId: 0,
                inputIndex: null,
                outputLocation: null,
                ShaderAttachmentAspect.Depth,
                ShaderAttachmentNumericClass.DepthStencil,
                ShaderAttachmentSampleMode.SingleSample,
                ShaderAttachmentLayerMode.SingleLayer);
            SharpGpuRasterCompatibilityPlan plan =
                SharpGpuShaderInterfaceAdapter.CreateRasterCompatibilityPlan(
                    CreateRasterManifest(
                        layout,
                        new ShaderAttachmentPhase(
                            0,
                            new[] { depth },
                            ShaderDepthStencilAccess.ReadOnly)),
                    "default",
                    "MainPS",
                    ShaderExecutionStage.Pixel);

            Assert.Equal(
                ERHISubPassFlags.ReadOnlyDepth,
                plan.AttachmentInterfaceSignature.DepthStencilFlags);
        }
        private static ShaderInterfaceManifest CreateRasterManifest(
            ShaderInterfaceLayout layout,
            ShaderAttachmentPhase phase)
        {
            ShaderInterfaceEntry entry = new(
                "MainPS",
                ShaderExecutionStage.Pixel,
                layout.Signature,
                new ShaderAttachmentInterface(
                    "default",
                    "MainPS",
                    ShaderExecutionStage.Pixel,
                    phase),
                new[]
                {
                    new ShaderArtifactIdentity(
                        ShaderArtifactKind.Dxil,
                        Digest,
                        byteLength: 16,
                        artifactName: "main.dxil"),
                });
            return new ShaderInterfaceManifest(
                Digest,
                new[] { new ShaderToolchainComponent("SharpShader", "1.0") },
                new[] { layout },
                new[]
                {
                    new ShaderInterfaceVariant(
                        "default",
                        defines: null,
                        new[] { entry }),
                },
                new[] { ShaderBackendLayoutPlanner.Plan(layout) },
                ShaderProgramTarget.DirectX12);
        }

        private static ShaderAttachmentDeclaration ColorAttachment(
            uint logicalAttachmentId,
            uint? inputIndex,
            uint? outputLocation,
            ShaderAttachmentSampleMode sampleMode =
                ShaderAttachmentSampleMode.SingleSample,
            ShaderAttachmentLayerMode layerMode =
                ShaderAttachmentLayerMode.SingleLayer,
            uint outputIndex = 0,
            ShaderAttachmentOrdering ordering = ShaderAttachmentOrdering.None,
            ShaderAttachmentFeedback feedback = ShaderAttachmentFeedback.None,
            ShaderBindingKey? sampledFeedbackBinding = null)
        {
            return new ShaderAttachmentDeclaration(
                logicalAttachmentId,
                inputIndex,
                outputLocation,
                ShaderAttachmentAspect.Color,
                ShaderAttachmentNumericClass.FloatingPoint,
                sampleMode,
                layerMode,
                outputIndex,
                outputComponent: 0,
                ordering,
                feedback,
                sampledFeedbackBinding);
        }
        private static ShaderInterfaceManifest CreateManifest(
            ShaderInterfaceLayout layout,
            ShaderBackendLayouts backends)
        {
            ShaderInterfaceEntry entry = new(
                "MainCS",
                ShaderExecutionStage.Compute,
                layout.Signature,
                new ShaderAttachmentInterface(
                    "default",
                    "MainCS",
                    ShaderExecutionStage.Compute),
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
                new[] { backends },
                ShaderProgramTarget.DirectX12);
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
