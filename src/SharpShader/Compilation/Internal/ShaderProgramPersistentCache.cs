using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpShader.Compilation.Internal
{
    internal sealed partial class ShaderProgramPersistentCache
    {
        private const uint CurrentSchemaVersion = 3;
        private const uint DependencySchemaVersion = 3;
        private const string FileExtension = ".sharpshader-cache-r3.json";
        private const string DependencyFileExtension =
            ".sharpshader-dependencies-r3.json";
        private const int MaximumPublishAttempts = 4;
        private const string GlobalLockFileName = ".sharpshader-cache-r3.lock";
        private const int MaximumLockAttempts = 500;
        private const int LockRetryMilliseconds = 10;
        private const string TemporaryFileSuffix = ".tmp";

        private static readonly UTF8Encoding s_StrictUtf8 = new(false, true);
        private static readonly JsonSerializerOptions s_JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
        };

        private readonly string m_Directory;
        private readonly ShaderProgramCacheLimits m_Limits;

        public ShaderProgramPersistentCache(
            string directory,
            ShaderProgramCacheLimits limits)
        {
            m_Directory = Path.GetFullPath(directory);
            m_Limits = limits;
        }

        public bool TryLoadPair(
            string provisionalKey,
            out ShaderProgramDependencySnapshot? dependencies,
            out ShaderProgramCompilation? compilation)
        {
            Directory.CreateDirectory(m_Directory);
            using FileStream cacheLock = AcquireGlobalLock();
            CleanupAbandonedWorkingFiles();
            string dependencyPath = GetDependencyPath(provisionalKey);
            dependencies = null;
            compilation = null;
            if (!File.Exists(dependencyPath))
            {
                EnforceRetention();
                return false;
            }

            try
            {
                dependencies = DecodeDependencies(
                    provisionalKey,
                    ReadBounded(dependencyPath));
            }
            catch (Exception exception) when (IsCorruption(exception))
            {
                QuarantineDependencyIndex(dependencyPath);
                EnforceRetention();
                dependencies = null;
                return false;
            }

            string artifactPath = GetEntryPath(dependencies.FinalKey);
            if (!TryLoadCore(dependencies.FinalKey, out compilation))
            {
                // An index is only useful as an atomic pair with its final
                // artifact. Remove the stale discoverability record while
                // holding the cross-process cache lock.
                File.Delete(dependencyPath);
                EnforceRetention();
                dependencies = null;
                compilation = null;
                return false;
            }

            EnforceRetention(dependencyPath, artifactPath);
            return true;
        }

        public void Store(
            ShaderProgramCompilation compilation,
            ShaderProgramDependencySnapshot dependencies)
        {
            ArgumentNullException.ThrowIfNull(compilation);
            ArgumentNullException.ThrowIfNull(dependencies);
            if (!dependencies.IsWarmable
                || !string.Equals(
                    compilation.CacheKey,
                    dependencies.FinalKey,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Only a warmable dependency snapshot matching the artifact "
                    + "final key can be published.",
                    nameof(dependencies));
            }

            Directory.CreateDirectory(m_Directory);
            using FileStream cacheLock = AcquireGlobalLock();
            CleanupAbandonedWorkingFiles();
            string artifactDestination = GetEntryPath(compilation.CacheKey);
            string artifactTemporary = Path.Combine(
                m_Directory,
                $".{compilation.CacheKey}.{Guid.NewGuid():N}{TemporaryFileSuffix}");
            string dependencyDestination =
                GetDependencyPath(dependencies.ProvisionalKey);
            string dependencyTemporary = Path.Combine(
                m_Directory,
                $".{dependencies.ProvisionalKey}.{Guid.NewGuid():N}"
                + $"{DependencyFileExtension}{TemporaryFileSuffix}");
            try
            {
                WritePackage(artifactTemporary, compilation);
                PublishAtomically(
                    compilation.CacheKey,
                    artifactTemporary,
                    artifactDestination);

                // The final-key artifact is durable before the provisional
                // dependency index can make it discoverable.
                WriteDependencyIndex(dependencyTemporary, dependencies);
                File.Move(
                    dependencyTemporary,
                    dependencyDestination,
                    overwrite: true);
                EnforceRetention(
                    artifactDestination,
                    dependencyDestination);
            }
            finally
            {
                if (File.Exists(artifactTemporary))
                {
                    File.Delete(artifactTemporary);
                }

                if (File.Exists(dependencyTemporary))
                {
                    File.Delete(dependencyTemporary);
                }
            }
        }

        private void PublishAtomically(
            string cacheKey,
            string temporary,
            string destination)
        {
            IOException? lastFailure = null;
            for (int attempt = 0; attempt < MaximumPublishAttempts; ++attempt)
            {
                try
                {
                    File.Move(temporary, destination, overwrite: false);
                    return;
                }
                catch (IOException ex)
                {
                    lastFailure = ex;
                }

                if (File.Exists(destination)
                    && TryLoadCore(cacheKey, out _))
                {
                    return;
                }
            }

            throw new IOException(
                $"Could not atomically publish shader cache entry {cacheKey} after "
                + $"{MaximumPublishAttempts} attempts.",
                lastFailure);
        }
        private bool TryLoadCore(
            string cacheKey,
            out ShaderProgramCompilation? compilation)
        {
            string path = GetEntryPath(cacheKey);
            compilation = null;
            if (!File.Exists(path))
            {
                return false;
            }

            byte[]? packageBytes = null;
            try
            {
                packageBytes = ReadBounded(path);
                compilation = Decode(cacheKey, packageBytes);
                return true;
            }
            catch (Exception ex) when (IsCorruption(ex))
            {
                string? observedDigest = packageBytes is null
                    ? null
                    : Convert.ToHexStringLower(SHA256.HashData(packageBytes));
                ClaimAndQuarantine(cacheKey, path, observedDigest);
                return false;
            }
        }

        private FileStream AcquireGlobalLock()
        {
            string lockPath = Path.Combine(m_Directory, GlobalLockFileName);
            IOException? lastFailure = null;
            for (int attempt = 0; attempt < MaximumLockAttempts; ++attempt)
            {
                try
                {
                    return new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 1,
                        FileOptions.DeleteOnClose);
                }
                catch (IOException exception)
                {
                    lastFailure = exception;
                    System.Threading.Thread.Sleep(LockRetryMilliseconds);
                }
            }

            throw new IOException(
                $"Could not acquire the shader cache lock {lockPath} after "
                + $"{MaximumLockAttempts * LockRetryMilliseconds} milliseconds.",
                lastFailure);
        }

        private byte[] ReadBounded(string path)
        {
            FileInfo file = new(path);
            if (file.Length <= 0
                || file.Length > m_Limits.MaximumCachePackageBytes
                || file.Length > int.MaxValue)
            {
                throw new CacheCorruptionException(
                    $"Shader cache package {path} has invalid length {file.Length}.");
            }

            byte[] bytes = new byte[checked((int)file.Length)];
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                throw new CacheCorruptionException(
                    $"Shader cache package {path} changed while being read.");
            }

            return bytes;
        }

    }
}
