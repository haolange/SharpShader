using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

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
}
