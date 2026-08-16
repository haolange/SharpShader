using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public readonly struct ShaderPhysicalBindingLocation : IEquatable<ShaderPhysicalBindingLocation>
    {
        public ShaderBackendKind Backend { get; }
        public uint Group { get; }
        public uint Binding { get; }
        public ShaderPhysicalBindingNamespace Namespace { get; }

        public ShaderPhysicalBindingLocation(
            ShaderBackendKind backend,
            uint group,
            uint binding,
            ShaderPhysicalBindingNamespace bindingNamespace)
        {
            if (!Enum.IsDefined(backend))
            {
                throw new ArgumentOutOfRangeException(nameof(backend), backend, "Shader backend is not defined.");
            }

            if (!Enum.IsDefined(bindingNamespace))
            {
                throw new ArgumentOutOfRangeException(nameof(bindingNamespace), bindingNamespace, "Physical binding namespace is not defined.");
            }

            bool validNamespace = backend switch
            {
                ShaderBackendKind.DirectX12 => bindingNamespace is ShaderPhysicalBindingNamespace.ShaderResource
                    or ShaderPhysicalBindingNamespace.Sampler
                    or ShaderPhysicalBindingNamespace.ConstantBuffer
                    or ShaderPhysicalBindingNamespace.UnorderedAccess,
                ShaderBackendKind.Vulkan => bindingNamespace == ShaderPhysicalBindingNamespace.Unified,
                ShaderBackendKind.Metal => bindingNamespace is ShaderPhysicalBindingNamespace.Buffer
                    or ShaderPhysicalBindingNamespace.Texture
                    or ShaderPhysicalBindingNamespace.Sampler,
                _ => false,
            };

            if (!validNamespace)
            {
                throw new ArgumentException(
                    $"Binding namespace {bindingNamespace} is not valid for backend {backend}.",
                    nameof(bindingNamespace));
            }

            Backend = backend;
            Group = group;
            Binding = binding;
            Namespace = bindingNamespace;
        }

        public bool Equals(ShaderPhysicalBindingLocation other)
        {
            return Backend == other.Backend
                && Group == other.Group
                && Binding == other.Binding
                && Namespace == other.Namespace;
        }

        public override bool Equals(object? obj) => obj is ShaderPhysicalBindingLocation other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Backend, Group, Binding, Namespace);
        public static bool operator ==(ShaderPhysicalBindingLocation left, ShaderPhysicalBindingLocation right) => left.Equals(right);
        public static bool operator !=(ShaderPhysicalBindingLocation left, ShaderPhysicalBindingLocation right) => !left.Equals(right);
    }
}
