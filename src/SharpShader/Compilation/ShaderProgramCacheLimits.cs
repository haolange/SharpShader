using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.Compilation
{

    public sealed class ShaderProgramCacheLimits
    {
        public static ShaderProgramCacheLimits Default { get; } = new();

        public long MaximumSourceBytes { get; }
        public int MaximumIncludeFileCount { get; }
        public long MaximumIncludeFileBytes { get; }
        public long MaximumIncludeTotalBytes { get; }
        public long MaximumCachePackageBytes { get; }
        public int MaximumMemoryCacheEntries { get; }
        public long MaximumMemoryCacheBytes { get; }
        public int MaximumPersistentCacheEntries { get; }
        public long MaximumPersistentCacheBytes { get; }
        public int MaximumQuarantineEntries { get; }
        public long MaximumQuarantineBytes { get; }

        public ShaderProgramCacheLimits(
            long maximumSourceBytes = 64L * 1024 * 1024,
            int maximumIncludeFileCount = 32768,
            long maximumIncludeFileBytes = 64L * 1024 * 1024,
            long maximumIncludeTotalBytes = 1024L * 1024 * 1024,
            long maximumCachePackageBytes = 1024L * 1024 * 1024,
            int maximumMemoryCacheEntries = 256,
            long maximumMemoryCacheBytes = 512L * 1024 * 1024,
            int maximumPersistentCacheEntries = 4096,
            long maximumPersistentCacheBytes = 8L * 1024 * 1024 * 1024,
            int maximumQuarantineEntries = 64,
            long maximumQuarantineBytes = 512L * 1024 * 1024)
        {
            if (maximumSourceBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumSourceBytes));
            }

            if (maximumIncludeFileCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumIncludeFileCount));
            }

            if (maximumIncludeFileBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumIncludeFileBytes));
            }

            if (maximumIncludeTotalBytes < maximumIncludeFileBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumIncludeTotalBytes),
                    "The include total-byte limit must be at least the per-file limit.");
            }

            if (maximumCachePackageBytes <= 0
                || maximumCachePackageBytes > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumCachePackageBytes));
            }

            if (maximumMemoryCacheEntries <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumMemoryCacheEntries));
            }

            if (maximumMemoryCacheBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumMemoryCacheBytes));
            }
            if (maximumPersistentCacheEntries <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumPersistentCacheEntries));
            }

            if (maximumPersistentCacheBytes < maximumCachePackageBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumPersistentCacheBytes),
                    "The persistent-cache byte limit must be at least the per-package limit.");
            }

            if (maximumQuarantineEntries < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumQuarantineEntries));
            }

            if (maximumQuarantineBytes < 0
                || maximumQuarantineBytes > maximumPersistentCacheBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumQuarantineBytes),
                    "The quarantine byte limit must be non-negative and no greater than the persistent-cache limit.");
            }

            MaximumSourceBytes = maximumSourceBytes;
            MaximumIncludeFileCount = maximumIncludeFileCount;
            MaximumIncludeFileBytes = maximumIncludeFileBytes;
            MaximumIncludeTotalBytes = maximumIncludeTotalBytes;
            MaximumCachePackageBytes = maximumCachePackageBytes;
            MaximumMemoryCacheEntries = maximumMemoryCacheEntries;
            MaximumMemoryCacheBytes = maximumMemoryCacheBytes;
            MaximumPersistentCacheEntries = maximumPersistentCacheEntries;
            MaximumPersistentCacheBytes = maximumPersistentCacheBytes;
            MaximumQuarantineEntries = maximumQuarantineEntries;
            MaximumQuarantineBytes = maximumQuarantineBytes;
        }
    }
}
