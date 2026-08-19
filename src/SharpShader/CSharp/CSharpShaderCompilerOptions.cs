using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.Compilation;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.CSharp
{
    public sealed class CSharpShaderCompilerOptions
    {
        private readonly ReadOnlyCollection<string> m_MetadataReferencePaths;
        private readonly ReadOnlyCollection<ShaderProgramVariant> m_Variants;

        public CSharpShaderCompilerOptions(
            ShaderProgramTarget targets = ShaderProgramTarget.All,
            ShaderModelVersion? shaderModel = null,
            IEnumerable<string>? metadataReferencePaths = null,
            IEnumerable<ShaderProgramVariant>? variants = null,
            bool enableDebugInfo = false,
            bool disableOptimizations = false,
            int optimizationLevel = 3)
        {
            if (targets == ShaderProgramTarget.None
                || (targets & ~ShaderProgramTarget.All) != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(targets),
                    targets,
                    "SharpSL compilation requires at least one defined target.");
            }

            if (optimizationLevel is < 0 or > 3)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(optimizationLevel),
                    optimizationLevel,
                    "Optimization level must be in [0, 3].");
            }

            Targets = targets;
            ShaderModel = shaderModel ?? new ShaderModelVersion(6, 6);
            m_MetadataReferencePaths = Array.AsReadOnly(
                metadataReferencePaths is null
                    ? Array.Empty<string>()
                    : new List<string>(metadataReferencePaths).ToArray());
            m_Variants = Array.AsReadOnly(
                variants is null
                    ? new[] { ShaderProgramVariant.Default }
                    : new List<ShaderProgramVariant>(variants).ToArray());
            EnableDebugInfo = enableDebugInfo;
            DisableOptimizations = disableOptimizations;
            OptimizationLevel = optimizationLevel;
        }

        public ShaderProgramTarget Targets { get; }
        public ShaderModelVersion ShaderModel { get; }
        public IReadOnlyList<string> MetadataReferencePaths => m_MetadataReferencePaths;
        public IReadOnlyList<ShaderProgramVariant> Variants => m_Variants;
        public bool EnableDebugInfo { get; }
        public bool DisableOptimizations { get; }
        public int OptimizationLevel { get; }
    }
}
