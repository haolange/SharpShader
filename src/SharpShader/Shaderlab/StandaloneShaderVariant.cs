using System;
using System.Linq;
using System.Collections.Generic;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.ShaderLab
{
    public sealed class StandaloneVariantCompiler
    {
        private readonly Dictionary<string, ShaderCompileResult> m_Cache = new(StringComparer.Ordinal);

        public ShaderCompileResult Compile(
            StandaloneShaderProgram program,
            StandaloneShaderEntry entry,
            ShaderVariantKey variant,
            ShaderTargetKind target,
            ShaderModelVersion shaderModel,
            IReadOnlyList<string>? includeDirs = null)
        {
            if (program == null) throw new ArgumentNullException(nameof(program));
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            string cacheKey = BuildCacheKey(program, entry, variant, target, shaderModel);
            if (m_Cache.TryGetValue(cacheKey, out ShaderCompileResult? cached))
            {
                return cached!;
            }

            IReadOnlyList<ShaderDefine> defines = variant.Keywords
                .Select(static keyword => new ShaderDefine(keyword, "1"))
                .ToArray();

            ShaderCompileRequest request;
            if (program.Kind == StandaloneShaderProgramKind.Compute)
            {
                request = new ShaderCompileRequest
                {
                    Source = program.Source,
                    SourceName = program.SourcePath,
                    EntryPoint = entry.EntryName,
                    Stage = ShaderStageKind.Compute,
                    ShaderModel = shaderModel,
                    Target = target,
                    IncludeDirs = includeDirs ?? Array.Empty<string>(),
                    Defines = defines,
                    Enable16BitTypes = true,
                    EnableDebugInfo = false,
                    DisableOptimizations = false,
                    OptimizationLevel = 3,
                };
            }
            else
            {
                string[] exports = program.Entries.Select(shaderEntry => shaderEntry.EntryName).Distinct(StringComparer.Ordinal).ToArray();
                request = new ShaderCompileRequest
                {
                    Source = program.Source,
                    SourceName = program.SourcePath,
                    EntryPoint = string.Empty,
                    Stage = ShaderStageKind.Library,
                    ShaderModel = shaderModel,
                    Target = target,
                    IncludeDirs = includeDirs ?? Array.Empty<string>(),
                    Defines = defines,
                    Exports = exports,
                    Enable16BitTypes = true,
                    EnableDebugInfo = false,
                    DisableOptimizations = false,
                    OptimizationLevel = 3,
                };
            }

            ShaderCompileResult result = SharpShader.HLSLCrossCompiler.HLSLCrossCompiler.Compile(request);
            m_Cache[cacheKey] = result;
            return result;
        }

        public Dictionary<ShaderVariantKey, Dictionary<string, ShaderCompileResult>> CompileAllVariants(
            StandaloneShaderProgram program,
            ShaderTargetKind target,
            ShaderModelVersion shaderModel,
            IReadOnlyList<string>? includeDirs = null)
        {
            if (program == null) throw new ArgumentNullException(nameof(program));

            Dictionary<ShaderVariantKey, Dictionary<string, ShaderCompileResult>> results = new();
            List<ShaderVariantKey> variants = program.EnumerateVariantKeys();
            for (int i = 0; i < variants.Count; ++i)
            {
                ShaderVariantKey variant = variants[i];
                Dictionary<string, ShaderCompileResult> entries = new(StringComparer.Ordinal);
                for (int j = 0; j < program.Entries.Count; ++j)
                {
                    StandaloneShaderEntry entry = program.Entries[j];
                    entries[entry.EntryName] = Compile(program, entry, variant, target, shaderModel, includeDirs);
                }

                results[variant] = entries;
            }

            return results;
        }

        private static string BuildCacheKey(
            StandaloneShaderProgram program,
            StandaloneShaderEntry entry,
            ShaderVariantKey variant,
            ShaderTargetKind target,
            ShaderModelVersion shaderModel)
        {
            return string.Join(
                '|',
                program.SourcePath,
                program.Kind.ToString(),
                entry.Stage.ToString(),
                entry.EntryName,
                target.ToString(),
                shaderModel.Major.ToString(),
                shaderModel.Minor.ToString(),
                variant.ToString());
        }
    }

    public static class ShaderVariantSelector
    {
        public static ShaderVariantKey SelectBestMatch(IEnumerable<ShaderVariantKey> availableVariants, IEnumerable<string> enabledKeywords)
        {
            if (availableVariants == null) throw new ArgumentNullException(nameof(availableVariants));

            HashSet<string> enabled = new HashSet<string>(enabledKeywords ?? Array.Empty<string>(), StringComparer.Ordinal);
            ShaderVariantKey? best = null;
            int bestScore = int.MinValue;

            foreach (ShaderVariantKey variant in availableVariants)
            {
                int matched = 0;
                bool containsUnknown = false;
                for (int i = 0; i < variant.Keywords.Count; ++i)
                {
                    if (enabled.Contains(variant.Keywords[i]))
                    {
                        matched++;
                    }
                    else
                    {
                        containsUnknown = true;
                        break;
                    }
                }

                if (containsUnknown)
                {
                    continue;
                }

                int score = matched;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = variant;
                }
            }

            if (best.HasValue)
            {
                return best.Value;
            }

            return new ShaderVariantKey(Array.Empty<string>());
        }
    }
}
