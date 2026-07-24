using System;

namespace SharpShader.Compilation.Internal
{
    internal static class ShaderBackendLayoutSemantics
    {
        public static VulkanDescriptorKind GetVulkanDescriptorKind(ShaderLogicalBinding binding)
        {
            ArgumentNullException.ThrowIfNull(binding);

            return binding.Shape.Kind switch
            {
                ShaderResourceKind.ConstantBuffer => VulkanDescriptorKind.UniformBuffer,
                ShaderResourceKind.Sampler => VulkanDescriptorKind.Sampler,
                ShaderResourceKind.Texture => binding.Shape.Access == ShaderResourceAccess.ReadOnly
                    ? VulkanDescriptorKind.SampledImage
                    : VulkanDescriptorKind.StorageImage,
                ShaderResourceKind.TypedBuffer => binding.Shape.Access == ShaderResourceAccess.ReadOnly
                    ? VulkanDescriptorKind.UniformTexelBuffer
                    : VulkanDescriptorKind.StorageTexelBuffer,
                ShaderResourceKind.AccelerationStructure => VulkanDescriptorKind.AccelerationStructure,
                ShaderResourceKind.InputAttachment => VulkanDescriptorKind.InputAttachment,
                ShaderResourceKind.FeedbackTexture => VulkanDescriptorKind.StorageImage,
                ShaderResourceKind.StructuredBuffer
                    or ShaderResourceKind.StorageBuffer
                    or ShaderResourceKind.ByteAddressBuffer
                    or ShaderResourceKind.AtomicCounter
                    or ShaderResourceKind.ShaderRecordBuffer => VulkanDescriptorKind.StorageBuffer,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(binding),
                    binding.Shape.Kind,
                    "No Vulkan descriptor mapping exists for the logical resource kind."),
            };
        }

        public static ShaderPhysicalBindingNamespace GetMetalNamespace(ShaderLogicalBinding binding)
        {
            ArgumentNullException.ThrowIfNull(binding);

            return binding.Shape.Kind switch
            {
                ShaderResourceKind.Sampler => ShaderPhysicalBindingNamespace.Sampler,
                ShaderResourceKind.Texture
                    or ShaderResourceKind.TypedBuffer
                    or ShaderResourceKind.InputAttachment
                    or ShaderResourceKind.FeedbackTexture => ShaderPhysicalBindingNamespace.Texture,
                ShaderResourceKind.ConstantBuffer
                    or ShaderResourceKind.StructuredBuffer
                    or ShaderResourceKind.StorageBuffer
                    or ShaderResourceKind.ByteAddressBuffer
                    or ShaderResourceKind.AccelerationStructure
                    or ShaderResourceKind.AtomicCounter
                    or ShaderResourceKind.ShaderRecordBuffer => ShaderPhysicalBindingNamespace.Buffer,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(binding),
                    binding.Shape.Kind,
                    "No Metal namespace mapping exists for the logical resource kind."),
            };
        }
    }
}
