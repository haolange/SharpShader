using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpShader.HLSLCrossCompiler.Internal;
using Xunit;

namespace SharpShader.Internal.Tests
{
    public sealed class SharpShaderNativeLibraryResolverConcurrencyTests
    {
        [Fact]
        public async Task LockedNativeFile_ShouldSerializeValidationAndRejectUseAfterDispose()
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                $"sharpshader-native-lock-{Guid.NewGuid():N}.bin");
            byte[] content = new byte[8 * 1024 * 1024];
            for (int index = 0; index < content.Length; ++index)
            {
                content[index] = unchecked((byte)(index * 31));
            }

            await File.WriteAllBytesAsync(path, content);
            SharpShaderNativeLibraryResolver.LockedNativeFile? lockedFile = null;
            try
            {
                SharpShaderNativeLibraryResolver.LockedNativeFile current =
                    SharpShaderNativeLibraryResolver.LockedNativeFile.Open(
                        "concurrency-test",
                        path);
                lockedFile = current;
                using ManualResetEventSlim start = new(initialState: false);
                Task[] validators = Enumerable.Range(0, 16)
                    .Select(_ => Task.Run(() =>
                    {
                        start.Wait();
                        for (int iteration = 0; iteration < 4; ++iteration)
                        {
                            current.ValidateUnchanged();
                        }
                    }))
                    .ToArray();

                start.Set();
                await Task.WhenAll(validators);

                current.Dispose();
                current.Dispose();
                Assert.Throws<ObjectDisposedException>(
                    current.ValidateUnchanged);
                Assert.Throws<ObjectDisposedException>(
                    () => current.CreateComponent());
            }
            finally
            {
                lockedFile?.Dispose();
                File.Delete(path);
            }
        }
    }
}
