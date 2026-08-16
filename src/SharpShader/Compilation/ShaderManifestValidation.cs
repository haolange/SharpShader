using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.Compilation.Internal;

namespace SharpShader.Compilation
{

    internal static class ShaderManifestValidation
    {
        public static void ValidateSha256(string value, string parameterName)
        {
            if (value.Length != ShaderLayoutSignature.ByteLength * 2)
            {
                throw new ArgumentException("SHA-256 digests must contain 64 lowercase hexadecimal characters.", parameterName);
            }

            foreach (char character in value)
            {
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f')))
                {
                    throw new ArgumentException("SHA-256 digests must contain 64 lowercase hexadecimal characters.", parameterName);
                }
            }
        }

        public static bool SequenceEqual<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
            where T : IEquatable<T>
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (int index = 0; index < left.Count; ++index)
            {
                if (!left[index].Equals(right[index]))
                {
                    return false;
                }
            }

            return true;
        }

        public static bool SequenceEqual<T>(
            IReadOnlyList<T> left,
            IReadOnlyList<T> right,
            IEqualityComparer<T> comparer)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (int index = 0; index < left.Count; ++index)
            {
                if (!comparer.Equals(left[index], right[index]))
                {
                    return false;
                }
            }

            return true;
        }

        public static int GetSequenceHashCode<T>(IReadOnlyList<T> values)
        {
            HashCode hash = new();
            foreach (T value in values)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }

        public static int GetSequenceHashCode<T>(IReadOnlyList<T> values, IEqualityComparer<T> comparer)
        {
            HashCode hash = new();
            foreach (T value in values)
            {
                hash.Add(value, comparer);
            }

            return hash.ToHashCode();
        }
    }
}
