using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using SharpShader.Compilation;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.ShaderLab
{
    public sealed class ShaderLabCompilerOptions
    {
        private readonly ReadOnlyCollection<string> m_IncludeDirectories;
        private readonly ReadOnlyCollection<ShaderDefine> m_GlobalDefines;
        private readonly ReadOnlyDictionary<ShaderBindingKey, uint> m_MetalArrayCapacities;

        public ShaderProgramTarget Targets { get; }
        public ShaderModelVersion ShaderModel { get; }
        public IReadOnlyList<string> IncludeDirectories => m_IncludeDirectories;
        public IReadOnlyList<ShaderDefine> GlobalDefines => m_GlobalDefines;
        public SpirvCompileOptions SpirvOptions { get; }
        public MslCompileOptions MslOptions { get; }
        public IReadOnlyDictionary<ShaderBindingKey, uint> MetalArrayCapacities =>
            m_MetalArrayCapacities;
        public bool Enable16BitTypes { get; }
        public bool EnableDebugInfo { get; }
        public bool DisableOptimizations { get; }
        public int OptimizationLevel { get; }
        public bool SkipValidation { get; }
        public bool TreatWarningsAsErrors { get; }

        public ShaderLabCompilerOptions(
            ShaderProgramTarget targets = ShaderProgramTarget.All,
            ShaderModelVersion? shaderModel = null,
            IEnumerable<string>? includeDirectories = null,
            IEnumerable<ShaderDefine>? globalDefines = null,
            SpirvCompileOptions? spirvOptions = null,
            MslCompileOptions? mslOptions = null,
            IReadOnlyDictionary<ShaderBindingKey, uint>? metalArrayCapacities = null,
            bool enable16BitTypes = true,
            bool enableDebugInfo = false,
            bool disableOptimizations = false,
            int optimizationLevel = 3,
            bool skipValidation = false,
            bool treatWarningsAsErrors = false)
        {
            if (targets == ShaderProgramTarget.None
                || (targets & ~ShaderProgramTarget.All) != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(targets),
                    targets,
                    "ShaderLab compilation requires at least one defined target.");
            }

            if (optimizationLevel is < 0 or > 3)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(optimizationLevel),
                    optimizationLevel,
                    "Optimization level must be in [0, 3].");
            }

            Targets = targets;
            ShaderModel = shaderModel ?? new ShaderModelVersion(6, 8);
            m_IncludeDirectories = Array.AsReadOnly(
                includeDirectories?.ToArray() ?? Array.Empty<string>());
            m_GlobalDefines = Array.AsReadOnly(
                globalDefines?.ToArray() ?? Array.Empty<ShaderDefine>());
            SpirvOptions = spirvOptions ?? SpirvCompileOptions.Default;
            MslOptions = mslOptions ?? MslCompileOptions.Default;
            m_MetalArrayCapacities = new ReadOnlyDictionary<ShaderBindingKey, uint>(
                metalArrayCapacities is null
                    ? new Dictionary<ShaderBindingKey, uint>()
                    : new Dictionary<ShaderBindingKey, uint>(metalArrayCapacities));
            Enable16BitTypes = enable16BitTypes;
            EnableDebugInfo = enableDebugInfo;
            DisableOptimizations = disableOptimizations;
            OptimizationLevel = optimizationLevel;
            SkipValidation = skipValidation;
            TreatWarningsAsErrors = treatWarningsAsErrors;
        }
    }
}
