using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SharpShader.HLSLCrossCompiler;
using SharpShader.HLSLCrossCompiler.Internal;

namespace SharpShader.Compilation.Internal
{
    internal readonly record struct ShaderProgramCompileUnitIdentity(
        string VariantKey,
        ShaderTargetKind Target,
        ShaderStageKind Stage,
        string EntryIdentity);

    internal sealed class ShaderProgramFrozenCompileUnit
    {
        public ShaderProgramCompileUnitIdentity Identity { get; }
        public DxcPreprocessedSource PreprocessedSource { get; }

        public ShaderProgramFrozenCompileUnit(
            ShaderProgramCompileUnitIdentity identity,
            DxcPreprocessedSource preprocessedSource)
        {
            if (string.IsNullOrWhiteSpace(identity.VariantKey)
                || string.IsNullOrWhiteSpace(identity.EntryIdentity))
            {
                throw new ArgumentException(
                    "A frozen shader compile-unit identity must be complete.",
                    nameof(identity));
            }

            ArgumentNullException.ThrowIfNull(preprocessedSource);
            Identity = identity;
            PreprocessedSource = preprocessedSource;
        }
    }

    internal sealed class ShaderProgramFrozenInput
    {
        public string SourceDigest { get; }
        public string FinalKey { get; }
        public ShaderProgramDependencySnapshot Dependencies { get; }

        public ShaderProgramFrozenInput(
            string sourceDigest,
            string finalKey,
            ShaderProgramDependencySnapshot dependencies)
        {
            if (!ShaderProgramDependencySnapshot.IsSha256Digest(sourceDigest)
                || !ShaderProgramDependencySnapshot.IsSha256Digest(finalKey))
            {
                throw new ArgumentException(
                    "Frozen shader source and final keys must be lowercase SHA-256.");
            }

            ArgumentNullException.ThrowIfNull(dependencies);
            SourceDigest = sourceDigest;
            FinalKey = finalKey;
            Dependencies = dependencies;
        }
    }

    internal sealed class ShaderProgramCompilationOutput
    {
        public ShaderProgramCompilation Compilation { get; }
        public ShaderProgramDependencySnapshot Dependencies { get; }

        public ShaderProgramCompilationOutput(
            ShaderProgramCompilation compilation,
            ShaderProgramDependencySnapshot dependencies)
        {
            ArgumentNullException.ThrowIfNull(compilation);
            ArgumentNullException.ThrowIfNull(dependencies);
            Compilation = compilation;
            Dependencies = dependencies;
        }
    }

    internal sealed class ShaderProgramDependencyFile
    {
        public string Path { get; }
        public long ByteLength { get; }
        public string ContentDigest { get; }

        public ShaderProgramDependencyFile(
            string path,
            long byteLength,
            string contentDigest)
        {
            if (!System.IO.Path.IsPathFullyQualified(path))
            {
                throw new ArgumentException(
                    "A shader dependency path must be fully qualified.",
                    nameof(path));
            }

            if (byteLength < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(byteLength));
            }

            if (!ShaderProgramDependencySnapshot.IsSha256Digest(contentDigest))
            {
                throw new ArgumentException(
                    "A shader dependency digest must be lowercase SHA-256.",
                    nameof(contentDigest));
            }

            Path = System.IO.Path.GetFullPath(path);
            ByteLength = byteLength;
            ContentDigest = contentDigest;
        }
    }

    internal sealed class ShaderProgramDirectoryTopology
    {
        public string RootPath { get; }
        public int EntryCount { get; }
        public string Digest { get; }

        public ShaderProgramDirectoryTopology(
            string rootPath,
            int entryCount,
            string digest)
        {
            if (!System.IO.Path.IsPathFullyQualified(rootPath))
            {
                throw new ArgumentException(
                    "A shader topology root must be fully qualified.",
                    nameof(rootPath));
            }

            if (entryCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(entryCount));
            }

            if (!ShaderProgramDependencySnapshot.IsSha256Digest(digest))
            {
                throw new ArgumentException(
                    "A shader topology digest must be lowercase SHA-256.",
                    nameof(digest));
            }

            RootPath = System.IO.Path.GetFullPath(rootPath);
            EntryCount = entryCount;
            Digest = digest;
        }
    }

    internal sealed class ShaderProgramDependencySnapshot
    {
        public const int FormatVersion = 1;

        private static readonly UTF8Encoding s_StrictUtf8 = new(false, true);

        private readonly ReadOnlyCollection<ShaderProgramDependencyFile> m_Files;
        private readonly ReadOnlyCollection<ShaderProgramDirectoryTopology> m_Topologies;

        public string ProvisionalKey { get; }
        public string FinalKey { get; }
        public bool IsWarmable { get; }
        public IReadOnlyList<ShaderProgramDependencyFile> Files => m_Files;
        public IReadOnlyList<ShaderProgramDirectoryTopology> Topologies => m_Topologies;

        public ShaderProgramDependencySnapshot(
            string provisionalKey,
            string finalKey,
            bool isWarmable,
            IEnumerable<ShaderProgramDependencyFile> files,
            IEnumerable<ShaderProgramDirectoryTopology> topologies)
        {
            if (!IsSha256Digest(provisionalKey))
            {
                throw new ArgumentException(
                    "The provisional shader key must be lowercase SHA-256.",
                    nameof(provisionalKey));
            }

            if (!IsSha256Digest(finalKey))
            {
                throw new ArgumentException(
                    "The final shader key must be lowercase SHA-256.",
                    nameof(finalKey));
            }

            ArgumentNullException.ThrowIfNull(files);
            ArgumentNullException.ThrowIfNull(topologies);
            ShaderProgramDependencyFile[] fileCopy = files.ToArray();
            ShaderProgramDirectoryTopology[] topologyCopy = topologies.ToArray();
            Array.Sort(fileCopy, static (left, right) =>
                string.CompareOrdinal(
                    NormalizePath(left.Path),
                    NormalizePath(right.Path)));
            Array.Sort(topologyCopy, static (left, right) =>
                string.CompareOrdinal(
                    NormalizePath(left.RootPath),
                    NormalizePath(right.RootPath)));
            EnsureUniquePaths(fileCopy, static value => value.Path, "dependency");
            EnsureUniquePaths(topologyCopy, static value => value.RootPath, "topology root");

            ProvisionalKey = provisionalKey;
            FinalKey = finalKey;
            IsWarmable = isWarmable;
            m_Files = Array.AsReadOnly(fileCopy);
            m_Topologies = Array.AsReadOnly(topologyCopy);
        }

        public bool ValidateWarmState(ShaderProgramCacheLimits limits)
        {
            ArgumentNullException.ThrowIfNull(limits);
            if (!IsWarmable
                || m_Files.Count > limits.MaximumIncludeFileCount
                || m_Topologies.Count > limits.MaximumIncludeFileCount)
            {
                return false;
            }

            long totalBytes = 0;
            foreach (ShaderProgramDependencyFile file in m_Files)
            {
                if (!TryHashFile(
                        file.Path,
                        limits,
                        ref totalBytes,
                        out long length,
                        out string digest)
                    || length != file.ByteLength
                    || !string.Equals(
                        digest,
                        file.ContentDigest,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }

            foreach (ShaderProgramDirectoryTopology topology in m_Topologies)
            {
                if (!TryComputeTopology(
                        topology.RootPath,
                        limits.MaximumIncludeFileCount,
                        out ShaderProgramDirectoryTopology? current)
                    || current is null
                    || current.EntryCount != topology.EntryCount
                    || !string.Equals(
                        current.Digest,
                        topology.Digest,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        public static ShaderProgramDependencySnapshot Create(
            string provisionalKey,
            string finalKey,
            string normalizedSourceName,
            bool includeSourceDirectoryTopology,
            IReadOnlyList<string> includeDirectories,
            IReadOnlyList<ShaderProgramDirectoryTopology> initialTopologies,
            bool initialTopologyCaptureComplete,
            IReadOnlyList<ShaderProgramFrozenCompileUnit> units,
            ShaderProgramCacheLimits limits)
        {
            ArgumentNullException.ThrowIfNull(includeDirectories);
            ArgumentNullException.ThrowIfNull(initialTopologies);
            ArgumentNullException.ThrowIfNull(units);
            ArgumentNullException.ThrowIfNull(limits);

            StringComparer pathComparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            Dictionary<string, ShaderProgramDependencyFile> files =
                new(pathComparer);
            bool warmable = true;
            long totalBytes = 0;
            foreach (ShaderProgramFrozenCompileUnit unit in units)
            {
                foreach (DxcCapturedInclude capture in
                         unit.PreprocessedSource.Includes)
                {
                    if (!TryNormalizeCapturedPath(
                            capture,
                            out string? resolvedPath))
                    {
                        warmable = false;
                        continue;
                    }

                    if (ContainsReparsePoint(resolvedPath))
                    {
                        warmable = false;
                        continue;
                    }

                    if (!File.Exists(resolvedPath))
                    {
                        throw DependencyChanged(resolvedPath);
                    }

                    if (files.TryGetValue(
                            resolvedPath,
                            out ShaderProgramDependencyFile? existing))
                    {
                        if (existing.ByteLength != capture.ByteLength
                            || !string.Equals(
                                existing.ContentDigest,
                                capture.ContentDigest,
                                StringComparison.Ordinal))
                        {
                            throw DependencyChanged(resolvedPath);
                        }

                        continue;
                    }

                    byte[] currentContent;
                    try
                    {
                        currentContent = ReadBoundedFile(
                            resolvedPath,
                            limits,
                            ref totalBytes);
                    }
                    catch (
                        Exception exception) when (
                        exception is IOException
                        or UnauthorizedAccessException)
                    {
                        throw DependencyChanged(resolvedPath, exception);
                    }

                    string currentDigest = Convert.ToHexStringLower(
                        SHA256.HashData(currentContent));
                    if (currentContent.LongLength != capture.ByteLength
                        || !string.Equals(
                            currentDigest,
                            capture.ContentDigest,
                            StringComparison.Ordinal))
                    {
                        throw DependencyChanged(resolvedPath);
                    }

                    ShaderProgramDependencyFile dependency = new(
                        resolvedPath,
                        currentContent.LongLength,
                        currentDigest);
                    files.Add(resolvedPath, dependency);
                }
            }

            IReadOnlyList<ShaderProgramDirectoryTopology> finalTopologies =
                CaptureTopologies(
                    normalizedSourceName,
                    includeSourceDirectoryTopology,
                    includeDirectories,
                    limits,
                    out bool finalTopologyCaptureComplete);
            if (initialTopologyCaptureComplete
                && finalTopologyCaptureComplete
                && !TopologySetsEqual(initialTopologies, finalTopologies))
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.CompileFailed,
                    "Shader include-candidate topology changed between the "
                    + "initial snapshot and finalization. The compilation was "
                    + "discarded to prevent a stale warm-cache index.");
            }

            warmable &=
                initialTopologyCaptureComplete
                && finalTopologyCaptureComplete;

            if (files.Count > limits.MaximumIncludeFileCount)
            {
                warmable = false;
            }

            return new ShaderProgramDependencySnapshot(
                provisionalKey,
                finalKey,
                warmable,
                files.Values,
                finalTopologies);
        }

        internal static IReadOnlyList<ShaderProgramDirectoryTopology>
            CaptureTopologies(
                string normalizedSourceName,
                bool includeSourceDirectoryTopology,
                IReadOnlyList<string> includeDirectories,
                ShaderProgramCacheLimits limits,
                out bool captureComplete)
        {
            ArgumentNullException.ThrowIfNull(normalizedSourceName);
            ArgumentNullException.ThrowIfNull(includeDirectories);
            ArgumentNullException.ThrowIfNull(limits);

            StringComparer pathComparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            HashSet<string> uniqueRoots = new(pathComparer);
            List<string> roots = new();

            void AddRoot(string root)
            {
                string fullPath = System.IO.Path.GetFullPath(root);
                if (uniqueRoots.Add(fullPath))
                {
                    roots.Add(fullPath);
                }
            }

            if (includeSourceDirectoryTopology
                && System.IO.Path.IsPathFullyQualified(normalizedSourceName)
                && System.IO.Path.GetDirectoryName(normalizedSourceName)
                    is { Length: > 0 } sourceDirectory)
            {
                AddRoot(sourceDirectory);
            }

            foreach (string includeDirectory in includeDirectories)
            {
                AddRoot(includeDirectory);
            }

            roots.Sort(StringComparer.Ordinal);
            captureComplete = true;
            List<ShaderProgramDirectoryTopology> topologies = new(roots.Count);
            foreach (string root in roots)
            {
                if (!TryComputeTopology(
                        root,
                        limits.MaximumIncludeFileCount,
                        out ShaderProgramDirectoryTopology? topology)
                    || topology is null)
                {
                    captureComplete = false;
                    continue;
                }

                topologies.Add(topology);
            }

            return Array.AsReadOnly(topologies.ToArray());
        }

        private static bool TopologySetsEqual(
            IReadOnlyList<ShaderProgramDirectoryTopology> left,
            IReadOnlyList<ShaderProgramDirectoryTopology> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (int index = 0; index < left.Count; ++index)
            {
                ShaderProgramDirectoryTopology leftValue = left[index];
                ShaderProgramDirectoryTopology rightValue = right[index];
                if (!string.Equals(
                        leftValue.RootPath,
                        rightValue.RootPath,
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal)
                    || leftValue.EntryCount != rightValue.EntryCount
                    || !string.Equals(
                        leftValue.Digest,
                        rightValue.Digest,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static ShaderCompilerException DependencyChanged(
            string path,
            Exception? innerException = null)
        {
            return new ShaderCompilerException(
                ShaderCompilerErrorCode.CompileFailed,
                $"Shader dependency '{path}' changed between DXC preprocessing "
                + "and dependency snapshot finalization. The compilation was discarded.",
                requestedProfile: "shader-program",
                innerException: innerException);
        }

        internal static bool TryComputeTopology(
            string rootPath,
            int maximumEntries,
            out ShaderProgramDirectoryTopology? topology)
        {
            topology = null;
            if (maximumEntries <= 0
                || !System.IO.Path.IsPathFullyQualified(rootPath)
                || !Directory.Exists(rootPath))
            {
                return false;
            }

            try
            {
                DirectoryInfo root = new(System.IO.Path.GetFullPath(rootPath));
                if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                List<(string RelativePath, byte Kind)> entries = new();
                Stack<DirectoryInfo> pending = new();
                pending.Push(root);
                while (pending.Count != 0)
                {
                    DirectoryInfo directory = pending.Pop();
                    FileSystemInfo[] children = directory.GetFileSystemInfos();
                    Array.Sort(children, static (left, right) =>
                        string.CompareOrdinal(left.Name, right.Name));
                    for (int index = children.Length - 1; index >= 0; --index)
                    {
                        FileSystemInfo child = children[index];
                        FileAttributes attributes = child.Attributes;
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            return false;
                        }

                        bool isDirectory =
                            (attributes & FileAttributes.Directory) != 0;
                        string relative = NormalizePath(
                            System.IO.Path.GetRelativePath(
                                root.FullName,
                                child.FullName));
                        entries.Add((relative, isDirectory ? (byte)'D' : (byte)'F'));
                        if (entries.Count > maximumEntries)
                        {
                            return false;
                        }

                        if (isDirectory)
                        {
                            pending.Push((DirectoryInfo)child);
                        }
                    }
                }

                entries.Sort(static (left, right) =>
                    string.CompareOrdinal(left.RelativePath, right.RelativePath));
                using IncrementalHash hash =
                    IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                AppendString(hash, "SharpShader.DirectoryTopology.v1");
                AppendInt32(hash, entries.Count);
                foreach ((string relativePath, byte kind) in entries)
                {
                    hash.AppendData(new[] { kind });
                    AppendString(hash, relativePath);
                }

                topology = new ShaderProgramDirectoryTopology(
                    root.FullName,
                    entries.Count,
                    Convert.ToHexStringLower(hash.GetHashAndReset()));
                return true;
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException)
            {
                return false;
            }
        }

        private static bool TryNormalizeCapturedPath(
            DxcCapturedInclude capture,
            out string path)
        {
            path = string.Empty;
            if (!System.IO.Path.IsPathFullyQualified(capture.RequestedPath))
            {
                return false;
            }

            try
            {
                path = System.IO.Path.GetFullPath(capture.RequestedPath);
                return true;
            }
            catch (Exception exception) when (
                exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
            {
                return false;
            }
        }

        private static bool TryHashFile(
            string path,
            ShaderProgramCacheLimits limits,
            ref long totalBytes,
            out long length,
            out string digest)
        {
            length = 0;
            digest = string.Empty;
            try
            {
                byte[] content = ReadBoundedFile(path, limits, ref totalBytes);
                length = content.LongLength;
                digest = Convert.ToHexStringLower(SHA256.HashData(content));
                return true;
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or ShaderCompilerException)
            {
                return false;
            }
        }

        private static byte[] ReadBoundedFile(
            string path,
            ShaderProgramCacheLimits limits,
            ref long totalBytes)
        {
            if (ContainsReparsePoint(path))
            {
                throw new IOException(
                    $"Shader dependency '{path}' traverses a reparse point.");
            }

            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            long length = stream.Length;
            if (length > limits.MaximumIncludeFileBytes
                || length > int.MaxValue)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.InvalidRequest,
                    $"Shader dependency '{path}' exceeds the per-file limit.");
            }

            totalBytes = checked(totalBytes + length);
            if (totalBytes > limits.MaximumIncludeTotalBytes)
            {
                throw new ShaderCompilerException(
                    ShaderCompilerErrorCode.InvalidRequest,
                    "Shader dependency closure exceeds the total-byte limit.");
            }

            byte[] content = new byte[checked((int)length)];
            stream.ReadExactly(content);
            if (stream.ReadByte() != -1 || stream.Length != length)
            {
                throw new IOException(
                    $"Shader dependency '{path}' changed while it was read.");
            }

            return content;
        }

        private static bool ContainsReparsePoint(string path)
        {
            string? current = System.IO.Path.GetFullPath(path);
            while (current is not null)
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                current = Directory.GetParent(current)?.FullName;
            }

            return false;
        }

        private static void EnsureUniquePaths<T>(
            IReadOnlyList<T> values,
            Func<T, string> pathSelector,
            string description)
        {
            StringComparer comparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            HashSet<string> paths = new(comparer);
            foreach (T value in values)
            {
                if (!paths.Add(pathSelector(value)))
                {
                    throw new ArgumentException(
                        $"Shader {description} paths must be unique.");
                }
            }
        }

        internal static bool IsSha256Digest(string value)
        {
            if (value is null || value.Length != 64)
            {
                return false;
            }

            foreach (char character in value)
            {
                if (character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f'))
                {
                    return false;
                }
            }

            return true;
        }

        private static string NormalizePath(string path)
        {
            return path
                .Replace(System.IO.Path.DirectorySeparatorChar, '/')
                .Replace(System.IO.Path.AltDirectorySeparatorChar, '/');
        }

        private static void AppendString(IncrementalHash hash, string value)
        {
            byte[] content = s_StrictUtf8.GetBytes(value);
            AppendInt32(hash, content.Length);
            hash.AppendData(content);
        }

        private static void AppendInt32(IncrementalHash hash, int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            hash.AppendData(bytes);
        }
    }
}
