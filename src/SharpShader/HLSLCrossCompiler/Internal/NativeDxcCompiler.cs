using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;

namespace SharpShader.HLSLCrossCompiler.Internal;

internal static unsafe class NativeDxcCompiler
{
    private const string DxcLibraryName = "dxcompiler";
    private const int DxcStringEncodingUnavailableHResult = unchecked((int)0x80AA000C);
    private const uint Utf8CodePage = 65001;
    private const uint Utf16CodePage = 1200;
    private const uint Utf32CodePage = 12000;

    // Candidate CLSIDs observed across different DXC drops.
    private static readonly Guid[] ClsidDxcLibraryCandidates =
    {
        new("6245D6AF-66E0-48FD-80B4-4D271796748C"),
        IDxcLibrary.Guid,
    };

    private static readonly Guid[] ClsidDxcCompilerCandidates =
    {
        new("73E22D93-E6CE-47F3-B5BF-F0664F39C1B0"),
        IDxcCompiler.Guid,
        IDxcCompiler3.Guid,
    };

    private static readonly Guid IidDxcLibrary = IDxcLibrary.Guid;
    private static readonly Guid IidDxcCompiler = IDxcCompiler.Guid;
    private static readonly Lazy<nint> DxcNativeHandle = new(LoadDxcNativeHandle);
    private static readonly Lazy<nint> DxcImageBase = new(ResolveDxcImageBase);

    [DllImport(DxcLibraryName, EntryPoint = "DxcCreateInstance", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    private static extern int NativeDxcCreateInstance(ref Guid clsid, nint iid, out nint instance);

    [DllImport(DxcLibraryName, EntryPoint = "DxcCreateInstance2", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    private static extern int NativeDxcCreateInstance2(nint malloc, ref Guid clsid, nint iid, out nint instance);

    [StructLayout(LayoutKind.Sequential)]
    private struct DlInfo
    {
        public nint FileName;
        public nint FileBase;
        public nint SymbolName;
        public nint SymbolAddress;
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dladdr", ExactSpelling = true)]
    private static extern int DlAddr(nint address, out DlInfo info);

    static NativeDxcCompiler()
    {
        MacNativePayloadSanitizer.EnsureKnownPayloadsAreSanitized(typeof(NativeDxcCompiler).Assembly);
        SharpShaderNativeLibraryResolver.EnsureResolverRegistered(typeof(NativeDxcCompiler).Assembly);
    }

    public static bool IsAvailable()
    {
        // Availability should reflect an end-to-end probe compile result rather than
        // partial activation checks, which can be false negatives on macOS.
        return ProbeCompilePath();
    }

    private static bool ProbeCompilePath()
    {
        ShaderCompileRequest request = ProbeShaderSourceFactory.CreateProbeRequest(
            ShaderStageKind.Vertex,
            new ShaderModelVersion(6, 0));

        try
        {
            ShaderCompileResult result = Compile(request);
            return result.Bytecode.Length > 0;
        }
        catch (ShaderCompilerException ex) when (
            ex.ErrorCode == ShaderCompilerErrorCode.BackendUnavailable ||
            ex.ErrorCode == ShaderCompilerErrorCode.CompileFailed ||
            ex.ErrorCode == ShaderCompilerErrorCode.ProfileUnsupported)
        {
            return false;
        }
    }

    public static ShaderCompileResult Compile(ShaderCompileRequest request)
    {
        request.Validate();

        string profile = DxcArgumentBuilder.BuildProfile(request.Stage, request.ShaderModel);
        List<string> arguments = DxcArgumentBuilder.BuildArguments(request, profile);
        string[] compileArguments = BuildLegacyCompilerArguments(arguments);

        byte[] sourceUtf8 = Encoding.UTF8.GetBytes(request.Source);

        try
        {
            using ComPtr<IDxcLibrary> library = CreateInstance<IDxcLibrary>(ClsidDxcLibraryCandidates, IidDxcLibrary, "IDxcLibrary");
            using ComPtr<IDxcCompiler> compiler = CreateInstance<IDxcCompiler>(ClsidDxcCompilerCandidates, IidDxcCompiler, "IDxcCompiler");

            using ComPtr<IDxcIncludeHandler> includeHandler = default;
            int hrInclude = library.Get().CreateIncludeHandler((IDxcIncludeHandler**)includeHandler.GetAddressOf());
            if (hrInclude < 0)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.BackendUnavailable,
                    $"Failed to create DXC include handler. HRESULT=0x{hrInclude:X8}");
            }

            using ComPtr<IDxcBlobEncoding> sourceBlob = default;
            using ComPtr<IDxcOperationResult> operationResult = default;

            fixed (byte* sourcePtr = sourceUtf8)
            {
                int hrSource = library.Get().CreateBlobWithEncodingFromPinned(
                    sourcePtr,
                    (uint)sourceUtf8.Length,
                    DXC.CPUtf8,
                    (IDxcBlobEncoding**)sourceBlob.GetAddressOf());

                if (hrSource < 0 || sourceBlob.Handle == null)
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.BackendUnavailable,
                        $"Failed to create DXC source blob. HRESULT=0x{hrSource:X8}");
                }

                string? entryPoint = request.Stage == ShaderStageKind.Library && string.IsNullOrWhiteSpace(request.EntryPoint)
                    ? null
                    : request.EntryPoint;

                using NativeWideStringMarshaller nativeArguments = NativeWideStringMarshaller.Create(
                    request.SourceName,
                    entryPoint,
                    profile,
                    compileArguments);

                int hrCompile = compiler.Get().Compile(
                    (IDxcBlob*)sourceBlob.Handle,
                    (char*)nativeArguments.SourceName,
                    (char*)nativeArguments.EntryPoint,
                    (char*)nativeArguments.Profile,
                    (char**)nativeArguments.Arguments,
                    nativeArguments.ArgumentCount,
                    (Define*)null,
                    0,
                    includeHandler.Handle,
                    (IDxcOperationResult**)operationResult.GetAddressOf());

                if (hrCompile < 0 && operationResult.Handle == null)
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.BackendUnavailable,
                        $"DXC compile invocation failed. HRESULT=0x{hrCompile:X8}");
                }
            }

            if (operationResult.Handle == null)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.BackendUnavailable,
                    "DXC compile returned no operation result object.");
            }

            string diagnostics = GetDiagnostics(operationResult);
            string warnings = ExtractWarnings(diagnostics);

            int compileStatus = 0;
            operationResult.Get().GetStatus(&compileStatus);
            if (compileStatus < 0)
            {
                ThrowCompileFailure(profile, diagnostics, compileStatus);
            }

            ComPtr<IDxcBlob> objectBlob = default;
            try
            {
                int hrObject = operationResult.Get().GetResult((IDxcBlob**)objectBlob.GetAddressOf());

                if (hrObject < 0 || objectBlob.Handle == null)
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.CompileFailed,
                        $"DXC object output is missing for profile {profile}. HRESULT=0x{hrObject:X8}",
                        diagnostics,
                        profile);
                }

                byte[] bytecode = CopyBlobToManaged(objectBlob);
                if (bytecode.Length == 0)
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.CompileFailed,
                        $"DXC produced empty bytecode for profile {profile}.",
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
            finally
            {
                objectBlob.Dispose();
            }
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

    private static ComPtr<T> CreateInstance<T>(Guid[] clsidCandidates, Guid iid, string interfaceName)
        where T : unmanaged, IComVtbl<T>
    {
        Exception? lastException = null;
        bool hasSymbolPointer = TryGetInterfaceIdSymbolPointer(interfaceName, out nint symbolPointer);
        nint[] legacyIidPointerCandidates = EnumerateLegacyIidPointerCandidates(interfaceName).ToArray();
        List<string> legacyPointersTried = new List<string>();

        foreach (Guid clsid in clsidCandidates)
        {
            Guid clsidLocal = clsid;
            Guid iidCopy = iid;

            try
            {
                int hr = TryCreateInstanceNative(ref clsidLocal, (nint)Unsafe.AsPointer(ref iidCopy), out nint instancePtr);
                if (hr >= 0 && instancePtr != 0)
                {
                    return new ComPtr<T>((T*)instancePtr);
                }

                lastException = new InvalidOperationException($"HRESULT=0x{hr:X8}");
            }
            catch (Exception ex) when (
                ex is FileNotFoundException or
                DllNotFoundException or
                BadImageFormatException or
                EntryPointNotFoundException or
                TypeInitializationException or
                InvalidCastException)
            {
                lastException = ex;
            }

            if (hasSymbolPointer)
            {
                clsidLocal = clsid;
                try
                {
                    int hr = TryCreateInstanceNative(ref clsidLocal, symbolPointer, out nint instancePtr);
                    if (hr >= 0 && instancePtr != 0)
                    {
                        return new ComPtr<T>((T*)instancePtr);
                    }

                    lastException = new InvalidOperationException($"HRESULT=0x{hr:X8}");
                }
                catch (Exception ex) when (
                    ex is FileNotFoundException or
                    DllNotFoundException or
                    BadImageFormatException or
                    EntryPointNotFoundException or
                    TypeInitializationException or
                    InvalidCastException)
                {
                    lastException = ex;
                }
            }

            foreach (nint legacyIidPointer in legacyIidPointerCandidates)
            {
                legacyPointersTried.Add($"0x{legacyIidPointer:X}");
                clsidLocal = clsid;
                try
                {
                    int hr = TryCreateInstanceNative(ref clsidLocal, legacyIidPointer, out nint instancePtr);
                    if (hr >= 0 && instancePtr != 0)
                    {
                        return new ComPtr<T>((T*)instancePtr);
                    }

                    lastException = new InvalidOperationException($"HRESULT=0x{hr:X8}");
                }
                catch (Exception ex) when (
                    ex is FileNotFoundException or
                    DllNotFoundException or
                    BadImageFormatException or
                    EntryPointNotFoundException or
                    TypeInitializationException or
                    InvalidCastException)
                {
                    lastException = ex;
                }
            }
        }

        string details = lastException == null
            ? string.Empty
            : $" Last error: {lastException.GetType().Name}: {lastException.Message}";

        string legacyDetails = legacyPointersTried.Count == 0
            ? $" LegacyIIDPointers=(none, known={legacyIidPointerCandidates.Length}, {BuildLegacyProbeSummary()})"
            : $" LegacyIIDPointers=[{string.Join(", ", legacyPointersTried.Distinct())}]";

        throw new ShaderCompilerException(
            ShaderCompilerErrorCode.BackendUnavailable,
            $"DXC backend initialization failed for {interfaceName}. CLSID candidates={string.Join(", ", clsidCandidates)} IID={iid}.{details}{legacyDetails}");
    }

    private static int TryCreateInstanceNative(ref Guid clsid, nint iidPointer, out nint instancePtr)
    {
        int hr = NativeDxcCreateInstance(ref clsid, iidPointer, out instancePtr);
        if (hr < 0 || instancePtr == 0)
        {
            hr = NativeDxcCreateInstance2(0, ref clsid, iidPointer, out instancePtr);
        }

        return hr;
    }

    private static bool TryGetInterfaceIdSymbolPointer(string interfaceName, out nint symbolPointer)
    {
        symbolPointer = 0;
        nint handle = DxcNativeHandle.Value;
        if (handle == 0)
        {
            return false;
        }

        string[] symbolCandidates = interfaceName switch
        {
            "IDxcLibrary" => new[] { "__ZN11IDxcLibrary14IDxcLibrary_IDE" },
            "IDxcCompiler" => new[] { "__ZN12IDxcCompiler15IDxcCompiler_IDE", "__ZN13IDxcCompiler216IDxcCompiler2_IDE" },
            _ => Array.Empty<string>(),
        };

        foreach (string symbol in symbolCandidates)
        {
            if (NativeLibrary.TryGetExport(handle, symbol, out symbolPointer))
            {
                return true;
            }
        }

        return false;
    }

    private static nint LoadDxcNativeHandle()
    {
        Assembly assembly = typeof(NativeDxcCompiler).Assembly;
        List<string> candidates = EnumerateDxcNativeCandidates(assembly)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string candidate in candidates)
        {
            MacNativePayloadSanitizer.EnsureFileIsSanitized(candidate);
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out nint handle))
            {
                string? dxcDirectory = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrWhiteSpace(dxcDirectory))
                {
                    EnsureDirectoryInProcessPath(dxcDirectory);
                }
                TryLoadCompanionDxilLibraries(candidate, candidates);
                return handle;
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            FailFastMissingNativeDxc(candidates);
        }

        return 0;
    }

    private static void TryLoadCompanionDxilLibraries(string loadedDxcPath, IReadOnlyList<string> dxcCandidates)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        HashSet<string> attemptedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TryLoadDxilSibling(loadedDxcPath, attemptedPaths);

        for (int i = 0; i < dxcCandidates.Count; ++i)
        {
            TryLoadDxilSibling(dxcCandidates[i], attemptedPaths);
        }
    }

    private static void TryLoadDxilSibling(string dxcPath, HashSet<string> attemptedPaths)
    {
        if (string.IsNullOrWhiteSpace(dxcPath))
        {
            return;
        }

        string? directory = Path.GetDirectoryName(dxcPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        EnsureDirectoryInProcessPath(directory);

        string dxilPath = Path.Combine(directory, "dxil.dll");
        string fullPath = Path.GetFullPath(dxilPath);
        if (!attemptedPaths.Add(fullPath) || !File.Exists(fullPath))
        {
            return;
        }

        NativeLibrary.TryLoad(fullPath, out _);
    }

    private static void EnsureDirectoryInProcessPath(string directory)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        string fullDirectory = Path.GetFullPath(directory);
        string existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (existingPath.Length > 0)
        {
            string[] segments = existingPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = 0; i < segments.Length; ++i)
            {
                string segment = segments[i];
                if (string.Equals(Path.GetFullPath(segment), fullDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        string updatedPath = string.IsNullOrWhiteSpace(existingPath)
            ? fullDirectory
            : $"{fullDirectory};{existingPath}";
        Environment.SetEnvironmentVariable("PATH", updatedPath);
    }

    private static void FailFastMissingNativeDxc(IReadOnlyList<string> candidates)
    {
        List<string> existingCandidates = new List<string>();
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                existingCandidates.Add(candidate);
            }
        }

        string configuredPath = Environment.GetEnvironmentVariable(SharpShaderNativeLibraryLayout.DxcEnvironmentVariableName) ?? "(unset)";
        string existingSummary = existingCandidates.Count == 0 ? "(none)" : string.Join(", ", existingCandidates);
        string candidateSummary = candidates.Count == 0 ? "(none)" : string.Join(", ", candidates);
        string message =
            "Native DXC is mandatory and no process-matched libdxcompiler could be loaded on this host. " +
            $"{SharpShaderNativeLibraryLayout.DxcEnvironmentVariableName}={configuredPath}. " +
            $"Existing candidates={existingSummary}. " +
            $"Probe candidates={candidateSummary}.";

#if DEBUG
        Debug.Fail(message);
        throw new InvalidOperationException(message);
#else
        Environment.FailFast(message);
#endif
    }

    private static nint ResolveDxcImageBase()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return 0;
        }

        nint handle = DxcNativeHandle.Value;
        if (handle == 0)
        {
            return 0;
        }

        if (!NativeLibrary.TryGetExport(handle, "DxcCreateInstance", out nint createInstancePtr))
        {
            return 0;
        }

        return DlAddr(createInstancePtr, out DlInfo info) == 0 ? 0 : info.FileBase;
    }

    private static IEnumerable<nint> EnumerateLegacyIidPointerCandidates(string interfaceName)
    {
        if (!OperatingSystem.IsMacOS())
        {
            yield break;
        }

        nint imageBase = DxcImageBase.Value;
        if (imageBase == 0)
        {
            yield break;
        }

        int[] gotOffsets = interfaceName switch
        {
            "IDxcLibrary" => new[] { 0xC1F028 },
            "IDxcCompiler" => new[] { 0xC1F030 },
            _ => Array.Empty<int>(),
        };

        HashSet<nint> visited = new HashSet<nint>();
        foreach (int gotOffset in gotOffsets)
        {
            nint slotAddress = imageBase + gotOffset;
            nint iidPointer = Marshal.ReadIntPtr(slotAddress);
            if (iidPointer != 0 && visited.Add(iidPointer))
            {
                yield return iidPointer;
            }
        }
    }

    private static string BuildLegacyProbeSummary()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return "macOS=false";
        }

        nint handle = DxcNativeHandle.Value;
        nint exportPtr = 0;
        bool hasExport = handle != 0 && NativeLibrary.TryGetExport(handle, "DxcCreateInstance", out exportPtr);
        nint imageBase = DxcImageBase.Value;
        nint probe0 = 0;
        nint probe1 = 0;
        try
        {
            if (imageBase != 0)
            {
                probe0 = Marshal.ReadIntPtr(imageBase + 0xC1F020);
                probe1 = Marshal.ReadIntPtr(imageBase + 0xC1F028);
            }
        }
        catch
        {
            // ignore probe failures in diagnostics
        }

        return $"handle=0x{handle:X}, export={(hasExport ? $"0x{exportPtr:X}" : "none")}, imageBase=0x{imageBase:X}, slot[0xC1F020]=0x{probe0:X}, slot[0xC1F028]=0x{probe1:X}";
    }

    private static IEnumerable<string> EnumerateDxcNativeCandidates(Assembly assembly)
    {
        return SharpShaderNativeLibraryResolver.EnumerateCandidates(DxcLibraryName, assembly);
    }

    private static string[] BuildLegacyCompilerArguments(List<string> arguments)
    {
        List<string> filtered = new List<string>(arguments.Count);
        for (int i = 0; i < arguments.Count; i++)
        {
            string argument = arguments[i];
            if (argument.Equals("-T", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("-E", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }

            filtered.Add(argument);
        }

        return filtered.ToArray();
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

    private static string GetDiagnostics(ComPtr<IDxcOperationResult> operationResult)
    {
        ComPtr<IDxcBlobEncoding> errorBlob = default;
        try
        {
            int hrErrors = operationResult.Get().GetErrorBuffer((IDxcBlobEncoding**)errorBlob.GetAddressOf());
            if (hrErrors < 0 || errorBlob.Handle == null)
            {
                return string.Empty;
            }

            nuint blobSize = errorBlob.Get().GetBufferSize();
            if (blobSize == 0)
            {
                return string.Empty;
            }

            if (blobSize > int.MaxValue)
            {
                return "DXC diagnostics output exceeds managed string size limits.";
            }

            byte[] bytes = new byte[(int)blobSize];
            IntPtr blobPointer = (IntPtr)errorBlob.Get().GetBufferPointer();
            if (blobPointer == IntPtr.Zero)
            {
                return string.Empty;
            }

            Marshal.Copy(blobPointer, bytes, 0, bytes.Length);

            int known = 0;
            uint codePage = 0;
            int hrEncoding = errorBlob.Get().GetEncoding(&known, &codePage);
            if (hrEncoding >= 0)
            {
                if (codePage == 0 && known != 0)
                {
                    codePage = OperatingSystem.IsWindows() ? Utf16CodePage : Utf32CodePage;
                }
            }
            else
            {
                codePage = 0;
            }

            string diagnostics = DecodeDiagnostics(bytes, codePage);
            return diagnostics.Trim('\0', '\r', '\n', ' ');
        }
        finally
        {
            errorBlob.Dispose();
        }
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

    private static string DecodeDiagnostics(byte[] bytes, uint codePage)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        if (TryDecodeByCodePage(bytes, codePage, out string decoded))
        {
            return decoded;
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static bool TryDecodeByCodePage(byte[] bytes, uint codePage, out string decoded)
    {
        decoded = string.Empty;
        if (bytes.Length == 0)
        {
            return true;
        }

        try
        {
            switch (codePage)
            {
                case Utf8CodePage:
                    decoded = Encoding.UTF8.GetString(bytes);
                    return true;
                case Utf16CodePage:
                    decoded = DecodeUtf16(bytes);
                    return true;
                case Utf32CodePage:
                    decoded = DecodeUtf32(bytes);
                    return true;
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static string DecodeUtf16(byte[] bytes)
    {
        int usableLength = bytes.Length - (bytes.Length % sizeof(char));
        if (usableLength <= 0)
        {
            return string.Empty;
        }

        return Encoding.Unicode.GetString(bytes, 0, usableLength);
    }

    private static string DecodeUtf32(byte[] bytes)
    {
        const int Utf32BytesPerCodePoint = 4;
        int usableLength = bytes.Length - (bytes.Length % Utf32BytesPerCodePoint);
        if (usableLength <= 0)
        {
            return string.Empty;
        }

        return new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: false)
            .GetString(bytes, 0, usableLength);
    }

    private static byte[] CopyBlobToManaged(ComPtr<IDxcBlob> blob)
    {
        nuint size = blob.Get().GetBufferSize();
        if (size == 0)
        {
            return Array.Empty<byte>();
        }

        if (size > int.MaxValue)
        {
            throw new ShaderCompilerException(
                ShaderCompilerErrorCode.CompileFailed,
                "DXC output blob exceeds managed array size limits.");
        }

        byte[] output = new byte[(int)size];
        Marshal.Copy((IntPtr)blob.Get().GetBufferPointer(), output, 0, output.Length);
        return output;
    }
}
