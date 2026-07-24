using System;
using System.Collections.Generic;
using System.Linq;
using SharpGPU;
using SharpShader.Compilation;
using SharpShader.SharpGPU;
using Xunit;

namespace Infinity.Rendering.Tests
{
    public sealed class SharpGpuShaderInterfaceAdapterTests
    {
        [Fact]
        public void Dx12Plan_ShouldPreserveStructuredSameSlotIdentity()
        {
            ShaderInterfaceLayout layout = CreateSameSlotLayout(table: 7);
            ShaderBackendLayouts backends = ShaderBackendLayoutPlanner.Plan(layout);

            SharpGpuArgumentTableLayoutPlan plan =
                SharpGpuShaderInterfaceAdapter.CreateArgumentTableLayoutPlan(
                    layout,
                    backends,
                    ERHIBackend.DirectX12);
            RHIArgumentTableLayoutDescriptor descriptor =
                Assert.Single(plan.CreateArgumentTableLayoutDescriptors());

            Assert.Equal(7u, descriptor.Index);
            Assert.Equal(4, descriptor.Elements.Length);
            Assert.All(descriptor.Elements.ToArray(), element => Assert.Equal(0u, element.Slot));
            Assert.Equal(
                new[]
                {
                    ERHIBindType.Texture2D,
                    ERHIBindType.Sampler,
                    ERHIBindType.UniformBuffer,
                    ERHIBindType.StorageTexture2D,
                },
                descriptor.Elements.ToArray().Select(element => element.Type));

            SharpGpuBindingLocation texture =
                plan.GetBinding(new ShaderBindingKey(7, 0, ShaderBindingClass.ShaderResource));
            SharpGpuBindingLocation sampler =
                plan.GetBinding(new ShaderBindingKey(7, 0, ShaderBindingClass.Sampler));
            Assert.NotEqual(texture.BindType, sampler.BindType);
            Assert.Equal(texture.Slot, sampler.Slot);
        }

        [Fact]
        public void VulkanPlan_ShouldMapSparseLogicalTablesToDensePhysicalTables()
        {
            ShaderLogicalBinding texture = CreateTexture(
                table: 4,
                slot: 11,
                ShaderResourceAccess.ReadOnly);
            ShaderLogicalBinding sampler = CreateSampler(table: 4, slot: 11);
            ShaderLogicalBinding constants = CreateConstantBuffer(table: 9, slot: 3);
            ShaderInterfaceLayout layout = new(new[] { constants, sampler, texture });
            ShaderBackendLayouts backends = ShaderBackendLayoutPlanner.Plan(layout);

            SharpGpuArgumentTableLayoutPlan plan =
                SharpGpuShaderInterfaceAdapter.CreateArgumentTableLayoutPlan(
                    layout,
                    backends,
                    ERHIBackend.Vulkan);
            RHIArgumentTableLayoutDescriptor[] descriptors =
                plan.CreateArgumentTableLayoutDescriptors();

            Assert.Equal(new uint[] { 0, 1 }, descriptors.Select(descriptor => descriptor.Index));
            Assert.Equal(new uint[] { 0, 1 }, descriptors[0].Elements.ToArray().Select(element => element.Slot));
            Assert.Equal(0u, Assert.Single(descriptors[1].Elements.ToArray()).Slot);
            Assert.Equal(0u, plan.GetBinding(texture.Key).ArgumentTableIndex);
            Assert.Equal(1u, plan.GetBinding(sampler.Key).Slot);
            Assert.Equal(1u, plan.GetBinding(constants.Key).ArgumentTableIndex);
        }

        [Fact]
        public void MetalDirectPlan_ShouldUseDensePerNamespaceSlotsInRootTable()
        {
            ShaderInterfaceLayout layout = CreateSameSlotLayout(table: 12);
            ShaderBackendLayouts backends = ShaderBackendLayoutPlanner.Plan(layout);

            SharpGpuArgumentTableLayoutPlan plan =
                SharpGpuShaderInterfaceAdapter.CreateArgumentTableLayoutPlan(
                    layout,
                    backends,
                    ERHIBackend.Metal);
            RHIArgumentTableLayoutDescriptor descriptor =
                Assert.Single(plan.CreateArgumentTableLayoutDescriptors());

            Assert.Equal(0u, descriptor.Index);
            Assert.Equal(0u, plan.GetBinding(
                new ShaderBindingKey(12, 0, ShaderBindingClass.ShaderResource)).Slot);
            Assert.Equal(0u, plan.GetBinding(
                new ShaderBindingKey(12, 0, ShaderBindingClass.Sampler)).Slot);
            Assert.Equal(0u, plan.GetBinding(
                new ShaderBindingKey(12, 0, ShaderBindingClass.ConstantBuffer)).Slot);
            Assert.Equal(1u, plan.GetBinding(
                new ShaderBindingKey(12, 0, ShaderBindingClass.UnorderedAccess)).Slot);
        }

        [Fact]
        public void MetalReferencePlan_ShouldPackArraysByCompiledByteOffset()
        {
            ShaderLogicalBinding textures = CreateTexture(
                table: 4,
                slot: 8,
                ShaderResourceAccess.ReadOnly,
                new ShaderArrayShape(new[] { ShaderArrayExtent.Bounded(2) }));
            ShaderLogicalBinding sampler = CreateSampler(table: 4, slot: 8);
            ShaderLogicalBinding constants = CreateConstantBuffer(table: 9, slot: 0);
            ShaderInterfaceLayout layout = new(new[] { constants, sampler, textures });
            ShaderBackendLayouts backends = ShaderBackendLayoutPlanner.Plan(layout);

            SharpGpuArgumentTableLayoutPlan plan =
                SharpGpuShaderInterfaceAdapter.CreateArgumentTableLayoutPlan(
                    layout,
                    backends,
                    ERHIBackend.Metal);
            RHIArgumentTableLayoutDescriptor[] descriptors =
                plan.CreateArgumentTableLayoutDescriptors();

            Assert.Equal(new uint[] { 0, 1 }, descriptors.Select(descriptor => descriptor.Index));
            Assert.Equal(
                new[] { (0u, 2u), (2u, 1u) },
                descriptors[0].Elements.ToArray().Select(element => (element.Slot, element.Count)));
            Assert.Equal((0u, 1u), Assert.Single(descriptors[1].Elements.ToArray()) is var element
                ? (element.Slot, element.Count)
                : default);
            Assert.Equal(2u, plan.GetBinding(sampler.Key).Slot);
        }

        [Fact]
        public void RuntimeArray_ShouldRequireOrCrossCheckResolvedCapacity()
        {
            ShaderLogicalBinding runtimeTexture = CreateTexture(
                table: 2,
                slot: 0,
                ShaderResourceAccess.ReadOnly,
                new ShaderArrayShape(new[] { ShaderArrayExtent.Runtime() }));
            ShaderInterfaceLayout layout = new(new[] { runtimeTexture });
            Dictionary<ShaderBindingKey, uint> capacities = new()
            {
                [runtimeTexture.Key] = 4,
            };
            ShaderBackendLayouts planned = ShaderBackendLayoutPlanner.Plan(layout, capacities);
            ShaderBackendLayouts dx12Only = new(layout.Signature, dx12: planned.Dx12);

            Assert.Throws<ArgumentException>(() =>
                SharpGpuShaderInterfaceAdapter.CreateArgumentTableLayoutPlan(
                    layout,
                    dx12Only,
                    ERHIBackend.DirectX12));

            SharpGpuArgumentTableLayoutPlan plan =
                SharpGpuShaderInterfaceAdapter.CreateArgumentTableLayoutPlan(
                    layout,
                    dx12Only,
                    ERHIBackend.DirectX12,
                    capacities);
            Assert.Equal(4u, Assert.Single(plan.Bindings).Count);

            Assert.Throws<ArgumentException>(() =>
                SharpGpuShaderInterfaceAdapter.CreateArgumentTableLayoutPlan(
                    layout,
                    planned,
                    ERHIBackend.Vulkan,
                    new Dictionary<ShaderBindingKey, uint>
                    {
                        [runtimeTexture.Key] = 5,
                    }));
        }

        [Fact]
        public void Adapter_ShouldRejectUnsupportedShapeStageAndCount()
        {
            ShaderLogicalBinding texture1D = new(
                new ShaderBindingKey(0, 0, ShaderBindingClass.ShaderResource),
                "Texture1D",
                null,
                new ShaderResourceShape(
                    ShaderResourceKind.Texture,
                    ShaderResourceDimension.Texture1D,
                    ShaderResourceAccess.ReadOnly),
                ShaderStageMask.Compute);
            ShaderInterfaceLayout textureLayout = new(new[] { texture1D });
            Assert.Throws<NotSupportedException>(() =>
                SharpGpuShaderInterfaceAdapter.CreateArgumentTableLayoutPlan(
                    textureLayout,
                    ShaderBackendLayoutPlanner.Plan(textureLayout),
                    ERHIBackend.DirectX12));

            ShaderLogicalBinding typedBuffer = new(
                new ShaderBindingKey(0, 0, ShaderBindingClass.ShaderResource),
                "TypedBuffer",
                null,
                new ShaderResourceShape(
                    ShaderResourceKind.TypedBuffer,
                    ShaderResourceDimension.Buffer,
                    ShaderResourceAccess.ReadOnly),
                ShaderStageMask.Compute);
            ShaderInterfaceLayout typedLayout = new(new[] { typedBuffer });
            Assert.Throws<NotSupportedException>(() =>
                SharpGpuShaderInterfaceAdapter.CreateArgumentTableLayoutPlan(
                    typedLayout,
                    ShaderBackendLayoutPlanner.Plan(typedLayout),
                    ERHIBackend.Vulkan));

            ShaderLogicalBinding hullTexture = new(
                new ShaderBindingKey(0, 0, ShaderBindingClass.ShaderResource),
                "HullTexture",
                null,
                new ShaderResourceShape(
                    ShaderResourceKind.Texture,
                    ShaderResourceDimension.Texture2D,
                    ShaderResourceAccess.ReadOnly),
                ShaderStageMask.Hull);
            ShaderInterfaceLayout hullLayout = new(new[] { hullTexture });
            Assert.Throws<NotSupportedException>(() =>
                SharpGpuShaderInterfaceAdapter.CreateArgumentTableLayoutPlan(
                    hullLayout,
                    ShaderBackendLayoutPlanner.Plan(hullLayout),
                    ERHIBackend.DirectX12));

            ShaderLogicalBinding runtime = CreateTexture(
                table: 0,
                slot: 0,
                ShaderResourceAccess.ReadOnly,
                new ShaderArrayShape(new[] { ShaderArrayExtent.Runtime() }));
            ShaderInterfaceLayout runtimeLayout = new(new[] { runtime });
            Dictionary<ShaderBindingKey, uint> excessiveCapacity = new()
            {
                [runtime.Key] = uint.MaxValue,
            };
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                SharpGpuShaderInterfaceAdapter.CreateArgumentTableLayoutPlan(
                    runtimeLayout,
                    ShaderBackendLayoutPlanner.Plan(runtimeLayout, excessiveCapacity),
                    ERHIBackend.Metal,
                    excessiveCapacity));
        }

        [Fact]
        public void DescriptorCopies_ShouldNotMutateImmutablePlan()
        {
            ShaderInterfaceLayout layout = CreateSameSlotLayout(table: 0);
            SharpGpuArgumentTableLayoutPlan plan =
                SharpGpuShaderInterfaceAdapter.CreateArgumentTableLayoutPlan(
                    layout,
                    ShaderBackendLayoutPlanner.Plan(layout),
                    ERHIBackend.Vulkan);

            RHIArgumentTableLayoutDescriptor[] first = plan.CreateArgumentTableLayoutDescriptors();
            first[0].Index = 99;
            first[0].Elements.Span[0].Slot = 99;
            RHIArgumentTableLayoutDescriptor[] second = plan.CreateArgumentTableLayoutDescriptors();

            Assert.Equal(0u, second[0].Index);
            Assert.Equal(0u, second[0].Elements.Span[0].Slot);
        }

        [Fact]
        public void NativeLayoutCreation_ShouldRollbackAndDisposeIdempotently()
        {
            ShaderInterfaceLayout layout = new(new[]
            {
                CreateTexture(table: 4, slot: 0, ShaderResourceAccess.ReadOnly),
                CreateConstantBuffer(table: 9, slot: 0),
            });
            SharpGpuArgumentTableLayoutPlan plan =
                SharpGpuShaderInterfaceAdapter.CreateArgumentTableLayoutPlan(
                    layout,
                    ShaderBackendLayoutPlanner.Plan(layout),
                    ERHIBackend.Vulkan);
            TrackingArgumentTableLayout first = new();
            int invocation = 0;

            Assert.Throws<InvalidOperationException>(() =>
                plan.CreateArgumentTableLayouts(descriptor =>
                {
                    return invocation++ == 0
                        ? first
                        : throw new InvalidOperationException("synthetic failure");
                }));
            Assert.True(first.IsDisposed);
            Assert.Equal(1, first.ReleaseCount);

            TrackingArgumentTableLayout second = new();
            TrackingArgumentTableLayout third = new();
            invocation = 0;
            SharpGpuArgumentTableLayouts owner =
                plan.CreateArgumentTableLayouts(descriptor =>
                    invocation++ == 0 ? second : third);
            owner.Dispose();
            owner.Dispose();

            Assert.True(owner.IsDisposed);
            Assert.Equal(1, second.ReleaseCount);
            Assert.Equal(1, third.ReleaseCount);
        }

        [Fact]
        public void AssemblyDependencies_ShouldRemainOneWayLeafOnly()
        {
            string[] adapterReferences = typeof(SharpGpuShaderInterfaceAdapter)
                .Assembly
                .GetReferencedAssemblies()
                .Select(reference => reference.Name!)
                .ToArray();
            string[] shaderReferences = typeof(ShaderInterfaceLayout)
                .Assembly
                .GetReferencedAssemblies()
                .Select(reference => reference.Name!)
                .ToArray();
            string[] gpuReferences = typeof(RHIArgumentTableLayoutDescriptor)
                .Assembly
                .GetReferencedAssemblies()
                .Select(reference => reference.Name!)
                .ToArray();

            Assert.Contains("SharpShader", adapterReferences);
            Assert.Contains("SharpGPU", adapterReferences);
            Assert.DoesNotContain("SharpGPU", shaderReferences);
            Assert.DoesNotContain("SharpShader", gpuReferences);
        }

        private static ShaderInterfaceLayout CreateSameSlotLayout(uint table)
        {
            return new ShaderInterfaceLayout(new[]
            {
                CreateTexture(table, 0, ShaderResourceAccess.ReadOnly),
                CreateSampler(table, 0),
                CreateConstantBuffer(table, 0),
                CreateTexture(table, 0, ShaderResourceAccess.ReadWrite),
            });
        }

        private static ShaderLogicalBinding CreateTexture(
            uint table,
            uint slot,
            ShaderResourceAccess access,
            ShaderArrayShape? array = null)
        {
            return new ShaderLogicalBinding(
                new ShaderBindingKey(
                    table,
                    slot,
                    access == ShaderResourceAccess.ReadOnly
                        ? ShaderBindingClass.ShaderResource
                        : ShaderBindingClass.UnorderedAccess),
                access == ShaderResourceAccess.ReadOnly ? "Texture" : "WritableTexture",
                null,
                new ShaderResourceShape(
                    ShaderResourceKind.Texture,
                    ShaderResourceDimension.Texture2D,
                    access,
                    array),
                ShaderStageMask.Compute);
        }

        private static ShaderLogicalBinding CreateSampler(uint table, uint slot)
        {
            return new ShaderLogicalBinding(
                new ShaderBindingKey(table, slot, ShaderBindingClass.Sampler),
                "Sampler",
                null,
                new ShaderResourceShape(
                    ShaderResourceKind.Sampler,
                    ShaderResourceDimension.Unknown,
                    ShaderResourceAccess.ReadOnly,
                    samplerKind: ShaderSamplerKind.Regular),
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

        private sealed class TrackingArgumentTableLayout : RHIArgumentTableLayout
        {
            public int ReleaseCount { get; private set; }

            protected override void Release()
            {
                ++ReleaseCount;
            }
        }
    }
}
