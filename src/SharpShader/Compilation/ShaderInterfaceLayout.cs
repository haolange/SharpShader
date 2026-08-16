using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.Compilation.Internal;

namespace SharpShader.Compilation
{

    public sealed class ShaderInterfaceLayout : IEquatable<ShaderInterfaceLayout>
    {
        private readonly ReadOnlyCollection<ShaderLogicalBinding> m_Bindings;
        private readonly byte[] m_CanonicalAbi;

        public ShaderLayoutSignature Signature { get; }
        public IReadOnlyList<ShaderLogicalBinding> Bindings => m_Bindings;

        public ShaderInterfaceLayout(IEnumerable<ShaderLogicalBinding> bindings)
            : this(bindings, ShaderLayoutCanonicalWriter.ComputeSha256)
        {
        }

        internal ShaderInterfaceLayout(
            IEnumerable<ShaderLogicalBinding> bindings,
            Func<ReadOnlyMemory<byte>, ShaderLayoutSignature> hashProvider)
        {
            ArgumentNullException.ThrowIfNull(bindings);
            ArgumentNullException.ThrowIfNull(hashProvider);

            ShaderLogicalBinding[] copy = new List<ShaderLogicalBinding>(bindings).ToArray();
            Array.Sort(copy, CompareBindings);

            for (int index = 0; index < copy.Length; ++index)
            {
                ArgumentNullException.ThrowIfNull(copy[index]);
                if (index > 0 && copy[index - 1].Key == copy[index].Key)
                {
                    throw new ArgumentException($"Logical layout contains duplicate binding {copy[index].Key}.", nameof(bindings));
                }
            }

            ValidateRegisterRanges(copy);

            m_Bindings = Array.AsReadOnly(copy);
            m_CanonicalAbi = ShaderLayoutCanonicalWriter.Write(m_Bindings);
            Signature = hashProvider(m_CanonicalAbi);
        }

        public bool Equals(ShaderInterfaceLayout? other)
        {
            return other is not null
                && Signature == other.Signature
                && m_CanonicalAbi.AsSpan().SequenceEqual(other.m_CanonicalAbi)
                && ContentEquals(other);
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderInterfaceLayout);
        public override int GetHashCode() => Signature.GetHashCode();

        internal bool ContentEquals(ShaderInterfaceLayout other)
        {
            if (m_Bindings.Count != other.m_Bindings.Count)
            {
                return false;
            }

            for (int index = 0; index < m_Bindings.Count; ++index)
            {
                if (!m_Bindings[index].Equals(other.m_Bindings[index]))
                {
                    return false;
                }
            }

            return true;
        }

        internal bool AbiEquals(ShaderInterfaceLayout other)
        {
            return Signature == other.Signature
                && m_CanonicalAbi.AsSpan().SequenceEqual(other.m_CanonicalAbi);
        }

        private static int CompareBindings(ShaderLogicalBinding? left, ShaderLogicalBinding? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            return ShaderBackendLayoutValidation.CompareKeys(left.Key, right.Key);
        }

        private static void ValidateRegisterRanges(ShaderLogicalBinding[] bindings)
        {
            for (int leftIndex = 0; leftIndex < bindings.Length; ++leftIndex)
            {
                ShaderLogicalBinding left = bindings[leftIndex];
                uint leftEnd = GetLastSlot(left);

                for (int rightIndex = leftIndex + 1; rightIndex < bindings.Length; ++rightIndex)
                {
                    ShaderLogicalBinding right = bindings[rightIndex];
                    if (right.Key.Table != left.Key.Table || right.Key.Type != left.Key.Type)
                    {
                        continue;
                    }

                    if ((left.StageMask & right.StageMask) == ShaderStageMask.None)
                    {
                        continue;
                    }

                    uint rightEnd = GetLastSlot(right);
                    if (left.Key.Slot <= rightEnd && right.Key.Slot <= leftEnd)
                    {
                        throw new ArgumentException(
                            $"Logical bindings {left.Key} and {right.Key} overlap for intersecting shader stages.",
                            nameof(bindings));
                    }
                }
            }
        }

        private static uint GetLastSlot(ShaderLogicalBinding binding)
        {
            uint? count = binding.Shape.Array.BoundedElementCount;
            return count.HasValue
                ? checked(binding.Key.Slot + count.Value - 1)
                : uint.MaxValue;
        }
    }
}
