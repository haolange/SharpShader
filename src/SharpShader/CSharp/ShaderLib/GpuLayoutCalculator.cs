using System;
using System.Collections.Generic;

namespace SharpShader.CSharp.ShaderLib
{
    public static class GpuLayoutCalculator
    {
        public static int AlignUp(int value, int alignment)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Value must be non-negative.");
            }

            if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(alignment),
                    alignment,
                    "Alignment must be a positive power of two.");
            }

            return (value + alignment - 1) & ~(alignment - 1);
        }

        public static GpuTypeShape Scalar(GpuScalarKind kind)
        {
            switch (kind)
            {
                case GpuScalarKind.Float:
                    return GpuTypeShape.Scalar("float", 4, 4);
                case GpuScalarKind.Int:
                    return GpuTypeShape.Scalar("int", 4, 4);
                case GpuScalarKind.UInt:
                    return GpuTypeShape.Scalar("uint", 4, 4);
                case GpuScalarKind.Bool:
                    return GpuTypeShape.Scalar("bool", 4, 4);
                case GpuScalarKind.Half:
                    return GpuTypeShape.Scalar("half", 2, 2);
                case GpuScalarKind.Double:
                    return GpuTypeShape.Scalar("double", 8, 8);
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, "Scalar kind is not defined.");
            }
        }

        public static GpuTypeShape Vector(GpuScalarKind kind, int count, GpuLayoutKind layout)
        {
            if (count < 1 || count > 4)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "Vector count must be in [1, 4].");
            }

            if (!Enum.IsDefined(typeof(GpuLayoutKind), layout))
            {
                throw new ArgumentOutOfRangeException(nameof(layout), layout, "Layout kind is not defined.");
            }

            GpuTypeShape element = Scalar(kind);
            int size = element.Size * count;
            int alignment;
            if (count <= 2)
            {
                alignment = size;
            }
            else if (layout == GpuLayoutKind.ConstantBuffer)
            {
                alignment = 16;
            }
            else
            {
                alignment = element.Alignment;
            }

            return GpuTypeShape.Scalar(element.Name + count, size, alignment);
        }

        public static GpuTypeShape Matrix(
            GpuScalarKind kind,
            int rows,
            int columns,
            GpuLayoutKind layout)
        {
            if (rows < 1 || rows > 4)
            {
                throw new ArgumentOutOfRangeException(nameof(rows), rows, "Matrix rows must be in [1, 4].");
            }

            if (columns < 1 || columns > 4)
            {
                throw new ArgumentOutOfRangeException(nameof(columns), columns, "Matrix columns must be in [1, 4].");
            }

            GpuTypeShape column = Vector(kind, rows, layout);
            int size = column.Size * columns;
            if (layout == GpuLayoutKind.ConstantBuffer)
            {
                size = AlignUp(column.Size, 16) * columns;
            }

            return GpuTypeShape.Scalar(
                Scalar(kind).Name + rows + "x" + columns,
                size,
                column.Alignment);
        }

        public static GpuTypeShape Structure(
            string name,
            IReadOnlyList<KeyValuePair<string, GpuTypeShape>> fields,
            GpuLayoutKind layout)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Structure name must not be empty.", nameof(name));
            }

            if (fields is null)
            {
                throw new ArgumentNullException(nameof(fields));
            }

            if (!Enum.IsDefined(typeof(GpuLayoutKind), layout))
            {
                throw new ArgumentOutOfRangeException(nameof(layout), layout, "Layout kind is not defined.");
            }

            List<GpuLayoutField> laidOut = new List<GpuLayoutField>(fields.Count);
            int offset = 0;
            int alignment = 1;
            for (int index = 0; index < fields.Count; ++index)
            {
                KeyValuePair<string, GpuTypeShape> field = fields[index];
                if (string.IsNullOrWhiteSpace(field.Key))
                {
                    throw new ArgumentException("Structure field name must not be empty.", nameof(fields));
                }

                if (field.Value is null)
                {
                    throw new ArgumentException("Structure field type must not be null.", nameof(fields));
                }

                int fieldAlign = field.Value.Alignment;
                if (layout == GpuLayoutKind.ConstantBuffer && fieldAlign > 16)
                {
                    fieldAlign = 16;
                }

                offset = AlignUp(offset, fieldAlign);
                laidOut.Add(new GpuLayoutField(field.Key, field.Value, offset));
                offset += field.Value.Size;
                if (fieldAlign > alignment)
                {
                    alignment = fieldAlign;
                }
            }

            int size = AlignUp(offset, alignment);
            if (layout == GpuLayoutKind.ConstantBuffer)
            {
                size = AlignUp(size, 16);
                if (alignment < 16 && laidOut.Count > 0)
                {
                    alignment = 16;
                }
            }

            return GpuTypeShape.Structure(name, size, alignment, laidOut);
        }

        public static int HostSequentialAlign(GpuScalarKind kind, int vectorCount)
        {
            if (vectorCount < 1 || vectorCount > 4)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(vectorCount),
                    vectorCount,
                    "Vector count must be in [1, 4].");
            }

            if (kind == GpuScalarKind.Bool)
            {
                return 1;
            }

            return Scalar(kind).Alignment;
        }

        public static int HostSequentialSize(GpuScalarKind kind, int vectorCount)
        {
            if (vectorCount < 1 || vectorCount > 4)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(vectorCount),
                    vectorCount,
                    "Vector count must be in [1, 4].");
            }

            if (kind == GpuScalarKind.Bool)
            {
                return vectorCount;
            }

            return Scalar(kind).Size * vectorCount;
        }
    }
}
