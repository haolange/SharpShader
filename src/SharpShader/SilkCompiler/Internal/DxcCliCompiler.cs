using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace SharpShader.SilkCompiler.Internal;

internal static class DxcCliCompiler
{
    private const string DxcCliPathEnvVar = "INFINITY_SHARPSHADER_DXC_CLI_PATH";
    private const string DxcLibraryPathEnvVar = "INFINITY_SHARPSHADER_DXCOMPILER_PATH";
    private static readonly Lazy<string?> DxcCliPath = new(ResolveDxcCliPath);

    public static bool IsAvailable()
    {
        return !string.IsNullOrWhiteSpace(DxcCliPath.Value) && File.Exists(DxcCliPath.Value);
    }

    public static ShaderCompileResult Compile(ShaderCompileRequest request)
    {
        request.Validate();

        string? dxcPath = DxcCliPath.Value;
        if (string.IsNullOrWhiteSpace(dxcPath) || !File.Exists(dxcPath))
        {
            throw new ShaderCompilerException(
                ShaderCompilerErrorCode.BackendUnavailable,
                "DXC CLI backend is unavailable. Set INFINITY_SHARPSHADER_DXC_CLI_PATH or ensure dxc is in PATH.");
        }

        string profile = DxcArgumentBuilder.BuildProfile(request.Stage, request.ShaderModel);
        List<string> arguments = DxcArgumentBuilder.BuildArguments(request, profile);

        string tempDir = Path.Combine(Path.GetTempPath(), "infinity_dxc_cli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        string sourcePath = Path.Combine(tempDir, "shader.hlsl");
        string outputPath = Path.Combine(tempDir, request.Target == ShaderTargetKind.SpirV ? "shader.spv" : "shader.bin");

        try
        {
            File.WriteAllText(sourcePath, request.Source, new UTF8Encoding(false));

            arguments.Add(sourcePath);
            arguments.Add("-Fo");
            arguments.Add(outputPath);

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = dxcPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = tempDir,
            };

            foreach (string argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            string? dyldLibraryPath = ResolveDyldLibraryPath();
            if (!string.IsNullOrWhiteSpace(dyldLibraryPath))
            {
                string existing = psi.Environment.TryGetValue("DYLD_LIBRARY_PATH", out string? value) ? value ?? string.Empty : string.Empty;
                psi.Environment["DYLD_LIBRARY_PATH"] = string.IsNullOrWhiteSpace(existing)
                    ? dyldLibraryPath
                    : $"{dyldLibraryPath}:{existing}";
            }

            using Process process = Process.Start(psi)
                ?? throw new ShaderCompilerException(ShaderCompilerErrorCode.BackendUnavailable, "Failed to start DXC CLI process.");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            string diagnostics = JoinDiagnostics(stdout, stderr);
            string warnings = ExtractWarnings(diagnostics);

            if (process.ExitCode != 0)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.CompileFailed,
                    $"DXC CLI compile failed for profile {profile}. ExitCode={process.ExitCode}",
                    diagnostics,
                    profile);
            }

            if (!File.Exists(outputPath))
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.CompileFailed,
                    $"DXC CLI compile produced no output for profile {profile}.",
                    diagnostics,
                    profile);
            }

            byte[] bytecode = File.ReadAllBytes(outputPath);
            if (bytecode.Length == 0)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.CompileFailed,
                    $"DXC CLI produced empty bytecode for profile {profile}.",
                    diagnostics,
                    profile);
            }

            return new ShaderCompileResult
            {
                Bytecode = bytecode,
                Diagnostics = diagnostics,
                Warnings = warnings,
            };
        }
        catch (ShaderCompilerException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            InvalidOperationException)
        {
            throw new ShaderCompilerException(
                ShaderCompilerErrorCode.BackendUnavailable,
                "DXC CLI backend failed unexpectedly.",
                ex.Message,
                profile,
                ex);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private static string? ResolveDxcCliPath()
    {
        foreach (string candidate in EnumerateDxcCliCandidates())
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateDxcCliCandidates()
    {
        string? configuredPath = Environment.GetEnvironmentVariable(DxcCliPathEnvVar);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            yield return configuredPath!;
            yield return Path.Combine(configuredPath!, "dxc");
            yield return Path.Combine(configuredPath!, "dxc-3.7");
        }

        string? configuredDylibPath = Environment.GetEnvironmentVariable(DxcLibraryPathEnvVar);
        if (!string.IsNullOrWhiteSpace(configuredDylibPath))
        {
            string baseDir = configuredDylibPath!;
            if (File.Exists(baseDir))
            {
                baseDir = Path.GetDirectoryName(baseDir) ?? baseDir;
            }

            yield return Path.Combine(baseDir, "dxc");
            yield return Path.Combine(baseDir, "dxc-3.7");
            yield return Path.Combine(baseDir, "..", "bin", "dxc");
            yield return Path.Combine(baseDir, "..", "bin", "dxc-3.7");
        }

        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (string pathDir in pathValue.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                yield return Path.Combine(pathDir, "dxc");
                yield return Path.Combine(pathDir, "dxc-3.7");
            }
        }
    }

    private static string? ResolveDyldLibraryPath()
    {
        string? configuredPath = Environment.GetEnvironmentVariable(DxcLibraryPathEnvVar);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        if (File.Exists(configuredPath))
        {
            return Path.GetDirectoryName(configuredPath);
        }

        if (Directory.Exists(configuredPath))
        {
            return configuredPath;
        }

        return null;
    }

    private static string JoinDiagnostics(string stdout, string stderr)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return stderr ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(stderr))
        {
            return stdout;
        }

        return $"{stdout}{Environment.NewLine}{stderr}";
    }

    private static string ExtractWarnings(string diagnostics)
    {
        if (string.IsNullOrWhiteSpace(diagnostics))
        {
            return string.Empty;
        }

        string[] warningLines = diagnostics
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("warning", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return warningLines.Length == 0
            ? string.Empty
            : string.Join(Environment.NewLine, warningLines);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore temp cleanup failures.
        }
    }
}
