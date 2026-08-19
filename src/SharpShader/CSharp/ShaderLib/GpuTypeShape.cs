using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.CSharp.ShaderLib
{
    public sealed class GpuTypeShape
    {
        private readonly ReadOnlyCollection<GpuLayoutField> m_Fields;

        private GpuTypeShape(
            string name,
            int size,
            int alignment,
            IList<GpuLayoutField> fields)
        {
            Name = name;
            Size = size;
            Alignment = alignment;
            m_Fields = new ReadOnlyCollection<GpuLayoutField>(fields);
        }

        public string Name { get; }
        public int Size { get; }
        public int Alignment { get; }
        public IReadOnlyList<GpuLayoutField> Fields => m_Fields;

        public static GpuTypeShape Scalar(string name, int size, int alignment)
        {
            ValidateMetrics(name, size, alignment);
            return new GpuTypeShape(name, size, alignment, Array.Empty<GpuLayoutField>());
        }

        public static GpuTypeShape Structure(
            string name,
            int size,
            int alignment,
            IReadOnlyList<GpuLayoutField> fields)
        {
            ValidateMetrics(name, size, alignment);
            if (fields is null)
            {
                throw new ArgumentNullException(nameof(fields));
            }

            GpuLayoutField[] copy = new GpuLayoutField[fields.Count];
            for (int index = 0; index < fields.Count; ++index)
            {
                copy[index] = fields[index] ?? throw new ArgumentException(
                    "Structure fields must not contain null.",
                    nameof(fields));
            }

            return new GpuTypeShape(name, size, alignment, copy);
        }

        private static void ValidateMetrics(string name, int size, int alignment)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Type name must not be empty.", nameof(name));
            }

            if (size < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(size), size, "Size must be non-negative.");
            }

            if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(alignment),
                    alignment,
                    "Alignment must be a positive power of two.");
            }
        }
    }
}
