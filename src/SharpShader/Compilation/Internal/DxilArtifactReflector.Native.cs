using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using SharpShader.HLSLCrossCompiler;
using SharpShader.HLSLCrossCompiler.Internal;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D12;
using DxcBuffer = Silk.NET.Direct3D.Compilers.Buffer;

namespace SharpShader.Compilation.Internal
{
    internal static unsafe partial class DxilArtifactReflector
    {

        private static string ReadRequiredUtf8Name(
            ShaderCompileRequest request,
            byte* pointer,
            string context)
        {
            if (pointer == null)
            {
                throw ReflectionFailure(request, $"{context} has a null name.");
            }

            int byteLength = 0;
            while (byteLength < MaximumUtf8NameByteLength && pointer[byteLength] != 0)
            {
                ++byteLength;
            }

            if (byteLength == MaximumUtf8NameByteLength)
            {
                throw ReflectionFailure(
                    request,
                    $"{context} exceeds the maximum supported UTF-8 name length of {MaximumUtf8NameByteLength - 1} bytes.");
            }

            string value;
            try
            {
                value = s_StrictUtf8.GetString(new ReadOnlySpan<byte>(pointer, byteLength));
            }
            catch (DecoderFallbackException ex)
            {
                throw ReflectionFailure(request, $"{context} is not valid UTF-8.", ex);
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                throw ReflectionFailure(request, $"{context} has an empty name.");
            }

            return value;
        }

        private static int ToManagedCount(
            ShaderCompileRequest request,
            uint value,
            string context)
        {
            if (value > int.MaxValue)
            {
                throw ReflectionFailure(
                    request,
                    $"{context} {value} exceeds managed collection limits.");
            }

            return (int)value;
        }

        private static void ThrowIfFailed(
            ShaderCompileRequest request,
            int hresult,
            string message)
        {
            if (hresult < 0)
            {
                throw ReflectionFailure(
                    request,
                    $"{message} HRESULT=0x{hresult:X8}");
            }
        }

        private static ComPtr<IDxcUtils> CreateDxcUtils()
        {
            Guid clsid = s_ClsidDxcUtils;
            Guid iid = IDxcUtils.Guid;
            int hresult = NativeDxcCreateInstance(ref clsid, ref iid, out nint instance);
            if (hresult < 0 || instance == 0)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.BackendUnavailable,
                    $"DXC reflection backend initialization failed. HRESULT=0x{hresult:X8}");
            }

            return new ComPtr<IDxcUtils>((IDxcUtils*)instance);
        }

        private static ComPtr<T> CreateReflection<T>(
            ShaderCompileRequest request,
            ComPtr<IDxcUtils> utils,
            DxcBuffer* reflectionBuffer,
            Guid iid,
            string interfaceName)
            where T : unmanaged, IComVtbl<T>
        {
            ComPtr<T> reflection = default;
            int hresult = utils.Get().CreateReflection(
                reflectionBuffer,
                ref iid,
                (void**)reflection.GetAddressOf());
            if (hresult < 0 || reflection.Handle == null)
            {
                reflection.Dispose();
                throw ReflectionFailure(
                    request,
                    $"DXC could not create {interfaceName} from the artifact reflection data. HRESULT=0x{hresult:X8}");
            }

            return reflection;
        }

        private static ShaderCompilerException ReflectionFailure(
            ShaderCompileRequest request,
            string message,
            Exception? innerException = null)
        {
            return new ShaderCompilerException(
                ShaderCompilerErrorCode.CompileFailed,
                message,
                requestedProfile: BuildRequestedProfile(request),
                innerException: innerException);
        }

        private static string BuildRequestedProfile(ShaderCompileRequest request)
        {
            string stagePrefix = request.Stage switch
            {
                ShaderStageKind.Vertex => "vs",
                ShaderStageKind.Hull => "hs",
                ShaderStageKind.Domain => "ds",
                ShaderStageKind.Geometry => "gs",
                ShaderStageKind.Pixel => "ps",
                ShaderStageKind.Compute => "cs",
                ShaderStageKind.Amplification => "as",
                ShaderStageKind.Mesh => "ms",
                ShaderStageKind.Library => "lib",
                _ => "unknown",
            };

            return $"{stagePrefix}_{request.ShaderModel.Major}_{request.ShaderModel.Minor}";
        }

        private readonly struct ReflectionScope
        {
            private readonly ID3D12ShaderReflection* m_Shader;
            private readonly ID3D12FunctionReflection* m_Function;

            public ReflectionScope(ID3D12ShaderReflection* shader)
            {
                m_Shader = shader;
                m_Function = null;
            }

            public ReflectionScope(ID3D12FunctionReflection* function)
            {
                m_Shader = null;
                m_Function = function;
            }

            public int GetResourceBindingDescription(
                uint index,
                ref ShaderInputBindDesc description)
            {
                return m_Shader != null
                    ? m_Shader->GetResourceBindingDesc(index, ref description)
                    : m_Function->GetResourceBindingDesc(index, ref description);
            }

            public ID3D12ShaderReflectionConstantBuffer* GetConstantBufferByIndex(uint index)
            {
                return m_Shader != null
                    ? m_Shader->GetConstantBufferByIndex(index)
                    : m_Function->GetConstantBufferByIndex(index);
            }
        }

        private readonly struct ReflectedConstantBuffer
        {
            public string Name { get; }
            public D3DCBufferType Type { get; }
            public ShaderConstantBufferLayout Layout { get; }

            public ReflectedConstantBuffer(
                string name,
                D3DCBufferType type,
                ShaderConstantBufferLayout layout)
            {
                Name = name;
                Type = type;
                Layout = layout;
            }
        }

        private readonly struct ReflectedTypeMember
        {
            public string Name { get; }
            public uint Offset { get; }
            public ID3D12ShaderReflectionType* Type { get; }

            public ReflectedTypeMember(
                string name,
                uint offset,
                ID3D12ShaderReflectionType* type)
            {
                Name = name;
                Offset = offset;
                Type = type;
            }
        }
}
}
