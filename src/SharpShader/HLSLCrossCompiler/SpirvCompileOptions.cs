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

        public IReadOnlyList<SpirvBindingShift> BindingShifts { get; init; } = Array.Empty<SpirvBindingShift>();

        public string? TargetEnvironment { get; init; }

        public IReadOnlyList<string> AdditionalArguments { get; init; } = Array.Empty<string>();
    }
}
