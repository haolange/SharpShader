using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.Compilation;
using Rhi = global::SharpGPU;

namespace SharpShader.SharpGPU
{

    public readonly struct SharpGpuAttachmentCompatibilityFact :
        IEquatable<SharpGpuAttachmentCompatibilityFact>
    {
        public uint LogicalAttachmentId { get; }
        public uint? InputIndex { get; }
        public uint? OutputLocation { get; }
        public uint OutputIndex { get; }
        public uint OutputComponent { get; }
        public ShaderAttachmentAspect Aspect { get; }
        public ShaderAttachmentNumericClass NumericClass { get; }
        public ShaderAttachmentSampleMode SampleMode { get; }
        public ShaderAttachmentLayerMode LayerMode { get; }

        internal SharpGpuAttachmentCompatibilityFact(
            ShaderAttachmentDeclaration declaration)
        {
            ArgumentNullException.ThrowIfNull(declaration);
            LogicalAttachmentId = declaration.LogicalAttachmentId;
            InputIndex = declaration.InputIndex;
            OutputLocation = declaration.OutputLocation;
            OutputIndex = declaration.OutputIndex;
            OutputComponent = declaration.OutputComponent;
            Aspect = declaration.Aspect;
            NumericClass = declaration.NumericClass;
            SampleMode = declaration.SampleMode;
            LayerMode = declaration.LayerMode;
        }

        public bool Equals(SharpGpuAttachmentCompatibilityFact other)
        {
            return LogicalAttachmentId == other.LogicalAttachmentId
                && InputIndex == other.InputIndex
                && OutputLocation == other.OutputLocation
                && OutputIndex == other.OutputIndex
                && OutputComponent == other.OutputComponent
                && Aspect == other.Aspect
                && NumericClass == other.NumericClass
                && SampleMode == other.SampleMode
                && LayerMode == other.LayerMode;
        }

        public override bool Equals(object? obj) =>
            obj is SharpGpuAttachmentCompatibilityFact other && Equals(other);

        public override int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(LogicalAttachmentId);
            hash.Add(InputIndex);
            hash.Add(OutputLocation);
            hash.Add(OutputIndex);
            hash.Add(OutputComponent);
            hash.Add(Aspect);
            hash.Add(NumericClass);
            hash.Add(SampleMode);
            hash.Add(LayerMode);
            return hash.ToHashCode();
        }

        public static bool operator ==(
            SharpGpuAttachmentCompatibilityFact left,
            SharpGpuAttachmentCompatibilityFact right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(
            SharpGpuAttachmentCompatibilityFact left,
            SharpGpuAttachmentCompatibilityFact right)
        {
            return !left.Equals(right);
        }
    }
}
