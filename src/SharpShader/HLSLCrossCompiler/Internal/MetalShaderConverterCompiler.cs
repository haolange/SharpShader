using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;

namespace SharpShader.HLSLCrossCompiler.Internal
{
    internal static class MetalShaderConverterCompiler
    {
        private const string ConverterPathEnvironmentVariable = "INFINITY_MSC_PATH";
        private const int MaxCapturedOutputCharacters = 1024 * 1024;
        private const long MaxMetalLibraryBytes = 256L * 1024 * 1024;
        private const long MaxReflectionBytes = 16L * 1024 * 1024;
        private const int CleanupAttemptCount = 3;
        private static readonly TimeSpan s_ProcessTerminationTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan s_OutputDrainTimeout = TimeSpan.FromSeconds(5);

        public static ShaderCompileResult CompileToMetalLibrary(ShaderCompileRequest request, ShaderCompilerExecutionContext? context)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            string requestedProfile = DxcArgumentBuilder.BuildProfile(request.Stage, request.ShaderModel);
            string tempDir = string.Empty;

            try
            {
                string converterPath = ResolveConverterPath(request);
                ShaderCompileRequest dxilRequest = request with { Target = ShaderTargetKind.Dxil };
                ShaderCompileResult dxilResult =
                    context?.NativeCompileOverride?.Invoke(dxilRequest) ??
                    NativeDxcCompiler.Compile(dxilRequest);
                if (dxilResult.Bytecode.Length == 0)
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.CompileFailed,
                        "DXIL output is empty, cannot convert to Metal library.",
                        dxilResult.Diagnostics,
                        requestedProfile);
                }

                tempDir = Path.Combine(
                    Path.GetTempPath(),
                    "Infinity",
                    "SharpShader",
                    "MetalShaderConverter",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);

                string dxilPath = Path.Combine(tempDir, "input.dxil");
                string outputPath = Path.Combine(tempDir, "output.metallib");
                string reflectionPath = Path.Combine(tempDir, "output.reflection.json");
                File.WriteAllBytes(dxilPath, dxilResult.Bytecode);
                request.CancellationToken.ThrowIfCancellationRequested();

                ConverterExecutionResult executionResult = RunConverterAsync(
                    converterPath,
                    dxilPath,
                    outputPath,
                    reflectionPath,
                    request).GetAwaiter().GetResult();
                string diagnostics = BuildDiagnostics(
                    dxilResult.Diagnostics,
                    executionResult.StandardOutput,
                    executionResult.StandardError);
                ValidateConverterExitCode(executionResult.ExitCode, diagnostics, requestedProfile);
                byte[] libraryBytes = ReadBoundedOutputFile(
                    outputPath,
                    "metallib",
                    MaxMetalLibraryBytes,
                    diagnostics,
                    requestedProfile);
                ValidateMetalLibrary(libraryBytes, diagnostics, requestedProfile);

                byte[] reflectionBytes = ReadBoundedOutputFile(
                    reflectionPath,
                    "reflection",
                    MaxReflectionBytes,
                    diagnostics,
                    requestedProfile);
                ValidateReflection(reflectionBytes, diagnostics, requestedProfile);

                string cleanupDiagnostics = CleanupTemporaryDirectory(ref tempDir);
                return new ShaderCompileResult
                {
                    Bytecode = libraryBytes,
                    ReflectionData = reflectionBytes,
                    Diagnostics = BuildDiagnostics(diagnostics, cleanupDiagnostics),
                    Warnings = dxilResult.Warnings,
                };
            }
            catch (OperationCanceledException ex)
            {
                string cleanupDiagnostics = CleanupTemporaryDirectory(ref tempDir);
                if (!string.IsNullOrWhiteSpace(cleanupDiagnostics))
                {
                    ex.Data["MetalShaderConverterCleanup"] = cleanupDiagnostics;
                }

                throw;
            }
            catch (ShaderCompilerException ex)
            {
                string cleanupDiagnostics = CleanupTemporaryDirectory(ref tempDir);
                if (string.IsNullOrWhiteSpace(cleanupDiagnostics))
                {
                    throw;
                }

                throw new ShaderCompilerException(
                    ex.ErrorCode,
                    ex.Message,
                    BuildDiagnostics(ex.Diagnostics, cleanupDiagnostics),
                    ex.RequestedProfile ?? requestedProfile,
                    ex);
            }
            catch (Exception ex) when (IsIoBoundaryException(ex))
            {
                string cleanupDiagnostics = CleanupTemporaryDirectory(ref tempDir);
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.CompileFailed,
                    "Metal Shader Converter I/O boundary failed.",
                    BuildDiagnostics(ex.Message, cleanupDiagnostics),
                    requestedProfile,
                    ex);
            }
            finally
            {
                CleanupTemporaryDirectory(ref tempDir);
            }
        }

        private static ProcessStartInfo BuildProcessStartInfo(
            string converterPath,
            string dxilPath,
            string outputPath,
            string reflectionPath,
            ShaderCompileRequest request)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = converterPath,
                WorkingDirectory = Path.GetDirectoryName(dxilPath) ?? string.Empty,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.ArgumentList.Add(dxilPath);
            startInfo.ArgumentList.Add($"-o={outputPath}");
            if (!string.IsNullOrWhiteSpace(request.EntryPoint))
            {
                startInfo.ArgumentList.Add($"--entry-point={request.EntryPoint}");
            }

            startInfo.ArgumentList.Add($"--output-reflection-file={reflectionPath}");
            startInfo.ArgumentList.Add(request.MslOptions.Platform switch
            {
                MslTargetPlatform.MacOS => "--deployment-os=macOS",
                MslTargetPlatform.IOS => "--deployment-os=iOS",
                _ => throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.InvalidRequest,
                    $"Unsupported Metal deployment platform '{request.MslOptions.Platform}'."),
            });
            return startInfo;
        }

        private static async Task<ConverterExecutionResult> RunConverterAsync(
            string converterPath,
            string dxilPath,
            string outputPath,
            string reflectionPath,
            ShaderCompileRequest request)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            ProcessStartInfo startInfo = BuildProcessStartInfo(
                converterPath,
                dxilPath,
                outputPath,
                reflectionPath,
                request);

            using Process process = new Process
            {
                StartInfo = startInfo,
            };

            try
            {
                if (!process.Start())
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.ToolLaunchFailed,
                        $"Metal Shader Converter failed to start from '{converterPath}'.");
                }
            }
            catch (ShaderCompilerException)
            {
                throw;
            }
            catch (Exception ex) when (IsProcessLaunchException(ex))
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.ToolLaunchFailed,
                    $"Metal Shader Converter could not be launched from '{converterPath}'.",
                    innerException: ex);
            }

            Task<string> standardOutputTask = ReadBoundedAsync(
                process.StandardOutput,
                MaxCapturedOutputCharacters);
            Task<string> standardErrorTask = ReadBoundedAsync(
                process.StandardError,
                MaxCapturedOutputCharacters);
            using CancellationTokenSource timeoutSource = new CancellationTokenSource(request.MetalShaderConverterTimeout);
            using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                request.CancellationToken,
                timeoutSource.Token);

            try
            {
                await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                timeoutSource.IsCancellationRequested &&
                !request.CancellationToken.IsCancellationRequested)
            {
                string terminationDiagnostics = TerminateProcessTree(process);
                (string standardOutput, string standardError) = await DrainOutputAsync(
                    standardOutputTask,
                    standardErrorTask).ConfigureAwait(false);
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.ToolTimedOut,
                    $"Metal Shader Converter exceeded the configured timeout of {request.MetalShaderConverterTimeout}.",
                    BuildDiagnostics(standardOutput, standardError, terminationDiagnostics));
            }
            catch (OperationCanceledException)
            {
                TerminateProcessTree(process);
                await DrainOutputAsync(standardOutputTask, standardErrorTask).ConfigureAwait(false);
                request.CancellationToken.ThrowIfCancellationRequested();
                throw;
            }

            (string completedStandardOutput, string completedStandardError) = await DrainOutputAsync(
                standardOutputTask,
                standardErrorTask).ConfigureAwait(false);
            return new ConverterExecutionResult(
                process.ExitCode,
                completedStandardOutput,
                completedStandardError);
        }

        internal static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters)
        {
            char[] buffer = new char[4096];
            StringBuilder captured = new StringBuilder(Math.Min(maximumCharacters, buffer.Length));
            long omittedCharacters = 0;

            while (true)
            {
                int charactersRead = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (charactersRead == 0)
                {
                    break;
                }

                int remainingCapacity = maximumCharacters - captured.Length;
                int charactersToCapture = Math.Min(Math.Max(remainingCapacity, 0), charactersRead);
                if (charactersToCapture > 0)
                {
                    captured.Append(buffer, 0, charactersToCapture);
                }

                omittedCharacters += charactersRead - charactersToCapture;
            }

            if (omittedCharacters > 0)
            {
                captured.AppendLine();
                captured.Append(CultureInfo.InvariantCulture, $"[output truncated; omitted {omittedCharacters} characters]");
            }

            return captured.ToString();
        }

        internal static void ValidateConverterExitCode(
            int exitCode,
            string diagnostics,
            string requestedProfile)
        {
            if (exitCode != 0)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.CompileFailed,
                    $"Metal Shader Converter exited with code {exitCode}.",
                    diagnostics,
                    requestedProfile);
            }
        }

        internal static byte[] ReadBoundedOutputFile(
            string path,
            string outputDescription,
            long maximumBytes,
            string diagnostics,
            string requestedProfile)
        {
            if (!File.Exists(path))
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.CompileFailed,
                    $"Metal Shader Converter did not produce the requested {outputDescription} output.",
                    diagnostics,
                    requestedProfile);
            }

            using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            long outputLength = stream.Length;
            if (outputLength == 0)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.CompileFailed,
                    $"Metal Shader Converter produced empty {outputDescription} output.",
                    diagnostics,
                    requestedProfile);
            }

            if (outputLength > maximumBytes)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.CompileFailed,
                    $"Metal Shader Converter {outputDescription} output is {outputLength} bytes, exceeding the {maximumBytes}-byte limit.",
                    diagnostics,
                    requestedProfile);
            }

            int boundedLength = checked((int)outputLength);
            byte[] output = new byte[boundedLength];
            int totalBytesRead = 0;
            while (totalBytesRead < output.Length)
            {
                int bytesRead = stream.Read(output, totalBytesRead, output.Length - totalBytesRead);
                if (bytesRead == 0)
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.CompileFailed,
                        $"Metal Shader Converter {outputDescription} output ended before its declared length.",
                        diagnostics,
                        requestedProfile);
                }

                totalBytesRead += bytesRead;
            }

            if (stream.ReadByte() != -1)
            {
                long finalLength = stream.Length;
                string lengthMessage = finalLength > maximumBytes
                    ? $" and exceeds the {maximumBytes}-byte limit"
                    : " while it was being read";
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.CompileFailed,
                    $"Metal Shader Converter {outputDescription} output changed{lengthMessage}.",
                    diagnostics,
                    requestedProfile);
            }

            return output;
        }

        internal static void ValidateMetalLibrary(
            byte[] libraryBytes,
            string diagnostics,
            string requestedProfile)
        {
            if (libraryBytes.Length < 4 ||
                libraryBytes[0] != (byte)'M' ||
                libraryBytes[1] != (byte)'T' ||
                libraryBytes[2] != (byte)'L' ||
                libraryBytes[3] != (byte)'B')
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.CompileFailed,
                    "Metal Shader Converter produced a metallib without the expected MTLB header.",
                    diagnostics,
                    requestedProfile);
            }
        }

        internal static void ValidateReflection(
            byte[] reflectionBytes,
            string diagnostics,
            string requestedProfile)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    reflectionBytes,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = 64,
                    });

                if (document.RootElement.ValueKind != JsonValueKind.Object &&
                    document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.CompileFailed,
                        "Metal Shader Converter reflection JSON root must be an object or array.",
                        diagnostics,
                        requestedProfile);
                }
            }
            catch (JsonException ex)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.CompileFailed,
                    "Metal Shader Converter produced invalid reflection JSON.",
                    BuildDiagnostics(diagnostics, ex.Message),
                    requestedProfile,
                    ex);
            }
        }

        private static string ResolveConverterPath(ShaderCompileRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.MetalShaderConverterPath))
            {
                return NormalizeConfiguredPath(
                    request.MetalShaderConverterPath,
                    "ShaderCompileRequest.MetalShaderConverterPath",
                    ShaderCompilerErrorCode.InvalidRequest);
            }

            string? environmentPath = Environment.GetEnvironmentVariable(ConverterPathEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(environmentPath))
            {
                return NormalizeConfiguredPath(
                    environmentPath,
                    ConverterPathEnvironmentVariable,
                    ShaderCompilerErrorCode.BackendUnavailable);
            }

            string? canonicalInstallPath = ResolveCanonicalInstallPath();
            if (canonicalInstallPath != null)
            {
                return canonicalInstallPath;
            }

            throw new ShaderCompilerException(
                ShaderCompilerErrorCode.BackendUnavailable,
                $"Metal Shader Converter was not found. Configure an absolute path with ShaderCompileRequest.MetalShaderConverterPath or {ConverterPathEnvironmentVariable}. " +
                "No PATH or xcrun probing is performed.");
        }

        private static string NormalizeConfiguredPath(
            string configuredPath,
            string configurationSource,
            ShaderCompilerErrorCode errorCode)
        {
            string trimmedPath = configuredPath.Trim();
            if (!Path.IsPathFullyQualified(trimmedPath))
            {
                throw new ShaderCompilerException(
                    errorCode,
                    $"{configurationSource} must be a fully-qualified path, but was '{trimmedPath}'.");
            }

            string normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(trimmedPath);
            }
            catch (Exception ex) when (
                ex is ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                throw new ShaderCompilerException(
                    errorCode,
                    $"{configurationSource} is not a valid absolute path.",
                    innerException: ex);
            }

            if (!File.Exists(normalizedPath))
            {
                throw new ShaderCompilerException(
                    errorCode,
                    $"{configurationSource} does not point to an existing Metal Shader Converter executable: '{normalizedPath}'.");
            }

            return normalizedPath;
        }

        private static string? ResolveCanonicalInstallPath()
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            string programFilesPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrWhiteSpace(programFilesPath))
            {
                return null;
            }

            string candidatePath = Path.GetFullPath(Path.Combine(
                programFilesPath,
                "Metal Shader Converter",
                "bin",
                "metal-shaderconverter.exe"));
            return File.Exists(candidatePath) ? candidatePath : null;
        }

        private static string TerminateProcessTree(Process process)
        {
            try
            {
                if (process.HasExited)
                {
                    return string.Empty;
                }

                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit((int)s_ProcessTerminationTimeout.TotalMilliseconds))
                {
                    return "Metal Shader Converter did not exit within the process termination timeout.";
                }
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                NotSupportedException or
                System.ComponentModel.Win32Exception)
            {
                return $"Failed to terminate Metal Shader Converter process tree: {ex.Message}";
            }

            return string.Empty;
        }

        private static async Task<(string StandardOutput, string StandardError)> DrainOutputAsync(
            Task<string> standardOutputTask,
            Task<string> standardErrorTask)
        {
            Task combinedTask = Task.WhenAll(standardOutputTask, standardErrorTask);
            Task completedTask = await Task.WhenAny(
                combinedTask,
                Task.Delay(s_OutputDrainTimeout)).ConfigureAwait(false);
            if (completedTask != combinedTask)
            {
                ObserveFault(combinedTask);
                return (
                    GetCompletedOutput(standardOutputTask, "stdout"),
                    GetCompletedOutput(standardErrorTask, "stderr"));
            }

            try
            {
                await combinedTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (IsIoBoundaryException(ex))
            {
                return (
                    GetCompletedOutput(standardOutputTask, "stdout"),
                    BuildDiagnostics(
                        GetCompletedOutput(standardErrorTask, "stderr"),
                        $"Failed to drain Metal Shader Converter output: {ex.Message}"));
            }

            return (standardOutputTask.Result, standardErrorTask.Result);
        }

        private static string GetCompletedOutput(Task<string> outputTask, string streamName)
        {
            if (outputTask.IsCompletedSuccessfully)
            {
                return outputTask.Result;
            }

            if (outputTask.IsFaulted)
            {
                Exception exception = outputTask.Exception?.GetBaseException() ??
                    new IOException("Unknown output capture failure.");
                return $"Failed to capture Metal Shader Converter {streamName}: {exception.Message}";
            }

            return string.Empty;
        }

        private static void ObserveFault(Task task)
        {
            _ = task.ContinueWith(
                static completedTask => _ = completedTask.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static string CleanupTemporaryDirectory(ref string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string cleanupPath = path;
            path = string.Empty;
            Exception? lastException = null;
            for (int attempt = 1; attempt <= CleanupAttemptCount; ++attempt)
            {
                try
                {
                    if (!Directory.Exists(cleanupPath))
                    {
                        return string.Empty;
                    }

                    Directory.Delete(cleanupPath, recursive: true);
                    return string.Empty;
                }
                catch (Exception ex) when (IsIoBoundaryException(ex))
                {
                    lastException = ex;
                    if (attempt < CleanupAttemptCount)
                    {
                        Thread.Sleep(TimeSpan.FromMilliseconds(25 * attempt));
                    }
                }
            }

            return $"Failed to remove Metal Shader Converter temporary directory '{cleanupPath}' after {CleanupAttemptCount} attempts: {lastException?.Message}";
        }

        private static bool IsProcessLaunchException(Exception ex)
        {
            return ex is InvalidOperationException or
                IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                System.ComponentModel.Win32Exception;
        }

        private static bool IsIoBoundaryException(Exception ex)
        {
            return ex is IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                PathTooLongException or
                System.Security.SecurityException;
        }

        private static string BuildDiagnostics(params string[] messages)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < messages.Length; ++i)
            {
                string message = messages[i];
                if (string.IsNullOrWhiteSpace(message))
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }

                sb.AppendLine(message.Trim());
            }

            return sb.ToString().Trim();
        }

        private readonly record struct ConverterExecutionResult(
            int ExitCode,
            string StandardOutput,
            string StandardError);
    }
}
