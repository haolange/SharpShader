using System;
using System.Buffers.Binary;
using System.Diagnostics;

namespace SharpShader.Compilation
{
    [DebuggerDisplay("{ToString(),nq}")]
    public readonly struct ShaderLayoutSignature :
        IEquatable<ShaderLayoutSignature>,
        IComparable<ShaderLayoutSignature>
    {
        public const int ByteLength = 32;

        private readonly ulong m_Part0;
        private readonly ulong m_Part1;
        private readonly ulong m_Part2;
        private readonly ulong m_Part3;

        public ShaderLayoutSignature(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != ByteLength)
            {
                throw new ArgumentException(
                    $"Shader layout signatures must contain exactly {ByteLength} bytes.",
                    nameof(bytes));
            }

            m_Part0 = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
            m_Part1 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[sizeof(ulong)..]);
            m_Part2 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(sizeof(ulong) * 2)..]);
            m_Part3 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(sizeof(ulong) * 3)..]);
        }

        public static ShaderLayoutSignature Parse(string text)
        {
            if (!TryParse(text, out ShaderLayoutSignature signature))
            {
                throw new FormatException(
                    $"Shader layout signatures must be {ByteLength * 2} lowercase hexadecimal characters.");
            }

            return signature;
        }

        public static bool TryParse(string? text, out ShaderLayoutSignature signature)
        {
            signature = default;
            if (text == null || text.Length != ByteLength * 2)
            {
                return false;
            }

            foreach (char character in text)
            {
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }

            Span<byte> bytes = stackalloc byte[ByteLength];
            for (int byteIndex = 0; byteIndex < ByteLength; byteIndex++)
            {
                int characterIndex = byteIndex * 2;
                bytes[byteIndex] = (byte)((ParseHexNibble(text[characterIndex]) << 4) |
                                          ParseHexNibble(text[characterIndex + 1]));
            }

            signature = new ShaderLayoutSignature(bytes);
            return true;
        }

        public byte[] ToArray()
        {
            byte[] bytes = new byte[ByteLength];
            CopyTo(bytes);
            return bytes;
        }

        public void CopyTo(Span<byte> destination)
        {
            if (destination.Length < ByteLength)
            {
                throw new ArgumentException(
                    $"The destination must contain at least {ByteLength} bytes.",
                    nameof(destination));
            }

            BinaryPrimitives.WriteUInt64LittleEndian(destination, m_Part0);
            BinaryPrimitives.WriteUInt64LittleEndian(destination[sizeof(ulong)..], m_Part1);
            BinaryPrimitives.WriteUInt64LittleEndian(destination[(sizeof(ulong) * 2)..], m_Part2);
            BinaryPrimitives.WriteUInt64LittleEndian(destination[(sizeof(ulong) * 3)..], m_Part3);
        }

        public int CompareTo(ShaderLayoutSignature other)
        {
            Span<byte> left = stackalloc byte[ByteLength];
            Span<byte> right = stackalloc byte[ByteLength];
            CopyTo(left);
            other.CopyTo(right);
            return left.SequenceCompareTo(right);
        }

        public bool Equals(ShaderLayoutSignature other)
        {
            return m_Part0 == other.m_Part0 &&
                   m_Part1 == other.m_Part1 &&
                   m_Part2 == other.m_Part2 &&
                   m_Part3 == other.m_Part3;
        }

        public override bool Equals(object? obj)
        {
            return obj is ShaderLayoutSignature other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(m_Part0, m_Part1, m_Part2, m_Part3);
        }

        public override string ToString()
        {
            Span<byte> bytes = stackalloc byte[ByteLength];
            CopyTo(bytes);
            return Convert.ToHexStringLower(bytes);
        }

        public static bool operator ==(ShaderLayoutSignature left, ShaderLayoutSignature right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ShaderLayoutSignature left, ShaderLayoutSignature right)
        {
            return !left.Equals(right);
        }

        public static bool operator <(ShaderLayoutSignature left, ShaderLayoutSignature right)
        {
            return left.CompareTo(right) < 0;
        }

        public static bool operator <=(ShaderLayoutSignature left, ShaderLayoutSignature right)
        {
            return left.CompareTo(right) <= 0;
        }

        public static bool operator >(ShaderLayoutSignature left, ShaderLayoutSignature right)
        {
            return left.CompareTo(right) > 0;
        }

        public static bool operator >=(ShaderLayoutSignature left, ShaderLayoutSignature right)
        {
            return left.CompareTo(right) >= 0;
        }

        private static int ParseHexNibble(char character)
        {
            return character <= '9'
                ? character - '0'
                : character - 'a' + 10;
        }
    }
}
