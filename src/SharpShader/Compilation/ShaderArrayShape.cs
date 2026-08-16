using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public sealed class ShaderArrayShape : IEquatable<ShaderArrayShape>
    {
        private readonly ReadOnlyCollection<ShaderArrayExtent> m_Extents;

        public static ShaderArrayShape Scalar { get; } = new ShaderArrayShape(Array.Empty<ShaderArrayExtent>());

        public IReadOnlyList<ShaderArrayExtent> Extents => m_Extents;
        public bool IsArray => m_Extents.Count != 0;
        public bool HasRuntimeExtent { get; }
        public bool HasSpecializationConstantExtent { get; }
        public uint? BoundedElementCount { get; }

        public ShaderArrayShape(IEnumerable<ShaderArrayExtent> extents)
        {
            ArgumentNullException.ThrowIfNull(extents);

            ShaderArrayExtent[] copy = extents is ShaderArrayExtent[] array
                ? (ShaderArrayExtent[])array.Clone()
                : new List<ShaderArrayExtent>(extents).ToArray();

            bool hasRuntime = false;
            bool hasSpecializationConstant = false;
            uint boundedCount = 1;
            for (int index = 0; index < copy.Length; ++index)
            {
                ShaderArrayExtent extent = copy[index];
                if (!Enum.IsDefined(extent.Kind))
                {
                    throw new ArgumentException($"Array extent {index} has an undefined kind.", nameof(extents));
                }

                switch (extent.Kind)
                {
                    case ShaderArrayExtentKind.Bounded:
                        if (extent.Value == 0)
                        {
                            throw new ArgumentException($"Array extent {index} has a zero bounded count.", nameof(extents));
                        }

                        boundedCount = checked(boundedCount * extent.Value);
                        break;
                    case ShaderArrayExtentKind.Runtime:
                        if (hasRuntime || index != copy.Length - 1)
                        {
                            throw new ArgumentException("A runtime extent may appear at most once and must be the final array dimension.", nameof(extents));
                        }

                        hasRuntime = true;
                        break;
                    case ShaderArrayExtentKind.SpecializationConstant:
                        hasSpecializationConstant = true;
                        break;
                    default:
                        throw new ArgumentException($"Array extent {index} has an unsupported kind.", nameof(extents));
                }
            }

            m_Extents = Array.AsReadOnly(copy);
            HasRuntimeExtent = hasRuntime;
            HasSpecializationConstantExtent = hasSpecializationConstant;
            BoundedElementCount = hasRuntime || hasSpecializationConstant ? null : boundedCount;
        }

        public bool Equals(ShaderArrayShape? other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            if (other is null || m_Extents.Count != other.m_Extents.Count)
            {
                return false;
            }

            for (int index = 0; index < m_Extents.Count; ++index)
            {
                if (m_Extents[index] != other.m_Extents[index])
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as ShaderArrayShape);

        public override int GetHashCode()
        {
            HashCode hash = new HashCode();
            foreach (ShaderArrayExtent extent in m_Extents)
            {
                hash.Add(extent);
            }

            return hash.ToHashCode();
        }
    }
}
