using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using SharpShader.Compilation;

namespace SharpShader.ShaderLab
{

    public readonly struct ShaderVariantKey : IEquatable<ShaderVariantKey>
    {
        public IReadOnlyList<string> Keywords { get; }

        public ShaderVariantKey(IEnumerable<string> keywords)
        {
            List<string> ordered = keywords
                .Where(static keyword => !string.IsNullOrWhiteSpace(keyword))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static keyword => keyword, StringComparer.Ordinal)
                .ToList();
            Keywords = ordered;
        }

        public bool Equals(ShaderVariantKey other)
        {
            if (Keywords.Count != other.Keywords.Count)
            {
                return false;
            }

            for (int i = 0; i < Keywords.Count; ++i)
            {
                if (!string.Equals(Keywords[i], other.Keywords[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj)
        {
            return obj is ShaderVariantKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            HashCode hash = new HashCode();
            for (int i = 0; i < Keywords.Count; ++i)
            {
                hash.Add(Keywords[i], StringComparer.Ordinal);
            }
            return hash.ToHashCode();
        }

        public override string ToString()
        {
            return Keywords.Count == 0
                ? "<default>"
                : string.Join(';', Keywords);
        }

        public static bool operator ==(ShaderVariantKey left, ShaderVariantKey right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ShaderVariantKey left, ShaderVariantKey right)
        {
            return !left.Equals(right);
        }
    }
}
