using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;

namespace SharpShader.HLSLCrossCompiler.Internal
{
    internal sealed class DxcCapturedInclude
    {
        private readonly byte[] m_Content;

        public string RequestedPath { get; }
        public int ByteLength => m_Content.Length;
        public string ContentDigest { get; }

        public DxcCapturedInclude(string requestedPath, ReadOnlySpan<byte> content)
        {
            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                throw new ArgumentException(
                    "A captured include path must not be empty.",
                    nameof(requestedPath));
            }

            RequestedPath = requestedPath;
            m_Content = content.ToArray();
            ContentDigest = Convert.ToHexStringLower(SHA256.HashData(m_Content));
        }

        public byte[] CopyContent()
        {
            return (byte[])m_Content.Clone();
        }
    }

    internal sealed unsafe class DxcDependencyCaptureIncludeHandler : IDisposable
    {
        private const int EFail = unchecked((int)0x80004005);
        private const int ENoInterface = unchecked((int)0x80004002);
        private const int EPointer = unchecked((int)0x80004003);

        private static readonly Guid s_IUnknownGuid =
            new("00000000-0000-0000-C000-000000000046");
        private static readonly void** s_Vtable = CreateVtable();

        private readonly CaptureState m_State;
        private ComPtr<IDxcIncludeHandler> m_Handler;
        private bool m_Disposed;

        public ComPtr<IDxcIncludeHandler> Handler
        {
            get
            {
                ObjectDisposedException.ThrowIf(m_Disposed, this);
                return m_Handler;
            }
        }

        public DxcDependencyCaptureIncludeHandler(
            ref ComPtr<IDxcIncludeHandler> defaultHandler,
            DxcDependencyCaptureLimits? limits,
            string sourceName,
            ref ComPtr<IDxcBlobEncoding> sourceBlob)
        {
            if (defaultHandler.Handle == null)
            {
                throw new ArgumentException(
                    "The default DXC include handler must not be null.",
                    nameof(defaultHandler));
            }

            if (sourceBlob.Handle == null)
            {
                throw new ArgumentException("The in-memory source blob must not be null.", nameof(sourceBlob));
            }

            m_State = new CaptureState(defaultHandler, limits, sourceName, sourceBlob);
            defaultHandler = default;
            sourceBlob = default;
            GCHandle stateHandle = default;
            IncludeHandlerInstance* instance = null;
            try
            {
                stateHandle = GCHandle.Alloc(m_State, GCHandleType.Normal);
                instance = (IncludeHandlerInstance*)NativeMemory.Alloc((nuint)sizeof(IncludeHandlerInstance));
                if (instance == null)
                {
                    throw new InvalidOperationException("Failed to allocate the DXC include handler.");
                }

                instance->Vtable = s_Vtable;
                instance->StateHandle = GCHandle.ToIntPtr(stateHandle);
                instance->ReferenceCount = 1;
                m_Handler = new ComPtr<IDxcIncludeHandler>((IDxcIncludeHandler*)instance);
            }
            catch
            {
                NativeMemory.Free(instance);
                if (stateHandle.IsAllocated)
                {
                    stateHandle.Free();
                }

                m_State.Dispose();
                throw;
            }
        }

        public IReadOnlyList<DxcCapturedInclude> CompleteCapture()
        {
            ObjectDisposedException.ThrowIf(m_Disposed, this);
            m_State.ThrowIfCallbackFailed();
            return m_State.CopyCaptures();
        }

        public void Dispose()
        {
            if (m_Disposed)
            {
                return;
            }

            m_Disposed = true;
            m_Handler.Dispose();
            m_Handler = default;
        }

        private static void** CreateVtable()
        {
            void** vtable = (void**)NativeMemory.Alloc(
                checked((nuint)(4 * sizeof(void*))));
            if (vtable == null)
            {
                throw new InvalidOperationException(
                    "Failed to allocate the DXC include-handler vtable.");
            }

            vtable[0] = (void*)(delegate* unmanaged[Stdcall]<
                IncludeHandlerInstance*,
                Guid*,
                void**,
                int>)&QueryInterface;
            vtable[1] = (void*)(delegate* unmanaged[Stdcall]<
                IncludeHandlerInstance*,
                uint>)&AddRef;
            vtable[2] = (void*)(delegate* unmanaged[Stdcall]<
                IncludeHandlerInstance*,
                uint>)&Release;
            vtable[3] = (void*)(delegate* unmanaged[Stdcall]<
                IncludeHandlerInstance*,
                char*,
                IDxcBlob**,
                int>)&LoadSource;
            return vtable;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int QueryInterface(
            IncludeHandlerInstance* instance,
            Guid* interfaceId,
            void** result)
        {
            if (interfaceId == null || result == null)
            {
                return EPointer;
            }

            *result = null;
            if (*interfaceId != IDxcIncludeHandler.Guid
                && *interfaceId != s_IUnknownGuid)
            {
                return ENoInterface;
            }

            AddReference(instance);
            *result = instance;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static uint AddRef(IncludeHandlerInstance* instance)
        {
            return AddReference(instance);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static uint Release(IncludeHandlerInstance* instance)
        {
            int referenceCount = Interlocked.Decrement(
                ref instance->ReferenceCount);
            if (referenceCount != 0)
            {
                return checked((uint)referenceCount);
            }

            GCHandle stateHandle = GCHandle.FromIntPtr(instance->StateHandle);
            if (stateHandle.Target is CaptureState state)
            {
                state.Dispose();
            }

            stateHandle.Free();
            NativeMemory.Free(instance);
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int LoadSource(
            IncludeHandlerInstance* instance,
            char* fileName,
            IDxcBlob** includeSource)
        {
            if (fileName == null || includeSource == null)
            {
                return EPointer;
            }

            *includeSource = null;
            CaptureState state = GetState(instance);
            IDxcBlob* loaded = null;
            try
            {
                string requestedPath = DecodeWideString(fileName);
                // DXC may ask the include handler for its main file. The caller's
                // in-memory source remains authoritative even when a disk file exists.
                if (state.TryLoadMainSource(requestedPath, includeSource))
                {
                    return 0;
                }

                int result = state.DefaultHandler.Get().LoadSource(
                    fileName,
                    &loaded);
                if (result < 0 || loaded == null)
                {
                    return result < 0 ? result : EFail;
                }

                nuint length = loaded->GetBufferSize();
                if (length > int.MaxValue)
                {
                    throw new InvalidOperationException(
                        $"DXC include '{requestedPath}' exceeds managed size limits.");
                }

                void* contentPointer = loaded->GetBufferPointer();
                if (length != 0 && contentPointer == null)
                {
                    throw new InvalidOperationException(
                        $"DXC include '{requestedPath}' returned a null content pointer.");
                }

                ReadOnlySpan<byte> content = length == 0
                    ? ReadOnlySpan<byte>.Empty
                    : new ReadOnlySpan<byte>(contentPointer, checked((int)length));
                // DXC on Unix probes parent directories (including "/") via LoadSource
                // before resolving real includes. Only physical files are
                // dependency-identity captures; directory probes still return
                // the default-handler blob to DXC unchanged.
                if (IsPhysicalIncludeFile(requestedPath))
                {
                    state.Record(requestedPath, content);
                }

                *includeSource = loaded;
                return result;
            }
            catch (Exception exception)
            {
                if (loaded != null)
                {
                    loaded->Release();
                }

                state.RecordCallbackFailure(exception);
                return EFail;
            }
        }

        private static string DecodeWideString(char* value)
        {
            if (OperatingSystem.IsWindows())
            {
                return new string(value);
            }

            StringBuilder result = new();
            for (int* codePoint = (int*)value; *codePoint != 0; codePoint++)
            {
                result.Append(char.ConvertFromUtf32(*codePoint));
            }

            return result.ToString();
        }

        private static bool IsPhysicalIncludeFile(string path)
        {
            return !string.IsNullOrWhiteSpace(path)
                && Path.IsPathFullyQualified(path)
                && File.Exists(path);
        }

        private static uint AddReference(IncludeHandlerInstance* instance)
        {
            int referenceCount = Interlocked.Increment(
                ref instance->ReferenceCount);
            return checked((uint)referenceCount);
        }

        private static CaptureState GetState(IncludeHandlerInstance* instance)
        {
            GCHandle stateHandle = GCHandle.FromIntPtr(instance->StateHandle);
            return (CaptureState)(stateHandle.Target
                ?? throw new InvalidOperationException(
                    "The DXC include-handler state has been released."));
        }

        private struct IncludeHandlerInstance
        {
            public void** Vtable;
            public nint StateHandle;
            public int ReferenceCount;
        }

        private sealed class CaptureState : IDisposable
        {
            private readonly object m_Sync = new();
            private readonly Dictionary<string, DxcCapturedInclude> m_Captures;
            private ComPtr<IDxcIncludeHandler> m_DefaultHandler;
            private readonly DxcDependencyCaptureLimits? m_Limits;
            private readonly string m_SourceName;
            private ComPtr<IDxcBlobEncoding> m_SourceBlob;
            private ExceptionDispatchInfo? m_CallbackFailure;
            private long m_TotalBytes;
            private bool m_Disposed;

            public ComPtr<IDxcIncludeHandler> DefaultHandler => m_DefaultHandler;

            public CaptureState(
                ComPtr<IDxcIncludeHandler> defaultHandler,
                DxcDependencyCaptureLimits? limits,
                string sourceName,
                ComPtr<IDxcBlobEncoding> sourceBlob)
            {
                m_DefaultHandler = defaultHandler;
                m_Limits = limits;
                m_SourceName = NormalizeSourceName(sourceName);
                m_SourceBlob = sourceBlob;
                m_Captures = new Dictionary<string, DxcCapturedInclude>(
                    OperatingSystem.IsWindows()
                        ? StringComparer.OrdinalIgnoreCase
                        : StringComparer.Ordinal);
            }

            public bool TryLoadMainSource(string requestedPath, IDxcBlob** result)
            {
                if (!string.Equals(m_SourceName, NormalizeSourceName(requestedPath),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    return false;
                }

                m_SourceBlob.Get().AddRef();
                *result = (IDxcBlob*)m_SourceBlob.Handle;
                return true;
            }

            private static string NormalizeSourceName(string name)
            {
                string normalized = name.Replace('\\', '/');
                while (normalized.StartsWith("./", StringComparison.Ordinal))
                {
                    normalized = normalized[2..];
                }

                return normalized;
            }

            public void Record(string requestedPath, ReadOnlySpan<byte> content)
            {
                if (m_Limits is null)
                {
                    return;
                }

                lock (m_Sync)
                {
                    if (!m_Captures.TryGetValue(
                            requestedPath,
                            out DxcCapturedInclude? existing))
                    {
                        ValidateNewDependency(requestedPath, content.Length);
                        DxcCapturedInclude capture = new(requestedPath, content);
                        m_Captures.Add(requestedPath, capture);
                        return;
                    }

                    if (content.Length == 0 && existing.ByteLength != 0)
                    {
                        // The default DXC handler uses an empty repeat blob to
                        // suppress duplicate includes. The first blob is the
                        // physical dependency identity.
                        return;
                    }

                    string contentDigest = Convert.ToHexStringLower(
                        SHA256.HashData(content));
                    if (content.Length != existing.ByteLength
                        || !string.Equals(
                            contentDigest,
                            existing.ContentDigest,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"DXC include '{requestedPath}' changed during preprocessing.");
                    }
                }
            }

            private void ValidateNewDependency(
                string requestedPath,
                int byteLength)
            {
                DxcDependencyCaptureLimits limits = m_Limits ?? throw new InvalidOperationException("Dependency capture is disabled.");
                if (m_Captures.Count >= limits.MaximumFileCount)
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.InvalidRequest,
                        "DXC include dependency count exceeds the configured limit.");
                }

                if (byteLength > limits.MaximumFileBytes)
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.InvalidRequest,
                        $"DXC include '{requestedPath}' exceeds the configured "
                        + "per-file byte limit.");
                }

                try
                {
                    m_TotalBytes = checked(m_TotalBytes + byteLength);
                }
                catch (OverflowException exception)
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.InvalidRequest,
                        "DXC include dependency bytes overflowed Int64.",
                        innerException: exception);
                }

                if (m_TotalBytes > limits.MaximumTotalBytes)
                {
                    throw new ShaderCompilerException(
                        ShaderCompilerErrorCode.InvalidRequest,
                        "DXC include dependency bytes exceed the configured total limit.");
                }
            }

            public void RecordCallbackFailure(Exception exception)
            {
                lock (m_Sync)
                {
                    m_CallbackFailure ??= ExceptionDispatchInfo.Capture(exception);
                }
            }

            public void ThrowIfCallbackFailed()
            {
                lock (m_Sync)
                {
                    m_CallbackFailure?.Throw();
                }
            }

            public IReadOnlyList<DxcCapturedInclude> CopyCaptures()
            {
                lock (m_Sync)
                {
                    DxcCapturedInclude[] captures =
                        new List<DxcCapturedInclude>(m_Captures.Values).ToArray();
                    Array.Sort(captures, static (left, right) =>
                        string.CompareOrdinal(left.RequestedPath, right.RequestedPath));
                    return Array.AsReadOnly(captures);
                }
            }

            public void Dispose()
            {
                if (m_Disposed)
                {
                    return;
                }

                m_Disposed = true;
                m_DefaultHandler.Dispose();
                m_DefaultHandler = default;
                m_SourceBlob.Dispose();
                m_SourceBlob = default;
            }
        }
    }
}
