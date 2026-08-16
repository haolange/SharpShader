using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public sealed class ShaderResourceShape : IEquatable<ShaderResourceShape>
    {
        public ShaderResourceKind Kind { get; }
        public ShaderResourceDimension Dimension { get; }
        public ShaderResourceAccess Access { get; }
        public ShaderArrayShape Array { get; }
        public uint? StructureStride { get; }
        public ShaderSamplerKind? SamplerKind { get; }
        public ShaderCounterKind CounterKind { get; }

        public ShaderResourceShape(
            ShaderResourceKind kind,
            ShaderResourceDimension dimension,
            ShaderResourceAccess access,
            ShaderArrayShape? array = null,
            uint? structureStride = null,
            ShaderSamplerKind? samplerKind = null,
            ShaderCounterKind counterKind = ShaderCounterKind.None)
        {
            if (!Enum.IsDefined(kind))
            {
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Resource kind is not defined.");
            }

            if (!Enum.IsDefined(dimension))
            {
                throw new ArgumentOutOfRangeException(nameof(dimension), dimension, "Resource dimension is not defined.");
            }

            if (!Enum.IsDefined(access))
            {
                throw new ArgumentOutOfRangeException(nameof(access), access, "Resource access is not defined.");
            }

            if (samplerKind.HasValue && !Enum.IsDefined(samplerKind.Value))
            {
                throw new ArgumentOutOfRangeException(nameof(samplerKind), samplerKind, "Sampler kind is not defined.");
            }

            if (!Enum.IsDefined(counterKind))
            {
                throw new ArgumentOutOfRangeException(nameof(counterKind), counterKind, "Counter kind is not defined.");
            }

            ValidateShape(kind, dimension, access, structureStride, samplerKind, counterKind);

            Kind = kind;
            Dimension = dimension;
            Access = access;
            Array = array ?? ShaderArrayShape.Scalar;
            StructureStride = structureStride;
            SamplerKind = samplerKind;
            CounterKind = counterKind;
        }

        internal void ValidateBindingClass(ShaderBindingClass bindingClass)
        {
            ShaderBindingClass expected = Kind switch
            {
                ShaderResourceKind.ConstantBuffer => ShaderBindingClass.ConstantBuffer,
                ShaderResourceKind.Sampler => ShaderBindingClass.Sampler,
                _ when Access == ShaderResourceAccess.ReadOnly => ShaderBindingClass.ShaderResource,
                _ => ShaderBindingClass.UnorderedAccess,
            };

            if (bindingClass != expected)
            {
                throw new ArgumentException(
                    $"Resource kind {Kind} with {Access} access requires binding class {expected}, not {bindingClass}.",
                    nameof(bindingClass));
            }
        }

        private static void ValidateShape(
            ShaderResourceKind kind,
            ShaderResourceDimension dimension,
            ShaderResourceAccess access,
            uint? structureStride,
            ShaderSamplerKind? samplerKind,
            ShaderCounterKind counterKind)
        {
            bool isTextureDimension = dimension is not ShaderResourceDimension.Unknown and not ShaderResourceDimension.Buffer;

            if (kind == ShaderResourceKind.Sampler)
            {
                if (dimension != ShaderResourceDimension.Unknown || access != ShaderResourceAccess.ReadOnly || !samplerKind.HasValue)
                {
                    throw new ArgumentException("Sampler resources require unknown dimension, read-only access, and sampler metadata.");
                }
            }
            else if (samplerKind.HasValue)
            {
                throw new ArgumentException("Sampler metadata is only valid for sampler resources.", nameof(samplerKind));
            }

            if (kind == ShaderResourceKind.ConstantBuffer
                && (dimension != ShaderResourceDimension.Buffer || access != ShaderResourceAccess.ReadOnly))
            {
                throw new ArgumentException("Constant buffers require buffer dimension and read-only access.");
            }

            if (kind is ShaderResourceKind.TypedBuffer or ShaderResourceKind.StructuredBuffer or ShaderResourceKind.StorageBuffer
                or ShaderResourceKind.ByteAddressBuffer or ShaderResourceKind.AtomicCounter or ShaderResourceKind.ShaderRecordBuffer)
            {
                if (dimension != ShaderResourceDimension.Buffer)
                {
                    throw new ArgumentException($"{kind} requires buffer dimension.", nameof(dimension));
                }
            }

            if (kind is ShaderResourceKind.Texture or ShaderResourceKind.InputAttachment or ShaderResourceKind.FeedbackTexture)
            {
                if (!isTextureDimension)
                {
                    throw new ArgumentException($"{kind} requires a texture dimension.", nameof(dimension));
                }
            }

            if (kind == ShaderResourceKind.AccelerationStructure
                && (dimension != ShaderResourceDimension.Unknown || access != ShaderResourceAccess.ReadOnly))
            {
                throw new ArgumentException("Acceleration structures require unknown dimension and read-only access.");
            }

            if (kind == ShaderResourceKind.InputAttachment && access != ShaderResourceAccess.ReadOnly)
            {
                throw new ArgumentException("Input attachments require read-only access.", nameof(access));
            }

            if (kind == ShaderResourceKind.AtomicCounter && access != ShaderResourceAccess.ReadWrite)
            {
                throw new ArgumentException("Atomic counters require read-write access.", nameof(access));
            }

            if (kind == ShaderResourceKind.FeedbackTexture && access == ShaderResourceAccess.ReadOnly)
            {
                throw new ArgumentException("Feedback textures require writable access.", nameof(access));
            }

            if (kind == ShaderResourceKind.ShaderRecordBuffer && access != ShaderResourceAccess.ReadOnly)
            {
                throw new ArgumentException("Shader-record buffers require read-only access.", nameof(access));
            }

            if (kind is ShaderResourceKind.StructuredBuffer or ShaderResourceKind.StorageBuffer)
            {
                if (!structureStride.HasValue || structureStride.Value == 0)
                {
                    throw new ArgumentException($"{kind} requires a positive structure stride.", nameof(structureStride));
                }
            }
            else if (structureStride.HasValue)
            {
                throw new ArgumentException("Structure stride is only valid for structured buffers.", nameof(structureStride));
            }

            if (counterKind != ShaderCounterKind.None
                && (kind != ShaderResourceKind.StructuredBuffer || access == ShaderResourceAccess.ReadOnly))
            {
                throw new ArgumentException("Counter metadata is only valid for writable structured buffers.", nameof(counterKind));
            }
        }

        public bool Equals(ShaderResourceShape? other)
        {
            return other is not null
                && Kind == other.Kind
                && Dimension == other.Dimension
                && Access == other.Access
                && Array.Equals(other.Array)
                && StructureStride == other.StructureStride
                && SamplerKind == other.SamplerKind
                && CounterKind == other.CounterKind;
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderResourceShape);
        public override int GetHashCode() => HashCode.Combine(Kind, Dimension, Access, Array, StructureStride, SamplerKind, CounterKind);
    }
}
