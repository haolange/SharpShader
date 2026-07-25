using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SharpShader.Compilation;

namespace SharpShader.HLSLCrossCompiler.Internal
{
    internal static class SharpShaderNativeLibraryResolver
    {
        private static readonly object s_Sync = new();
        private static bool s_ResolverRegistered;
        private static SharpShaderDxcToolchain? s_DxcToolchain;

        public static void EnsureResolverRegistered(Assembly assembly)
        {
            ArgumentNullException.ThrowIfNull(assembly);
            lock (s_Sync)
            {
                if (s_ResolverRegistered)
                {
                    return;
                }

                NativeLibrary.SetDllImportResolver(assembly, ResolveLibraryImport);
                s_ResolverRegistered = true;
            }
        }

        public static SharpShaderDxcToolchain ResolveDxcToolchain(Assembly assembly)
        {
            ArgumentNullException.ThrowIfNull(assembly);
            EnsureResolverRegistered(assembly);
            lock (s_Sync)
            {
                s_DxcToolchain ??= LoadCanonicalDxcToolchain(assembly);
                return s_DxcToolchain;
            }
        }

        internal static IReadOnlyList<ShaderToolchainComponent> CreateIdentityForTesting(
            string compilerPath,
            string companionPath)
        {
            using LockedNativeFile compiler = LockedNativeFile.Open("DXC", compilerPath);
            using LockedNativeFile companion = LockedNativeFile.Open("DXIL", companionPath);
            return new[]
            {
                compiler.CreateComponent(),
                companion.CreateComponent(),
            };
        }

        private static nint ResolveLibraryImport(
            string libraryName,
            Assembly assembly,
            DllImportSearchPath? searchPath)
        {
            if (!SharpShaderNativeLibraryLayout.IsDxcLibraryName(libraryName))
            {
                return 0;
            }

            return ResolveDxcToolchain(assembly).CompilerHandle;
        }

        private static SharpShaderDxcToolchain LoadCanonicalDxcToolchain(Assembly assembly)
        {
            string compilerPath = SharpShaderNativeLibraryLayout
                .EnumerateLibraryCandidates("dxcompiler", assembly)
                .FirstOrDefault(File.Exists)
                ?? throw new DllNotFoundException(
                    "The canonical DXC native library is missing. "
                    + "Default native-library probing is disabled.");
            string? companionName =
                SharpShaderNativeLibraryLayout.ResolveCompanionFileName();
            string compilerDirectory = Path.GetDirectoryName(compilerPath)
                ?? throw new DllNotFoundException(
                    $"Canonical DXC library '{compilerPath}' has no parent directory.");
            string? companionPath = companionName is null
                ? null
                : Path.Combine(compilerDirectory, companionName);

            LockedNativeFile compiler = LockedNativeFile.Open("DXC", compilerPath);
            LockedNativeFile? companion = companionPath is null
                ? null
                : LockedNativeFile.Open("DXIL", companionPath);
            nint companionHandle = 0;
            nint compilerHandle = 0;
            try
            {
                if (companion is not null
                    && !NativeLibrary.TryLoad(companion.Path, out companionHandle))
                {
                    throw new DllNotFoundException(
                        $"Failed to load required canonical DXIL companion '{companion.Path}'.");
                }

                if (!NativeLibrary.TryLoad(compiler.Path, out compilerHandle))
                {
                    throw new DllNotFoundException(
                        $"Failed to load canonical DXC library '{compiler.Path}'.");
                }

                compiler.ValidateUnchanged();
                companion?.ValidateUnchanged();
                return new SharpShaderDxcToolchain(
                    compiler,
                    companion,
                    compilerHandle,
                    companionHandle);
            }
            catch
            {
                if (compilerHandle != 0)
                {
                    NativeLibrary.Free(compilerHandle);
                }

                if (companionHandle != 0)
                {
                    NativeLibrary.Free(companionHandle);
                }

                compiler.Dispose();
                companion?.Dispose();
                throw;
            }
        }

        internal sealed class LockedNativeFile : IDisposable
        {
            private readonly object m_Sync = new();
            private readonly FileStream m_Stream;
            private readonly long m_Length;
            private readonly DateTime m_LastWriteTimeUtc;
            private bool m_Disposed;

            public string Name { get; }
            public string Path { get; }
            public string Version { get; }
            public string ContentDigest { get; }

            private LockedNativeFile(
                string name,
                string path,
                FileStream stream,
                long length,
                DateTime lastWriteTimeUtc,
                string version,
                string contentDigest)
            {
                Name = name;
                Path = path;
                m_Stream = stream;
                m_Length = length;
                m_LastWriteTimeUtc = lastWriteTimeUtc;
                Version = version;
                ContentDigest = contentDigest;
            }

            public static LockedNativeFile Open(string name, string path)
            {
                string fullPath = System.IO.Path.GetFullPath(path);
                FileStream stream;
                try
                {
                    stream = new FileStream(
                        fullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 64 * 1024,
                        FileOptions.SequentialScan);
                }
                catch (Exception ex) when (
                    ex is FileNotFoundException
                    or DirectoryNotFoundException
                    or IOException
                    or UnauthorizedAccessException)
                {
                    throw new DllNotFoundException(
                        $"Required canonical {name} library '{fullPath}' could not be opened.",
                        ex);
                }

                try
                {
                    long length = stream.Length;
                    DateTime lastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath);
                    string digest = Convert.ToHexStringLower(SHA256.HashData(stream));
                    stream.Position = 0;
                    FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(fullPath);
                    string version = versionInfo.ProductVersion
                        ?? versionInfo.FileVersion
                        ?? "content-addressed";
                    return new LockedNativeFile(
                        name,
                        fullPath,
                        stream,
                        length,
                        lastWriteTimeUtc,
                        version,
                        digest);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }

            public ShaderToolchainComponent CreateComponent()
            {
                lock (m_Sync)
                {
                    ThrowIfDisposed();
                    return new ShaderToolchainComponent(Name, Version, ContentDigest);
                }
            }

            public void ValidateUnchanged()
            {
                lock (m_Sync)
                {
                    ThrowIfDisposed();
                    if (m_Stream.Length != m_Length
                        || File.GetLastWriteTimeUtc(Path) != m_LastWriteTimeUtc)
                    {
                        throw new DllNotFoundException(
                            $"Canonical {Name} library '{Path}' changed while it was being loaded.");
                    }

                    m_Stream.Position = 0;
                    string digest = Convert.ToHexStringLower(SHA256.HashData(m_Stream));
                    m_Stream.Position = 0;
                    if (!string.Equals(digest, ContentDigest, StringComparison.Ordinal))
                    {
                        throw new DllNotFoundException(
                            $"Canonical {Name} library '{Path}' changed while it was being loaded.");
                    }
                }
            }

            public void Dispose()
            {
                lock (m_Sync)
                {
                    if (m_Disposed)
                    {
                        return;
                    }

                    m_Disposed = true;
                    m_Stream.Dispose();
                }
            }

            private void ThrowIfDisposed()
            {
                if (m_Disposed)
                {
                    throw new ObjectDisposedException(
                        nameof(LockedNativeFile),
                        $"Canonical {Name} library lock for '{Path}' is disposed.");
                }
            }
        }
    }

    internal sealed class SharpShaderDxcToolchain
    {
        private readonly SharpShaderNativeLibraryResolver.LockedNativeFile m_Compiler;
        private readonly SharpShaderNativeLibraryResolver.LockedNativeFile? m_Companion;
        private readonly IReadOnlyList<ShaderToolchainComponent> m_Components;

        public nint CompilerHandle { get; }
        public nint CompanionHandle { get; }
        public ShaderToolchainComponent CompilerComponent => m_Compiler.CreateComponent();
        public ShaderToolchainComponent? CompanionComponent =>
            m_Companion?.CreateComponent();
        public IReadOnlyList<ShaderToolchainComponent> Components => m_Components;

        internal SharpShaderDxcToolchain(
            SharpShaderNativeLibraryResolver.LockedNativeFile compiler,
            SharpShaderNativeLibraryResolver.LockedNativeFile? companion,
            nint compilerHandle,
            nint companionHandle)
        {
            m_Compiler = compiler;
            m_Companion = companion;
            CompilerHandle = compilerHandle;
            CompanionHandle = companionHandle;
            List<ShaderToolchainComponent> components = new()
            {
                compiler.CreateComponent(),
            };
            if (companion is not null)
            {
                components.Add(companion.CreateComponent());
            }

            m_Components = Array.AsReadOnly(components.ToArray());
        }
    }

    internal static class SharpShaderNativeLibraryLayout
    {
        public const string ThirdPartyNativeRootEnvironmentVariableName =
            "INFINITY_THIRDPARTY_NATIVE_ROOT";

        private const string ThirdPartyVendorRootName = "Microsoft";
        private const string ThirdPartyRootName = "DXC";

        private enum NativeLibraryKey
        {
            Dxc,
        }

        public static IEnumerable<string> EnumerateLibraryCandidates(
            string libraryName,
            Assembly assembly)
        {
            if (!TryResolveLibraryKey(libraryName, out NativeLibraryKey key))
            {
                yield break;
            }

            string? nativeFileName = ResolvePlatformNativeFileName(key);
            if (string.IsNullOrWhiteSpace(nativeFileName))
            {
                yield break;
            }

            string baseDirectory = AppContext.BaseDirectory;
            string assemblyDirectory =
                Path.GetDirectoryName(assembly.Location) ?? baseDirectory;
            string? osFolder = ResolveBuildOsFolder();
            string? archFolder = ResolveBuildArchFolder();
            if (string.IsNullOrWhiteSpace(osFolder)
                || string.IsNullOrWhiteSpace(archFolder))
            {
                yield break;
            }

            HashSet<string> emitted = new(StringComparer.OrdinalIgnoreCase);
            foreach (string thirdPartyNativeRoot in
                     EnumerateThirdPartyNativeRoots(baseDirectory, assemblyDirectory))
            {
                string thirdPartyCandidate = Path.Combine(
                    thirdPartyNativeRoot,
                    ThirdPartyVendorRootName,
                    ThirdPartyRootName,
                    osFolder,
                    archFolder,
                    nativeFileName);

                string canonicalCandidate = Path.GetFullPath(thirdPartyCandidate);
                if (emitted.Add(canonicalCandidate))
                {
                    yield return canonicalCandidate;
                }
            }
        }

        public static string? ResolveCompanionFileName()
        {
            if (OperatingSystem.IsWindows())
            {
                return "dxil.dll";
            }

            if (OperatingSystem.IsLinux())
            {
                return "libdxil.so";
            }

            return null;
        }

        public static bool IsDxcLibraryName(string libraryName)
        {
            return TryResolveLibraryKey(libraryName, out _);
        }

        private static IEnumerable<string> EnumerateThirdPartyNativeRoots(
            params string[] locations)
        {
            string? configuredRoot = Environment.GetEnvironmentVariable(
                ThirdPartyNativeRootEnvironmentVariableName);
            if (!string.IsNullOrWhiteSpace(configuredRoot))
            {
                yield return Path.GetFullPath(configuredRoot.Trim());
                yield break;
            }

            foreach (string binariesRoot in EnumerateCanonicalBinariesRoots(locations))
            {
                yield return Path.Combine(binariesRoot, "ThirdParty");
            }
        }

        private static IEnumerable<string> EnumerateCanonicalBinariesRoots(
            params string[] locations)
        {
            HashSet<string> emitted = new(StringComparer.OrdinalIgnoreCase);
            string marker =
                $"{Path.DirectorySeparatorChar}Binaries{Path.DirectorySeparatorChar}";

            foreach (string location in locations)
            {
                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(location);
                }
                catch (Exception ex) when (
                    ex is ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
                {
                    continue;
                }

                string normalized = fullPath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                int markerIndex = normalized.IndexOf(
                    marker,
                    StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0)
                {
                    continue;
                }

                string binariesRoot = normalized.Substring(
                    0,
                    markerIndex + marker.Length - 1);
                if (emitted.Add(binariesRoot))
                {
                    yield return binariesRoot;
                }
            }
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

        private static bool TryResolveLibraryKey(
            string libraryName,
            out NativeLibraryKey key)
        {
            string normalized = NormalizeLibraryName(libraryName);
            if (normalized.Equals("dxcompiler", StringComparison.OrdinalIgnoreCase))
            {
                key = NativeLibraryKey.Dxc;
                return true;
            }

            key = default;
            return false;
        }

        private static string NormalizeLibraryName(string libraryName)
        {
            if (string.IsNullOrWhiteSpace(libraryName))
            {
                return string.Empty;
            }

            string fileName = Path.GetFileNameWithoutExtension(libraryName.Trim());
            if (fileName.StartsWith("lib", StringComparison.OrdinalIgnoreCase))
            {
                fileName = fileName.Substring(3);
            }

            return fileName;
        }

        private static string? ResolvePlatformNativeFileName(NativeLibraryKey key)
        {
            if (OperatingSystem.IsWindows())
            {
                return key switch
                {
                    NativeLibraryKey.Dxc => "dxcompiler.dll",
                    _ => null,
                };
            }

            if (OperatingSystem.IsLinux())
            {
                return key switch
                {
                    NativeLibraryKey.Dxc => "libdxcompiler.so",
                    _ => null,
                };
            }

            if (OperatingSystem.IsMacOS())
            {
                return key switch
                {
                    NativeLibraryKey.Dxc => "libdxcompiler.dylib",
                    _ => null,
                };
            }

            return null;
        }
    }
}
