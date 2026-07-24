using System;
using System.Linq;
using System.Collections.Generic;

namespace SharpShader.ShaderLab
{
    internal static class ShaderVariantEnumeration
    {
        public static List<ShaderVariantKey> Enumerate(IReadOnlyList<ShaderKeywordGroup> groups)
        {
            ArgumentNullException.ThrowIfNull(groups);

            List<ShaderVariantKey> variants = new List<ShaderVariantKey>();
            EnumerateRecursive(groups, 0, new List<string>(), variants);
            if (variants.Count == 0)
            {
                variants.Add(new ShaderVariantKey(Array.Empty<string>()));
            }

            return variants
                .Distinct()
                .OrderBy(static variant => variant.ToString(), StringComparer.Ordinal)
                .ToList();
        }

        private static void EnumerateRecursive(
            IReadOnlyList<ShaderKeywordGroup> groups,
            int groupIndex,
            List<string> selectedKeywords,
            List<ShaderVariantKey> destination)
        {
            if (groupIndex >= groups.Count)
            {
                destination.Add(new ShaderVariantKey(selectedKeywords));
                return;
            }

            ShaderKeywordGroup group = groups[groupIndex]
                ?? throw new ArgumentException(
                    "Shader keyword groups must not contain null entries.",
                    nameof(groups));
            if (group.Keywords.Count == 0)
            {
                EnumerateRecursive(groups, groupIndex + 1, selectedKeywords, destination);
                return;
            }

            foreach (string keyword in group.Keywords)
            {
                if (string.IsNullOrWhiteSpace(keyword))
                {
                    throw new ArgumentException(
                        "Shader keyword groups must not contain empty alternatives.",
                        nameof(groups));
                }

                bool enabled = !string.Equals(keyword, "_", StringComparison.Ordinal);
                if (enabled)
                {
                    selectedKeywords.Add(keyword);
                }

                EnumerateRecursive(groups, groupIndex + 1, selectedKeywords, destination);
                if (enabled)
                {
                    selectedKeywords.RemoveAt(selectedKeywords.Count - 1);
                }
            }
        }
    }

    public static class ShaderVariantSelector
    {
        public static ShaderVariantKey SelectExactMatch(
            IEnumerable<ShaderVariantKey> availableVariants,
            IEnumerable<string>? enabledKeywords)
        {
            ArgumentNullException.ThrowIfNull(availableVariants);

            ShaderVariantKey requested = new ShaderVariantKey(
                enabledKeywords ?? Array.Empty<string>());
            bool found = false;
            ShaderVariantKey selected = default;
            foreach (ShaderVariantKey candidate in availableVariants)
            {
                if (!candidate.Equals(requested))
                {
                    continue;
                }

                if (found)
                {
                    throw new InvalidOperationException(
                        $"Shader variant set contains duplicate key {requested}.");
                }

                found = true;
                selected = candidate;
            }

            if (found)
            {
                return selected;
            }

            throw new KeyNotFoundException(
                $"No shader variant exactly matches enabled keywords {requested}.");
        }
    }
}
