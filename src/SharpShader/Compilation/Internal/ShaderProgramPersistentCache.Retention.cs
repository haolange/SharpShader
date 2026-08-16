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

        private void ClaimAndQuarantine(
            string cacheKey,
            string path,
            string? observedDigest)
        {
            if (!File.Exists(path))
            {
                return;
            }

            string claim = $"{path}.claim-{Guid.NewGuid():N}";
            try
            {
                File.Move(path, claim, overwrite: false);
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (IOException) when (!File.Exists(path))
            {
                return;
            }

            bool sameObservedEntity =
                observedDigest is not null
                && TryComputePackageDigest(claim, out string? claimedDigest)
                && string.Equals(
                    observedDigest,
                    claimedDigest,
                    StringComparison.Ordinal);
            if (!sameObservedEntity && TryDecodeFile(cacheKey, claim))
            {
                RestoreValidClaim(cacheKey, path, claim);
                return;
            }

            MoveClaimToQuarantine(path, claim);
        }

        private bool TryComputePackageDigest(
            string path,
            out string? digest)
        {
            try
            {
                digest = Convert.ToHexStringLower(
                    SHA256.HashData(ReadBounded(path)));
                return true;
            }
            catch (Exception exception) when (IsCorruption(exception))
            {
                digest = null;
                return false;
            }
        }

        private bool TryDecodeFile(string cacheKey, string path)
        {
            try
            {
                _ = Decode(cacheKey, ReadBounded(path));
                return true;
            }
            catch (Exception exception) when (IsCorruption(exception))
            {
                return false;
            }
        }

        private void RestoreValidClaim(
            string cacheKey,
            string destination,
            string claim)
        {
            for (int attempt = 0; attempt < MaximumPublishAttempts; ++attempt)
            {
                try
                {
                    File.Move(claim, destination, overwrite: false);
                    return;
                }
                catch (IOException) when (File.Exists(destination))
                {
                    if (TryDecodeFile(cacheKey, destination))
                    {
                        File.Delete(claim);
                        return;
                    }

                    ClaimAndQuarantine(cacheKey, destination, observedDigest: null);
                }
            }

            throw new IOException(
                $"Could not restore valid shader cache claim {claim}.");
        }

        private static void MoveClaimToQuarantine(
            string destination,
            string claim)
        {
            string quarantined =
                $"{destination}.corrupt-"
                + $"{DateTime.UtcNow:yyyyMMddHHmmssfffffff}-"
                + $"{Guid.NewGuid():N}";
            File.Move(claim, quarantined, overwrite: false);
        }

        private void CleanupAbandonedWorkingFiles()
        {
            foreach (string temporary in Directory.EnumerateFiles(
                         m_Directory,
                         $".*{TemporaryFileSuffix}",
                         SearchOption.TopDirectoryOnly))
            {
                if (IsCacheTemporaryFile(temporary)
                    || IsDependencyTemporaryFile(temporary))
                {
                    File.Delete(temporary);
                }
            }

            foreach (string claim in Directory.EnumerateFiles(
                         m_Directory,
                         $"*{FileExtension}.claim-*",
                         SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(claim);
                int marker = fileName.IndexOf(
                    FileExtension + ".claim-",
                    StringComparison.Ordinal);
                if (marker != 64)
                {
                    File.Delete(claim);
                    continue;
                }

                string cacheKey = fileName[..marker];
                try
                {
                    ValidateDigest(cacheKey, "claim key");
                }
                catch (CacheCorruptionException)
                {
                    File.Delete(claim);
                    continue;
                }

                string destination = GetEntryPath(cacheKey);
                if (TryDecodeFile(cacheKey, claim))
                {
                    RestoreValidClaim(cacheKey, destination, claim);
                }
                else
                {
                    MoveClaimToQuarantine(destination, claim);
                }
            }
        }
        private static bool IsCacheTemporaryFile(string path)
        {
            string fileName = Path.GetFileName(path);
            if (fileName.Length != 102
                || fileName[0] != '.'
                || fileName[65] != '.'
                || !fileName.EndsWith(
                    TemporaryFileSuffix,
                    StringComparison.Ordinal)
                || !Guid.TryParseExact(
                    fileName.Substring(66, 32),
                    "N",
                    out _))
            {
                return false;
            }

            try
            {
                ValidateDigest(
                    fileName.Substring(1, 64),
                    "temporary key");
                return true;
            }
            catch (CacheCorruptionException)
            {
                return false;
            }
        }

        private static bool IsDependencyTemporaryFile(string path)
        {
            string fileName = Path.GetFileName(path);
            string suffix = DependencyFileExtension + TemporaryFileSuffix;
            int expectedLength = 98 + suffix.Length;
            if (fileName.Length != expectedLength
                || fileName[0] != '.'
                || fileName[65] != '.'
                || !fileName.EndsWith(suffix, StringComparison.Ordinal)
                || !Guid.TryParseExact(
                    fileName.Substring(66, 32),
                    "N",
                    out _))
            {
                return false;
            }

            try
            {
                ValidateDigest(
                    fileName.Substring(1, 64),
                    "dependency temporary key");
                return true;
            }
            catch (CacheCorruptionException)
            {
                return false;
            }
        }


        private void EnforceRetention(params string?[] protectedLivePaths)
        {
            HashSet<string> protectedPaths = new(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);
            foreach (string? protectedPath in protectedLivePaths)
            {
                if (protectedPath is not null)
                {
                    protectedPaths.Add(Path.GetFullPath(protectedPath));
                }
            }

            List<CacheFileRecord> quarantine = EnumerateCacheFiles(
                $"*{FileExtension}.corrupt-*");
            quarantine.AddRange(EnumerateCacheFiles(
                $"*{DependencyFileExtension}.corrupt-*"));
            quarantine.Sort(CacheFileRecord.Compare);
            TrimOldest(
                quarantine,
                m_Limits.MaximumQuarantineEntries,
                m_Limits.MaximumQuarantineBytes,
                protectedPaths: null);

            List<CacheFileRecord> live = EnumerateCacheFiles(
                $"*{FileExtension}");
            live.AddRange(EnumerateCacheFiles(
                $"*{DependencyFileExtension}"));
            live.Sort(CacheFileRecord.Compare);
            int maximumLiveFileCount = (int)Math.Min(
                int.MaxValue,
                (long)m_Limits.MaximumPersistentCacheEntries * 2);
            TrimOldest(
                live,
                maximumLiveFileCount,
                m_Limits.MaximumPersistentCacheBytes,
                protectedPaths);

            quarantine = EnumerateCacheFiles(
                $"*{FileExtension}.corrupt-*");
            quarantine.AddRange(EnumerateCacheFiles(
                $"*{DependencyFileExtension}.corrupt-*"));
            live = EnumerateCacheFiles($"*{FileExtension}");
            live.AddRange(EnumerateCacheFiles(
                $"*{DependencyFileExtension}"));
            long totalBytes = SumBytes(quarantine, live);
            if (totalBytes <= m_Limits.MaximumPersistentCacheBytes)
            {
                return;
            }

            totalBytes = DeleteUntilTotalFits(
                quarantine,
                totalBytes,
                m_Limits.MaximumPersistentCacheBytes,
                protectedPaths: null);
            _ = DeleteUntilTotalFits(
                live,
                totalBytes,
                m_Limits.MaximumPersistentCacheBytes,
                protectedPaths);
        }

        private List<CacheFileRecord> EnumerateCacheFiles(string pattern)
        {
            List<CacheFileRecord> records = new();
            foreach (string path in Directory.EnumerateFiles(
                         m_Directory,
                         pattern,
                         SearchOption.TopDirectoryOnly))
            {
                FileInfo file = new(path);
                if (file.Exists)
                {
                    records.Add(new CacheFileRecord(
                        file.FullName,
                        file.Length,
                        file.LastWriteTimeUtc));
                }
            }

            records.Sort(CacheFileRecord.Compare);
            return records;
        }

        private static long SumBytes(
            IReadOnlyList<CacheFileRecord> first,
            IReadOnlyList<CacheFileRecord> second)
        {
            try
            {
                long total = 0;
                foreach (CacheFileRecord record in first)
                {
                    total = checked(total + record.Length);
                }

                foreach (CacheFileRecord record in second)
                {
                    total = checked(total + record.Length);
                }

                return total;
            }
            catch (OverflowException)
            {
                return long.MaxValue;
            }
        }

        private static void TrimOldest(
            List<CacheFileRecord> records,
            int maximumCount,
            long maximumBytes,
            IReadOnlySet<string>? protectedPaths)
        {
            long totalBytes = SumBytes(records, Array.Empty<CacheFileRecord>());
            while (records.Count > maximumCount || totalBytes > maximumBytes)
            {
                int victim = FindOldestDeletable(records, protectedPaths);
                if (victim < 0)
                {
                    throw new IOException(
                        "The protected shader cache entry exceeds the configured retention budget.");
                }

                CacheFileRecord record = records[victim];
                File.Delete(record.Path);
                records.RemoveAt(victim);
                totalBytes = Math.Max(0, totalBytes - record.Length);
            }
        }

        private static long DeleteUntilTotalFits(
            List<CacheFileRecord> records,
            long totalBytes,
            long maximumBytes,
            IReadOnlySet<string>? protectedPaths)
        {
            while (totalBytes > maximumBytes)
            {
                int victim = FindOldestDeletable(records, protectedPaths);
                if (victim < 0)
                {
                    return totalBytes;
                }

                CacheFileRecord record = records[victim];
                File.Delete(record.Path);
                records.RemoveAt(victim);
                totalBytes = Math.Max(0, totalBytes - record.Length);
            }

            return totalBytes;
        }

        private static int FindOldestDeletable(
            IReadOnlyList<CacheFileRecord> records,
            IReadOnlySet<string>? protectedPaths)
        {
            for (int index = 0; index < records.Count; ++index)
            {
                if (protectedPaths is null
                    || !protectedPaths.Contains(records[index].Path))
                {
                    return index;
                }
            }

            return -1;
        }
        private sealed class CacheFileRecord
        {
            public string Path { get; }
            public long Length { get; }
            public DateTime LastWriteTimeUtc { get; }

            public CacheFileRecord(
                string path,
                long length,
                DateTime lastWriteTimeUtc)
            {
                Path = path;
                Length = length;
                LastWriteTimeUtc = lastWriteTimeUtc;
            }

            public static int Compare(
                CacheFileRecord left,
                CacheFileRecord right)
            {
                int time = left.LastWriteTimeUtc.CompareTo(
                    right.LastWriteTimeUtc);
                return time != 0
                    ? time
                    : string.CompareOrdinal(left.Path, right.Path);
            }
        }
}
}
