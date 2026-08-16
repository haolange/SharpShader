using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    internal static class ShaderBackendLayoutValidation
    {
        public static T[] MaterializeAndSort<T>(
            IEnumerable<T> values,
            Func<T, ShaderBindingKey> keySelector,
            string parameterName)
        {
            ArgumentNullException.ThrowIfNull(values, parameterName);
            T[] copy = new List<T>(values).ToArray();
            Array.Sort(copy, (left, right) => CompareKeys(keySelector(left), keySelector(right)));
            return copy;
        }

        public static int CompareKeys(ShaderBindingKey left, ShaderBindingKey right)
        {
            int table = left.Table.CompareTo(right.Table);
            if (table != 0)
            {
                return table;
            }

            int slot = left.Slot.CompareTo(right.Slot);
            return slot != 0 ? slot : left.Type.CompareTo(right.Type);
        }

        public static void ValidateMetalNamespace(ShaderPhysicalBindingNamespace bindingNamespace, string parameterName)
        {
            if (bindingNamespace is not ShaderPhysicalBindingNamespace.Buffer
                and not ShaderPhysicalBindingNamespace.Texture
                and not ShaderPhysicalBindingNamespace.Sampler)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    bindingNamespace,
                    "Metal bindings must use the buffer, texture, or sampler namespace.");
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

        public static int GetSequenceHashCode<T>(IReadOnlyList<T> values)
        {
            HashCode hash = new();
            foreach (T value in values)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }
}
