using SharpShader.Compilation;
using SharpShader.CSharp;
using Xunit;

namespace Infinity.Rendering.Tests
{
    public sealed class CSharpShaderBaselineTests
    {
        [Fact]
        public void Mandelbrot_CompilesDx12AndVulkan()
        {
            CSharpShaderCompilation compilation = CSharpShaderCompiler.Shared.Compile(
                CSharpShaderSources.Mandelbrot,
                "Mandelbrot.cs",
                new CSharpShaderCompilerOptions(
                    ShaderProgramTarget.DirectX12 | ShaderProgramTarget.Vulkan));

            Assert.False(compilation.Primary.Translation.HasErrors);
            Assert.Equal(2, compilation.Primary.Program.Artifacts.Count);
            Assert.Contains(
                compilation.Primary.Program.Artifacts,
                static artifact => artifact.Identity.ArtifactKind == ShaderArtifactKind.Dxil);
            Assert.Contains(
                compilation.Primary.Program.Artifacts,
                static artifact => artifact.Identity.ArtifactKind == ShaderArtifactKind.SpirV);
        }

        [Fact]
        public void TextureCard_CompilesDx12()
        {
            CSharpShaderCompilation compilation = CSharpShaderCompiler.Shared.Compile(
                CSharpShaderSources.TextureCard,
                "TextureCard.cs",
                new CSharpShaderCompilerOptions(ShaderProgramTarget.DirectX12));

            Assert.Equal(2, compilation.Primary.Translation.Entries.Count);
            Assert.NotEmpty(compilation.Primary.Program.Artifacts);
        }

        [Fact]
        public void RayQuery_CompilesDxilAndSpirv()
        {
            CSharpShaderCompilation compilation = CSharpShaderCompiler.Shared.Compile(
                CSharpShaderSources.RayQuery,
                "RayQuery.cs",
                new CSharpShaderCompilerOptions(
                    ShaderProgramTarget.DirectX12 | ShaderProgramTarget.Vulkan));

            Assert.True(compilation.Primary.Translation.Entries[0].UsesRayQuery);
            Assert.Contains(
                compilation.Primary.Program.Artifacts,
                static artifact => artifact.Identity.ArtifactKind == ShaderArtifactKind.Dxil);
            Assert.Contains(
                compilation.Primary.Program.Artifacts,
                static artifact => artifact.Identity.ArtifactKind == ShaderArtifactKind.SpirV);
        }
    }
}
