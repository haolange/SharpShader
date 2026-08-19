using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.Compilation;
using Rhi = global::SharpGPU;

namespace SharpShader.SharpGPU
{

    public sealed class SharpGpuBindingTableLayouts : IDisposable
    {
        private readonly ReadOnlyCollection<Rhi.RHIBindingTableLayout> m_Layouts;
        private bool m_IsDisposed;

        public IReadOnlyList<Rhi.RHIBindingTableLayout> Layouts => m_Layouts;
        public bool IsDisposed => m_IsDisposed;

        internal SharpGpuBindingTableLayouts(Rhi.RHIBindingTableLayout[] layouts)
        {
            m_Layouts = Array.AsReadOnly(layouts);
        }

        public void Dispose()
        {
            if (m_IsDisposed)
            {
                return;
            }

            for (int index = m_Layouts.Count - 1; index >= 0; --index)
            {
                m_Layouts[index].Dispose();
            }

            m_IsDisposed = true;
        }
    }
}
