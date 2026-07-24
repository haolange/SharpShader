using System;
using System.Collections.Generic;
using System.Linq;
using SharpShader.Compilation;
using Xunit;

namespace Infinity.Rendering.Tests
{
    public sealed class ShaderBackendLayoutPlannerTests
    {
        [Fact]
        public void Plan_ShouldPreserveDx12IdentityAndPartitionSameSlotForVulkanAndMetal()
        {
            ShaderLogicalBinding texture = CreateTexture(
                table: 7,
                slot: 0,
                name: "Texture",
                ShaderResourceAccess.ReadOnly);
            ShaderLogicalBinding sampler = CreateSampler(
                table: 7,
                slot: 0,
                ShaderSamplerKind.Unknown);
            ShaderLogicalBinding constants = CreateConstantBuffer(table: 7, slot: 0);
            ShaderLogicalBinding writableTexture = CreateTexture(
                table: 7,
                slot: 0,
                name: "WritableTexture",
                ShaderResourceAccess.ReadWrite);
            ShaderInterfaceLayout layout = new(new[]
            {
                writableTexture,
                constants,
                sampler,
                texture,
            });

            ShaderBackendLayouts result = ShaderBackendLayoutPlanner.Plan(layout);

            Assert.Equal(
                new[]
                {
                    ShaderBindingClass.ShaderResource,
                    ShaderBindingClass.Sampler,
                    ShaderBindingClass.ConstantBuffer,
                    ShaderBindingClass.UnorderedAccess,
                },
                result.Dx12!.Bindings.Select(mapping => mapping.RegisterClass));
            Assert.All(result.Dx12.Bindings, mapping =>
            {
                Assert.Equal(mapping.LogicalBinding.Table, mapping.RegisterSpace);
                Assert.Equal(mapping.LogicalBinding.Slot, mapping.ShaderRegister);
                Assert.Equal(mapping.LogicalBinding.Type, mapping.RegisterClass);
            });

            Assert.Equal(new uint[] { 0, 1, 2, 3 }, result.Vulkan!.Bindings.Select(mapping => mapping.Binding));
            Assert.Equal(
                new[]
                {
                    VulkanDescriptorKind.SampledImage,
                    VulkanDescriptorKind.Sampler,
                    VulkanDescriptorKind.UniformBuffer,
                    VulkanDescriptorKind.StorageImage,
                },
                result.Vulkan.Bindings.Select(mapping => mapping.DescriptorKind));

            Assert.Empty(result.Metal!.ReferenceBufferBindings);
            Assert.Equal(
                new[]
                {
                    (ShaderPhysicalBindingNamespace.Texture, 0u),
                    (ShaderPhysicalBindingNamespace.Sampler, 0u),
                    (ShaderPhysicalBindingNamespace.Buffer, 0u),
                    (ShaderPhysicalBindingNamespace.Texture, 1u),
                },
                result.Metal.DirectBindings.Select(mapping => (mapping.Namespace, mapping.Index)));
            Assert.All(result.Metal.DirectBindings, mapping => Assert.Equal(MetalShaderBackendLayout.RootArgumentTable, mapping.ArgumentTable));
        }

        [Fact]
        public void Plan_ShouldMapArtifactNormalizedStorageBufferWithoutGuessing()
        {
            ShaderLogicalBinding storage = new(
                new ShaderBindingKey(1, 4, ShaderBindingClass.UnorderedAccess),
                "Storage",
                null,
                new ShaderResourceShape(
                    ShaderResourceKind.StorageBuffer,
                    ShaderResourceDimension.Buffer,
                    ShaderResourceAccess.ReadWrite,
                    structureStride: 16),
                ShaderStageMask.Compute);
            ShaderBackendLayouts result =
                ShaderBackendLayoutPlanner.Plan(new ShaderInterfaceLayout(new[] { storage }));

            VulkanShaderBindingMapping vulkan = Assert.Single(result.Vulkan!.Bindings);
            Assert.Equal(VulkanDescriptorKind.StorageBuffer, vulkan.DescriptorKind);
            MetalDirectBindingMapping metal = Assert.Single(result.Metal!.DirectBindings);
            Assert.Equal(ShaderPhysicalBindingNamespace.Buffer, metal.Namespace);
        }

        [Fact]
        public void Plan_ShouldUseOneMetalRootTableAndOneReferenceBufferPerLogicalTable()
        {
            ShaderInterfaceLayout layout = new(new[]
            {
                CreateConstantBuffer(table: 9, slot: 0),
                CreateSampler(table: 4, slot: 0),
                CreateTexture(table: 4, slot: 0, name: "Texture", ShaderResourceAccess.ReadOnly),
            });

            ShaderBackendLayouts result = ShaderBackendLayoutPlanner.Plan(layout);

            Assert.Empty(result.Metal!.DirectBindings);
            Assert.Equal(
                new[]
                {
                    (0u, 0UL),
                    (0u, 8UL),
                    (1u, 0UL),
                },
                result.Metal.ReferenceBufferBindings.Select(
                    mapping => (mapping.ReferenceBufferIndex, mapping.ByteOffset)));
            Assert.All(
                result.Metal.ReferenceBufferBindings,
                mapping => Assert.Equal(
                    MetalShaderBackendLayout.RootArgumentTable,
                    mapping.ArgumentTable));
            Assert.All(result.Metal.ReferenceBufferBindings, mapping => Assert.Equal(1u, mapping.ReferenceCount));

            Assert.Equal(new uint[] { 0, 1, 0 }, result.Vulkan!.Bindings.Select(mapping => mapping.Binding));
            Assert.Equal(new uint[] { 0, 0, 1 }, result.Vulkan.Bindings.Select(mapping => mapping.DescriptorSet));
        }

        [Fact]
        public void Plan_ShouldLayOutBoundedRuntimeAndSpecializationArraysContiguously()
        {
            ShaderLogicalBinding boundedTexture = CreateTexture(
                table: 3,
                slot: 0,
                name: "BoundedTextures",
                ShaderResourceAccess.ReadOnly,
                new ShaderArrayShape(new[] { ShaderArrayExtent.Bounded(2) }));
            ShaderLogicalBinding sampler = CreateSampler(table: 3, slot: 0);
            ShaderLogicalBinding runtimeStorage = new(
                new ShaderBindingKey(3, 1, ShaderBindingClass.UnorderedAccess),
                "RuntimeStorage",
                null,
                new ShaderResourceShape(
                    ShaderResourceKind.StorageBuffer,
                    ShaderResourceDimension.Buffer,
                    ShaderResourceAccess.ReadWrite,
                    new ShaderArrayShape(new[] { ShaderArrayExtent.Runtime() }),
                    structureStride: 16),
                ShaderStageMask.Compute);
            ShaderLogicalBinding specializedTexture = CreateTexture(
                table: 3,
                slot: 3,
                name: "SpecializedTextures",
                ShaderResourceAccess.ReadOnly,
                new ShaderArrayShape(new[] { ShaderArrayExtent.SpecializationConstant(11) }));
            ShaderInterfaceLayout layout = new(new[]
            {
                specializedTexture,
                runtimeStorage,
                sampler,
                boundedTexture,
            });
            Dictionary<ShaderBindingKey, uint> capacities = new()
            {
                [runtimeStorage.Key] = 4,
                [specializedTexture.Key] = 3,
            };

            ShaderBackendLayouts result = ShaderBackendLayoutPlanner.Plan(layout, capacities);

            Assert.Empty(result.Metal!.DirectBindings);
            Assert.Equal(
                new[]
                {
                    (0UL, 2u),
                    (16UL, 1u),
                    (24UL, 4u),
                    (56UL, 3u),
                },
                result.Metal.ReferenceBufferBindings.Select(
                    mapping => (mapping.ByteOffset, mapping.ReferenceCount)));
            Assert.Equal(new uint[] { 0, 1, 2, 3 }, result.Vulkan!.Bindings.Select(mapping => mapping.Binding));
        }

        [Fact]
        public void Plan_ShouldRejectMissingZeroUnknownAndBoundedCapacities()
        {
            ShaderLogicalBinding runtime = CreateTexture(
                table: 0,
                slot: 0,
                name: "RuntimeTextures",
                ShaderResourceAccess.ReadOnly,
                new ShaderArrayShape(new[] { ShaderArrayExtent.Runtime() }));
            ShaderInterfaceLayout runtimeLayout = new(new[] { runtime });

            Assert.Throws<ArgumentException>(() => ShaderBackendLayoutPlanner.Plan(runtimeLayout));
            Assert.Throws<ArgumentOutOfRangeException>(() => ShaderBackendLayoutPlanner.Plan(
                runtimeLayout,
                new Dictionary<ShaderBindingKey, uint> { [runtime.Key] = 0 }));
            Assert.Throws<ArgumentException>(() => ShaderBackendLayoutPlanner.Plan(
                runtimeLayout,
                new Dictionary<ShaderBindingKey, uint>
                {
                    [new ShaderBindingKey(8, 8, ShaderBindingClass.ShaderResource)] = 1,
                }));

            ShaderLogicalBinding bounded = CreateTexture(
                table: 0,
                slot: 0,
                name: "BoundedTextures",
                ShaderResourceAccess.ReadOnly,
                new ShaderArrayShape(new[] { ShaderArrayExtent.Bounded(2) }));
            Assert.Throws<ArgumentException>(() => ShaderBackendLayoutPlanner.Plan(
                new ShaderInterfaceLayout(new[] { bounded }),
                new Dictionary<ShaderBindingKey, uint> { [bounded.Key] = 2 }));
        }

        [Fact]
        public void Plan_ShouldBeDeterministicForReorderedLogicalInputs()
        {
            ShaderLogicalBinding texture = CreateTexture(
                table: 2,
                slot: 0,
                name: "Texture",
                ShaderResourceAccess.ReadOnly);
            ShaderLogicalBinding sampler = CreateSampler(table: 2, slot: 0);
            ShaderLogicalBinding constants = CreateConstantBuffer(table: 2, slot: 0);

            ShaderBackendLayouts forward = ShaderBackendLayoutPlanner.Plan(
                new ShaderInterfaceLayout(new[] { texture, sampler, constants }));
            ShaderBackendLayouts reverse = ShaderBackendLayoutPlanner.Plan(
                new ShaderInterfaceLayout(new[] { constants, sampler, texture }));

            Assert.Equal(forward, reverse);
        }

        [Fact]
        public void Plan_ShouldReturnCompleteEmptyBackendLayoutsForResourceFreeShader()
        {
            ShaderBackendLayouts result =
                ShaderBackendLayoutPlanner.Plan(new ShaderInterfaceLayout(Array.Empty<ShaderLogicalBinding>()));

            Assert.Empty(result.Dx12!.Bindings);
            Assert.Empty(result.Vulkan!.Bindings);
            Assert.Empty(result.Metal!.DirectBindings);
            Assert.Empty(result.Metal.ReferenceBufferBindings);
        }

        [Fact]
        public void MetalReferenceMapping_ShouldRejectByteRangeOverflow()
        {
            ShaderBindingKey key = new(0, 0, ShaderBindingClass.ShaderResource);

            Assert.Throws<OverflowException>(() => new MetalReferenceBufferBindingMapping(
                key,
                MetalShaderBackendLayout.RootArgumentTable,
                ShaderPhysicalBindingNamespace.Texture,
                referenceBufferIndex: 0,
                byteOffset: ulong.MaxValue - 7,
                referenceCount: 2));
        }

        private static ShaderLogicalBinding CreateTexture(
            uint table,
            uint slot,
            string name,
            ShaderResourceAccess access,
            ShaderArrayShape? array = null)
        {
            ShaderBindingClass bindingClass = access == ShaderResourceAccess.ReadOnly
                ? ShaderBindingClass.ShaderResource
                : ShaderBindingClass.UnorderedAccess;
            return new ShaderLogicalBinding(
                new ShaderBindingKey(table, slot, bindingClass),
                name,
                null,
                new ShaderResourceShape(
                    ShaderResourceKind.Texture,
                    ShaderResourceDimension.Texture2D,
                    access,
                    array),
                ShaderStageMask.Compute);
        }

        private static ShaderLogicalBinding CreateSampler(
            uint table,
            uint slot,
            ShaderSamplerKind samplerKind = ShaderSamplerKind.Regular)
        {
            return new ShaderLogicalBinding(
                new ShaderBindingKey(table, slot, ShaderBindingClass.Sampler),
                "Sampler",
                null,
                new ShaderResourceShape(
                    ShaderResourceKind.Sampler,
                    ShaderResourceDimension.Unknown,
                    ShaderResourceAccess.ReadOnly,
                    samplerKind: samplerKind),
                ShaderStageMask.Compute);
        }

        private static ShaderLogicalBinding CreateConstantBuffer(uint table, uint slot)
        {
            return new ShaderLogicalBinding(
                new ShaderBindingKey(table, slot, ShaderBindingClass.ConstantBuffer),
                "Constants",
                null,
                new ShaderResourceShape(
                    ShaderResourceKind.ConstantBuffer,
                    ShaderResourceDimension.Buffer,
                    ShaderResourceAccess.ReadOnly),
                ShaderStageMask.Compute);
        }
    }
}
