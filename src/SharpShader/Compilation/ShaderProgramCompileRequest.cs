using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.Compilation
{

    public sealed class ShaderProgramCompileRequest
    {
        private readonly ReadOnlyCollection<ShaderProgramEntry> m_Entries;
        private readonly ReadOnlyCollection<ShaderProgramVariant> m_Variants;
        private readonly ReadOnlyCollection<ShaderDefine> m_GlobalDefines;
        private readonly ReadOnlyCollection<ShaderAttachmentInterface> m_AttachmentInterfaces;
        private readonly ReadOnlyCollection<string> m_IncludeDirectories;
        private readonly ReadOnlyDictionary<ShaderBindingKey, uint> m_MetalArrayCapacities;

        public string Source { get; }
        public string SourceName { get; }
        /// <summary>Stable resource identity used by artifact caches, independent of checkout paths.</summary>
        public string SourceIdentity { get; }
        public IReadOnlyList<ShaderProgramEntry> Entries => m_Entries;
        public IReadOnlyList<ShaderProgramVariant> Variants => m_Variants;
        public IReadOnlyList<ShaderDefine> GlobalDefines => m_GlobalDefines;
        public IReadOnlyList<ShaderAttachmentInterface> AttachmentInterfaces => m_AttachmentInterfaces;
        public IReadOnlyList<string> IncludeDirectories => m_IncludeDirectories;
        public IReadOnlyDictionary<ShaderBindingKey, uint> MetalArrayCapacities => m_MetalArrayCapacities;
        public ShaderProgramTarget Targets { get; }
        public ShaderModelVersion ShaderModel { get; }
        public SpirvCompileOptions SpirvOptions { get; }
        public MslCompileOptions MslOptions { get; }
        public bool Enable16BitTypes { get; }
        public bool EnableDebugInfo { get; }
        public bool DisableOptimizations { get; }
        public int OptimizationLevel { get; }
        public bool SkipValidation { get; }
        public bool TreatWarningsAsErrors { get; }

        public ShaderProgramCompileRequest(
            string source,
            string sourceName,
            IEnumerable<ShaderProgramEntry> entries,
            IEnumerable<ShaderProgramVariant> variants,
            ShaderProgramTarget targets,
            ShaderModelVersion shaderModel,
            IEnumerable<ShaderDefine>? globalDefines = null,
            IEnumerable<string>? includeDirectories = null,
            SpirvCompileOptions? spirvOptions = null,
            MslCompileOptions? mslOptions = null,
            IReadOnlyDictionary<ShaderBindingKey, uint>? metalArrayCapacities = null,
            bool enable16BitTypes = true,
            bool enableDebugInfo = false,
            bool disableOptimizations = false,
            int optimizationLevel = 3,
            bool skipValidation = false,
            bool treatWarningsAsErrors = false,
            IEnumerable<ShaderAttachmentInterface>? attachmentInterfaces = null,
            string? sourceIdentity = null)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
            ArgumentNullException.ThrowIfNull(entries);
            ArgumentNullException.ThrowIfNull(variants);

            ShaderProgramEntry[] entryCopy = new List<ShaderProgramEntry>(entries).ToArray();
            Array.Sort(entryCopy, ShaderProgramModelValidation.CompareEntries);
            ShaderProgramModelValidation.ValidateEntries(entryCopy);

            ShaderProgramVariant[] variantCopy = new List<ShaderProgramVariant>(variants).ToArray();
            Array.Sort(variantCopy, static (left, right) =>
                string.CompareOrdinal(left?.Key, right?.Key));
            ShaderProgramModelValidation.ValidateVariants(variantCopy);

            ShaderDefine[] globalDefineCopy = globalDefines is null
                ? Array.Empty<ShaderDefine>()
                : new List<ShaderDefine>(globalDefines).ToArray();
            Array.Sort(globalDefineCopy, ShaderProgramModelValidation.CompareDefines);
            ShaderProgramModelValidation.ValidateDefines(globalDefineCopy, nameof(globalDefines));

            ShaderAttachmentInterface[] attachmentCopy = attachmentInterfaces is null
                ? Array.Empty<ShaderAttachmentInterface>()
                : new List<ShaderAttachmentInterface>(attachmentInterfaces).ToArray();
            Array.Sort(attachmentCopy, CompareAttachmentInterfaces);
            ValidateAttachmentInterfaces(
                attachmentCopy,
                entryCopy,
                variantCopy);
            string[] includeCopy = includeDirectories is null
                ? Array.Empty<string>()
                : new List<string>(includeDirectories).ToArray();
            Dictionary<ShaderBindingKey, uint> capacityCopy = metalArrayCapacities is null
                ? new Dictionary<ShaderBindingKey, uint>()
                : new Dictionary<ShaderBindingKey, uint>(metalArrayCapacities);

            Source = source;
            SourceName = sourceName;
            SourceIdentity = (sourceIdentity ?? (System.IO.Path.IsPathRooted(sourceName)
                ? System.IO.Path.GetFileName(sourceName)
                : sourceName)).Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(SourceIdentity) || SourceIdentity.StartsWith('/')
                || (SourceIdentity.Length >= 2 && char.IsAsciiLetter(SourceIdentity[0]) && SourceIdentity[1] == ':')
                || SourceIdentity.Any(char.IsControl))
            {
                throw new ArgumentException("Source identity must be a non-empty logical resource name.", nameof(sourceIdentity));
            }
            m_Entries = Array.AsReadOnly(entryCopy);
            m_Variants = Array.AsReadOnly(variantCopy);
            m_GlobalDefines = Array.AsReadOnly(globalDefineCopy);
            m_AttachmentInterfaces = Array.AsReadOnly(attachmentCopy);
            m_IncludeDirectories = Array.AsReadOnly(includeCopy);
            m_MetalArrayCapacities =
                new ReadOnlyDictionary<ShaderBindingKey, uint>(capacityCopy);
            Targets = targets;
            ShaderModel = shaderModel;
            SpirvCompileOptions requestedSpirv =
                spirvOptions ?? SpirvCompileOptions.Default;
            ArgumentNullException.ThrowIfNull(requestedSpirv.BindingShifts);
            ArgumentNullException.ThrowIfNull(requestedSpirv.AdditionalArguments);
            SpirvOptions = new SpirvCompileOptions
            {
                UseDxLayout = requestedSpirv.UseDxLayout,
                UseGlLayout = requestedSpirv.UseGlLayout,
                UseScalarLayout = requestedSpirv.UseScalarLayout,
                InvertY = requestedSpirv.InvertY,
                BindingShifts = Array.AsReadOnly(new List<SpirvBindingShift>(
                    requestedSpirv.BindingShifts).ToArray()),
                TargetEnvironment = requestedSpirv.TargetEnvironment,
                AdditionalArguments = Array.AsReadOnly(new List<string>(
                    requestedSpirv.AdditionalArguments).ToArray()),
            };
            MslCompileOptions requestedMsl = mslOptions ?? MslCompileOptions.Default;
            MslOptions = requestedMsl with
            {
                Platform = requestedMsl.Platform,
                MslVersion = requestedMsl.MslVersion,
            };
            Enable16BitTypes = enable16BitTypes;
            EnableDebugInfo = enableDebugInfo;
            DisableOptimizations = disableOptimizations;
            OptimizationLevel = optimizationLevel;
            SkipValidation = skipValidation;
            TreatWarningsAsErrors = treatWarningsAsErrors;
        }

        public ShaderAttachmentInterface GetAttachmentInterface(
            string variantKey,
            string entryPoint,
            ShaderExecutionStage stage)
        {
            ArgumentNullException.ThrowIfNull(variantKey);
            ArgumentNullException.ThrowIfNull(entryPoint);
            _ = ShaderStageMaskUtility.FromStage(stage);
            foreach (ShaderAttachmentInterface attachmentInterface in
                     m_AttachmentInterfaces)
            {
                if (attachmentInterface.Stage == stage
                    && string.Equals(
                        attachmentInterface.VariantKey,
                        variantKey,
                        StringComparison.Ordinal)
                    && string.Equals(
                        attachmentInterface.EntryPoint,
                        entryPoint,
                        StringComparison.Ordinal))
                {
                    return attachmentInterface;
                }
            }

            if (stage != ShaderExecutionStage.Pixel)
            {
                return new ShaderAttachmentInterface(
                    variantKey,
                    entryPoint,
                    stage);
            }

            throw new KeyNotFoundException(
                $"No attachment interface exists for {variantKey}/{entryPoint} ({stage}).");
        }

        private static int CompareAttachmentInterfaces(
            ShaderAttachmentInterface? left,
            ShaderAttachmentInterface? right)
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
            return stage != 0
                ? stage
                : string.CompareOrdinal(left.EntryPoint, right.EntryPoint);
        }

        private static void ValidateAttachmentInterfaces(
            ShaderAttachmentInterface[] attachmentInterfaces,
            ShaderProgramEntry[] entries,
            ShaderProgramVariant[] variants)
        {
            HashSet<(string Variant, string Entry, ShaderExecutionStage Stage)> expected =
                new();
            foreach (ShaderProgramVariant variant in variants)
            {
                foreach (ShaderProgramEntry entry in entries)
                {
                    if (entry.Stage == ShaderExecutionStage.Pixel)
                    {
                        expected.Add((variant.Key, entry.Name, entry.Stage));
                    }
                }
            }

            foreach (ShaderAttachmentInterface attachmentInterface in
                     attachmentInterfaces)
            {
                ArgumentNullException.ThrowIfNull(attachmentInterface);
                if (attachmentInterface.Stage != ShaderExecutionStage.Pixel)
                {
                    throw new ArgumentException(
                        "Only pixel shader entries accept an explicit attachment interface.",
                        nameof(attachmentInterfaces));
                }

                if (!expected.Remove((
                    attachmentInterface.VariantKey,
                    attachmentInterface.EntryPoint,
                    attachmentInterface.Stage)))
                {
                    throw new ArgumentException(
                        $"Attachment interface {attachmentInterface.VariantKey}/"
                        + $"{attachmentInterface.EntryPoint} ({attachmentInterface.Stage}) "
                        + "is duplicated or does not identify a requested shader entry.",
                        nameof(attachmentInterfaces));
                }
            }

            if (expected.Count != 0)
            {
                (string variant, string entry, ShaderExecutionStage stage) =
                    System.Linq.Enumerable.First(expected);
                throw new ArgumentException(
                    $"Pixel shader entry {variant}/{entry} ({stage}) requires an explicit "
                    + "ShaderAttachmentInterface compile input.",
                    nameof(attachmentInterfaces));
            }
        }
    }
}
