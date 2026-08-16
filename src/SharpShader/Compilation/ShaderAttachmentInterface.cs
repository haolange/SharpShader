using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    /// <summary>
    /// Explicit, variant-qualified attachment ABI supplied to shader compilation.
    /// It is intentionally separate from ordinary descriptor-resource reflection.
    /// </summary>
    public sealed class ShaderAttachmentInterface : IEquatable<ShaderAttachmentInterface>
    {
        public const uint CurrentAbiRevision = 1;

        public uint AbiRevision { get; }
        public string VariantKey { get; }
        public string EntryPoint { get; }
        public ShaderExecutionStage Stage { get; }
        public ShaderAttachmentPhase? Phase { get; }

        public ShaderAttachmentInterface(
            string variantKey,
            string entryPoint,
            ShaderExecutionStage stage,
            ShaderAttachmentPhase? phase = null,
            uint abiRevision = CurrentAbiRevision)
        {
            if (abiRevision != CurrentAbiRevision)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(abiRevision),
                    abiRevision,
                    $"Attachment ABI revision must be {CurrentAbiRevision}.");
            }

            if (string.IsNullOrWhiteSpace(variantKey))
            {
                throw new ArgumentException(
                    "Attachment interface variant key must not be empty.",
                    nameof(variantKey));
            }

            if (string.IsNullOrWhiteSpace(entryPoint))
            {
                throw new ArgumentException(
                    "Attachment interface entry point must not be empty.",
                    nameof(entryPoint));
            }

            _ = ShaderStageMaskUtility.FromStage(stage);
            if (stage == ShaderExecutionStage.Pixel && phase is null)
            {
                throw new ArgumentNullException(
                    nameof(phase),
                    "A pixel shader entry requires one explicit raster phase.");
            }

            if (stage != ShaderExecutionStage.Pixel && phase is not null)
            {
                throw new ArgumentException(
                    "Only pixel shader entries may declare a raster attachment phase.",
                    nameof(phase));
            }

            AbiRevision = abiRevision;
            VariantKey = variantKey;
            EntryPoint = entryPoint;
            Stage = stage;
            Phase = phase;
        }

        public bool Equals(ShaderAttachmentInterface? other)
        {
            return other is not null
                && AbiRevision == other.AbiRevision
                && string.Equals(VariantKey, other.VariantKey, StringComparison.Ordinal)
                && string.Equals(EntryPoint, other.EntryPoint, StringComparison.Ordinal)
                && Stage == other.Stage
                && Equals(Phase, other.Phase);
        }

        public override bool Equals(object? obj) =>
            Equals(obj as ShaderAttachmentInterface);

        public override int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(AbiRevision);
            hash.Add(VariantKey, StringComparer.Ordinal);
            hash.Add(EntryPoint, StringComparer.Ordinal);
            hash.Add(Stage);
            hash.Add(Phase);
            return hash.ToHashCode();
        }
    }
}
