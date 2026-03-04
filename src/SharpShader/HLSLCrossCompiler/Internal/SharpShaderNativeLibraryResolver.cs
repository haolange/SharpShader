using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SharpShader.HLSLCrossCompiler.Internal;

internal static class SharpShaderNativeLibraryResolver
{
    private static readonly object Sync = new();
    private static bool ResolverRegistered;

    public static void EnsureResolverRegistered(Assembly assembly)
    {
        lock (Sync)
        {
            if (ResolverRegistered)
            {
                return;
            }

            try
            {
                NativeLibrary.SetDllImportResolver(assembly, ResolveLibraryImport);
            }
            catch (InvalidOperationException)
            {
                // Resolver was already set by another initialization path.
            }

            ResolverRegistered = true;
        }
    }

    public static IEnumerable<string> EnumerateCandidates(string libraryName, Assembly assembly)
    {
        return SharpShaderNativeLibraryLayout.EnumerateLibraryCandidates(libraryName, assembly);
    }

    private static nint ResolveLibraryImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        foreach (string candidate in SharpShaderNativeLibraryLayout.EnumerateLibraryCandidates(libraryName, assembly))
        {
            MacNativePayloadSanitizer.EnsureFileIsSanitized(candidate);
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out nint handle))
            {
                return handle;
            }
        }

        return 0;
    }
}

internal static class SharpShaderNativeLibraryLayout
{
    public const string DxcEnvironmentVariableName = "INFINITY_SHARPSHADER_DXCOMPILER_PATH";

    private const string ThirdPartyVendorRootName = "Microsoft";
    private const string ThirdPartyRootName = "DXC";

    private enum NativeLibraryKey
    {
        Dxc,
    }

    public static IEnumerable<string> EnumerateKnownPayloadDirectories(Assembly assembly)
    {
        string baseDir = AppContext.BaseDirectory;
        string assemblyDir = Path.GetDirectoryName(assembly.Location) ?? baseDir;

        HashSet<string> emitted = new(StringComparer.OrdinalIgnoreCase);

        foreach (string root in EnumerateSearchRoots(baseDir, assemblyDir))
        {
            string thirdPartyMicrosoftArm64 = Path.Combine(root, "Binaries", "ThirdParty", ThirdPartyVendorRootName, ThirdPartyRootName, "macOS", "ARM64");
            if (emitted.Add(thirdPartyMicrosoftArm64))
            {
                yield return thirdPartyMicrosoftArm64;
            }

            string thirdPartyMicrosoftAmd64 = Path.Combine(root, "Binaries", "ThirdParty", ThirdPartyVendorRootName, ThirdPartyRootName, "macOS", "AMD64");
            if (emitted.Add(thirdPartyMicrosoftAmd64))
            {
                yield return thirdPartyMicrosoftAmd64;
            }

        }
    }

    public static IEnumerable<string> EnumerateLibraryCandidates(string libraryName, Assembly assembly)
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

        string baseDir = AppContext.BaseDirectory;
        string assemblyDir = Path.GetDirectoryName(assembly.Location) ?? baseDir;
        string? osFolder = ResolveBuildOsFolder();
        string? archFolder = ResolveBuildArchFolder();

        HashSet<string> emitted = new(StringComparer.OrdinalIgnoreCase);

        if (key == NativeLibraryKey.Dxc)
        {
            foreach (string configured in EnumerateConfiguredDxcCandidates(nativeFileName))
            {
                if (emitted.Add(configured))
                {
                    yield return configured;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(osFolder) && !string.IsNullOrWhiteSpace(archFolder))
        {
            foreach (string root in EnumerateSearchRoots(baseDir, assemblyDir))
            {
                string thirdPartyCandidate = Path.Combine(
                    root,
                    "Binaries",
                    "ThirdParty",
                    ThirdPartyVendorRootName,
                    ThirdPartyRootName,
                    osFolder,
                    archFolder,
                    nativeFileName);

                if (emitted.Add(thirdPartyCandidate))
                {
                    yield return thirdPartyCandidate;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateConfiguredDxcCandidates(string nativeFileName)
    {
        string? configuredPath = Environment.GetEnvironmentVariable(DxcEnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            yield break;
        }

        string normalized = configuredPath.Trim();
        yield return normalized;
        yield return Path.Combine(normalized, nativeFileName);
    }

    private static IEnumerable<string> EnumerateSearchRoots(params string[] startPoints)
    {
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        foreach (string start in startPoints)
        {
            if (string.IsNullOrWhiteSpace(start))
            {
                continue;
            }

            DirectoryInfo? current = new(start);
            int depth = 0;
            while (current != null && depth < 16)
            {
                if (visited.Add(current.FullName))
                {
                    yield return current.FullName;
                }

                current = current.Parent;
                depth++;
            }
        }

        string cwd = Directory.GetCurrentDirectory();
        if (visited.Add(cwd))
        {
            yield return cwd;
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

    private static bool TryResolveLibraryKey(string libraryName, out NativeLibraryKey key)
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
