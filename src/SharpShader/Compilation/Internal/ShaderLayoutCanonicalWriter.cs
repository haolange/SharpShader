using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace SharpShader.Compilation.Internal
{
    internal static class ShaderLayoutCanonicalWriter
    {
        private const uint CanonicalFormatMagic = 0x494C5353;
        private const uint CanonicalFormatVersion = 1;

        public static byte[] Write(IReadOnlyList<ShaderLogicalBinding> bindings)
        {
            ArgumentNullException.ThrowIfNull(bindings);

            ArrayBufferWriter<byte> writer = new();
            WriteUInt32(writer, CanonicalFormatMagic);
            WriteUInt32(writer, CanonicalFormatVersion);
            WriteCount(writer, bindings.Count);

            foreach (ShaderLogicalBinding binding in bindings)
            {
                ArgumentNullException.ThrowIfNull(binding);
                WriteUInt32(writer, binding.Key.Table);
                WriteUInt32(writer, binding.Key.Slot);
                WriteInt32(writer, (int)binding.Key.Type);
                WriteResourceShape(writer, binding.Shape);
                WriteUInt64(writer, (ulong)binding.StageMask);
                WriteBoolean(writer, binding.ConstantBufferLayout is not null);
                if (binding.ConstantBufferLayout is not null)
                {
                    WriteConstantBufferLayout(writer, binding.ConstantBufferLayout);
                }
            }

            return writer.WrittenSpan.ToArray();
        }

        public static ShaderLayoutSignature ComputeSha256(ReadOnlyMemory<byte> canonicalBytes)
        {
            return new ShaderLayoutSignature(SHA256.HashData(canonicalBytes.Span));
        }

        private static void WriteResourceShape(ArrayBufferWriter<byte> writer, ShaderResourceShape shape)
        {
            WriteInt32(writer, (int)shape.Kind);
            WriteInt32(writer, (int)shape.Dimension);
            WriteInt32(writer, (int)shape.Access);
            WriteArrayShape(writer, shape.Array);
            WriteNullableUInt32(writer, shape.StructureStride);
            WriteNullableInt32(writer, shape.SamplerKind.HasValue ? (int)shape.SamplerKind.Value : null);
            WriteInt32(writer, (int)shape.CounterKind);
        }

        private static void WriteArrayShape(ArrayBufferWriter<byte> writer, ShaderArrayShape shape)
        {
            WriteCount(writer, shape.Extents.Count);
            foreach (ShaderArrayExtent extent in shape.Extents)
            {
                WriteInt32(writer, (int)extent.Kind);
                WriteUInt32(writer, extent.Value);
            }
        }

        private static void WriteConstantBufferLayout(
            ArrayBufferWriter<byte> writer,
            ShaderConstantBufferLayout layout)
        {
            WriteUInt32(writer, layout.ByteSize);
            WriteCount(writer, layout.Variables.Count);
            foreach (ShaderValueMember variable in layout.Variables)
            {
                WriteValueMember(writer, variable);
            }
        }

        private static void WriteValueMember(ArrayBufferWriter<byte> writer, ShaderValueMember member)
        {
            WriteUInt32(writer, member.ByteOffset);
            WriteUInt32(writer, member.ByteSize);
            WriteValueLayout(writer, member.Value);
        }

        private static void WriteValueLayout(ArrayBufferWriter<byte> writer, ShaderValueLayout layout)
        {
            WriteInt32(writer, (int)layout.Kind);
            WriteNullableInt32(writer, layout.ScalarType.HasValue ? (int)layout.ScalarType.Value : null);
            WriteUInt32(writer, layout.Rows);
            WriteUInt32(writer, layout.Columns);
            WriteArrayShape(writer, layout.Array);
            WriteNullableUInt32(writer, layout.ByteSize);
            WriteNullableUInt32(writer, layout.ProvenArrayStride);
            WriteNullableUInt32(writer, layout.ProvenMatrixStride);
            WriteNullableInt32(writer, layout.MatrixMajorOrder.HasValue ? (int)layout.MatrixMajorOrder.Value : null);
            WriteCount(writer, layout.Members.Count);
            foreach (ShaderValueMember member in layout.Members)
            {
                WriteValueMember(writer, member);
            }
        }

        private static void WriteCount(ArrayBufferWriter<byte> writer, int count)
        {
            WriteUInt32(writer, checked((uint)count));
        }

        private static void WriteBoolean(ArrayBufferWriter<byte> writer, bool value)
        {
            Span<byte> destination = writer.GetSpan(sizeof(byte));
            destination[0] = value ? (byte)1 : (byte)0;
            writer.Advance(sizeof(byte));
        }

        private static void WriteInt32(ArrayBufferWriter<byte> writer, int value)
        {
            Span<byte> destination = writer.GetSpan(sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(destination, value);
            writer.Advance(sizeof(int));
        }

        private static void WriteUInt32(ArrayBufferWriter<byte> writer, uint value)
        {
            Span<byte> destination = writer.GetSpan(sizeof(uint));
            BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
            writer.Advance(sizeof(uint));
        }

        private static void WriteUInt64(ArrayBufferWriter<byte> writer, ulong value)
        {
            Span<byte> destination = writer.GetSpan(sizeof(ulong));
            BinaryPrimitives.WriteUInt64LittleEndian(destination, value);
            writer.Advance(sizeof(ulong));
        }

        private static void WriteNullableInt32(ArrayBufferWriter<byte> writer, int? value)
        {
            WriteBoolean(writer, value.HasValue);
            if (value.HasValue)
            {
                WriteInt32(writer, value.Value);
            }
        }

        private static void WriteNullableUInt32(ArrayBufferWriter<byte> writer, uint? value)
        {
            WriteBoolean(writer, value.HasValue);
            if (value.HasValue)
            {
                WriteUInt32(writer, value.Value);
            }
        }
    }
}
