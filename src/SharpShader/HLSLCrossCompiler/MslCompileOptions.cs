namespace SharpShader.HLSLCrossCompiler
{
    public enum MslTargetPlatform
    {
        MacOS,
        IOS,
    }

    public sealed record MslCompileOptions
    {
        public static MslCompileOptions Default { get; } = new();

        public MslTargetPlatform Platform { get; init; } = MslTargetPlatform.MacOS;

        public uint MslVersion { get; init; }

        public bool EnableArgumentBuffers { get; init; }

        /// <summary>
        /// Argument buffer tier. 0 = Tier 1 (flat), 1 = Tier 2 (struct-per-descriptor-set).
        /// Tier 2 generates one struct per descriptor set, enabling per-table binding.
        /// Only effective when <see cref="EnableArgumentBuffers"/> is true.
        /// </summary>
        public uint ArgumentBuffersTier { get; init; }

        public bool ForceNativeArrays { get; init; }

        public bool PadFragmentOutputComponents { get; init; }

        public bool CaptureOutputToBuffer { get; init; }

        public bool EnablePointSizeBuiltin { get; init; }

        /// <summary>
        /// When true, SPIRV-Cross decorates each argument buffer struct with [[id(N)]] attributes
        /// allowing discrete resource updates without re-encoding the entire argument buffer.
        /// </summary>
        public bool EnableDecorateArgumentBufferIndex { get; init; }
    }
}
