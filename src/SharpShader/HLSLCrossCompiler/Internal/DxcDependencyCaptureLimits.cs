using System;

namespace SharpShader.HLSLCrossCompiler.Internal
{
    internal readonly record struct DxcDependencyCaptureLimits
    {
        public int MaximumFileCount { get; }
        public long MaximumFileBytes { get; }
        public long MaximumTotalBytes { get; }
        public long MaximumPreprocessedBytes { get; }

        public DxcDependencyCaptureLimits(
            int maximumFileCount,
            long maximumFileBytes,
            long maximumTotalBytes,
            long maximumPreprocessedBytes)
        {
            if (maximumFileCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumFileCount));
            }

            if (maximumFileBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
            }

            if (maximumTotalBytes < maximumFileBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumTotalBytes));
            }

            if (maximumPreprocessedBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumPreprocessedBytes));
            }

            MaximumFileCount = maximumFileCount;
            MaximumFileBytes = maximumFileBytes;
            MaximumTotalBytes = maximumTotalBytes;
            MaximumPreprocessedBytes = maximumPreprocessedBytes;
        }
    }
}
