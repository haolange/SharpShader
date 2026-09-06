using System;
using System.IO;
using System.Linq;
using SharpShader.Compilation;
using SharpShader.Compilation.Internal;
using SharpShader.HLSLCrossCompiler;
using SharpShader.HLSLCrossCompiler.Internal;
using Xunit;

namespace Infinity.Rendering.Tests
{
    public sealed class SharpShaderAttachmentAbiTests
    {
        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public void FramebufferReadWriteStore_AssignsDeclaredOutput_AndUsesProgrammableBlend()
        {
            const string source = """
                #include "AttachmentABI.hlsl"

                SHARPSHADER_DECLARE_FRAMEBUFFER_READ_WRITE_2D(
                    float4, OrderedColor, 3, 0);

                struct PixelOutput
                {
                    SHARPSHADER_DECLARE_COLOR_OUTPUT(float4, Color, 0);
                };

                PixelOutput PSMain(float4 position : SV_Position)
                {
                    int2 pixel = int2(position.xy);
                    float4 previous = SHARPSHADER_LOAD_FRAMEBUFFER_READ_WRITE(
                        OrderedColor, pixel);
                    PixelOutput output;
                    SHARPSHADER_STORE_FRAMEBUFFER_READ_WRITE(
                        OrderedColor, output.Color, pixel, previous + 1.0);
                    return output;
                }
                """;
            ShaderAttachmentDeclaration declaration = Color(
                logicalAttachmentId: 3,
                inputIndex: 0,
                outputLocation: 0);

            ShaderProgramCompilation compilation = Compile(
                source,
                ShaderProgramTarget.All,
                new ShaderAttachmentPhase(0, new[] { declaration }),
                "attachment-rov.hlsl");

            ShaderInterfaceEntry entry = Assert.Single(
                Assert.Single(compilation.Manifest.Variants).Entries);
            ShaderAttachmentDeclaration frozen =
                entry.AttachmentInterface.Phase!.Attachments[0];
            Assert.Equal(0u, frozen.InputIndex);
            Assert.Equal(0u, frozen.OutputLocation);

            ShaderEntryPointReflection dxilEntry = Assert.Single(
                CompileDxilAndReflect(source, "attachment-rov.hlsl")
                    .EntryPoints);
            Assert.True((dxilEntry.AttachmentRequirements
                & ShaderAttachmentArtifactRequirement.RasterOrderedViews) != 0);

            ShaderEntryPointReflection spirvEntry = Assert.Single(
                ReflectSpirvArtifact(
                    compilation,
                    source,
                    "attachment-rov.hlsl").EntryPoints);
            Assert.True((spirvEntry.AttachmentRequirements
                & ShaderAttachmentArtifactRequirement
                    .FramebufferLocalRead) != 0);
            Assert.False((spirvEntry.AttachmentRequirements
                & ShaderAttachmentArtifactRequirement
                    .OrderedPixelFragmentInterlock) != 0);

            ShaderBackendLayouts backends = Assert.Single(
                compilation.Manifest.BackendLayouts);
            MslTranslationBindingPlan metalPlan =
                MslTranslationBindingPlan.Create(
                    "PSMain",
                    ShaderExecutionStage.Pixel,
                    backends.Vulkan!.Bindings,
                    backends.Metal!,
                    entry.AttachmentInterface,
                    spirvEntry,
                    SpirvBindingRemapper.GetPrivateAttachmentDescriptorSet(
                        backends.Vulkan));
            Assert.True(metalPlan.UsesFramebufferFetch);
            Assert.True(metalPlan.HasPrivateAttachments);

            MslArtifactReflection msl = ReflectMsl(compilation);
            MslColorAttachmentIoReflection output =
                Assert.Single(msl.ColorOutputs);
            Assert.Equal(0u, output.Location);
            Assert.Equal(0u, output.Index);
            MslColorAttachmentIoReflection input =
                Assert.Single(msl.ColorInputs);
            Assert.Equal(0u, input.Location);
            Assert.Empty(msl.TextureBindings);
            Assert.Empty(msl.RasterOrderGroups);
        }
        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public void LayeredFramebufferReadWrite_CompilesAsArrayShapeAcrossAllArtifacts()
        {
            const string source = """
                #include "AttachmentABI.hlsl"

                SHARPSHADER_DECLARE_FRAMEBUFFER_READ_WRITE_2D_ARRAY(
                    float4, PreviousColor, 0, 0);

                struct PixelOutput
                {
                    SHARPSHADER_DECLARE_COLOR_OUTPUT(float4, Color, 0);
                };

                PixelOutput PSMain(float4 position : SV_Position)
                {
                    int2 pixel = int2(position.xy);
                    float4 previous =
                        SHARPSHADER_LOAD_FRAMEBUFFER_READ_WRITE_ARRAY(
                            PreviousColor, pixel, 0);
                    PixelOutput output;
                    SHARPSHADER_STORE_FRAMEBUFFER_READ_WRITE_ARRAY(
                        PreviousColor, output.Color, pixel, 0, previous);
                    return output;
                }
                """;
            ShaderAttachmentDeclaration declaration = Color(
                logicalAttachmentId: 0,
                inputIndex: 0,
                outputLocation: 0,
                layerMode: ShaderAttachmentLayerMode.Layered);

            ShaderProgramCompilation compilation = Compile(
                source,
                ShaderProgramTarget.All,
                new ShaderAttachmentPhase(0, new[] { declaration }),
                "attachment-layered-input.hlsl");

            ShaderAttachmentDeclaration frozen = Assert.Single(
                Assert.Single(
                    Assert.Single(compilation.Manifest.Variants).Entries)
                    .AttachmentInterface.Phase!.Attachments);
            Assert.Equal(ShaderAttachmentLayerMode.Layered, frozen.LayerMode);
            MslArtifactReflection msl = ReflectMsl(compilation);
            MslColorAttachmentIoReflection input =
                Assert.Single(msl.ColorInputs);
            Assert.Equal(0u, input.Location);
            MslColorAttachmentIoReflection output =
                Assert.Single(msl.ColorOutputs);
            Assert.Equal(0u, output.Location);
        }

        [Theory]
        [InlineData(0u)]
        [InlineData(3u)]
        [Trait("Category", "SharpShaderAttachment")]
        public void MetalLocalInput_MapsInputSlotIndependentlyFromLogicalId(
            uint inputLogicalAttachmentId)
        {
            const string source = """
                #include "AttachmentABI.hlsl"

                SHARPSHADER_DECLARE_LOCAL_INPUT_2D(
                    float4, PreviousColor, 0);

                struct PixelOutput
                {
                    SHARPSHADER_DECLARE_COLOR_OUTPUT(float4, NextColor, 1);
                };

                PixelOutput PSMain(float4 position : SV_Position)
                {
                    PixelOutput output;
                    output.NextColor = SHARPSHADER_LOAD_LOCAL_INPUT(
                        PreviousColor, int2(position.xy));
                    return output;
                }
                """;
            ShaderAttachmentDeclaration input = Color(
                logicalAttachmentId: inputLogicalAttachmentId,
                inputIndex: 0,
                outputLocation: null);
            ShaderAttachmentDeclaration output = Color(
                logicalAttachmentId: 1,
                inputIndex: null,
                outputLocation: 1);

            ShaderProgramCompilation compilation = Compile(
                source,
                ShaderProgramTarget.MetalMsl,
                new ShaderAttachmentPhase(0, new[] { input, output }),
                $"attachment-metal-read-{inputLogicalAttachmentId}-write-1.hlsl");

            MslArtifactReflection msl = ReflectMsl(compilation);
            MslColorAttachmentIoReflection reflectedInput =
                Assert.Single(msl.ColorInputs);
            Assert.Equal(0u, reflectedInput.Location);
            Assert.Equal(0u, reflectedInput.Index);
            MslColorAttachmentIoReflection reflectedOutput =
                Assert.Single(msl.ColorOutputs);
            Assert.Equal(1u, reflectedOutput.Location);
            Assert.Equal(0u, reflectedOutput.Index);
        }

        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public void MetalBindingPlan_UsesInputSlotForInputOnlyFetch()
        {
            ShaderAttachmentDeclaration input = Color(
                logicalAttachmentId: 3,
                inputIndex: 0,
                outputLocation: null);
            ShaderAttachmentInterface attachmentInterface = new(
                "default",
                "PSMain",
                ShaderExecutionStage.Pixel,
                new ShaderAttachmentPhase(0, new[] { input }));
            ShaderEntryPointReflection reflection = new(
                "PSMain",
                ShaderExecutionStage.Pixel);

            MslTranslationBindingPlan plan = MslTranslationBindingPlan.Create(
                "PSMain",
                ShaderExecutionStage.Pixel,
                Array.Empty<VulkanShaderBindingMapping>(),
                new MetalShaderBackendLayout(),
                attachmentInterface,
                reflection,
                privateAttachmentDescriptorSet: 31);

            Assert.True(plan.UsesFramebufferFetch);
            MslTranslationResourceBinding resource = Assert.Single(plan.Resources);
            Assert.True(resource.IsPrivateAttachment);
            Assert.Equal(0u, resource.Binding);
            Assert.Equal(0u, resource.MetalIndex);
        }

        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public void LayeredFramebufferReadWriteAttachment_RemapsArrayShape()
        {
            const string source = """
                #include "AttachmentABI.hlsl"

                SHARPSHADER_DECLARE_FRAMEBUFFER_READ_WRITE_2D_ARRAY(
                    float4, OrderedColor, 3, 0);

                struct PixelOutput
                {
                    SHARPSHADER_DECLARE_COLOR_OUTPUT(float4, Color, 0);
                };

                PixelOutput PSMain(float4 position : SV_Position)
                {
                    int2 pixel = int2(position.xy);
                    float4 previous = SHARPSHADER_LOAD_FRAMEBUFFER_READ_WRITE_ARRAY(
                        OrderedColor, pixel, 0);
                    PixelOutput output;
                    SHARPSHADER_STORE_FRAMEBUFFER_READ_WRITE_ARRAY(
                        OrderedColor, output.Color, pixel, 0, previous + 1.0);
                    return output;
                }
                """;
            ShaderAttachmentDeclaration declaration = Color(
                logicalAttachmentId: 3,
                inputIndex: 0,
                outputLocation: 0,
                layerMode: ShaderAttachmentLayerMode.Layered);

            ShaderProgramCompilation compilation = Compile(
                source,
                ShaderProgramTarget.DirectX12 | ShaderProgramTarget.Vulkan,
                new ShaderAttachmentPhase(0, new[] { declaration }),
                "attachment-layered-rov.hlsl");

            Assert.False(Artifact(
                compilation,
                ShaderArtifactKind.Dxil).Content.IsEmpty);
            Assert.False(Artifact(
                compilation,
                ShaderArtifactKind.SpirV).Content.IsEmpty);
        }
        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public void SparseLogicalOutputs_DoNotRequireSyntheticShaderOutputs()
        {
            const string source = """
                #include "AttachmentABI.hlsl"

                struct PixelOutput
                {
                    SHARPSHADER_DECLARE_COLOR_OUTPUT(float4, First, 0);
                    SHARPSHADER_DECLARE_COLOR_OUTPUT(uint4, Fourth, 3);
                };

                PixelOutput PSMain()
                {
                    PixelOutput output;
                    output.First = 1.0;
                    output.Fourth = uint4(2, 3, 4, 5);
                    return output;
                }
                """;
            ShaderAttachmentDeclaration first = Color(
                logicalAttachmentId: 0,
                inputIndex: null,
                outputLocation: 0);
            ShaderAttachmentDeclaration fourth = Color(
                logicalAttachmentId: 3,
                inputIndex: null,
                outputLocation: 3,
                numericClass: ShaderAttachmentNumericClass.UnsignedInteger);

            ShaderProgramCompilation compilation = Compile(
                source,
                ShaderProgramTarget.All,
                new ShaderAttachmentPhase(0, new[] { first, fourth }),
                "attachment-sparse-output.hlsl");

            ShaderAttachmentPhase phase = Assert.Single(
                Assert.Single(compilation.Manifest.Variants).Entries)
                .AttachmentInterface.Phase!;
            Assert.Equal(new uint[] { 0, 3 }, phase.Attachments
                .Select(attachment => attachment.LogicalAttachmentId)
                .Distinct()
                .ToArray());
            Assert.Equal(new uint?[] { 0, 3 }, phase.Attachments
                .Select(attachment => attachment.OutputLocation)
                .ToArray());

            AssertColorOutputLocations(
                CompileDxilAndReflect(
                    source,
                    "attachment-sparse-output.hlsl"),
                0,
                3);
            AssertColorOutputLocations(
                ReflectSpirvArtifact(
                    compilation,
                    source,
                    "attachment-sparse-output.hlsl"),
                0,
                3);

            MslArtifactReflection msl = ReflectMsl(compilation);
            Assert.Equal(
                new uint[] { 0, 3 },
                msl.ColorOutputs.Select(static output => output.Location));
        }
        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public void DualSource_CompilesForDxilAndSpirv_ButMetalFailsClosed()
        {
            const string source = """
                #include "AttachmentABI.hlsl"

                struct PixelOutput
                {
                    SHARPSHADER_DECLARE_COLOR_OUTPUT(float4, Primary, 0);
                    SHARPSHADER_DECLARE_DUAL_SOURCE_COLOR_OUTPUT(
                        float4, Secondary);
                };

                PixelOutput PSMain()
                {
                    PixelOutput output;
                    output.Primary = float4(1, 0, 0, 1);
                    output.Secondary = float4(0, 1, 0, 1);
                    return output;
                }
                """;
            ShaderAttachmentDeclaration primary = Color(
                logicalAttachmentId: 0,
                inputIndex: null,
                outputLocation: 0);
            ShaderAttachmentDeclaration secondary = Color(
                logicalAttachmentId: 0,
                inputIndex: null,
                outputLocation: 0,
                outputIndex: 1);
            ShaderAttachmentPhase phase = new(
                0,
                new[] { primary, secondary });

            ShaderProgramCompilation portable = Compile(
                source,
                ShaderProgramTarget.DirectX12 | ShaderProgramTarget.Vulkan,
                phase,
                "attachment-dual-source.hlsl");
            Assert.False(Artifact(portable, ShaderArtifactKind.Dxil).Content.IsEmpty);
            Assert.False(Artifact(portable, ShaderArtifactKind.SpirV).Content.IsEmpty);

            ShaderCompilerException exception = Assert.Throws<ShaderCompilerException>(
                () => new ShaderProgramCompiler().Compile(CreateRequest(
                    source,
                    ShaderProgramTarget.MetalMsl,
                    phase,
                    "attachment-dual-source-metal.hlsl")));
            Assert.Contains(
                "dual-source",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public void AttachmentContract_RejectsAdditionalColorOutputWithDualSource()
        {
            ShaderAttachmentDeclaration primary = Color(
                logicalAttachmentId: 0,
                inputIndex: null,
                outputLocation: 0);
            ShaderAttachmentDeclaration secondary = Color(
                logicalAttachmentId: 0,
                inputIndex: null,
                outputLocation: 0,
                outputIndex: 1);
            ShaderAttachmentDeclaration independent = Color(
                logicalAttachmentId: 2,
                inputIndex: null,
                outputLocation: 2);

            Assert.Throws<ArgumentException>(() =>
                new ShaderAttachmentPhase(
                    0,
                    new[] { primary, secondary, independent }));
        }

        [Theory]
        [InlineData("SHARPSHADER_DECLARE_DEPTH_OUTPUT", ShaderDepthExport.Depth)]
        [InlineData("SHARPSHADER_DECLARE_DEPTH_GREATER_EQUAL_OUTPUT", ShaderDepthExport.DepthGreaterEqual)]
        [InlineData("SHARPSHADER_DECLARE_DEPTH_LESS_EQUAL_OUTPUT", ShaderDepthExport.DepthLessEqual)]
        [Trait("Category", "SharpShaderAttachment")]
        public void DepthExportModes_AreNormalizedAcrossArtifacts(
            string declarationMacro,
            ShaderDepthExport depthExport)
        {
            string source = """
                #include "AttachmentABI.hlsl"

                struct PixelOutput
                {
                    $DEPTH$(float, Depth);
                };

                PixelOutput PSMain()
                {
                    PixelOutput output;
                    output.Depth = 0.5;
                    return output;
                }
                """.Replace("$DEPTH$", declarationMacro, StringComparison.Ordinal);
            ShaderAttachmentDeclaration depth = new(
                logicalAttachmentId: 8,
                inputIndex: null,
                outputLocation: null,
                ShaderAttachmentAspect.Depth,
                ShaderAttachmentNumericClass.DepthStencil,
                ShaderAttachmentSampleMode.SingleSample,
                ShaderAttachmentLayerMode.SingleLayer);
            ShaderAttachmentPhase phase = new(
                0,
                new[] { depth },
                ShaderDepthStencilAccess.ReadWrite,
                depthExport);

            ShaderProgramCompilation compilation = Compile(
                source,
                ShaderProgramTarget.All,
                phase,
                $"attachment-{depthExport}.hlsl");

            Assert.Equal(
                depthExport,
                Assert.Single(
                    Assert.Single(compilation.Manifest.Variants).Entries)
                    .AttachmentInterface.Phase!.DepthExport);
            Assert.False(Artifact(compilation, ShaderArtifactKind.Dxil).Content.IsEmpty);
            Assert.False(Artifact(compilation, ShaderArtifactKind.SpirV).Content.IsEmpty);
            Assert.Equal(
                depthExport,
                ReflectMsl(compilation).DepthExport);
        }

        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public void StencilReferenceExport_IsValidatedAcrossAllArtifacts()
        {
            const string source = """
                #include "AttachmentABI.hlsl"

                struct PixelOutput
                {
                    SHARPSHADER_DECLARE_STENCIL_REFERENCE_OUTPUT(Stencil);
                };

                PixelOutput PSMain()
                {
                    PixelOutput output;
                    output.Stencil = 7;
                    return output;
                }
                """;
            ShaderAttachmentDeclaration stencil = new(
                logicalAttachmentId: 8,
                inputIndex: null,
                outputLocation: null,
                ShaderAttachmentAspect.Stencil,
                ShaderAttachmentNumericClass.DepthStencil,
                ShaderAttachmentSampleMode.SingleSample,
                ShaderAttachmentLayerMode.SingleLayer);
            ShaderAttachmentPhase phase = new(
                0,
                new[] { stencil },
                ShaderDepthStencilAccess.ReadWrite,
                stencilExport: ShaderStencilExport.StencilReference);

            ShaderProgramCompilation compilation = Compile(
                source,
                ShaderProgramTarget.All,
                phase,
                "attachment-stencil-ref.hlsl");

            Assert.False(Artifact(compilation, ShaderArtifactKind.Dxil).Content.IsEmpty);
            Assert.False(Artifact(compilation, ShaderArtifactKind.SpirV).Content.IsEmpty);
            Assert.Equal(
                ShaderStencilExport.StencilReference,
                ReflectMsl(compilation).StencilExport);
        }

        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public void AttachmentContract_AcceptsInputAndOutputOverlapAsReadWrite()
        {
            ShaderAttachmentDeclaration declaration = Color(
                logicalAttachmentId: 0,
                inputIndex: 0,
                outputLocation: 0);
            ShaderAttachmentPhase phase =
                new(0, new[] { declaration });

            ShaderAttachmentDeclaration frozen =
                Assert.Single(phase.Attachments);
            Assert.Equal(0u, frozen.InputIndex);
            Assert.Equal(0u, frozen.OutputLocation);
        }

        [Fact]
        [Trait("Category", "SharpShaderAttachment")]
        public void AttachmentContract_ShouldRequireMatchingExportAspects()
        {
            ShaderAttachmentDeclaration depth = DepthStencil(
                ShaderAttachmentAspect.Depth);
            ShaderAttachmentDeclaration stencil = DepthStencil(
                ShaderAttachmentAspect.Stencil);

            Assert.Throws<ArgumentException>(() =>
                new ShaderAttachmentPhase(
                    0,
                    new[] { stencil },
                    ShaderDepthStencilAccess.ReadWrite,
                    ShaderDepthExport.Depth));
            Assert.Throws<ArgumentException>(() =>
                new ShaderAttachmentPhase(
                    0,
                    new[] { depth },
                    ShaderDepthStencilAccess.ReadWrite,
                    stencilExport:
                        ShaderStencilExport.StencilReference));

            static ShaderAttachmentDeclaration DepthStencil(
                ShaderAttachmentAspect aspect)
            {
                return new ShaderAttachmentDeclaration(
                    logicalAttachmentId: 8,
                    inputIndex: null,
                    outputLocation: null,
                    aspect,
                    ShaderAttachmentNumericClass.DepthStencil,
                    ShaderAttachmentSampleMode.SingleSample,
                    ShaderAttachmentLayerMode.SingleLayer);
            }
        }
        private static ShaderArtifactReflection CompileDxilAndReflect(
            string source,
            string sourceName)
        {
            ShaderCompileRequest request = new()
            {
                Source = source,
                SourceName = sourceName,
                EntryPoint = "PSMain",
                Stage = ShaderStageKind.Pixel,
                ShaderModel = new ShaderModelVersion(6, 6),
                Target = ShaderTargetKind.Dxil,
                IncludeDirs = new[] { ResolveIncludeDirectory() },
            };
            try
            {
                ShaderCompileResult compiled = HLSLCrossCompiler.Compile(
                    request);
                return DxilArtifactReflector.Reflect(request, compiled);
            }
            catch (ShaderCompilerException exception)
            {
                throw new Xunit.Sdk.XunitException(
                    $"{exception.Message}{Environment.NewLine}"
                    + exception.Diagnostics);
            }
        }

        private static ShaderArtifactReflection ReflectSpirvArtifact(
            ShaderProgramCompilation compilation,
            string source,
            string sourceName)
        {
            ShaderCompileRequest request = new()
            {
                Source = source,
                SourceName = sourceName,
                EntryPoint = "PSMain",
                Stage = ShaderStageKind.Pixel,
                ShaderModel = new ShaderModelVersion(6, 6),
                Target = ShaderTargetKind.SpirV,
                IncludeDirs = new[] { ResolveIncludeDirectory() },
            };
            return SpirvArtifactReflector.Reflect(
                request,
                new ShaderCompileResult
                {
                    Bytecode = Artifact(
                        compilation,
                        ShaderArtifactKind.SpirV).Content.ToArray(),
                });
        }

        private static MslArtifactReflection ReflectMsl(
            ShaderProgramCompilation compilation)
        {
            string source = Assert.IsType<string>(
                Artifact(
                    compilation,
                    ShaderArtifactKind.MslSource).Text);
            return MslArtifactReflector.Reflect(
                source,
                "PSMain",
                ShaderExecutionStage.Pixel);
        }

        private static void AssertColorOutputLocations(
            ShaderArtifactReflection reflection,
            params uint[] expectedLocations)
        {
            uint[] actualLocations = Assert.Single(reflection.EntryPoints)
                .StageOutputs
                .Where(static output =>
                    output.BuiltIn is ShaderStageIoBuiltIn.None
                        or ShaderStageIoBuiltIn.Color)
                .Select(static output => output.Location!.Value)
                .OrderBy(static location => location)
                .ToArray();
            Assert.Equal(expectedLocations, actualLocations);
        }
        private static ShaderAttachmentDeclaration Color(
            uint logicalAttachmentId,
            uint? inputIndex,
            uint? outputLocation,
            ShaderAttachmentNumericClass numericClass =
                ShaderAttachmentNumericClass.FloatingPoint,
            ShaderAttachmentSampleMode sampleMode =
                ShaderAttachmentSampleMode.SingleSample,
            ShaderAttachmentLayerMode layerMode =
                ShaderAttachmentLayerMode.SingleLayer,
            uint outputIndex = 0,
            uint outputComponent = 0)
        {
            return new ShaderAttachmentDeclaration(
                logicalAttachmentId,
                inputIndex,
                outputLocation,
                ShaderAttachmentAspect.Color,
                numericClass,
                sampleMode,
                layerMode,
                outputIndex,
                outputComponent);
        }

        private static ShaderProgramCompilation Compile(
            string source,
            ShaderProgramTarget targets,
            ShaderAttachmentPhase phase,
            string sourceName)
        {
            try
            {
                return new ShaderProgramCompiler().Compile(
                    CreateRequest(source, targets, phase, sourceName));
            }
            catch (ShaderCompilerException exception)
            {
                throw new Xunit.Sdk.XunitException(
                    $"{exception.Message}{Environment.NewLine}"
                    + exception.Diagnostics);
            }
        }

        private static ShaderProgramCompileRequest CreateRequest(
            string source,
            ShaderProgramTarget targets,
            ShaderAttachmentPhase phase,
            string sourceName)
        {
            return new ShaderProgramCompileRequest(
                source,
                sourceName,
                new[]
                {
                    new ShaderProgramEntry(
                        "PSMain",
                        ShaderExecutionStage.Pixel),
                },
                new[] { ShaderProgramVariant.Default },
                targets,
                new ShaderModelVersion(6, 6),
                includeDirectories: new[] { ResolveIncludeDirectory() },
                attachmentInterfaces: new[]
                {
                    new ShaderAttachmentInterface(
                        "default",
                        "PSMain",
                        ShaderExecutionStage.Pixel,
                        phase),
                });
        }

        private static ShaderProgramArtifact Artifact(
            ShaderProgramCompilation compilation,
            ShaderArtifactKind kind)
        {
            return compilation.GetArtifact(
                "default",
                "PSMain",
                ShaderExecutionStage.Pixel,
                kind);
        }

        private static string ResolveIncludeDirectory()
        {
            return Path.Combine(
                ResolveRepositoryRoot(),
                "Engine",
                "Source",
                "Runtime",
                "Graphics",
                "SharpShader",
                "Includes");
        }

        private static string ResolveRepositoryRoot()
        {
            DirectoryInfo? current = new(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(
                        current.FullName,
                        "InfinityBrowser.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException(
                "Unable to resolve repository root.");
        }
    }
}
