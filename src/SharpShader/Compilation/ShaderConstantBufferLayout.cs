using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public sealed class ShaderConstantBufferLayout : IEquatable<ShaderConstantBufferLayout>
    {
        private readonly ReadOnlyCollection<ShaderValueMember> m_Variables;

        public string Name { get; }
        public uint ByteSize { get; }
        public IReadOnlyList<ShaderValueMember> Variables => m_Variables;

        public ShaderConstantBufferLayout(string name, uint byteSize, IEnumerable<ShaderValueMember> variables)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Constant-buffer name must not be empty.", nameof(name));
            }

            if (byteSize == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(byteSize), "Constant-buffer byte size must be greater than zero.");
            }

            ArgumentNullException.ThrowIfNull(variables);
            ShaderValueMember[] copy = new List<ShaderValueMember>(variables).ToArray();
            Array.Sort(copy, static (left, right) =>
            {
                int offset = left.ByteOffset.CompareTo(right.ByteOffset);
                return offset != 0 ? offset : string.CompareOrdinal(left.Name, right.Name);
            });

            if (copy.Length == 0)
            {
                throw new ArgumentException("Constant buffers must contain at least one reflected variable.", nameof(variables));
            }

            HashSet<string> names = new(StringComparer.Ordinal);
            uint previousEnd = 0;
            foreach (ShaderValueMember variable in copy)
            {
                ArgumentNullException.ThrowIfNull(variable);
                if (!names.Add(variable.Name))
                {
                    throw new ArgumentException($"Constant buffer contains duplicate variable {variable.Name}.", nameof(variables));
                }

                if (variable.ByteOffset < previousEnd)
                {
                    throw new ArgumentException($"Constant-buffer variable {variable.Name} overlaps a previous variable.", nameof(variables));
                }

                uint variableEnd = checked(variable.ByteOffset + variable.ByteSize);
                if (variableEnd > byteSize)
                {
                    throw new ArgumentException($"Constant-buffer variable {variable.Name} exceeds the buffer byte size.", nameof(variables));
                }

                variable.Value.ValidateConstantBufferCompatible($"{name}.{variable.Name}");
                previousEnd = variableEnd;
            }

            Name = name;
            ByteSize = byteSize;
            m_Variables = Array.AsReadOnly(copy);
        }

        public bool Equals(ShaderConstantBufferLayout? other)
        {
            if (other is null
                || !string.Equals(Name, other.Name, StringComparison.Ordinal)
                || ByteSize != other.ByteSize
                || m_Variables.Count != other.m_Variables.Count)
            {
                return false;
            }

            for (int index = 0; index < m_Variables.Count; ++index)
            {
                if (!m_Variables[index].Equals(other.m_Variables[index]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderConstantBufferLayout);

        public override int GetHashCode()
        {
            HashCode hash = new HashCode();
            hash.Add(Name, StringComparer.Ordinal);
            hash.Add(ByteSize);
            foreach (ShaderValueMember variable in m_Variables)
            {
                hash.Add(variable);
            }

            return hash.ToHashCode();
        }
    }
}
