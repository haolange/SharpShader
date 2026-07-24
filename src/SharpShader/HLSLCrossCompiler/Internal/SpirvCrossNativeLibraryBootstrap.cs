using System;
using System.IO;
using Silk.NET.Core.Contexts;
using Silk.NET.SPIRV.Cross;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SharpShader.Compilation;

namespace SharpShader.HLSLCrossCompiler.Internal
{
    internal static class SpirvCrossNativeLibraryBootstrap
    {
        private const string ThirdPartyVendorRootName = "Khronos";
        private const string ThirdPartyLibraryRootName = "SPIRV-Cross";

        private static readonly object s_Sync = new();
        private static SharpShaderNativeLibraryResolver.LockedNativeFile?
            s_ToolchainFile;

        public static Cross CreateApi()
        {
            SharpShaderNativeLibraryResolver.LockedNativeFile toolchain =
                ResolveToolchainFile();
            toolchain.ValidateUnchanged();
            Cross cross = CreateApiFromExactPath(toolchain.Path);
            try
            {
                toolchain.ValidateUnchanged();
                return cross;
            }
            catch
            {
                cross.Dispose();
                throw;
            }
        }

        internal static ShaderToolchainComponent ResolveToolchainComponent()
        {
            return ResolveToolchainFile().CreateComponent();
        }

        private static SharpShaderNativeLibraryResolver.LockedNativeFile
            ResolveToolchainFile()
        {
            lock (s_Sync)
            {
                s_ToolchainFile ??=
                    SharpShaderNativeLibraryResolver.LockedNativeFile.Open(
                        "SPIRV-Cross",
                        ResolveCanonicalLibraryPath(
                            Environment.GetEnvironmentVariable(
                                SharpShaderNativeLibraryLayout
                                    .ThirdPartyNativeRootEnvironmentVariableName),
                            AppContext.BaseDirectory,
                            typeof(SpirvCrossNativeLibraryBootstrap)
                                .Assembly.Location));
                return s_ToolchainFile;
            }
        }

        internal static Cross CreateApiFromExactPath(string exactPath)
        {
            string canonicalPath;
            try
            {
                canonicalPath = Path.GetFullPath(exactPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw BuildLoadFailureException(exactPath, ex);
            }

            if (!File.Exists(canonicalPath))
            {
                throw BuildLoadFailureException(canonicalPath);
            }

            INativeContext? nativeContext = null;
            try
            {
                nativeContext = Cross.CreateDefaultContext(new[] { canonicalPath });
                Cross cross = new Cross(nativeContext);
                nativeContext = null;
                return cross;
            }
            catch (Exception ex)
            {
                throw BuildLoadFailureException(canonicalPath, ex);
            }
            finally
            {
                nativeContext?.Dispose();
            }
        }

        internal static string ResolveCanonicalLibraryPathForTesting(
            string? configuredThirdPartyRoot,
            string baseDirectory,
            string assemblyLocation)
        {
            return ResolveCanonicalLibraryPath(configuredThirdPartyRoot, baseDirectory, assemblyLocation);
        }

        private static string ResolveCanonicalLibraryPath(
            string? configuredThirdPartyRoot,
            string baseDirectory,
            string assemblyLocation)
        {
            string? osFolder = ResolveBuildOsFolder();
            string? archFolder = ResolveBuildArchFolder();
            string? nativeFileName = ResolvePlatformNativeFileName();
            if (string.IsNullOrWhiteSpace(osFolder)
                || string.IsNullOrWhiteSpace(archFolder)
                || string.IsNullOrWhiteSpace(nativeFileName))
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.BackendUnavailable,
                    "SPIRV-Cross native backend is unsupported for the current OS or process architecture.");
            }

            string thirdPartyRoot;
            if (!string.IsNullOrWhiteSpace(configuredThirdPartyRoot))
            {
                try
                {
                    thirdPartyRoot = Path.GetFullPath(configuredThirdPartyRoot.Trim());
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    throw BuildLoadFailureException(configuredThirdPartyRoot, ex);
                }
            }
            else
            {
                HashSet<string> binariesRoots = new(StringComparer.OrdinalIgnoreCase);
                AddCanonicalBinariesRoot(baseDirectory, binariesRoots);
                AddCanonicalBinariesRoot(Path.GetDirectoryName(assemblyLocation), binariesRoots);

                if (binariesRoots.Count == 0)
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.BackendUnavailable,
                        $"SPIRV-Cross native backend cannot resolve the canonical Binaries root from AppContext.BaseDirectory or the SharpShader assembly location. Set {SharpShaderNativeLibraryLayout.ThirdPartyNativeRootEnvironmentVariableName} to the exact ThirdParty root.");
                }

                if (binariesRoots.Count != 1)
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.BackendUnavailable,
                        $"SPIRV-Cross native backend resolved multiple canonical Binaries roots. Set {SharpShaderNativeLibraryLayout.ThirdPartyNativeRootEnvironmentVariableName} to select one exact ThirdParty root.");
                }

                using HashSet<string>.Enumerator enumerator = binariesRoots.GetEnumerator();
                enumerator.MoveNext();
                thirdPartyRoot = Path.Combine(enumerator.Current, "ThirdParty");
            }

            return Path.GetFullPath(
                Path.Combine(
                    thirdPartyRoot,
                    ThirdPartyVendorRootName,
                    ThirdPartyLibraryRootName,
                    osFolder,
                    archFolder,
                    nativeFileName));
        }

        private static void AddCanonicalBinariesRoot(string? location, HashSet<string> destination)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                return;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(location);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }

            string marker = $"{Path.DirectorySeparatorChar}Binaries{Path.DirectorySeparatorChar}";
            string normalized = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            int markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return;
            }

            destination.Add(normalized.Substring(0, markerIndex + marker.Length - 1));
        }

        private static ShaderCompilerException BuildLoadFailureException(string exactPath, Exception? innerException = null)
        {
            string detail = innerException == null ? string.Empty : $" {innerException.Message}";
            return new ShaderCompilerException(
                ShaderCompilerErrorCode.BackendUnavailable,
                $"SPIRV-Cross native backend failed to load canonical library '{exactPath}'.{detail}",
                innerException?.Message ?? string.Empty,
                innerException: innerException);
        }

        private static string? ResolveBuildOsFolder()
        {
            if (OperatingSystem.IsWindows())
            {
                return "Win";
            }

            if (OperatingSystem.IsLinux())
            {
                return "Linux";
            }

            if (OperatingSystem.IsMacOS())
            {
                return "macOS";
            }

            return null;
        }

        private static string? ResolveBuildArchFolder()
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "AMD64",
                Architecture.Arm64 => "ARM64",
                _ => null,
            };
        }

        private static string? ResolvePlatformNativeFileName()
        {
            if (OperatingSystem.IsWindows())
            {
                return "spirv-cross.dll";
            }

            if (OperatingSystem.IsLinux())
            {
                return "libspirv-cross.so";
            }

            if (OperatingSystem.IsMacOS())
            {
                return "libspirv-cross.dylib";
            }

            return null;
        }
    }
}
