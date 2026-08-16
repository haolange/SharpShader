using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public sealed class ShaderResourceBindingReflection : IEquatable<ShaderResourceBindingReflection>
    {
        public ShaderLogicalBinding LogicalBinding { get; }
        public string Name => LogicalBinding.CanonicalName;
        public ShaderBindingKey Key => LogicalBinding.Key;
        public ShaderResourceShape Shape => LogicalBinding.Shape;
        public ShaderStageMask Stages => LogicalBinding.StageMask;
        public ShaderBindingProvenance Provenance => LogicalBinding.Provenance;
        public ShaderConstantBufferLayout? ConstantBufferLayout => LogicalBinding.ConstantBufferLayout;
        public ShaderPhysicalBindingLocation PhysicalLocation { get; }

        public ShaderResourceBindingReflection(
            ShaderLogicalBinding logicalBinding,
            ShaderPhysicalBindingLocation physicalLocation)
        {
            ArgumentNullException.ThrowIfNull(logicalBinding);
            ValidatePhysicalNamespace(logicalBinding.Key, physicalLocation);
            uint? boundedCount = logicalBinding.Shape.Array.BoundedElementCount;
            if (boundedCount.HasValue)
            {
                _ = checked(physicalLocation.Binding + boundedCount.Value - 1);
            }

            LogicalBinding = logicalBinding;
            PhysicalLocation = physicalLocation;
        }

        public ShaderResourceBindingReflection(
            string name,
            ShaderBindingKey key,
            ShaderResourceShape shape,
            ShaderStageMask stages,
            ShaderPhysicalBindingLocation physicalLocation)
            : this(
                new ShaderLogicalBinding(
                    key,
                    name,
                    null,
                    shape,
                    stages,
                    null,
                    ShaderBindingProvenance.Unknown),
                physicalLocation)
        {
        }

        private static void ValidatePhysicalNamespace(ShaderBindingKey key, ShaderPhysicalBindingLocation location)
        {
            ShaderPhysicalBindingNamespace? expected = location.Backend == ShaderBackendKind.DirectX12
                ? key.Type switch
                {
                    ShaderBindingClass.ShaderResource => ShaderPhysicalBindingNamespace.ShaderResource,
                    ShaderBindingClass.Sampler => ShaderPhysicalBindingNamespace.Sampler,
                    ShaderBindingClass.ConstantBuffer => ShaderPhysicalBindingNamespace.ConstantBuffer,
                    ShaderBindingClass.UnorderedAccess => ShaderPhysicalBindingNamespace.UnorderedAccess,
                    _ => null,
                }
                : null;

            if (expected.HasValue && location.Namespace != expected.Value)
            {
                throw new ArgumentException(
                    $"DX12 physical namespace {location.Namespace} does not match logical binding class {key.Type}.",
                    nameof(location));
            }
        }

        public bool Equals(ShaderResourceBindingReflection? other)
        {
            return other is not null
                && LogicalBinding.Equals(other.LogicalBinding)
                && PhysicalLocation == other.PhysicalLocation;
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderResourceBindingReflection);
        public override int GetHashCode() => HashCode.Combine(LogicalBinding, PhysicalLocation);
    }
}
