using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace SharpShader.HLSLCrossCompiler.Internal;

internal static class SpirvCrossNativeLibraryBootstrap
{
    private const string ThirdPartyRootEnvironmentVariableName = "INFINITY_THIRDPARTY_NATIVE_ROOT";
    private const string PrimaryVendor = "Khronos";
    private static readonly object Sync = new();
    private static bool s_Attempted;
    private static bool s_Loaded;

    public static void EnsureLoaded()
    {
        lock (Sync)
        {
            if (s_Attempted)
            {
                if (!s_Loaded)
                {
                    throw BuildLoadFailureException();
                }

                return;
            }

            s_Attempted = true;
            s_Loaded = TryLoadFromCandidates();
            if (!s_Loaded)
            {
                throw BuildLoadFailureException();
            }
        }
    }

    private static bool TryLoadFromCandidates()
    {
        foreach (string candidate in EnumerateCandidates())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static Exception BuildLoadFailureException()
    {
        string nativeFileName = ResolvePlatformNativeFileName();
        if (string.IsNullOrWhiteSpace(nativeFileName))
        {
            return new ShaderCompilerException(
                ShaderCompilerErrorCode.BackendUnavailable,
                "SPIRV-Cross native backend is unavailable on current platform.");
        }

        return new ShaderCompilerException(
            ShaderCompilerErrorCode.BackendUnavailable,
            $"SPIRV-Cross native backend is unavailable. Expected '{nativeFileName}' under Binaries/ThirdParty/{PrimaryVendor}/SPIRV-Cross/<OS>/<Arch>/.");
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        string osFolder = ResolveBuildOsFolder();
        string archFolder = ResolveBuildArchFolder();
        string nativeFileName = ResolvePlatformNativeFileName();
        if (string.IsNullOrWhiteSpace(osFolder) || string.IsNullOrWhiteSpace(archFolder) || string.IsNullOrWhiteSpace(nativeFileName))
        {
            yield break;
        }

        HashSet<string> emitted = new(StringComparer.OrdinalIgnoreCase);
        foreach (string root in EnumerateThirdPartyRoots())
        {
            foreach (string thirdPartyRoot in EnumerateThirdPartyRootCandidates(root, PrimaryVendor))
            {
                string flattened = Path.Combine(thirdPartyRoot, "SPIRV-Cross", osFolder, archFolder, nativeFileName);
                if (emitted.Add(flattened))
                {
                    yield return flattened;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateThirdPartyRoots()
    {
        string? explicitRoot = Environment.GetEnvironmentVariable(ThirdPartyRootEnvironmentVariableName);
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            foreach (string item in explicitRoot.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string resolved = TryResolveDirectoryExplicitPath(item);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    yield return resolved;
                }
            }
        }

        HashSet<string> emitted = new(StringComparer.OrdinalIgnoreCase);
        foreach (string start in EnumerateSearchRoots())
        {
            DirectoryInfo? current = new(start);
            for (int i = 0; i < 16 && current != null; ++i)
            {
                string binaries = Path.Combine(current.FullName, "Binaries");
                if (Directory.Exists(binaries) && emitted.Add(current.FullName))
                {
                    yield return current.FullName;
                }

                current = current.Parent;
            }
        }
    }

    private static IEnumerable<string> EnumerateSearchRoots()
    {
        string baseDirectory = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            yield return baseDirectory;
        }

        string cwd = Directory.GetCurrentDirectory();
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            yield return cwd;
        }
    }

    private static IEnumerable<string> EnumerateThirdPartyRootCandidates(string root, string vendor)
    {
        string normalizedRoot = TryResolveDirectoryExplicitPath(root);
        if (string.IsNullOrWhiteSpace(normalizedRoot))
        {
            yield break;
        }

        string trimmedRoot = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string rootName = Path.GetFileName(trimmedRoot);

        yield return Path.Combine(trimmedRoot, "Binaries", "ThirdParty", vendor);
        yield return Path.Combine(trimmedRoot, "ThirdParty", vendor);
        yield return Path.Combine(trimmedRoot, vendor);

        if (rootName.Equals("Binaries", StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.Combine(trimmedRoot, "ThirdParty", vendor);
        }
        else if (rootName.Equals("ThirdParty", StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.Combine(trimmedRoot, vendor);
        }
        else if (rootName.Equals(vendor, StringComparison.OrdinalIgnoreCase))
        {
            yield return trimmedRoot;
        }
    }

    private static string TryResolveDirectoryExplicitPath(string candidateRoot)
    {
        if (string.IsNullOrWhiteSpace(candidateRoot))
        {
            return string.Empty;
        }

        try
        {
            string normalized = Path.GetFullPath(candidateRoot);
            if (Directory.Exists(normalized))
            {
                return normalized;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string ResolveBuildOsFolder()
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

        return string.Empty;
    }

    private static string ResolveBuildArchFolder()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "AMD64",
            Architecture.Arm64 => "ARM64",
            _ => string.Empty,
        };
    }

    private static string ResolvePlatformNativeFileName()
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

        return string.Empty;
    }
}
