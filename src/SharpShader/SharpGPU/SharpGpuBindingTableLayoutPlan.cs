using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.Compilation;
using Rhi = global::SharpGPU;

namespace SharpShader.SharpGPU
{

    public sealed class SharpGpuBindingTableLayoutPlan
    {
        private readonly ReadOnlyCollection<SharpGpuBindingLocation> m_Bindings;
        private readonly Dictionary<ShaderBindingKey, SharpGpuBindingLocation> m_BindingsByLogicalKey;
        private readonly PhysicalTable[] m_Tables;

        public Rhi.ERHIBackend Backend { get; }
        public ShaderLayoutSignature LogicalLayoutSignature { get; }
        public IReadOnlyList<SharpGpuBindingLocation> Bindings => m_Bindings;
        public int BindingTableCount => m_Tables.Length;

        internal SharpGpuBindingTableLayoutPlan(
            Rhi.ERHIBackend backend,
            ShaderLayoutSignature logicalLayoutSignature,
            SharpGpuBindingLocation[] bindings,
            PhysicalTable[] tables)
        {
            Backend = backend;
            LogicalLayoutSignature = logicalLayoutSignature;
            m_Bindings = Array.AsReadOnly(bindings);
            m_Tables = tables;
            m_BindingsByLogicalKey = new Dictionary<ShaderBindingKey, SharpGpuBindingLocation>(bindings.Length);
            foreach (SharpGpuBindingLocation binding in bindings)
            {
                if (!m_BindingsByLogicalKey.TryAdd(binding.LogicalBinding, binding))
                {
                    throw new InvalidOperationException(
                        $"The SharpGPU plan contains duplicate logical binding {binding.LogicalBinding}.");
                }
            }
        }

        public SharpGpuBindingLocation GetBinding(ShaderBindingKey logicalBinding)
        {
            return m_BindingsByLogicalKey.TryGetValue(logicalBinding, out SharpGpuBindingLocation binding)
                ? binding
                : throw new KeyNotFoundException(
                    $"The SharpGPU {Backend} layout does not contain logical binding {logicalBinding}.");
        }

        public bool TryGetBinding(
            ShaderBindingKey logicalBinding,
            out SharpGpuBindingLocation binding)
        {
            return m_BindingsByLogicalKey.TryGetValue(logicalBinding, out binding);
        }

        public Rhi.RHIBindingTableLayoutDescriptor[] CreateBindingTableLayoutDescriptors()
        {
            Rhi.RHIBindingTableLayoutDescriptor[] descriptors =
                new Rhi.RHIBindingTableLayoutDescriptor[m_Tables.Length];
            for (int tableIndex = 0; tableIndex < m_Tables.Length; ++tableIndex)
            {
                PhysicalTable table = m_Tables[tableIndex];
                Rhi.RHIBindingTableLayoutElement[] elements =
                    new Rhi.RHIBindingTableLayoutElement[table.Elements.Length];
                Array.Copy(table.Elements, elements, elements.Length);
                descriptors[tableIndex] = new Rhi.RHIBindingTableLayoutDescriptor
                {
                    Index = table.Index,
                    Elements = elements,
                };
            }

            return descriptors;
        }

        public SharpGpuBindingTableLayouts CreateBindingTableLayouts(Rhi.RHIDevice device)
        {
            ArgumentNullException.ThrowIfNull(device);
            if (device.BackendType != Backend)
            {
                throw new ArgumentException(
                    $"Cannot create a {Backend} SharpGPU layout plan on a {device.BackendType} device.",
                    nameof(device));
            }

            return CreateBindingTableLayouts(
                descriptor => device.CreateBindingTableLayout(descriptor));
        }

        internal SharpGpuBindingTableLayouts CreateBindingTableLayouts(
            Func<Rhi.RHIBindingTableLayoutDescriptor, Rhi.RHIBindingTableLayout> factory)
        {
            ArgumentNullException.ThrowIfNull(factory);

            Rhi.RHIBindingTableLayoutDescriptor[] descriptors =
                CreateBindingTableLayoutDescriptors();
            List<Rhi.RHIBindingTableLayout> layouts = new(descriptors.Length);
            try
            {
                foreach (Rhi.RHIBindingTableLayoutDescriptor descriptor in descriptors)
                {
                    Rhi.RHIBindingTableLayout layout = factory(descriptor)
                        ?? throw new InvalidOperationException(
                            $"SharpGPU returned a null argument-table layout for physical table {descriptor.Index}.");
                    layouts.Add(layout);
                }

                return new SharpGpuBindingTableLayouts(layouts.ToArray());
            }
            catch (Exception creationError)
            {
                List<Exception>? failures = null;
                for (int index = layouts.Count - 1; index >= 0; --index)
                {
                    try
                    {
                        layouts[index].Dispose();
                    }
                    catch (Exception releaseError)
                    {
                        failures ??= new() { creationError };
                        failures.Add(releaseError);
                    }
                }

                if (failures is not null)
                {
                    throw new AggregateException("Binding layout creation and rollback failed.", failures);
                }
                throw;
            }
        }

        internal sealed class PhysicalTable
        {
            public uint Index { get; }
            public Rhi.RHIBindingTableLayoutElement[] Elements { get; }

            public PhysicalTable(uint index, Rhi.RHIBindingTableLayoutElement[] elements)
            {
                Index = index;
                Elements = elements;
            }
        }
    }
}
