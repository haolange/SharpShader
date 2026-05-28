using System;
using System.IO;
using System.Text;
using System.Diagnostics;

namespace SharpShader.HLSLCrossCompiler.Internal
{
    internal static class MetalShaderConverterCompiler
    {
        private const string ConverterPathEnv = "INFINITY_MSC_PATH";

        public static ShaderCompileResult CompileToMetalLibrary(ShaderCompileRequest request, ShaderCompilerExecutionContext? context)
        {
            if (request.AppleMetalStrategy != AppleMetalCompileStrategy.MetalShaderConverter)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.InvalidRequest,
                    $"Target '{ShaderTargetKind.MetalLibrary}' requires AppleMetalStrategy='{AppleMetalCompileStrategy.MetalShaderConverter}'.");
            }

            if (!OperatingSystem.IsMacOS())
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.BackendUnavailable,
                    "Metal Shader Converter path is only available on macOS.");
            }

            string? converterPath = ResolveConverterPath();
            if (string.IsNullOrWhiteSpace(converterPath))
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.BackendUnavailable,
                    "Metal Shader Converter (msc) is required but not found. Please install Xcode Metal Developer Tools / Metal Shader Converter.");
            }

            ShaderCompileRequest dxilRequest = request with { Target = ShaderTargetKind.Dxil };
            ShaderCompileResult dxilResult = context?.NativeCompileOverride?.Invoke(dxilRequest) ?? NativeDxcCompiler.Compile(dxilRequest);
            if (dxilResult.Bytecode.Length == 0)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.CompileFailed,
                    "DXIL output is empty, cannot convert to Metal library.",
                    dxilResult.Diagnostics);
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "InfinityMsc", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string dxilPath = Path.Combine(tempDir, "input.dxil");
            string outputPath = Path.Combine(tempDir, "output.metallib");

            try
            {
                File.WriteAllBytes(dxilPath, dxilResult.Bytecode);
                string arguments = BuildConverterArguments(dxilPath, outputPath, request);
                (int exitCode, string stdOut, string stdErr) = RunConverter(converterPath, arguments);

                string diagnostics = BuildDiagnostics(stdOut, stdErr);
                if (exitCode != 0 || !File.Exists(outputPath))
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.CompileFailed,
                        "Metal Shader Converter failed to produce metallib output.",
                        diagnostics);
                }

                byte[] libraryBytes = File.ReadAllBytes(outputPath);
                if (libraryBytes.Length == 0)
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.CompileFailed,
                        "Metal Shader Converter produced empty metallib output.",
                        diagnostics);
                }

                return new ShaderCompileResult
                {
                    Bytecode = libraryBytes,
                    Diagnostics = diagnostics,
                };
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        private static string BuildConverterArguments(string dxilPath, string outputPath, ShaderCompileRequest request)
        {
            // Note: Converter CLI options can evolve. Keep a minimal invocation and include
            // stage/entry hints for better diagnostics when tool supports them.
            string stage = request.Stage switch
            {
                ShaderStageKind.Vertex => "vertex",
                ShaderStageKind.Pixel => "fragment",
                ShaderStageKind.Compute => "compute",
                ShaderStageKind.Library => "library",
                _ => "library",
            };

            return $"--input \"{dxilPath}\" --output \"{outputPath}\" --stage {stage} --entry \"{request.EntryPoint}\"";
        }

        private static (int exitCode, string stdOut, string stdErr) RunConverter(string fileName, string arguments)
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using Process process = new Process { StartInfo = psi };
            process.Start();
            string stdOut = process.StandardOutput.ReadToEnd();
            string stdErr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, stdOut, stdErr);
        }

        private static string? ResolveConverterPath()
        {
            string? configured = Environment.GetEnvironmentVariable(ConverterPathEnv);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                string full = Path.GetFullPath(configured.Trim());
                if (File.Exists(full))
                {
                    return full;
                }
            }

            if (TryResolveViaXcrun(out string? pathFromXcrun))
            {
                return pathFromXcrun;
            }

            if (TryResolveFromPath("msc", out string? mscFromPath))
            {
                return mscFromPath;
            }

            return null;
        }

        private static bool TryResolveViaXcrun(out string? resolvedPath)
        {
            resolvedPath = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/xcrun",
                    Arguments = "--find metal-shaderconverter",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using Process process = new Process { StartInfo = psi };
                process.Start();
                string path = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    resolvedPath = path;
                    return true;
                }
            }
            catch
            {
                // Probe-only path.
            }

            return false;
        }

        private static bool TryResolveFromPath(string binaryName, out string? resolvedPath)
        {
            resolvedPath = null;
            string? pathVar = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathVar))
            {
                return false;
            }

            string[] paths = pathVar.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = 0; i < paths.Length; ++i)
            {
                string candidate = Path.Combine(paths[i], binaryName);
                if (File.Exists(candidate))
                {
                    resolvedPath = candidate;
                    return true;
                }
            }

            return false;
        }

        private static string BuildDiagnostics(string stdOut, string stdErr)
        {
            StringBuilder sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(stdOut))
            {
                sb.AppendLine(stdOut.Trim());
            }

            if (!string.IsNullOrWhiteSpace(stdErr))
            {
                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }

                sb.AppendLine(stdErr.Trim());
            }

            return sb.ToString().Trim();
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
                // Best effort cleanup.
            }
        }
    }
}
