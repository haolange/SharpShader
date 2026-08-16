using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.Compilation
{

    public sealed class ShaderProgramCompilerOptions
    {
        public string? PersistentCacheDirectory { get; }
        public ShaderProgramCacheLimits CacheLimits { get; }

        public ShaderProgramCompilerOptions(
            string? persistentCacheDirectory = null,
            ShaderProgramCacheLimits? cacheLimits = null)
        {
            if (persistentCacheDirectory is not null
                && string.IsNullOrWhiteSpace(persistentCacheDirectory))
            {
                throw new ArgumentException(
                    "A persistent cache directory must be null or non-empty.",
                    nameof(persistentCacheDirectory));
            }

            PersistentCacheDirectory = persistentCacheDirectory is null
                ? null
                : System.IO.Path.GetFullPath(persistentCacheDirectory);
            CacheLimits = cacheLimits ?? ShaderProgramCacheLimits.Default;
        }
    }
}
