using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;

using SharpShader.Compilation;
using SharpShader.CSharp;
using SharpShader.HLSLCrossCompiler;

[assembly: InternalsVisibleTo("Infinity.Rendering.Tests")]

namespace SharpShader.Tool
{
    internal static partial class SharpShaderCli
    {
        private const long MaximumSourceFileBytes = 64L * 1024 * 1024;
        private static readonly UTF8Encoding s_StrictUtf8 = new(false, true);
        private static readonly char[] s_TargetSeparators = [',', '+'];
        private const int SuccessExitCode = 0;
        private const int FailureExitCode = 1;
        private const int UsageExitCode = 2;

        public static int Execute(
            string[] args,
            TextWriter output,
            TextWriter error)
        {
            ArgumentNullException.ThrowIfNull(args);
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(error);

            if (args.Length == 0)
            {
                WriteUsage(error);
                return UsageExitCode;
            }

            try
            {
                string command = args[0].ToLowerInvariant();
                return command switch
                {
                    "compile" => Compile(args.AsSpan(1), output),
                    "inspect" => Inspect(args.AsSpan(1), output),
                    "validate" => Validate(args.AsSpan(1), output),
                    "help" or "--help" or "-h" => Help(output),
                    _ => throw new CliUsageException(
                        $"Unknown command '{args[0]}'."),
                };
            }
            catch (CliUsageException exception)
            {
                error.WriteLine(exception.Message);
                WriteUsage(error);
                return UsageExitCode;
            }
            catch (Exception exception)
            {
                error.WriteLine($"SharpShader.Tool: {exception.Message}");
                return FailureExitCode;
            }
        }

        private static int Compile(
            ReadOnlySpan<string> args,
            TextWriter output)
        {
            CompileArguments parsed = CompileArguments.Parse(args);
            ShaderProgramCompilation compilation = CompileSource(parsed);

            ShaderArtifactPackage.Write(
                parsed.OutputDirectory,
                compilation);
            output.WriteLine(
                $"compiled cache-key={compilation.CacheKey} "
                + $"variants={compilation.Manifest.Variants.Count} "
                + $"artifacts={compilation.Artifacts.Count}");
            output.WriteLine(
                Path.Combine(parsed.OutputDirectory, ShaderArtifactPackage.ManifestFileName));
            return SuccessExitCode;
        }

        private static int Inspect(
            ReadOnlySpan<string> args,
            TextWriter output)
        {
            string path = ParseSinglePath(args, "inspect");
            ShaderInterfaceManifest manifest =
                ShaderArtifactPackage.LoadManifest(path);
            ShaderArtifactPackage.WriteSummary(manifest, output);
            return SuccessExitCode;
        }

        private static int Validate(
            ReadOnlySpan<string> args,
            TextWriter output)
        {
            string path = ParseSinglePath(args, "validate");
            ShaderArtifactPackage.Validate(path);
            output.WriteLine($"valid {Path.GetFullPath(path)}");
            return SuccessExitCode;
        }

        private static ShaderProgramCompilation CompileSource(CompileArguments parsed)
        {
            if (string.Equals(
                    Path.GetExtension(parsed.SourcePath),
                    ".cs",
                    StringComparison.OrdinalIgnoreCase))
            {
                CSharpShaderCompilation compilation = new CSharpShaderCompiler(
                    new ShaderProgramCompiler(
                        new ShaderProgramCompilerOptions(parsed.CacheDirectory)))
                    .Compile(
                        ReadUtf8TextBounded(parsed.SourcePath),
                        parsed.SourcePath,
                        new CSharpShaderCompilerOptions(
                            parsed.Targets,
                            parsed.ShaderModel,
                            variants: parsed.CreateVariantsForCSharp(),
                            enableDebugInfo: parsed.EnableDebugInfo,
                            disableOptimizations: parsed.DisableOptimizations,
                            optimizationLevel: parsed.OptimizationLevel));
                return compilation.Primary.Program;
            }

            return new ShaderProgramCompiler(
                new ShaderProgramCompilerOptions(parsed.CacheDirectory))
                .Compile(parsed.CreateRequest());
        }

        private static int Help(TextWriter output)
        {
            WriteUsage(output);
            return SuccessExitCode;
        }

        private static string ParseSinglePath(
            ReadOnlySpan<string> args,
            string command)
        {
            if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
            {
                throw new CliUsageException(
                    $"Usage: SharpShader.Tool {command} <package-directory|manifest.json>");
            }

            return args[0];
        }

        private static void WriteUsage(TextWriter writer)
        {
            writer.WriteLine("SharpShader.Tool compile --source <file> --output <directory> [options]");
            writer.WriteLine("  .hlsl/.shader require --entry; .cs discovers SharpSL attributes.");
            writer.WriteLine("SharpShader.Tool inspect <package-directory|manifest.json>");
            writer.WriteLine("SharpShader.Tool validate <package-directory|manifest.json>");
            writer.WriteLine("Compile options:");
            writer.WriteLine("  --entry <stage>:<name>                 Repeat for every entry.");
            writer.WriteLine("  --target <dx12|vulkan|metal|all>[,...] Default: all.");
            writer.WriteLine("  --variant <key>                        Repeat for every variant; default: default.");
            writer.WriteLine("  --variant-define <key>:<name>[=<value>] Attach a define to a declared variant.");
            writer.WriteLine("  --define <name>[=<value>]               Add a global define.");
            writer.WriteLine("  --include <directory>                   Add an include directory.");
            writer.WriteLine("  --attachment-interface <json>          Required when any pixel entry is compiled.");
            writer.WriteLine("  --cache <directory>                     Enable the content-addressed persistent cache.");
            writer.WriteLine("  --shader-model <6.0-6.8>                Default: 6.8.");
            writer.WriteLine("  --metal-capacity <table>:<slot>:<t|s|b|u>=<count>");
            writer.WriteLine("  --debug --disable-optimizations --optimization-level <0-3> --warnings-as-errors");
        }
        private static string ReadUtf8TextBounded(string path)
        {
            FileInfo file = new(path);
            if (file.Length > MaximumSourceFileBytes
                || file.Length > int.MaxValue)
            {
                throw new IOException(
                    $"Shader source {path} contains {file.Length} bytes, "
                    + $"exceeding the {MaximumSourceFileBytes}-byte limit.");
            }

            byte[] bytes = new byte[checked((int)file.Length)];
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                throw new IOException(
                    $"Shader source {path} changed while being read.");
            }

            string source;
            try
            {
                source = s_StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    $"Shader source {path} is not valid UTF-8.",
                    exception);
            }

            return source.Length > 0 && source[0] == '\uFEFF'
                ? source[1..]
                : source;
        }



    }
}
