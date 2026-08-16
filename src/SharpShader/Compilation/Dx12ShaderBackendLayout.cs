using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public sealed class Dx12ShaderBackendLayout : IEquatable<Dx12ShaderBackendLayout>
    {
        private readonly ReadOnlyCollection<Dx12ShaderBindingMapping> m_Bindings;

        public IReadOnlyList<Dx12ShaderBindingMapping> Bindings => m_Bindings;

        public Dx12ShaderBackendLayout(IEnumerable<Dx12ShaderBindingMapping> bindings)
        {
            Dx12ShaderBindingMapping[] copy = ShaderBackendLayoutValidation.MaterializeAndSort(
                bindings,
                static mapping => mapping.LogicalBinding,
                nameof(bindings));

            HashSet<ShaderBindingKey> logicalBindings = new();
            HashSet<(uint Space, uint Register, ShaderBindingClass Class)> physicalBindings = new();
            foreach (Dx12ShaderBindingMapping mapping in copy)
            {
                if (!logicalBindings.Add(mapping.LogicalBinding))
                {
                    throw new ArgumentException($"DX12 layout contains duplicate logical binding {mapping.LogicalBinding}.", nameof(bindings));
                }

                if (!physicalBindings.Add((mapping.RegisterSpace, mapping.ShaderRegister, mapping.RegisterClass)))
                {
                    throw new ArgumentException(
                        $"DX12 layout contains duplicate physical register {mapping.RegisterClass}{mapping.ShaderRegister}, space{mapping.RegisterSpace}.",
                        nameof(bindings));
                }
            }

            m_Bindings = Array.AsReadOnly(copy);
        }

        public bool Equals(Dx12ShaderBackendLayout? other)
        {
            return other is not null && ShaderBackendLayoutValidation.SequenceEqual(m_Bindings, other.m_Bindings);
        }

        public override bool Equals(object? obj) => Equals(obj as Dx12ShaderBackendLayout);
        public override int GetHashCode() => ShaderBackendLayoutValidation.GetSequenceHashCode(m_Bindings);
    }
}
