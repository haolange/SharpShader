using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace SharpShader.HLSLCrossCompiler.Internal;

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

        foreach (string root in SharpShaderNativeLibraryLayout.EnumerateKnownPayloadDirectories(assembly))
        {
            EnsureDirectoryIsSanitized(root);
        }

        string? configuredDxcPath = Environment.GetEnvironmentVariable(SharpShaderNativeLibraryLayout.DxcEnvironmentVariableName);
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
