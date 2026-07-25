using System;
using System.Collections.Generic;
using SharpShader.Compilation;
using SharpShader.Compilation.Internal;
using SharpShader.HLSLCrossCompiler;
using SharpShader.HLSLCrossCompiler.Internal;
using Xunit;

namespace Infinity.Rendering.Tests
{
    public sealed class MslArtifactReflectionContractTests
    {
        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public void FinalMslArtifactReflection_IsStructuredAndFailsClosed()
        {
            const string trustedSource = """
                /*
                    [[color(6)]]
                */
                #define IGNORED_ATTACHMENT [[color(5)]]
                constant char* PseudoAttribute = "[[color(7)]]";

                struct FragmentOutput
                {
                    float4 Color [[color(0)]];
                };

                fragment FragmentOutput PSMain()
                {
                    return {};
                }
                """;
            MslArtifactReflection trusted = Reflect(trustedSource);
            MslColorAttachmentIoReflection trustedOutput =
                Assert.Single(trusted.ColorOutputs);
            Assert.Equal(0u, trustedOutput.Location);
            Assert.Empty(trusted.ColorInputs);
            Assert.Empty(trusted.TextureBindings);
            Assert.Empty(trusted.RasterOrderGroups);

            AssertParserFailure("""
                struct FragmentOutput
                {
                    float4 Color [[color(0)]];
                };
                fragment Wrapper<FragmentOutput> PSMain() { return {}; }
                """);
            AssertParserFailure("""
                struct FragmentInput
                {
                    float4 Previous [[color(0)]];
                };
                fragment void PSMain(
                    Wrapper<FragmentInput> Input [[stage_in]])
                {
                }
                """);

            AssertParserFailure("""
                struct FragmentOutput
                {
                    float4 Color [[color(0)];
                };
                fragment FragmentOutput PSMain() { return {}; }
                """);
            AssertParserFailure("""
                struct FragmentOutput
                {
                    float4 A [[color(0)]];
                    float4 B [[color(0)]];
                };
                fragment FragmentOutput PSMain() { return {}; }
                """);
            AssertParserFailure("""
                struct FragmentOutput
                {
                    float4 Color [[color(0), unknown_attachment(1)]];
                };
                fragment FragmentOutput PSMain() { return {}; }
                """);
            AssertParserFailure("""
                struct FragmentOutput
                {
                    float4 Color [[color(0), invariant(7)]];
                };
                fragment FragmentOutput PSMain() { return {}; }
                """);
            AssertParserFailure("""
                fragment void PSMain(
                    float4 Previous [[color(0), flat(1)]])
                {
                }
                """);
            AssertParserFailure("""
                struct FragmentOutput
                {
                    Wrapper<float4> Color [[color(0)]];
                };
                fragment FragmentOutput PSMain() { return {}; }
                """);
            AssertParserFailure("""
                fragment void PSMain(
                    Wrapper<float4> Previous [[color(0)]])
                {
                }
                """);
            AssertParserFailure("""
                #if 0
                struct PhantomOutput
                {
                    float4 Phantom [[color(7)]];
                };
                #endif

                struct FragmentOutput
                {
                    float4 Color [[color(0)]];
                };
                fragment FragmentOutput PSMain() { return {}; }
                """);
            AssertParserFailure("""
                fragment float4 PSMain(
                    Wrapper<texture2d<float, access::read_write>> Ordered
                        [[texture(3), raster_order_group(0)]])
                    [[color(0)]]
                {
                    return {};
                }
                """);
            AssertParserFailure("""
                fragment float4 PSMain(
                    texture_magic<float, access::read_write> Ordered
                        [[texture(3), raster_order_group(0)]])
                    [[color(0)]]
                {
                    return {};
                }
                """);
            AssertParserFailure("""
                fragment float4 PSMain(
                    texture2d<float, access::write> Ordered
                        [[texture(3), raster_order_group(0)]])
                    [[color(0)]]
                {
                    return {};
                }
                """);

            ShaderAttachmentPhase rasterOrderedPhase = new(
                0,
                new[]
                {
                    Color(
                        logicalAttachmentId: 3,
                        inputIndex: 0,
                        outputLocation: 0,
                        ordering: ShaderAttachmentOrdering.RasterOrdered),
                });
            ShaderStageIoReflection rasterOutput = new(
                "Color",
                ShaderStageIoDirection.Output,
                location: 0,
                index: 0,
                component: 0,
                ShaderStageIoBuiltIn.None,
                ShaderAttachmentNumericClass.FloatingPoint,
                componentCount: 4);
            (
                ShaderAttachmentInterface RasterContract,
                MslTranslationBindingPlan RasterPlan) =
                CreatePlan(
                    rasterOrderedPhase,
                    new[] { rasterOutput },
                    privateMetalTextureBase: 0);
            AssertValidationFailure(
                RasterContract,
                RasterPlan,
                """
                struct FragmentOutput
                {
                    float4 Color [[color(0)]];
                };
                fragment FragmentOutput PSMain(
                    device float4* Ordered
                        [[buffer(3), raster_order_group(0)]])
                {
                    return {};
                }
                """);
            AssertValidationFailure(
                RasterContract,
                RasterPlan,
                """
                struct FragmentOutput
                {
                    float4 Color [[color(0)]];
                };
                fragment FragmentOutput PSMain(
                    texture2d<float, access::read_write> Ordered
                        [[texture(4), raster_order_group(1)]])
                {
                    return {};
                }
                """);

            ShaderAttachmentDeclaration depth = new(
                logicalAttachmentId: 8,
                inputIndex: null,
                outputLocation: null,
                ShaderAttachmentAspect.Depth,
                ShaderAttachmentNumericClass.DepthStencil,
                ShaderAttachmentSampleMode.SingleSample,
                ShaderAttachmentLayerMode.SingleLayer);
            (
                ShaderAttachmentInterface DepthContract,
                MslTranslationBindingPlan DepthPlan) =
                CreatePlan(
                    new ShaderAttachmentPhase(
                        0,
                        new[] { depth },
                        ShaderDepthStencilAccess.ReadWrite,
                        ShaderDepthExport.DepthGreaterEqual));
            AssertValidationFailure(
                DepthContract,
                DepthPlan,
                """
                struct FragmentOutput
                {
                    float Depth [[depth(less)]];
                };
                fragment FragmentOutput PSMain() { return {}; }
                """);

            (
                ShaderAttachmentInterface LocalInputContract,
                MslTranslationBindingPlan LocalInputPlan) =
                CreatePlan(
                    new ShaderAttachmentPhase(
                        0,
                        new[]
                        {
                            Color(
                                logicalAttachmentId: 3,
                                inputIndex: 0,
                                outputLocation: null),
                        }));
            AssertValidationFailure(
                LocalInputContract,
                LocalInputPlan,
                """
                fragment void PSMain(float4 Previous [[color(2)]])
                {
                }
                """);
        }

        private static MslArtifactReflection Reflect(string source)
        {
            return MslArtifactReflector.Reflect(
                source,
                "PSMain",
                ShaderExecutionStage.Pixel);
        }

        private static void AssertParserFailure(string source)
        {
            ShaderCompilerException exception =
                Assert.Throws<ShaderCompilerException>(() => Reflect(source));
            Assert.Equal(
                ShaderCompilerErrorCode.MslTranslateFailed,
                exception.ErrorCode);
        }

        private static void AssertValidationFailure(
            ShaderAttachmentInterface attachmentInterface,
            MslTranslationBindingPlan bindingPlan,
            string source)
        {
            ShaderCompilerException exception =
                Assert.Throws<ShaderCompilerException>(() =>
                    ShaderAttachmentInterfaceValidator.ValidateMsl(
                        attachmentInterface,
                        Reflect(source),
                        bindingPlan));
            Assert.Equal(
                ShaderCompilerErrorCode.MslTranslateFailed,
                exception.ErrorCode);
        }

        private static (
            ShaderAttachmentInterface Contract,
            MslTranslationBindingPlan Plan) CreatePlan(
                ShaderAttachmentPhase phase,
                IReadOnlyList<ShaderStageIoReflection>? stageOutputs = null,
                uint privateMetalTextureBase = 0)
        {
            ShaderAttachmentInterface contract = new(
                "default",
                "PSMain",
                ShaderExecutionStage.Pixel,
                phase);
            ShaderEntryPointReflection reflection = new(
                "PSMain",
                ShaderExecutionStage.Pixel,
                stageOutputs: stageOutputs);
            MslTranslationBindingPlan plan =
                MslTranslationBindingPlan.Create(
                    "PSMain",
                    ShaderExecutionStage.Pixel,
                    Array.Empty<VulkanShaderBindingMapping>(),
                    new MetalShaderBackendLayout(),
                    contract,
                    reflection,
                    privateAttachmentDescriptorSet: 31,
                    privateMetalTextureBase);
            return (contract, plan);
        }

        private static ShaderAttachmentDeclaration Color(
            uint logicalAttachmentId,
            uint? inputIndex,
            uint? outputLocation,
            ShaderAttachmentOrdering ordering =
                ShaderAttachmentOrdering.None)
        {
            return new ShaderAttachmentDeclaration(
                logicalAttachmentId,
                inputIndex,
                outputLocation,
                ShaderAttachmentAspect.Color,
                ShaderAttachmentNumericClass.FloatingPoint,
                ShaderAttachmentSampleMode.SingleSample,
                ShaderAttachmentLayerMode.SingleLayer,
                ordering: ordering);
        }
    }
}
