using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SharpShader
{
    /// <summary>Process-wide native toolchain locations. Configure before the first compilation.</summary>
    public static class SharpShaderNativeLibraries
    {
        private static readonly object s_Sync = new();
        private static string? s_DxcDirectory;
        private static string? s_SpirvCrossDirectory;
        private static bool s_Frozen;

        /// <summary>Selects exact directories containing the compiler and its companion libraries.</summary>
        public static void Configure(string dxcDirectory, string spirvCrossDirectory)
        {
            string dxc = ValidateDirectory(dxcDirectory);
            string spirvCross = ValidateDirectory(spirvCrossDirectory);
            lock (s_Sync)
            {
                if (s_Frozen)
                {
                    throw new InvalidOperationException("SharpShader native locations are frozen after the first toolchain request.");
                }

                s_DxcDirectory = dxc;
                s_SpirvCrossDirectory = spirvCross;
            }
        }

        internal static string Resolve(bool dxc, string fileName)
        {
            lock (s_Sync)
            {
                s_Frozen = true;
                string? configured = dxc ? s_DxcDirectory : s_SpirvCrossDirectory;
                if (configured is not null)
                {
                    return Path.Combine(configured, fileName);
                }

                // NuGet copies RID-specific assets beside the app when publishing for a RID,
                // and retains runtimes/<rid>/native for a portable build.
                string os = OperatingSystem.IsWindows() ? "win"
                    : OperatingSystem.IsLinux() ? "linux"
                    : OperatingSystem.IsMacOS() ? "osx"
                    : throw new PlatformNotSupportedException("SharpShader native compilation requires Windows, Linux or macOS.");
                string arch = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 => "x64",
                    Architecture.Arm64 => "arm64",
                    _ => throw new PlatformNotSupportedException("SharpShader native compilation requires x64 or ARM64."),
                };
                string portable = Path.Combine(AppContext.BaseDirectory, "runtimes", os + "-" + arch, "native", fileName);
                return File.Exists(portable) ? portable : Path.Combine(AppContext.BaseDirectory, fileName);
            }
        }

        private static string ValidateDirectory(string directory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(directory);
            if (!Path.IsPathFullyQualified(directory))
            {
                throw new ArgumentException("Native toolchain directories must be absolute paths.", nameof(directory));
            }

            string fullPath = Path.GetFullPath(directory);
            if (!Directory.Exists(fullPath))
            {
                throw new DirectoryNotFoundException(fullPath);
            }

            return fullPath;
        }
    }
}
