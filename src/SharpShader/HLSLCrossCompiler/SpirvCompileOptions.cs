using System;
using System.Collections.Generic;

namespace SharpShader.HLSLCrossCompiler
{
    public sealed record SpirvCompileOptions
    {
        public static SpirvCompileOptions Default { get; } = new();

        public bool UseDxLayout { get; init; } = true;

        public bool UseGlLayout { get; init; }

        public bool UseScalarLayout { get; init; }

        public bool InvertY { get; init; }

        public int? TextureBindingShift { get; init; }

        public uint TextureBindingSpace { get; init; }

        public int? SamplerBindingShift { get; init; }

        public uint SamplerBindingSpace { get; init; }

        public int? UavBindingShift { get; init; }

        public uint UavBindingSpace { get; init; }

        public int? CBufferBindingShift { get; init; }

        public uint CBufferBindingSpace { get; init; }

        public string? TargetEnvironment { get; init; }

        public IReadOnlyList<string> AdditionalArguments { get; init; } = Array.Empty<string>();
    }
}
