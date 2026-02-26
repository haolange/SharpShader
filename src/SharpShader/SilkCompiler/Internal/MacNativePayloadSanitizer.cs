using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace SharpShader.SilkCompiler.Internal;

internal static class MacNativePayloadSanitizer
{
    private static readonly object Sync = new();
    private static readonly HashSet<string> SanitizedPaths = new(StringComparer.OrdinalIgnoreCase);

    public static void EnsureKnownPayloadsAreSanitized(Assembly assembly)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        foreach (string root in EnumerateKnownPayloadRoots(assembly))
        {
            EnsureDirectoryIsSanitized(root);
        }

        string? configuredDxcPath = Environment.GetEnvironmentVariable("INFINITY_SHARPSHADER_DXCOMPILER_PATH");
        EnsureFileOrDirectoryIsSanitized(configuredDxcPath);
    }

    public static void EnsureFileIsSanitized(string? path)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        if (!TryNormalizePath(path, out string normalized) || !File.Exists(normalized))
        {
            return;
        }

        if (TryMarkSanitized(normalized))
        {
            TryClearQuarantine(normalized, recursive: false);
        }
    }

    public static void EnsureDirectoryIsSanitized(string? path)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        if (!TryNormalizePath(path, out string normalized) || !Directory.Exists(normalized))
        {
            return;
        }

        if (TryMarkSanitized(normalized))
        {
            TryClearQuarantine(normalized, recursive: true);
        }
    }

    public static void EnsureFileOrDirectoryIsSanitized(string? path)
    {
        if (!TryNormalizePath(path, out string normalized))
        {
            return;
        }

        if (File.Exists(normalized))
        {
            EnsureFileIsSanitized(normalized);
            return;
        }

        if (Directory.Exists(normalized))
        {
            EnsureDirectoryIsSanitized(normalized);
        }
    }

    private static IEnumerable<string> EnumerateKnownPayloadRoots(Assembly assembly)
    {
        string baseDir = AppContext.BaseDirectory;
        string assemblyDir = Path.GetDirectoryName(assembly.Location) ?? baseDir;

        HashSet<string> emitted = new(StringComparer.OrdinalIgnoreCase);

        foreach (string candidate in EnumerateRuntimeCandidates(baseDir))
        {
            if (emitted.Add(candidate))
            {
                yield return candidate;
            }
        }

        foreach (string candidate in EnumerateRuntimeCandidates(assemblyDir))
        {
            if (emitted.Add(candidate))
            {
                yield return candidate;
            }
        }

        foreach (string root in EnumerateSearchRoots(baseDir, assemblyDir))
        {
            string runtimeRoot = Path.Combine(root, "Source", "Graphics", "SharpShader", "runtimes");
            foreach (string candidate in EnumerateRuntimeCandidates(runtimeRoot))
            {
                if (emitted.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateRuntimeCandidates(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            yield break;
        }

        yield return Path.Combine(root, "runtimes", "osx-arm64", "native");
        yield return Path.Combine(root, "runtimes", "osx-x64", "native");
        yield return Path.Combine(root, "osx-arm64", "native");
        yield return Path.Combine(root, "osx-x64", "native");
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

            DirectoryInfo? current = new DirectoryInfo(start);
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

    private static bool TryNormalizePath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryMarkSanitized(string normalizedPath)
    {
        lock (Sync)
        {
            return SanitizedPaths.Add(normalizedPath);
        }
    }

    private static void TryClearQuarantine(string path, bool recursive)
    {
        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo("/usr/bin/xattr")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(recursive ? "-dr" : "-d");
            startInfo.ArgumentList.Add("com.apple.quarantine");
            startInfo.ArgumentList.Add(path);

            using Process process = Process.Start(startInfo)!;
            process.WaitForExit(2_000);
            _ = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
        }
        catch
        {
            // Best effort only; native load/compile path provides diagnostics on failure.
        }
    }
}
