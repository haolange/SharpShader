using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.Compilation
{

    internal static class ShaderProgramModelValidation
    {
        internal static int CompareDefines(ShaderDefine left, ShaderDefine right)
        {
            int name = string.CompareOrdinal(left.Name, right.Name);
            return name != 0 ? name : string.CompareOrdinal(left.Value, right.Value);
        }

        internal static int CompareEntries(ShaderProgramEntry? left, ShaderProgramEntry? right)
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

            int stage = left.Stage.CompareTo(right.Stage);
            return stage != 0 ? stage : string.CompareOrdinal(left.Name, right.Name);
        }

        internal static int CompareArtifacts(
            ShaderProgramArtifact? left,
            ShaderProgramArtifact? right)
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

            int variant = string.CompareOrdinal(left.VariantKey, right.VariantKey);
            if (variant != 0)
            {
                return variant;
            }

            int stage = left.Stage.CompareTo(right.Stage);
            if (stage != 0)
            {
                return stage;
            }

            int entry = string.CompareOrdinal(left.EntryPoint, right.EntryPoint);
            return entry != 0
                ? entry
                : left.Identity.ArtifactKind.CompareTo(right.Identity.ArtifactKind);
        }

        internal static void ValidateDefines(ShaderDefine[] defines, string parameterName)
        {
            for (int index = 0; index < defines.Length; ++index)
            {
                ShaderDefine define = defines[index];
                if (string.IsNullOrWhiteSpace(define.Name))
                {
                    throw new ArgumentException(
                        "Shader defines must not contain empty names.",
                        parameterName);
                }

                if (index > 0
                    && string.Equals(defines[index - 1].Name, define.Name, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Shader define {define.Name} is duplicated.",
                        parameterName);
                }
            }
        }

        internal static void ValidateEntries(ShaderProgramEntry[] entries)
        {
            if (entries.Length == 0)
            {
                throw new ArgumentException(
                    "Shader program compilation requires at least one entry point.",
                    nameof(entries));
            }

            for (int index = 0; index < entries.Length; ++index)
            {
                ArgumentNullException.ThrowIfNull(entries[index]);
                if (index > 0
                    && entries[index - 1].Stage == entries[index].Stage
                    && string.Equals(entries[index - 1].Name, entries[index].Name, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Shader program entry {entries[index].Name} ({entries[index].Stage}) is duplicated.",
                        nameof(entries));
                }
            }
        }

        internal static void ValidateVariants(ShaderProgramVariant[] variants)
        {
            if (variants.Length == 0)
            {
                throw new ArgumentException(
                    "Shader program compilation requires at least one variant.",
                    nameof(variants));
            }

            for (int index = 0; index < variants.Length; ++index)
            {
                ArgumentNullException.ThrowIfNull(variants[index]);
                if (index > 0
                    && string.Equals(variants[index - 1].Key, variants[index].Key, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Shader program variant {variants[index].Key} is duplicated.",
                        nameof(variants));
                }
            }
        }
    }
}
