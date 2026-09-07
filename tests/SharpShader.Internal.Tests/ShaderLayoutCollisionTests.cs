using System;
using SharpShader.Compilation;
using Xunit;

namespace SharpShader.Internal.Tests
{
    public sealed class ShaderLayoutCollisionTests
    {
        [Fact]
        public void LayoutPool_ShouldRejectStructuralHashCollision()
        {
            ShaderLayoutSignature signature = new(new byte[ShaderLayoutSignature.ByteLength]);
            ShaderInterfaceLayout first = CreateLayout(ShaderResourceDimension.Texture2D, signature);
            ShaderInterfaceLayout second = CreateLayout(ShaderResourceDimension.Texture3D, signature);
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
                new ShaderInterfaceManifest(
                    new string('a', 64),
                    new[] { new ShaderToolchainComponent("SharpShader", "1.0.0") },
                    new[] { first, second },
                    Array.Empty<ShaderInterfaceVariant>(),
                    Array.Empty<ShaderBackendLayouts>(),
                    ShaderProgramTarget.All));
            Assert.Contains("signature collision", exception.Message, StringComparison.Ordinal);
        }

        private static ShaderInterfaceLayout CreateLayout(
            ShaderResourceDimension dimension, ShaderLayoutSignature signature)
        {
            ShaderLogicalBinding binding = new(
                new ShaderBindingKey(2, 0, ShaderBindingClass.ShaderResource),
                "Texture", null,
                new ShaderResourceShape(ShaderResourceKind.Texture, dimension, ShaderResourceAccess.ReadOnly),
                ShaderStageMask.Pixel);
            return new ShaderInterfaceLayout(new[] { binding }, _ => signature);
        }
    }
}
