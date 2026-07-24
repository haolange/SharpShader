using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpShader.Compilation.Internal
{
    internal sealed class ShaderProgramPersistentCache
    {
        private const uint CurrentSchemaVersion = 1;
        private const uint DependencySchemaVersion = 1;
        private const string FileExtension = ".sharpshader-cache.json";
        private const string DependencyFileExtension =
            ".sharpshader-dependencies.json";
        private const int MaximumPublishAttempts = 4;
        private const string GlobalLockFileName = ".sharpshader-cache.lock";
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

        private void WritePackage(
            string path,
            ShaderProgramCompilation compilation)
        {
            byte[] manifestBytes =
                ShaderInterfaceManifestSerializer.SerializeToUtf8Bytes(
                    compilation.Manifest);
            using FileStream file = new(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.WriteThrough);
            using SizeLimitedWriteStream limited = new(
                file,
                m_Limits.MaximumCachePackageBytes);
            using Utf8JsonWriter writer = new(
                limited,
                new JsonWriterOptions
                {
                    Indented = false,
                    SkipValidation = false,
                });

            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
            writer.WriteString("cacheKey", compilation.CacheKey);
            writer.WriteString(
                "manifestDigest",
                Convert.ToHexStringLower(SHA256.HashData(manifestBytes)));
            writer.WriteBase64String("manifest", manifestBytes);
            writer.WriteStartArray("artifacts");
            foreach (ShaderProgramArtifact artifact in compilation.Artifacts)
            {
                byte[] content = artifact.CopyContent();
                if ((ulong)content.Length != artifact.Identity.ByteLength)
                {
                    throw new InvalidDataException(
                        $"Shader artifact {artifact.Identity.ContentDigest} "
                        + "changed before cache publication.");
                }

                writer.WriteStartObject();
                writer.WriteString("variantKey", artifact.VariantKey);
                writer.WriteString("entryPoint", artifact.EntryPoint);
                writer.WriteNumber("stage", (int)artifact.Stage);
                writer.WriteNumber(
                    "artifactKind",
                    (int)artifact.Identity.ArtifactKind);
                writer.WriteString(
                    "artifactName",
                    artifact.Identity.ArtifactName);
                writer.WriteString(
                    "contentDigest",
                    artifact.Identity.ContentDigest);
                writer.WriteNumber(
                    "byteLength",
                    artifact.Identity.ByteLength);
                writer.WriteBase64String("content", content);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
            limited.Flush();
            file.Flush(flushToDisk: true);
        }

        private void WriteDependencyIndex(
            string path,
            ShaderProgramDependencySnapshot dependencies)
        {
            using FileStream file = new(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.WriteThrough);
            using SizeLimitedWriteStream limited = new(
                file,
                m_Limits.MaximumCachePackageBytes);
            using Utf8JsonWriter writer = new(
                limited,
                new JsonWriterOptions
                {
                    Indented = false,
                    SkipValidation = false,
                });

            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", DependencySchemaVersion);
            writer.WriteString("provisionalKey", dependencies.ProvisionalKey);
            writer.WriteString("finalKey", dependencies.FinalKey);
            writer.WriteBoolean("isWarmable", dependencies.IsWarmable);
            writer.WriteStartArray("files");
            foreach (ShaderProgramDependencyFile dependency in
                     dependencies.Files)
            {
                writer.WriteStartObject();
                writer.WriteString("path", dependency.Path);
                writer.WriteNumber("byteLength", dependency.ByteLength);
                writer.WriteString("contentDigest", dependency.ContentDigest);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("topologies");
            foreach (ShaderProgramDirectoryTopology topology in
                     dependencies.Topologies)
            {
                writer.WriteStartObject();
                writer.WriteString("rootPath", topology.RootPath);
                writer.WriteNumber("entryCount", topology.EntryCount);
                writer.WriteString("digest", topology.Digest);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
            limited.Flush();
            file.Flush(flushToDisk: true);
        }

        private ShaderProgramDependencySnapshot DecodeDependencies(
            string expectedProvisionalKey,
            ReadOnlySpan<byte> bytes)
        {
            ValidateJson(bytes);
            DependencyIndexDocument? document =
                JsonSerializer.Deserialize<DependencyIndexDocument>(
                    bytes,
                    s_JsonOptions);
            if (document is null
                || document.SchemaVersion != DependencySchemaVersion
                || !document.IsWarmable
                || !string.Equals(
                    document.ProvisionalKey,
                    expectedProvisionalKey,
                    StringComparison.Ordinal))
            {
                throw new CacheCorruptionException(
                    "Shader dependency index identity or schema is invalid.");
            }

            ValidateDigest(document.ProvisionalKey, "dependency provisional key");
            ValidateDigest(document.FinalKey, "dependency final key");
            if (document.Files is null
                || document.Topologies is null
                || document.Files.Length > m_Limits.MaximumIncludeFileCount
                || document.Topologies.Length > m_Limits.MaximumIncludeFileCount)
            {
                throw new CacheCorruptionException(
                    "Shader dependency index exceeds the configured entry limit.");
            }

            try
            {
                ShaderProgramDependencyFile[] files =
                    new ShaderProgramDependencyFile[document.Files.Length];
                for (int index = 0; index < files.Length; ++index)
                {
                    DependencyFileDocument source = document.Files[index]
                        ?? throw new CacheCorruptionException(
                            "Shader dependency index contains a null file.");
                    files[index] = new ShaderProgramDependencyFile(
                        source.Path,
                        source.ByteLength,
                        source.ContentDigest);
                }

                ShaderProgramDirectoryTopology[] topologies =
                    new ShaderProgramDirectoryTopology[
                        document.Topologies.Length];
                for (int index = 0; index < topologies.Length; ++index)
                {
                    DependencyTopologyDocument source =
                        document.Topologies[index]
                        ?? throw new CacheCorruptionException(
                            "Shader dependency index contains a null topology.");
                    topologies[index] = new ShaderProgramDirectoryTopology(
                        source.RootPath,
                        source.EntryCount,
                        source.Digest);
                }

                return new ShaderProgramDependencySnapshot(
                    document.ProvisionalKey,
                    document.FinalKey,
                    isWarmable: true,
                    files,
                    topologies);
            }
            catch (CacheCorruptionException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is ArgumentException
                or OverflowException)
            {
                throw new CacheCorruptionException(
                    "Shader dependency index contains an invalid value.",
                    exception);
            }
        }

        private static ShaderProgramCompilation Decode(
            string expectedCacheKey,
            ReadOnlySpan<byte> packageBytes)
        {
            ValidateJson(packageBytes);
            CachePackageDocument? document =
                JsonSerializer.Deserialize<CachePackageDocument>(
                    packageBytes,
                    s_JsonOptions);
            if (document is null)
            {
                throw new CacheCorruptionException(
                    "Shader cache package must contain an object.");
            }

            if (document.SchemaVersion != CurrentSchemaVersion)
            {
                throw new CacheCorruptionException(
                    $"Shader cache schema {document.SchemaVersion} is unsupported.");
            }

            if (!string.Equals(
                    document.CacheKey,
                    expectedCacheKey,
                    StringComparison.Ordinal))
            {
                throw new CacheCorruptionException(
                    "Shader cache key does not match its file identity.");
            }

            ValidateDigest(document.ManifestDigest, "manifest digest");
            if (document.Manifest is null || document.Manifest.Length == 0)
            {
                throw new CacheCorruptionException(
                    "Shader cache manifest payload is empty.");
            }

            string actualManifestDigest =
                Convert.ToHexStringLower(SHA256.HashData(document.Manifest));
            if (!string.Equals(
                    document.ManifestDigest,
                    actualManifestDigest,
                    StringComparison.Ordinal))
            {
                throw new CacheCorruptionException(
                    "Shader cache manifest digest does not match its payload.");
            }

            ShaderInterfaceManifest manifest;
            try
            {
                manifest = ShaderInterfaceManifestSerializer.Deserialize(
                    document.Manifest);
            }
            catch (Exception ex) when (
                ex is JsonException
                or ArgumentException
                or FormatException
                or InvalidOperationException
                or OverflowException)
            {
                throw new CacheCorruptionException(
                    "Shader cache manifest is invalid.",
                    ex);
            }

            Dictionary<(string Variant, string Entry, ShaderExecutionStage Stage,
                ShaderArtifactKind Kind), ShaderArtifactIdentity> expectedArtifacts =
                CollectManifestArtifacts(manifest);
            CacheArtifactDocument[] artifactDocuments =
                document.Artifacts ?? Array.Empty<CacheArtifactDocument>();
            if (artifactDocuments.Length != expectedArtifacts.Count)
            {
                throw new CacheCorruptionException(
                    "Shader cache artifact count does not match its manifest.");
            }

            List<ShaderProgramArtifact> artifacts =
                new(artifactDocuments.Length);
            HashSet<(string Variant, string Entry, ShaderExecutionStage Stage,
                ShaderArtifactKind Kind)> seen = new();
            foreach (CacheArtifactDocument artifactDocument in artifactDocuments)
            {
                ValidateArtifact(
                    artifactDocument,
                    expectedArtifacts,
                    seen,
                    artifacts);
            }

            return new ShaderProgramCompilation(
                expectedCacheKey,
                manifest,
                artifacts);
        }

        private static Dictionary<(string Variant, string Entry,
            ShaderExecutionStage Stage, ShaderArtifactKind Kind),
            ShaderArtifactIdentity> CollectManifestArtifacts(
                ShaderInterfaceManifest manifest)
        {
            Dictionary<(string, string, ShaderExecutionStage, ShaderArtifactKind),
                ShaderArtifactIdentity> result = new();
            foreach (ShaderInterfaceVariant variant in manifest.Variants)
            {
                foreach (ShaderInterfaceEntry entry in variant.Entries)
                {
                    foreach (ShaderArtifactIdentity artifact in entry.Artifacts)
                    {
                        if (!result.TryAdd(
                                (
                                    variant.Key,
                                    entry.Name,
                                    entry.Stage,
                                    artifact.ArtifactKind),
                                artifact))
                        {
                            throw new CacheCorruptionException(
                                $"Manifest contains duplicate artifact identity for "
                                + $"{variant.Key}/{entry.Name}/{artifact.ArtifactKind}.");
                        }
                    }
                }
            }

            return result;
        }

        private static void ValidateArtifact(
            CacheArtifactDocument document,
            IReadOnlyDictionary<(string Variant, string Entry,
                ShaderExecutionStage Stage, ShaderArtifactKind Kind),
                ShaderArtifactIdentity> expectedArtifacts,
            HashSet<(string Variant, string Entry, ShaderExecutionStage Stage,
                ShaderArtifactKind Kind)> seen,
            List<ShaderProgramArtifact> destination)
        {
            if (string.IsNullOrWhiteSpace(document.VariantKey)
                || string.IsNullOrWhiteSpace(document.EntryPoint)
                || !Enum.IsDefined(document.Stage)
                || !Enum.IsDefined(document.ArtifactKind)
                || document.Content is null
                || document.Content.Length == 0)
            {
                throw new CacheCorruptionException(
                    "Shader cache artifact metadata is incomplete or invalid.");
            }

            (string, string, ShaderExecutionStage, ShaderArtifactKind) key =
                (
                    document.VariantKey,
                    document.EntryPoint,
                    document.Stage,
                    document.ArtifactKind);
            if (!seen.Add(key))
            {
                throw new CacheCorruptionException(
                    $"Shader cache contains duplicate artifact "
                    + $"{document.VariantKey}/{document.EntryPoint}/{document.ArtifactKind}.");
            }

            if (!expectedArtifacts.TryGetValue(
                    key,
                    out ShaderArtifactIdentity? expected))
            {
                throw new CacheCorruptionException(
                    $"Shader cache contains artifact not declared by the manifest: "
                    + $"{document.VariantKey}/{document.EntryPoint}/{document.ArtifactKind}.");
            }

            ValidateDigest(document.ContentDigest, "artifact digest");
            string actualDigest =
                Convert.ToHexStringLower(SHA256.HashData(document.Content));
            if (!string.Equals(
                    document.ContentDigest,
                    actualDigest,
                    StringComparison.Ordinal)
                || !string.Equals(
                    expected.ContentDigest,
                    actualDigest,
                    StringComparison.Ordinal)
                || document.ByteLength != (ulong)document.Content.Length
                || expected.ByteLength != (ulong)document.Content.Length
                || !string.Equals(
                    document.ArtifactName,
                    expected.ArtifactName,
                    StringComparison.Ordinal))
            {
                throw new CacheCorruptionException(
                    $"Shader cache artifact identity is invalid for "
                    + $"{document.VariantKey}/{document.EntryPoint}/{document.ArtifactKind}.");
            }

            string? text = null;
            if (document.ArtifactKind == ShaderArtifactKind.MslSource)
            {
                try
                {
                    text = s_StrictUtf8.GetString(document.Content);
                }
                catch (DecoderFallbackException ex)
                {
                    throw new CacheCorruptionException(
                        "Cached MSL source is not valid UTF-8.",
                        ex);
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new CacheCorruptionException(
                        "Cached MSL source is empty.");
                }
            }

            destination.Add(new ShaderProgramArtifact(
                document.VariantKey,
                document.EntryPoint,
                document.Stage,
                expected,
                document.Content,
                text));
        }

        private static void ValidateDigest(string? value, string description)
        {
            if (value is null || value.Length != 64)
            {
                throw new CacheCorruptionException(
                    $"Shader cache {description} is not a SHA-256 value.");
            }

            foreach (char character in value)
            {
                if (!((character >= '0' && character <= '9')
                      || (character >= 'a' && character <= 'f')))
                {
                    throw new CacheCorruptionException(
                        $"Shader cache {description} is not lowercase hexadecimal.");
                }
            }
        }

        private static void ValidateJson(ReadOnlySpan<byte> bytes)
        {
            try
            {
                Utf8JsonReader reader = new(
                    bytes,
                    new JsonReaderOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = 128,
                    });
                using JsonDocument document = JsonDocument.ParseValue(ref reader);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || reader.Read())
                {
                    throw new CacheCorruptionException(
                        "Shader cache package must contain one JSON object.");
                }

                ValidateNoDuplicateProperties(document.RootElement, "$");
            }
            catch (JsonException ex)
            {
                throw new CacheCorruptionException(
                    "Shader cache package contains invalid JSON.",
                    ex);
            }
        }

        private static void ValidateNoDuplicateProperties(
            JsonElement element,
            string path)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                HashSet<string> names = new(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new CacheCorruptionException(
                            $"Shader cache package contains duplicate property "
                            + $"{path}.{property.Name}.");
                    }

                    ValidateNoDuplicateProperties(
                        property.Value,
                        $"{path}.{property.Name}");
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    ValidateNoDuplicateProperties(item, $"{path}[{index}]");
                    ++index;
                }
            }
        }

        private string GetEntryPath(string cacheKey)
        {
            ValidateDigest(cacheKey, "key");
            return Path.Combine(m_Directory, cacheKey + FileExtension);
        }

        private string GetDependencyPath(string provisionalKey)
        {
            ValidateDigest(provisionalKey, "dependency key");
            return Path.Combine(
                m_Directory,
                provisionalKey + DependencyFileExtension);
        }

        private static void QuarantineDependencyIndex(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            string quarantined =
                $"{path}.corrupt-"
                + $"{DateTime.UtcNow:yyyyMMddHHmmssfffffff}-"
                + $"{Guid.NewGuid():N}";
            try
            {
                File.Move(path, quarantined, overwrite: false);
            }
            catch (FileNotFoundException)
            {
            }
            catch (IOException) when (!File.Exists(path))
            {
            }
        }

        private static bool IsCorruption(Exception exception)
        {
            return exception is CacheCorruptionException
                or JsonException
                or FormatException
                or InvalidDataException
                or EndOfStreamException;
        }

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

        private sealed class SizeLimitedWriteStream : Stream
        {
            private readonly Stream m_Inner;
            private readonly long m_MaximumBytes;
            private long m_BytesWritten;

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => m_BytesWritten;

            public override long Position
            {
                get => m_BytesWritten;
                set => throw new NotSupportedException();
            }

            public SizeLimitedWriteStream(
                Stream inner,
                long maximumBytes)
            {
                ArgumentNullException.ThrowIfNull(inner);
                if (!inner.CanWrite)
                {
                    throw new ArgumentException(
                        "The cache package stream must be writable.",
                        nameof(inner));
                }

                if (maximumBytes <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(maximumBytes));
                }

                m_Inner = inner;
                m_MaximumBytes = maximumBytes;
            }

            public override void Flush()
            {
                m_Inner.Flush();
            }

            public override int Read(
                byte[] buffer,
                int offset,
                int count)
            {
                throw new NotSupportedException();
            }

            public override long Seek(
                long offset,
                SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(
                byte[] buffer,
                int offset,
                int count)
            {
                ArgumentNullException.ThrowIfNull(buffer);
                ArgumentOutOfRangeException.ThrowIfNegative(offset);
                ArgumentOutOfRangeException.ThrowIfNegative(count);
                if (buffer.Length - offset < count)
                {
                    throw new ArgumentException(
                        "The cache package write range is invalid.",
                        nameof(count));
                }

                EnsureCapacity(count);
                m_Inner.Write(buffer, offset, count);
                m_BytesWritten += count;
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                EnsureCapacity(buffer.Length);
                m_Inner.Write(buffer);
                m_BytesWritten += buffer.Length;
            }

            public override void WriteByte(byte value)
            {
                EnsureCapacity(1);
                m_Inner.WriteByte(value);
                ++m_BytesWritten;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    m_Inner.Flush();
                }

                base.Dispose(disposing);
            }

            private void EnsureCapacity(int count)
            {
                if (m_BytesWritten > m_MaximumBytes - count)
                {
                    throw new IOException(
                        $"Shader cache package exceeds the configured "
                        + $"{m_MaximumBytes}-byte limit.");
                }
            }
        }

        private sealed class DependencyIndexDocument
        {
            public uint SchemaVersion { get; set; }
            public string ProvisionalKey { get; set; } = string.Empty;
            public string FinalKey { get; set; } = string.Empty;
            public bool IsWarmable { get; set; }
            public DependencyFileDocument?[]? Files { get; set; }
            public DependencyTopologyDocument?[]? Topologies { get; set; }
        }

        private sealed class DependencyFileDocument
        {
            public string Path { get; set; } = string.Empty;
            public long ByteLength { get; set; }
            public string ContentDigest { get; set; } = string.Empty;
        }

        private sealed class DependencyTopologyDocument
        {
            public string RootPath { get; set; } = string.Empty;
            public int EntryCount { get; set; }
            public string Digest { get; set; } = string.Empty;
        }

        private sealed class CachePackageDocument
        {
            public uint SchemaVersion { get; set; }
            public string CacheKey { get; set; } = string.Empty;
            public string ManifestDigest { get; set; } = string.Empty;
            public byte[] Manifest { get; set; } = Array.Empty<byte>();
            public CacheArtifactDocument[] Artifacts { get; set; } =
                Array.Empty<CacheArtifactDocument>();
        }

        private sealed class CacheArtifactDocument
        {
            public string VariantKey { get; set; } = string.Empty;
            public string EntryPoint { get; set; } = string.Empty;
            public ShaderExecutionStage Stage { get; set; }
            public ShaderArtifactKind ArtifactKind { get; set; }
            public string? ArtifactName { get; set; }
            public string ContentDigest { get; set; } = string.Empty;
            public ulong ByteLength { get; set; }
            public byte[] Content { get; set; } = Array.Empty<byte>();
        }

        private sealed class CacheCorruptionException : Exception
        {
            public CacheCorruptionException(string message)
                : base(message)
            {
            }

            public CacheCorruptionException(string message, Exception innerException)
                : base(message, innerException)
            {
            }
        }
    }
}
