using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SharpShader.SilkCompiler.Internal;

internal static class RosettaDxcHostClient
{
    private const int HelperTimeoutMilliseconds = 15_000;
    private const int StreamDrainTimeoutMilliseconds = 2_000;

    private static readonly object HealthGate = new();
    private static string? s_HelperUnavailableReason;

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static bool IsRosettaInstalled()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo("/usr/bin/arch")
            {
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-x86_64");
            startInfo.ArgumentList.Add("/usr/bin/true");

            using Process process = Process.Start(startInfo)!;
            process.WaitForExit(5_000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryResolveHelperPath(out string helperPath, out string errorMessage)
    {
        if (TryGetMarkedUnavailableReason(out string unavailableReason))
        {
            helperPath = string.Empty;
            errorMessage = unavailableReason;
            return false;
        }

        string? configuredPath = Environment.GetEnvironmentVariable("INFINITY_SHARPSHADER_DXCHOST_PATH");
        HashSet<string> candidates = new HashSet<string>(StringComparer.Ordinal);
        List<string> incompatibleCandidates = new List<string>();

        AddCandidate(candidates, configuredPath);
        AddCandidate(candidates, Path.Combine(AppContext.BaseDirectory, "runtimes", "osx-x64", "tools", "SharpShader.DxcHost", "SharpShader.DxcHost"));
        AddCandidate(candidates, Path.Combine(AppContext.BaseDirectory, "tools", "SharpShader.DxcHost", "SharpShader.DxcHost"));
        AddCandidate(candidates, Path.Combine(AppContext.BaseDirectory, "SharpShader.DxcHost"));

        foreach (string root in EnumerateSearchRoots())
        {
            AddCandidate(candidates, Path.Combine(root, "Source", "Graphics", "SharpShader", "runtimes", "osx-x64", "tools", "SharpShader.DxcHost", "SharpShader.DxcHost"));
            AddCandidate(candidates, Path.Combine(root, "Source", "Tools", "SharpShader.DxcHost", "bin", "x64", "Debug", "net10.0", "SharpShader.DxcHost"));
            AddCandidate(candidates, Path.Combine(root, "Source", "Tools", "SharpShader.DxcHost", "bin", "x64", "Release", "net10.0", "SharpShader.DxcHost"));
            AddCandidate(candidates, Path.Combine(root, "Binaries", "Graphics", "SharpShader", "runtimes", "osx-x64", "tools", "SharpShader.DxcHost", "SharpShader.DxcHost"));
        }

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                if (IsRosettaCompatibleExecutable(candidate))
                {
                    helperPath = candidate;
                    errorMessage = string.Empty;
                    return true;
                }

                incompatibleCandidates.Add(candidate);
            }
        }

        helperPath = string.Empty;
        if (incompatibleCandidates.Count > 0)
        {
            errorMessage =
                "Located SharpShader.DxcHost candidates, but none are x86_64-compatible for Rosetta. " +
                $"Incompatible candidates: {string.Join(", ", incompatibleCandidates)}. " +
                "Publish x64 helper with: dotnet publish Source/Tools/SharpShader.DxcHost/SharpShader.DxcHost.csproj -c Release -r osx-x64 --self-contained true";
        }
        else
        {
            errorMessage =
                "Cannot locate SharpShader.DxcHost helper. " +
                "Set INFINITY_SHARPSHADER_DXCHOST_PATH or publish helper to runtimes/osx-x64/tools/SharpShader.DxcHost.";
        }

        return false;
    }

    private static IEnumerable<string> EnumerateSearchRoots()
    {
        HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] startPoints = new[]
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
        };

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
    }

    private static void AddCandidate(HashSet<string> candidates, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        string normalized = candidate;
        if (Directory.Exists(normalized))
        {
            normalized = Path.Combine(normalized, "SharpShader.DxcHost");
        }

        try
        {
            normalized = Path.GetFullPath(normalized);
        }
        catch
        {
            // Ignore malformed candidate paths.
        }

        candidates.Add(normalized);
    }

    public static ShaderCompileResult Compile(ShaderCompileRequest request, string helperPath)
    {
        if (TryGetMarkedUnavailableReason(out string unavailableReason))
        {
            throw new ShaderCompilerException(
                ShaderCompilerErrorCode.RosettaHelperUnavailable,
                unavailableReason);
        }

        RosettaDxcHostCompileRequest payload = new RosettaDxcHostCompileRequest
        {
            Request = request,
        };

        ProcessStartInfo startInfo = new ProcessStartInfo("/usr/bin/arch")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-x86_64");
        startInfo.ArgumentList.Add(helperPath);

        using Process process = Process.Start(startInfo)
            ?? throw MarkUnavailableAndCreateException(
                ShaderCompilerErrorCode.RosettaHelperUnavailable,
                $"Failed to launch Rosetta helper process: {helperPath}");

        string payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        try
        {
            process.StandardInput.Write(payloadJson);
            process.StandardInput.Flush();
            process.StandardInput.Close();
        }
        catch (IOException ioEx)
        {
            string writeStdErr = process.StandardError.ReadToEnd();
            throw MarkUnavailableAndCreateException(
                ShaderCompilerErrorCode.BackendUnavailable,
                $"Rosetta helper terminated before request write completed: {helperPath}.",
                string.IsNullOrWhiteSpace(writeStdErr) ? ioEx.Message : writeStdErr,
                ioEx);
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(HelperTimeoutMilliseconds))
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
                // Ignore kill exceptions; timeout has already failed this path.
            }

            throw MarkUnavailableAndCreateException(
                ShaderCompilerErrorCode.BackendUnavailable,
                "Rosetta helper timed out while compiling shader.",
                AwaitTaskOrDefault(stderrTask));
        }

        string stdout = AwaitTaskOrDefault(stdoutTask);
        string stderr = AwaitTaskOrDefault(stderrTask);

        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw MarkUnavailableAndCreateException(
                ShaderCompilerErrorCode.BackendUnavailable,
                "Rosetta helper returned empty output.",
                stderr);
        }

        RosettaDxcHostCompileResponse? response = JsonSerializer.Deserialize<RosettaDxcHostCompileResponse>(stdout, JsonOptions);
        if (response == null)
        {
            throw MarkUnavailableAndCreateException(
                ShaderCompilerErrorCode.BackendUnavailable,
                "Rosetta helper output could not be parsed.",
                stdout + Environment.NewLine + stderr);
        }

        if (!response.Success)
        {
            if (response.ErrorCode is ShaderCompilerErrorCode.BackendUnavailable or ShaderCompilerErrorCode.RosettaHelperUnavailable)
            {
                throw MarkUnavailableAndCreateException(
                    response.ErrorCode,
                    string.IsNullOrWhiteSpace(response.Message)
                        ? "Rosetta helper reported compile failure."
                        : response.Message,
                    response.Diagnostics);
            }

            throw new ShaderCompilerException(
                response.ErrorCode,
                string.IsNullOrWhiteSpace(response.Message)
                    ? "Rosetta helper reported compile failure."
                    : response.Message,
                response.Diagnostics);
        }

        if (response.Result == null)
        {
            throw new ShaderCompilerException(
                ShaderCompilerErrorCode.BackendUnavailable,
                "Rosetta helper succeeded but no compile result payload was returned.");
        }

        return response.Result;
    }

    private static ShaderCompilerException MarkUnavailableAndCreateException(
        ShaderCompilerErrorCode errorCode,
        string message,
        string diagnostics = "",
        Exception? innerException = null)
    {
        lock (HealthGate)
        {
            s_HelperUnavailableReason = $"Rosetta DXC helper marked unavailable for this process. {message}";
        }

        return new ShaderCompilerException(errorCode, message, diagnostics, innerException: innerException);
    }

    private static bool TryGetMarkedUnavailableReason(out string reason)
    {
        lock (HealthGate)
        {
            if (!string.IsNullOrWhiteSpace(s_HelperUnavailableReason))
            {
                reason = s_HelperUnavailableReason;
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    private static string AwaitTaskOrDefault(Task<string> task)
    {
        try
        {
            if (!task.Wait(StreamDrainTimeoutMilliseconds))
            {
                return string.Empty;
            }

            return task.IsCompletedSuccessfully ? task.Result : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsRosettaCompatibleExecutable(string helperPath)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return true;
        }

        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo("/usr/bin/file")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(helperPath);

            using Process process = Process.Start(startInfo)!;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5_000);

            if (process.ExitCode != 0)
            {
                return false;
            }

            string lower = output.ToLowerInvariant();
            if (lower.Contains("x86_64", StringComparison.Ordinal))
            {
                return true;
            }

            if (lower.Contains("arm64", StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
