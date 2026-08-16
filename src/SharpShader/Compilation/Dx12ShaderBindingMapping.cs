using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public readonly struct Dx12ShaderBindingMapping : IEquatable<Dx12ShaderBindingMapping>
    {
        public ShaderBindingKey LogicalBinding { get; }
        public uint RegisterSpace { get; }
        public uint ShaderRegister { get; }
        public ShaderBindingClass RegisterClass { get; }

        public Dx12ShaderBindingMapping(
            ShaderBindingKey logicalBinding,
            uint registerSpace,
            uint shaderRegister,
            ShaderBindingClass registerClass)
        {
            if (!Enum.IsDefined(registerClass))
            {
                throw new ArgumentOutOfRangeException(nameof(registerClass), registerClass, "DX12 register class is not defined.");
            }

            if (logicalBinding.Type != registerClass)
            {
                throw new ArgumentException(
                    $"DX12 register class {registerClass} does not match logical binding class {logicalBinding.Type}.",
                    nameof(registerClass));
            }

            if (logicalBinding.Table != registerSpace || logicalBinding.Slot != shaderRegister)
            {
                throw new ArgumentException(
                    "DX12 mappings must preserve the logical table/slot as register space/register.");
            }

            LogicalBinding = logicalBinding;
            RegisterSpace = registerSpace;
            ShaderRegister = shaderRegister;
            RegisterClass = registerClass;
        }

        public bool Equals(Dx12ShaderBindingMapping other)
        {
            return LogicalBinding == other.LogicalBinding
                && RegisterSpace == other.RegisterSpace
                && ShaderRegister == other.ShaderRegister
                && RegisterClass == other.RegisterClass;
        }

        public override bool Equals(object? obj) => obj is Dx12ShaderBindingMapping other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(LogicalBinding, RegisterSpace, ShaderRegister, RegisterClass);
        public static bool operator ==(Dx12ShaderBindingMapping left, Dx12ShaderBindingMapping right) => left.Equals(right);
        public static bool operator !=(Dx12ShaderBindingMapping left, Dx12ShaderBindingMapping right) => !left.Equals(right);
    }
}
