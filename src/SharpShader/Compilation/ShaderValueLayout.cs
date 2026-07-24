using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{
    public enum ShaderScalarType
    {
        Boolean,
        Int8,
        UInt8,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Float16,
        Float32,
        Float64,
    }

    public enum ShaderValueKind
    {
        Scalar,
        Vector,
        Matrix,
        Struct,
    }

    public enum ShaderMatrixMajorOrder
    {
        RowMajor,
        ColumnMajor,
    }

    public sealed class ShaderValueMember : IEquatable<ShaderValueMember>
    {
        public string Name { get; }
        public uint ByteOffset { get; }
        public uint ByteSize { get; }
        public ShaderValueLayout Value { get; }

        public ShaderValueMember(string name, uint byteOffset, uint byteSize, ShaderValueLayout value)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Value member name must not be empty.", nameof(name));
            }

            if (byteSize == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(byteSize), "Value member byte size must be greater than zero.");
            }

            _ = checked(byteOffset + byteSize);
            ArgumentNullException.ThrowIfNull(value);

            Name = name;
            ByteOffset = byteOffset;
            ByteSize = byteSize;
            Value = value;
        }

        public bool Equals(ShaderValueMember? other)
        {
            return other is not null
                && string.Equals(Name, other.Name, StringComparison.Ordinal)
                && ByteOffset == other.ByteOffset
                && ByteSize == other.ByteSize
                && Value.Equals(other.Value);
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderValueMember);
        public override int GetHashCode() => HashCode.Combine(Name, ByteOffset, ByteSize, Value);
    }

    public sealed class ShaderValueLayout : IEquatable<ShaderValueLayout>
    {
        private readonly ReadOnlyCollection<ShaderValueMember> m_Members;

        public ShaderValueKind Kind { get; }
        public ShaderScalarType? ScalarType { get; }
        public uint Rows { get; }
        public uint Columns { get; }
        public ShaderArrayShape Array { get; }
        public uint? ByteSize { get; }
        public uint? ProvenArrayStride { get; }
        public uint? ProvenMatrixStride { get; }
        public ShaderMatrixMajorOrder? MatrixMajorOrder { get; }
        public IReadOnlyList<ShaderValueMember> Members => m_Members;

        public ShaderValueLayout(
            ShaderValueKind kind,
            ShaderScalarType? scalarType,
            uint rows,
            uint columns,
            ShaderArrayShape? array = null,
            uint? byteSize = null,
            uint? provenArrayStride = null,
            uint? provenMatrixStride = null,
            ShaderMatrixMajorOrder? matrixMajorOrder = null,
            IEnumerable<ShaderValueMember>? members = null)
        {
            if (!Enum.IsDefined(kind))
            {
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Shader value kind is not defined.");
            }

            if (scalarType.HasValue && !Enum.IsDefined(scalarType.Value))
            {
                throw new ArgumentOutOfRangeException(nameof(scalarType), scalarType, "Shader scalar type is not defined.");
            }

            if (matrixMajorOrder.HasValue && !Enum.IsDefined(matrixMajorOrder.Value))
            {
                throw new ArgumentOutOfRangeException(nameof(matrixMajorOrder), matrixMajorOrder, "Matrix major order is not defined.");
            }

            if (byteSize == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(byteSize), "A known byte size must be greater than zero.");
            }

            if (provenArrayStride == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(provenArrayStride), "A proven array stride must be greater than zero.");
            }

            if (provenMatrixStride == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(provenMatrixStride), "A proven matrix stride must be greater than zero.");
            }

            ShaderArrayShape effectiveArray = array ?? ShaderArrayShape.Scalar;
            ShaderValueMember[] memberCopy = members is null
                ? System.Array.Empty<ShaderValueMember>()
                : new List<ShaderValueMember>(members).ToArray();
            System.Array.Sort(memberCopy, CompareMembers);

            ValidateShape(
                kind,
                scalarType,
                rows,
                columns,
                effectiveArray,
                byteSize,
                provenArrayStride,
                provenMatrixStride,
                matrixMajorOrder,
                memberCopy);

            Kind = kind;
            ScalarType = scalarType;
            Rows = rows;
            Columns = columns;
            Array = effectiveArray;
            ByteSize = byteSize;
            ProvenArrayStride = provenArrayStride;
            ProvenMatrixStride = provenMatrixStride;
            MatrixMajorOrder = matrixMajorOrder;
            m_Members = System.Array.AsReadOnly(memberCopy);
        }

        private static int CompareMembers(ShaderValueMember? left, ShaderValueMember? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            int offset = left.ByteOffset.CompareTo(right.ByteOffset);
            return offset != 0 ? offset : string.CompareOrdinal(left.Name, right.Name);
        }

        private static void ValidateShape(
            ShaderValueKind kind,
            ShaderScalarType? scalarType,
            uint rows,
            uint columns,
            ShaderArrayShape array,
            uint? byteSize,
            uint? provenArrayStride,
            uint? provenMatrixStride,
            ShaderMatrixMajorOrder? matrixMajorOrder,
            ShaderValueMember[] members)
        {
            if (provenArrayStride.HasValue && !array.IsArray)
            {
                throw new ArgumentException("Array stride is only valid for an array value.", nameof(provenArrayStride));
            }

            if (array.HasRuntimeExtent && byteSize.HasValue)
            {
                throw new ArgumentException("A runtime-sized value cannot have a fixed byte size.", nameof(byteSize));
            }

            bool isStruct = kind == ShaderValueKind.Struct;
            if (isStruct)
            {
                if (scalarType.HasValue || rows != 0 || columns != 0 || matrixMajorOrder.HasValue || provenMatrixStride.HasValue)
                {
                    throw new ArgumentException("Struct values do not have scalar, row, column, or matrix metadata.");
                }

                if (members.Length == 0)
                {
                    throw new ArgumentException("Struct values must contain at least one member.", nameof(members));
                }
            }
            else
            {
                if (!scalarType.HasValue || members.Length != 0)
                {
                    throw new ArgumentException("Scalar, vector, and matrix values require a scalar type and cannot contain members.");
                }

                switch (kind)
                {
                    case ShaderValueKind.Scalar when rows != 1 || columns != 1:
                        throw new ArgumentException("Scalar values require a 1x1 shape.");
                    case ShaderValueKind.Vector when rows != 1 || columns is < 2 or > 4:
                        throw new ArgumentException("Vector values require one row and two to four columns.");
                    case ShaderValueKind.Matrix when rows is < 1 or > 4 || columns is < 1 or > 4:
                        throw new ArgumentException("Matrix values require one to four rows and columns.");
                }

                if (kind == ShaderValueKind.Matrix)
                {
                    if (!matrixMajorOrder.HasValue)
                    {
                        throw new ArgumentException("Matrix values require an explicit major order.", nameof(matrixMajorOrder));
                    }
                }
                else if (matrixMajorOrder.HasValue || provenMatrixStride.HasValue)
                {
                    throw new ArgumentException("Matrix metadata is only valid for matrix values.");
                }
            }

            HashSet<string> names = new(StringComparer.Ordinal);
            uint previousEnd = 0;
            foreach (ShaderValueMember member in members)
            {
                ArgumentNullException.ThrowIfNull(member);
                if (!names.Add(member.Name))
                {
                    throw new ArgumentException($"Struct contains duplicate member name {member.Name}.", nameof(members));
                }

                if (member.ByteOffset < previousEnd)
                {
                    throw new ArgumentException($"Struct member {member.Name} overlaps a previous member.", nameof(members));
                }

                uint memberEnd = checked(member.ByteOffset + member.ByteSize);
                if (byteSize.HasValue && memberEnd > byteSize.Value)
                {
                    throw new ArgumentException($"Struct member {member.Name} exceeds the declared byte size.", nameof(members));
                }

                previousEnd = memberEnd;
            }
        }

        internal void ValidateConstantBufferCompatible(string path)
        {
            if (Array.HasRuntimeExtent || Array.HasSpecializationConstantExtent)
            {
                throw new ArgumentException(
                    $"Constant-buffer value {path} cannot use runtime or specialization-constant array extents.",
                    nameof(path));
            }

            foreach (ShaderValueMember member in m_Members)
            {
                member.Value.ValidateConstantBufferCompatible($"{path}.{member.Name}");
            }
        }

        public bool Equals(ShaderValueLayout? other)
        {
            if (other is null
                || Kind != other.Kind
                || ScalarType != other.ScalarType
                || Rows != other.Rows
                || Columns != other.Columns
                || !Array.Equals(other.Array)
                || ByteSize != other.ByteSize
                || ProvenArrayStride != other.ProvenArrayStride
                || ProvenMatrixStride != other.ProvenMatrixStride
                || MatrixMajorOrder != other.MatrixMajorOrder
                || m_Members.Count != other.m_Members.Count)
            {
                return false;
            }

            for (int index = 0; index < m_Members.Count; ++index)
            {
                if (!m_Members[index].Equals(other.m_Members[index]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderValueLayout);

        public override int GetHashCode()
        {
            HashCode hash = new HashCode();
            hash.Add(Kind);
            hash.Add(ScalarType);
            hash.Add(Rows);
            hash.Add(Columns);
            hash.Add(Array);
            hash.Add(ByteSize);
            hash.Add(ProvenArrayStride);
            hash.Add(ProvenMatrixStride);
            hash.Add(MatrixMajorOrder);
            foreach (ShaderValueMember member in m_Members)
            {
                hash.Add(member);
            }

            return hash.ToHashCode();
        }
    }

    public sealed class ShaderConstantBufferLayout : IEquatable<ShaderConstantBufferLayout>
    {
        private readonly ReadOnlyCollection<ShaderValueMember> m_Variables;

        public string Name { get; }
        public uint ByteSize { get; }
        public IReadOnlyList<ShaderValueMember> Variables => m_Variables;

        public ShaderConstantBufferLayout(string name, uint byteSize, IEnumerable<ShaderValueMember> variables)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Constant-buffer name must not be empty.", nameof(name));
            }

            if (byteSize == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(byteSize), "Constant-buffer byte size must be greater than zero.");
            }

            ArgumentNullException.ThrowIfNull(variables);
            ShaderValueMember[] copy = new List<ShaderValueMember>(variables).ToArray();
            Array.Sort(copy, static (left, right) =>
            {
                int offset = left.ByteOffset.CompareTo(right.ByteOffset);
                return offset != 0 ? offset : string.CompareOrdinal(left.Name, right.Name);
            });

            if (copy.Length == 0)
            {
                throw new ArgumentException("Constant buffers must contain at least one reflected variable.", nameof(variables));
            }

            HashSet<string> names = new(StringComparer.Ordinal);
            uint previousEnd = 0;
            foreach (ShaderValueMember variable in copy)
            {
                ArgumentNullException.ThrowIfNull(variable);
                if (!names.Add(variable.Name))
                {
                    throw new ArgumentException($"Constant buffer contains duplicate variable {variable.Name}.", nameof(variables));
                }

                if (variable.ByteOffset < previousEnd)
                {
                    throw new ArgumentException($"Constant-buffer variable {variable.Name} overlaps a previous variable.", nameof(variables));
                }

                uint variableEnd = checked(variable.ByteOffset + variable.ByteSize);
                if (variableEnd > byteSize)
                {
                    throw new ArgumentException($"Constant-buffer variable {variable.Name} exceeds the buffer byte size.", nameof(variables));
                }

                variable.Value.ValidateConstantBufferCompatible($"{name}.{variable.Name}");
                previousEnd = variableEnd;
            }

            Name = name;
            ByteSize = byteSize;
            m_Variables = Array.AsReadOnly(copy);
        }

        public bool Equals(ShaderConstantBufferLayout? other)
        {
            if (other is null
                || !string.Equals(Name, other.Name, StringComparison.Ordinal)
                || ByteSize != other.ByteSize
                || m_Variables.Count != other.m_Variables.Count)
            {
                return false;
            }

            for (int index = 0; index < m_Variables.Count; ++index)
            {
                if (!m_Variables[index].Equals(other.m_Variables[index]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderConstantBufferLayout);

        public override int GetHashCode()
        {
            HashCode hash = new HashCode();
            hash.Add(Name, StringComparer.Ordinal);
            hash.Add(ByteSize);
            foreach (ShaderValueMember variable in m_Variables)
            {
                hash.Add(variable);
            }

            return hash.ToHashCode();
        }
    }
}
