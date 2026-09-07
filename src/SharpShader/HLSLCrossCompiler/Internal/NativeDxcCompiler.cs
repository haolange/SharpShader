using System;
using System.IO;
using System.Linq;
using System.Text;
using Silk.NET.Core.Native;
using System.Collections.Generic;
using Silk.NET.Direct3D.Compilers;
using System.Runtime.InteropServices;

namespace SharpShader.HLSLCrossCompiler.Internal
{
    internal static unsafe class NativeDxcCompiler
    {
        private const string DxcLibraryName = "dxcompiler";
        private const int DxcStringEncodingUnavailableHResult = unchecked((int)0x80AA000C);

        private static readonly Guid s_ClsidDxcUtils = new("6245D6AF-66E0-48FD-80B4-4D271796748C");
        private static readonly Guid s_ClsidDxcCompiler = new("73E22D93-E6CE-47F3-B5BF-F0664F39C1B0");

        [DllImport(DxcLibraryName, EntryPoint = "DxcCreateInstance", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        private static extern int NativeDxcCreateInstance(ref Guid clsid, ref Guid iid, out nint instance);

        static NativeDxcCompiler()
        {
            SharpShaderNativeLibraryResolver.EnsureResolverRegistered(typeof(NativeDxcCompiler).Assembly);
        }

        public static bool IsAvailable()
        {
            ShaderCompileRequest request = ProbeShaderSourceFactory.CreateProbeRequest(
                ShaderStageKind.Vertex,
                new ShaderModelVersion(6, 0));

            try
            {
                return Compile(request).Bytecode.Length > 0;
            }
            catch (ShaderCompilerException ex) when (
                ex.ErrorCode == ShaderCompilerErrorCode.BackendUnavailable ||
                ex.ErrorCode == ShaderCompilerErrorCode.CompileFailed ||
                ex.ErrorCode == ShaderCompilerErrorCode.ProfileUnsupported)
            {
                return false;
            }
        }

        internal static DxcPreprocessedSource Preprocess(
            ShaderCompileRequest request,
            DxcDependencyCaptureLimits limits)
        {
            request.Validate();

            string profile = DxcArgumentBuilder.BuildProfile(
                request.Stage,
                request.ShaderModel);
            List<string> arguments = DxcArgumentBuilder.BuildArguments(
                request,
                profile);
            arguments.Add("-P");
            string[] additionalArguments = BuildAdditionalCompilerArguments(
                arguments);
            byte[] sourceUtf8 = Encoding.UTF8.GetBytes(request.Source);

            try
            {
                using ComPtr<IDxcUtils> utils = CreateInstance<IDxcUtils>(
                    s_ClsidDxcUtils,
                    IDxcUtils.Guid,
                    nameof(IDxcUtils));
                using ComPtr<IDxcCompiler3> compiler = CreateInstance<IDxcCompiler3>(
                    s_ClsidDxcCompiler,
                    IDxcCompiler3.Guid,
                    nameof(IDxcCompiler3));
                using DxcDependencyCaptureIncludeHandler captureHandler =
                    CreateSourceHandler(utils, request.SourceName, sourceUtf8, limits);
                using ComPtr<IDxcCompilerArgs> compilerArguments =
                    BuildCompilerArguments(
                        utils,
                        request,
                        profile,
                        additionalArguments);
                using ComPtr<IDxcResult> result = CompileSource(
                    compiler,
                    captureHandler.Handler,
                    compilerArguments,
                    sourceUtf8);

                IReadOnlyList<DxcCapturedInclude> captures =
                    captureHandler.CompleteCapture();
                string diagnostics = GetDiagnosticsOutput(result);
                int compileStatus = 0;
                int statusResult = result.Get().GetStatus(&compileStatus);
                if (statusResult < 0)
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.BackendUnavailable,
                        $"Failed to read DXC preprocess status. HRESULT=0x{statusResult:X8}",
                        diagnostics,
                        profile);
                }

                if (compileStatus < 0)
                {
                    ThrowCompileFailure(profile, diagnostics, compileStatus);
                }

                byte[] content = GetRequiredBlobOutput(
                    result,
                    OutKind.Hlsl,
                    "preprocessed HLSL",
                    profile,
                    diagnostics,
                    limits.MaximumPreprocessedBytes);
                string source;
                try
                {
                    source = new UTF8Encoding(false, true).GetString(content);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.CompileFailed,
                        "DXC returned preprocessed HLSL that is not valid UTF-8.",
                        diagnostics,
                        profile,
                        exception);
                }

                return new DxcPreprocessedSource(
                    source,
                    content,
                    captures);
            }
            catch (ShaderCompilerException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is FileNotFoundException
                or DllNotFoundException
                or BadImageFormatException
                or EntryPointNotFoundException
                or TypeInitializationException
                or InvalidCastException)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.BackendUnavailable,
                    "DXC native preprocessor is unavailable in current process.",
                    exception.Message,
                    profile,
                    exception);
            }
        }

        public static ShaderCompileResult Compile(ShaderCompileRequest request)
        {
            request.Validate();

            string profile = DxcArgumentBuilder.BuildProfile(request.Stage, request.ShaderModel);
            string[] additionalArguments = BuildAdditionalCompilerArguments(
                DxcArgumentBuilder.BuildArguments(request, profile));
            byte[] sourceUtf8 = Encoding.UTF8.GetBytes(request.Source);

            try
            {
                using ComPtr<IDxcUtils> utils = CreateInstance<IDxcUtils>(s_ClsidDxcUtils, IDxcUtils.Guid, nameof(IDxcUtils));
                using ComPtr<IDxcCompiler3> compiler = CreateInstance<IDxcCompiler3>(s_ClsidDxcCompiler, IDxcCompiler3.Guid, nameof(IDxcCompiler3));
                using DxcDependencyCaptureIncludeHandler includeHandler =
                    CreateSourceHandler(utils, request.SourceName, sourceUtf8, null);
                using ComPtr<IDxcCompilerArgs> compilerArguments = BuildCompilerArguments(
                    utils,
                    request,
                    profile,
                    additionalArguments);
                using ComPtr<IDxcResult> result = CompileSource(
                    compiler,
                    includeHandler.Handler,
                    compilerArguments,
                    sourceUtf8);
                _ = includeHandler.CompleteCapture();

                string diagnostics = GetDiagnosticsOutput(result);
                string warnings = ExtractWarnings(diagnostics);

                int compileStatus = 0;
                int hrStatus = result.Get().GetStatus(&compileStatus);
                if (hrStatus < 0)
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.BackendUnavailable,
                        $"Failed to read DXC compile status. HRESULT=0x{hrStatus:X8}",
                        diagnostics,
                        profile);
                }

                if (compileStatus < 0)
                {
                    ThrowCompileFailure(profile, diagnostics, compileStatus);
                }

                byte[] bytecode = GetRequiredBlobOutput(result, OutKind.Object, "object", profile, diagnostics);
                byte[] reflectionData = GetOptionalBlobOutput(result, OutKind.Reflection, profile, diagnostics, out _);
                byte[] pdbData = GetOptionalBlobOutput(result, OutKind.Pdb, profile, diagnostics, out string? pdbName);
                byte[] shaderHash = GetOptionalBlobOutput(result, OutKind.ShaderHash, profile, diagnostics, out _);
                if (shaderHash.Length != 0 && shaderHash.Length != 20)
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.CompileFailed,
                        $"DXC shader hash output has invalid size {shaderHash.Length}; expected 20 bytes.",
                        diagnostics,
                        profile);
                }

                return new ShaderCompileResult
                {
                    Bytecode = bytecode,
                    ReflectionData = reflectionData,
                    PdbData = pdbData,
                    PdbName = pdbName,
                    ShaderHash = shaderHash,
                    Diagnostics = diagnostics,
                    Warnings = warnings,
                };
            }
            catch (ShaderCompilerException)
            {
                throw;
            }
            catch (Exception ex) when (
                ex is FileNotFoundException or
                DllNotFoundException or
                BadImageFormatException or
                EntryPointNotFoundException or
                TypeInitializationException or
                InvalidCastException)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.BackendUnavailable,
                    "DXC native backend is unavailable in current process.",
                    ex.Message,
                    profile,
                    ex);
            }
        }

        private static ComPtr<T> CreateInstance<T>(Guid clsid, Guid iid, string interfaceName)
            where T : unmanaged, IComVtbl<T>
        {
            Guid clsidCopy = clsid;
            Guid iidCopy = iid;
            int hr = NativeDxcCreateInstance(ref clsidCopy, ref iidCopy, out nint instance);
            if (hr < 0 || instance == 0)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.BackendUnavailable,
                    $"DXC backend initialization failed for {interfaceName}. HRESULT=0x{hr:X8}");
            }

            return new ComPtr<T>((T*)instance);
        }

        private static DxcDependencyCaptureIncludeHandler CreateSourceHandler(
            ComPtr<IDxcUtils> utils,
            string sourceName,
            byte[] sourceUtf8,
            DxcDependencyCaptureLimits? limits)
        {
            ComPtr<IDxcIncludeHandler> defaultHandler = default;
            ComPtr<IDxcBlobEncoding> sourceBlob = default;
            try
            {
                defaultHandler = CreateIncludeHandler(utils);
                sourceBlob = CreateSourceBlob(utils, sourceUtf8);
                return new DxcDependencyCaptureIncludeHandler(
                    ref defaultHandler, limits, sourceName, ref sourceBlob);
            }
            finally
            {
                sourceBlob.Dispose();
                defaultHandler.Dispose();
            }
        }

        private static ComPtr<IDxcBlobEncoding> CreateSourceBlob(
            ComPtr<IDxcUtils> utils,
            byte[] sourceUtf8)
        {
            ComPtr<IDxcBlobEncoding> blob = default;
            fixed (byte* source = sourceUtf8)
            {
                int result = utils.Get().CreateBlob(source, checked((uint)sourceUtf8.Length), DXC.CPUtf8,
                    (IDxcBlobEncoding**)blob.GetAddressOf());
                if (result < 0 || blob.Handle == null)
                {
                    blob.Dispose();
                    throw new ShaderCompilerException(ShaderCompilerErrorCode.BackendUnavailable,
                        $"Failed to preserve the DXC in-memory source. HRESULT=0x{result:X8}");
                }
            }

            return blob;
        }

        private static ComPtr<IDxcIncludeHandler> CreateIncludeHandler(ComPtr<IDxcUtils> utils)
        {
            ComPtr<IDxcIncludeHandler> includeHandler = default;
            int hr = utils.Get().CreateDefaultIncludeHandler((IDxcIncludeHandler**)includeHandler.GetAddressOf());
            if (hr < 0 || includeHandler.Handle == null)
            {
                includeHandler.Dispose();
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.BackendUnavailable,
                    $"Failed to create DXC include handler. HRESULT=0x{hr:X8}");
            }

            return includeHandler;
        }

        private static ComPtr<IDxcCompilerArgs> BuildCompilerArguments(
            ComPtr<IDxcUtils> utils,
            ShaderCompileRequest request,
            string profile,
            IReadOnlyList<string> additionalArguments)
        {
            string? entryPoint = request.Stage == ShaderStageKind.Library && string.IsNullOrWhiteSpace(request.EntryPoint)
                ? null
                : request.EntryPoint;

            using NativeWideStringMarshaller nativeArguments = NativeWideStringMarshaller.Create(
                request.SourceName,
                entryPoint,
                profile,
                additionalArguments);

            ComPtr<IDxcCompilerArgs> compilerArguments = default;
            int hr = utils.Get().BuildArguments(
                (char*)nativeArguments.SourceName,
                (char*)nativeArguments.EntryPoint,
                (char*)nativeArguments.Profile,
                (char**)nativeArguments.Arguments,
                nativeArguments.ArgumentCount,
                (Define*)null,
                0,
                (IDxcCompilerArgs**)compilerArguments.GetAddressOf());

            if (hr < 0 || compilerArguments.Handle == null)
            {
                compilerArguments.Dispose();
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.BackendUnavailable,
                    $"Failed to build DXC compiler arguments for {request.SourceName}. HRESULT=0x{hr:X8}");
            }

            return compilerArguments;
        }

        private static ComPtr<IDxcResult> CompileSource(
            ComPtr<IDxcCompiler3> compiler,
            ComPtr<IDxcIncludeHandler> includeHandler,
            ComPtr<IDxcCompilerArgs> compilerArguments,
            byte[] sourceUtf8)
        {
            ComPtr<IDxcResult> result = default;
            fixed (byte* sourcePointer = sourceUtf8)
            {
                char** arguments = compilerArguments.Get().GetArguments();
                uint argumentCount = compilerArguments.Get().GetCount();
                if (arguments == null || argumentCount == 0)
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.BackendUnavailable,
                        "DXC returned an empty compiler argument list.");
                }

                Silk.NET.Direct3D.Compilers.Buffer sourceBuffer = new Silk.NET.Direct3D.Compilers.Buffer
                {
                    Ptr = sourcePointer,
                    Size = (nuint)sourceUtf8.Length,
                    Encoding = DXC.CPUtf8,
                };

                Guid resultIid = IDxcResult.Guid;
                int hr = compiler.Get().Compile(
                    &sourceBuffer,
                    arguments,
                    argumentCount,
                    includeHandler.Handle,
                    ref resultIid,
                    (void**)result.GetAddressOf());

                if (hr < 0 || result.Handle == null)
                {
                    result.Dispose();
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.BackendUnavailable,
                        $"DXC compile invocation failed. HRESULT=0x{hr:X8}");
                }
            }

            return result;
        }

        private static byte[] GetRequiredBlobOutput(
            ComPtr<IDxcResult> result,
            OutKind outputKind,
            string outputName,
            string profile,
            string diagnostics,
            long maximumBytes = long.MaxValue)
        {
            if (!result.Get().HasOutput(outputKind))
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.CompileFailed,
                    $"DXC {outputName} output is missing for profile {profile}.",
                    diagnostics,
                    profile);
            }

            byte[] output = GetBlobOutput(
                result,
                outputKind,
                profile,
                diagnostics,
                out _,
                maximumBytes);
            if (output.Length == 0)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.CompileFailed,
                    $"DXC produced empty {outputName} output for profile {profile}.",
                    diagnostics,
                    profile);
            }

            return output;
        }

        private static byte[] GetOptionalBlobOutput(
            ComPtr<IDxcResult> result,
            OutKind outputKind,
            string profile,
            string diagnostics,
            out string? outputName)
        {
            outputName = null;
            return result.Get().HasOutput(outputKind)
                ? GetBlobOutput(result, outputKind, profile, diagnostics, out outputName)
                : Array.Empty<byte>();
        }

        private static byte[] GetBlobOutput(
            ComPtr<IDxcResult> result,
            OutKind outputKind,
            string profile,
            string diagnostics,
            out string? outputName,
            long maximumBytes = long.MaxValue)
        {
            ComPtr<IDxcBlob> blob = default;
            ComPtr<IDxcBlobWide> nameBlob = default;
            outputName = null;
            try
            {
                Guid blobIid = IDxcBlob.Guid;
                int hr = result.Get().GetOutput(
                    outputKind,
                    ref blobIid,
                    (void**)blob.GetAddressOf(),
                    (IDxcBlobWide**)nameBlob.GetAddressOf());

                if (hr < 0 || blob.Handle == null)
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.CompileFailed,
                        $"Failed to read DXC {outputKind} output for profile {profile}. HRESULT=0x{hr:X8}",
                        diagnostics,
                        profile);
                }

                if (nameBlob.Handle != null)
                {
                    outputName = CopyWideStringToManaged(nameBlob);
                }

                return CopyBlobToManaged(blob, maximumBytes, outputKind);
            }
            finally
            {
                nameBlob.Dispose();
                blob.Dispose();
            }
        }

        private static string CopyWideStringToManaged(ComPtr<IDxcBlobWide> blob)
        {
            nuint length = blob.Get().GetStringLength();
            char* pointer = blob.Get().GetStringPointer();
            if (length == 0 || pointer == null)
            {
                return string.Empty;
            }

            if (length > int.MaxValue)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.CompileFailed,
                    "DXC output name exceeds managed string size limits.");
            }

            if (OperatingSystem.IsWindows())
            {
                return new string(pointer, 0, (int)length);
            }

            StringBuilder builder = new StringBuilder((int)length);
            int* codePoints = (int*)pointer;
            for (int index = 0; index < (int)length; index++)
            {
                builder.Append(char.ConvertFromUtf32(codePoints[index]));
            }

            return builder.ToString();
        }

        private static string GetDiagnosticsOutput(ComPtr<IDxcResult> result)
        {
            const OutKind outputKind = OutKind.Errors;
            if (!result.Get().HasOutput(outputKind))
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.BackendUnavailable,
                    "DXC result did not expose the required diagnostics output.");
            }

            ComPtr<IDxcBlobUtf8> blob = default;
            try
            {
                Guid blobIid = IDxcBlobUtf8.Guid;
                int hr = result.Get().GetOutput(
                    outputKind,
                    ref blobIid,
                    (void**)blob.GetAddressOf(),
                    (IDxcBlobWide**)null);

                if (hr < 0 || blob.Handle == null)
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.BackendUnavailable,
                        $"Failed to read DXC diagnostics output. HRESULT=0x{hr:X8}");
                }

                nuint length = blob.Get().GetStringLength();
                if (length == 0 || blob.Get().GetStringPointer() == null)
                {
                    return string.Empty;
                }

                if (length > int.MaxValue)
                {
                    return "DXC diagnostics output exceeds managed string size limits.";
                }

                return Encoding.UTF8
                    .GetString(new ReadOnlySpan<byte>(blob.Get().GetStringPointer(), (int)length))
                    .Trim('\0', '\r', '\n', ' ');
            }
            finally
            {
                blob.Dispose();
            }
        }

        private static string[] BuildAdditionalCompilerArguments(List<string> arguments)
        {
            List<string> additionalArguments = new List<string>(arguments.Count);
            for (int index = 0; index < arguments.Count; index++)
            {
                string argument = arguments[index];
                if (argument.Equals("-T", StringComparison.OrdinalIgnoreCase)
                    || argument.Equals("-E", StringComparison.OrdinalIgnoreCase))
                {
                    if (index + 1 >= arguments.Count)
                    {
                        throw new ShaderCompilerException(
                            ShaderCompilerErrorCode.InvalidRequest,
                            $"DXC argument {argument} is missing its value.");
                    }

                    index++;
                    continue;
                }

                additionalArguments.Add(argument);
            }

            return additionalArguments.ToArray();
        }

        private static void ThrowCompileFailure(string profile, string diagnostics, int compileStatus)
        {
            if (compileStatus == DxcStringEncodingUnavailableHResult)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.BackendUnavailable,
                    $"DXC native backend returned HRESULT=0x{compileStatus:X8} for profile {profile}. Native DXC is unavailable in current runtime.",
                    diagnostics,
                    profile);
            }

            if (IsProfileUnsupportedDiagnostic(diagnostics))
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.ProfileUnsupported,
                    $"Requested profile {profile} is unsupported by current DXC backend.",
                    diagnostics,
                    profile);
            }

            throw new ShaderCompilerException(
                ShaderCompilerErrorCode.CompileFailed,
                $"DXC compile failed for profile {profile}. HRESULT=0x{compileStatus:X8}",
                diagnostics,
                profile);
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

        private static bool IsProfileUnsupportedDiagnostic(string diagnostics)
        {
            if (string.IsNullOrWhiteSpace(diagnostics))
            {
                return false;
            }

            return diagnostics.Contains("invalid profile", StringComparison.OrdinalIgnoreCase)
                   || diagnostics.Contains("unsupported shader model", StringComparison.OrdinalIgnoreCase)
                   || diagnostics.Contains("profile is not supported", StringComparison.OrdinalIgnoreCase)
                   || diagnostics.Contains("unrecognized target profile", StringComparison.OrdinalIgnoreCase)
                   || diagnostics.Contains("shader model 6.8 is only available", StringComparison.OrdinalIgnoreCase);
        }

        private static byte[] CopyBlobToManaged(
            ComPtr<IDxcBlob> blob,
            long maximumBytes,
            OutKind outputKind)
        {
            nuint size = blob.Get().GetBufferSize();
            if (size == 0)
            {
                return Array.Empty<byte>();
            }

            if ((ulong)size > (ulong)maximumBytes)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.InvalidRequest,
                    $"DXC {outputKind} output exceeds the configured byte limit "
                    + $"of {maximumBytes}.");
            }

            if (size > int.MaxValue)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.CompileFailed,
                    "DXC output blob exceeds managed array size limits.");
            }

            void* source = blob.Get().GetBufferPointer();
            if (source == null)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.CompileFailed,
                    "DXC returned a non-empty output blob with a null data pointer.");
            }

            byte[] output = new byte[(int)size];
            Marshal.Copy((nint)source, output, 0, output.Length);
            return output;
        }
    }
}
