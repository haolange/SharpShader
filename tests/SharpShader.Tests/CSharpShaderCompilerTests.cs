using System.Linq;
using SharpShader.Compilation;
using SharpShader.CSharp;
using SharpShader.CSharp.Frontend;
using SharpShader.HLSLCrossCompiler;
using SharpShader.SharpGPU;
using Xunit;

namespace SharpShader.Tests
{
    public sealed class CSharpShaderCompilerTests
    {
        [Fact]
        public void Compile_WaveActiveSum_ProducesDxilAndKeepsExistingWaveLowering()
        {
            CSharpShaderCompilation compilation = CSharpShaderCompiler.Shared.Compile(
                CSharpShaderSources.WaveActiveSum,
                "WaveActiveSum.cs",
                new CSharpShaderCompilerOptions(ShaderProgramTarget.DirectX12));

            Assert.True(compilation.Primary.Translation.Entries[0].UsesWaveOperations);
            Assert.Contains("WaveActiveSum(", compilation.Primary.Translation.Hlsl);
            Assert.DoesNotContain("WaveMMA", compilation.Primary.Translation.Hlsl);
            ShaderProgramCompilation program = compilation.Primary.Program;
            Assert.Contains(
                program.Artifacts,
                static artifact => artifact.Identity.ArtifactKind == ShaderArtifactKind.Dxil);
        }

        [Fact]
        public void Compile_WriteConstant_ProducesDxilAndManifest()
        {
            CSharpShaderCompilation compilation = CSharpShaderCompiler.Shared.Compile(
                CSharpShaderSources.WriteConstant,
                "WriteConstant.cs",
                new CSharpShaderCompilerOptions(ShaderProgramTarget.DirectX12));

            ShaderProgramCompilation program = compilation.Primary.Program;
            Assert.NotEmpty(program.Artifacts);
            Assert.Contains(
                program.Artifacts,
                static artifact => artifact.Identity.ArtifactKind == ShaderArtifactKind.Dxil);
            ShaderInterfaceLayout layout = Assert.Single(program.Manifest.LogicalLayouts);
            Assert.Contains(layout.Bindings, static binding =>
                binding.Key == new ShaderBindingKey(0, 0, ShaderBindingClass.UnorderedAccess));
        }

        [Fact]
        public void Compile_TextureCard_BuildsAttachmentAndAdapterPlan()
        {
            CSharpShaderCompilation compilation = CSharpShaderCompiler.Shared.Compile(
                CSharpShaderSources.TextureCard,
                "TextureCard.cs",
                new CSharpShaderCompilerOptions(ShaderProgramTarget.DirectX12));

            ShaderProgramCompilation program = compilation.Primary.Program;
            Assert.Contains(
                program.Manifest.LogicalLayouts[0].Bindings,
                static binding => binding.Key.Type == ShaderBindingClass.ConstantBuffer);
            Assert.Contains(
                program.Manifest.LogicalLayouts[0].Bindings,
                static binding => binding.Key.Type == ShaderBindingClass.ShaderResource);
            Assert.Contains(
                program.Manifest.LogicalLayouts[0].Bindings,
                static binding => binding.Key.Type == ShaderBindingClass.Sampler);

            SharpGpuBindingTableLayoutPlan plan =
                SharpGpuShaderInterfaceAdapter.CreateBindingTableLayoutPlan(
                    program.Manifest,
                    "default",
                    "fs",
                    ShaderExecutionStage.Pixel,
                    global::SharpGPU.ERHIBackend.DirectX12);
            Assert.True(plan.BindingTableCount > 0);
        }

        [Fact]
        public void Compile_VariantDefine_ReparsesPreprocessor()
        {
            const string source = @"
using SharpMath;
using SharpShader.CSharp.ShaderLib;
public static class VariantShader {
    [Binding(0,0)] public static RWStructuredBuffer<uint> Output;
    [NumThreads(1,1,1)] [ComputeShader]
    public static void CSMain() {
#if USE_TINT
        Output.Store(0, 2u);
#else
        Output.Store(0, 1u);
#endif
    }
}";
            CSharpShaderCompilation compilation = CSharpShaderCompiler.Shared.Compile(
                source,
                "Variant.cs",
                new CSharpShaderCompilerOptions(
                    ShaderProgramTarget.DirectX12,
                    variants: new[]
                    {
                        ShaderProgramVariant.Default,
                        new ShaderProgramVariant(
                            "USE_TINT",
                            new[] { new ShaderDefine("USE_TINT", "1") }),
                    }));

            Assert.Equal(2, compilation.Variants.Count);
            Assert.Contains("1", compilation.Variants[0].Translation.Hlsl);
            Assert.Contains("2", compilation.Variants[1].Translation.Hlsl);
        }

        [Fact]
        public void Compile_InvalidSource_ThrowsTranslationException()
        {
            CSharpShaderTranslationException exception = Assert.Throws<CSharpShaderTranslationException>(
                static () => CSharpShaderCompiler.Shared.Compile(
                    "public static class Empty {}",
                    "empty.cs",
                    new CSharpShaderCompilerOptions(ShaderProgramTarget.DirectX12)));
            Assert.True(exception.Translation.HasErrors);
        }
    }
}
