using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using SharpShader.Compilation;
using SharpShader.HLSLCrossCompiler;


namespace SharpShader.Tool
{

    internal static class ShaderArtifactPackage
    {
        public const string ManifestFileName = "manifest.json";
        private const string ArtifactsDirectoryName = "artifacts";
        private const long MaximumManifestBytes = 64L * 1024 * 1024;
        private const long MaximumArtifactBytes = 1024L * 1024 * 1024;
        private const long MaximumPackageBytes = 4L * 1024 * 1024 * 1024;

        public static void Write(
            string outputDirectory,
            ShaderProgramCompilation compilation)
        {
            ArgumentNullException.ThrowIfNull(compilation);
            string destination = Path.GetFullPath(outputDirectory);
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                throw new IOException(
                    $"Output path already exists: {destination}");
            }

            string? parent = Path.GetDirectoryName(destination);
            if (string.IsNullOrWhiteSpace(parent))
            {
                throw new IOException(
                    $"Output directory has no parent: {destination}");
            }

            Directory.CreateDirectory(parent);
            string staging = Path.Combine(
                parent,
                $".{Path.GetFileName(destination)}.tmp-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            try
            {
                WriteStaged(staging, compilation);
                Directory.Move(staging, destination);
            }
            catch
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }

                throw;
            }
        }

        public static ShaderInterfaceManifest LoadManifest(string path)
        {
            string manifestPath = ResolveManifestPath(path);
            byte[] bytes = ReadFileBounded(
                manifestPath,
                MaximumManifestBytes,
                "Shader interface manifest");
            return ShaderInterfaceManifestSerializer.Deserialize(bytes);
        }

        public static void Validate(string path)
        {
            string manifestPath = ResolveManifestPath(path);
            string? packageDirectory = Path.GetDirectoryName(manifestPath);
            if (string.IsNullOrWhiteSpace(packageDirectory))
            {
                throw new InvalidDataException(
                    $"Shader package manifest has no parent directory: {manifestPath}");
            }

            ValidatePackageRoot(packageDirectory, manifestPath);
            ShaderInterfaceManifest manifest = LoadManifest(manifestPath);
            string artifactsDirectory = Path.Combine(
                packageDirectory,
                ArtifactsDirectoryName);
            StringComparer fileNameComparer =
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal;
            Dictionary<string, ShaderArtifactIdentity> expectedFiles =
                new(fileNameComparer);
            foreach (ShaderInterfaceVariant variant in manifest.Variants)
            {
                foreach (ShaderInterfaceEntry entry in variant.Entries)
                {
                    foreach (ShaderArtifactIdentity artifact in entry.Artifacts)
                    {
                        string fileName = GetArtifactFileName(artifact);
                        if (expectedFiles.TryGetValue(
                                fileName,
                                out ShaderArtifactIdentity? existing))
                        {
                            if (!existing.Equals(artifact))
                            {
                                throw new InvalidDataException(
                                    $"Manifest maps incompatible artifacts to {fileName}.");
                            }
                        }
                        else
                        {
                            expectedFiles.Add(fileName, artifact);
                        }
                    }
                }
            }

            long packageBytes = CheckedPackageAdd(
                0,
                new FileInfo(manifestPath).Length);
            int actualEntryCount = 0;
            int expectedEntryCount = expectedFiles.Count;
            foreach (string actualEntry in Directory.EnumerateFileSystemEntries(
                         artifactsDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                ++actualEntryCount;
                if (actualEntryCount > expectedEntryCount)
                {
                    throw new InvalidDataException(
                        "Shader artifact package contains unexpected entries.");
                }

                string fileName = Path.GetFileName(actualEntry);
                if (!expectedFiles.Remove(
                        fileName,
                        out ShaderArtifactIdentity? identity))
                {
                    throw new InvalidDataException(
                        $"Shader artifact package contains unexpected entry {actualEntry}.");
                }

                packageBytes = CheckedPackageAdd(
                    packageBytes,
                    ValidateArtifactFile(actualEntry, identity));
            }

            if (actualEntryCount != expectedEntryCount
                || expectedFiles.Count != 0)
            {
                throw new InvalidDataException(
                    "Shader artifact package contains missing artifact files.");
            }
        }

        public static void WriteSummary(
            ShaderInterfaceManifest manifest,
            TextWriter output)
        {
            output.WriteLine($"schema: {manifest.SchemaVersion}");
            output.WriteLine($"source: {manifest.SourceDigest}");
            output.WriteLine($"toolchain-components: {manifest.ToolchainComponents.Count}");
            output.WriteLine($"logical-layouts: {manifest.LogicalLayouts.Count}");
            output.WriteLine($"variants: {manifest.Variants.Count}");
            foreach (ShaderInterfaceVariant variant in manifest.Variants)
            {
                output.WriteLine(
                    $"variant {variant.Key} entries={variant.Entries.Count}");
                foreach (ShaderInterfaceEntry entry in variant.Entries)
                {
                    output.WriteLine(
                        $"  {entry.Stage}:{entry.Name} "
                        + $"layout={entry.LogicalLayoutSignature} "
                        + $"artifacts={entry.Artifacts.Count}");
                    foreach (ShaderArtifactIdentity artifact in entry.Artifacts)
                    {
                        output.WriteLine(
                            $"    {artifact.ArtifactKind} "
                            + $"{artifact.ContentDigest} "
                            + $"{artifact.ByteLength}");
                    }
                }
            }

            foreach (ShaderInterfaceLayout layout in manifest.LogicalLayouts)
            {
                output.WriteLine(
                    $"layout {layout.Signature} bindings={layout.Bindings.Count}");
                foreach (ShaderLogicalBinding binding in layout.Bindings)
                {
                    output.WriteLine(
                        $"  logical table={binding.Key.Table} "
                        + $"slot={binding.Key.Slot} "
                        + $"type={binding.Key.Type} "
                        + $"name={binding.CanonicalName} "
                        + $"kind={binding.Shape.Kind} "
                        + $"dimension={binding.Shape.Dimension} "
                        + $"access={binding.Shape.Access} "
                        + $"stages={binding.StageMask}");
                }
            }

            foreach (ShaderBackendLayouts layouts in manifest.BackendLayouts)
            {
                output.WriteLine(
                    $"backend-layout {layouts.LogicalLayoutSignature}");
                if (layouts.Dx12 is not null)
                {
                    foreach (Dx12ShaderBindingMapping mapping in layouts.Dx12.Bindings)
                    {
                        output.WriteLine(
                            $"  dx12 {mapping.LogicalBinding} -> "
                            + $"{GetRegisterPrefix(mapping.RegisterClass)}"
                            + $"{mapping.ShaderRegister},space{mapping.RegisterSpace}");
                    }
                }

                if (layouts.Vulkan is not null)
                {
                    foreach (VulkanShaderBindingMapping mapping in layouts.Vulkan.Bindings)
                    {
                        output.WriteLine(
                            $"  vulkan {mapping.LogicalBinding} -> "
                            + $"set={mapping.DescriptorSet},binding={mapping.Binding},"
                            + $"kind={mapping.DescriptorKind}");
                    }
                }

                if (layouts.Metal is not null)
                {
                    foreach (MetalDirectBindingMapping mapping in layouts.Metal.DirectBindings)
                    {
                        output.WriteLine(
                            $"  metal-direct {mapping.LogicalBinding} -> "
                            + $"table={mapping.BindingTable},"
                            + $"namespace={mapping.Namespace},index={mapping.Index}");
                    }

                    foreach (MetalReferenceBufferBindingMapping mapping in
                             layouts.Metal.ReferenceBufferBindings)
                    {
                        output.WriteLine(
                            $"  metal-reference {mapping.LogicalBinding} -> "
                            + $"table={mapping.BindingTable},"
                            + $"buffer={mapping.ReferenceBufferIndex},"
                            + $"offset={mapping.ByteOffset},"
                            + $"count={mapping.ReferenceCount},"
                            + $"namespace={mapping.ResourceNamespace}");
                    }
                }
            }
        }

        private static void WriteStaged(
            string stagingDirectory,
            ShaderProgramCompilation compilation)
        {
            string artifactsDirectory = Path.Combine(
                stagingDirectory,
                ArtifactsDirectoryName);
            Directory.CreateDirectory(artifactsDirectory);
            byte[] manifestBytes =
                ShaderInterfaceManifestSerializer.SerializeToUtf8Bytes(
                    compilation.Manifest);
            if (manifestBytes.LongLength > MaximumManifestBytes)
            {
                throw new InvalidDataException(
                    $"Shader interface manifest exceeds the "
                    + $"{MaximumManifestBytes}-byte limit.");
            }
            long packageBytes = manifestBytes.LongLength;

            Dictionary<ArtifactLookupKey, ShaderArtifactIdentity> identities =
                CollectManifestArtifacts(compilation.Manifest);
            HashSet<string> writtenFiles = new(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);
            foreach (ShaderProgramArtifact artifact in compilation.Artifacts)
            {
                ArtifactLookupKey key = new(
                    artifact.VariantKey,
                    artifact.EntryPoint,
                    artifact.Stage,
                    artifact.Identity.ArtifactKind);
                if (!identities.TryGetValue(
                        key,
                        out ShaderArtifactIdentity? expected)
                    || !expected.Equals(artifact.Identity))
                {
                    throw new InvalidDataException(
                        $"Compiled artifact {key} does not match the manifest.");
                }

                string fileName = GetArtifactFileName(expected);
                string artifactPath = Path.Combine(
                    artifactsDirectory,
                    fileName);
                if (writtenFiles.Add(fileName))
                {
                    if (expected.ByteLength > (ulong)MaximumArtifactBytes)
                    {
                        throw new InvalidDataException(
                            $"Shader artifact {fileName} exceeds the "
                            + $"{MaximumArtifactBytes}-byte limit.");
                    }

                    packageBytes = CheckedPackageAdd(
                        packageBytes,
                        checked((long)expected.ByteLength));
                    using FileStream stream = new(
                        artifactPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 64 * 1024,
                        FileOptions.WriteThrough);
                    artifact.CopyContentTo(stream);
                    stream.Flush(flushToDisk: true);
                }
                else
                {
                    _ = ValidateArtifactFile(artifactPath, expected);
                }

                identities.Remove(key);
            }

            if (identities.Count != 0)
            {
                throw new InvalidDataException(
                    "The manifest references artifacts that were not returned by the compiler.");
            }

            File.WriteAllBytes(
                Path.Combine(stagingDirectory, ManifestFileName),
                manifestBytes);
        }

        private static Dictionary<ArtifactLookupKey, ShaderArtifactIdentity>
            CollectManifestArtifacts(ShaderInterfaceManifest manifest)
        {
            Dictionary<ArtifactLookupKey, ShaderArtifactIdentity> result = new();
            foreach (ShaderInterfaceVariant variant in manifest.Variants)
            {
                foreach (ShaderInterfaceEntry entry in variant.Entries)
                {
                    foreach (ShaderArtifactIdentity artifact in entry.Artifacts)
                    {
                        ArtifactLookupKey key = new(
                            variant.Key,
                            entry.Name,
                            entry.Stage,
                            artifact.ArtifactKind);
                        if (!result.TryAdd(key, artifact))
                        {
                            throw new InvalidDataException(
                                $"Manifest contains duplicate artifact {key}.");
                        }
                    }
                }
            }

            return result;
        }

        private static long ValidateArtifactFile(
            string path,
            ShaderArtifactIdentity identity)
        {
            FileInfo file = new(path);
            if (!file.Exists)
            {
                throw new FileNotFoundException(
                    "Shader artifact is missing.",
                    path);
            }

            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0
                || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Shader artifact must be a regular file: {path}");
            }

            if (file.Length > MaximumArtifactBytes
                || identity.ByteLength > (ulong)MaximumArtifactBytes)
            {
                throw new InvalidDataException(
                    $"Shader artifact {path} exceeds the "
                    + $"{MaximumArtifactBytes}-byte limit.");
            }

            if (checked((ulong)file.Length) != identity.ByteLength)
            {
                throw new InvalidDataException(
                    $"Shader artifact length mismatch: {path}");
            }

            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            string digest = Convert.ToHexStringLower(
                SHA256.HashData(stream));
            if (!string.Equals(
                    digest,
                    identity.ContentDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Shader artifact digest mismatch: {path}");
            }

            return file.Length;
        }

        private static string ResolveManifestPath(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (Directory.Exists(fullPath))
            {
                fullPath = Path.Combine(fullPath, ManifestFileName);
            }
            else if (!string.Equals(
                         Path.GetFileName(fullPath),
                         ManifestFileName,
                         StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Shader package manifest must be named {ManifestFileName}.");
            }

            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException(
                    "Shader interface manifest was not found.",
                    fullPath);
            }

            FileAttributes attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.Directory) != 0
                || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Shader package manifest must be a regular file: {fullPath}");
            }

            return fullPath;
        }
        private static void ValidatePackageRoot(
            string packageDirectory,
            string manifestPath)
        {
            FileAttributes rootAttributes =
                File.GetAttributes(packageDirectory);
            if ((rootAttributes & FileAttributes.Directory) == 0
                || (rootAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Shader package root must be a regular directory: {packageDirectory}");
            }

            string artifactsDirectory = Path.Combine(
                packageDirectory,
                ArtifactsDirectoryName);
            int entryCount = 0;
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                         packageDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                ++entryCount;
                string name = Path.GetFileName(entry);
                if (!string.Equals(
                        name,
                        ManifestFileName,
                        StringComparison.Ordinal)
                    && !string.Equals(
                        name,
                        ArtifactsDirectoryName,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Shader package contains unexpected root entry {entry}.");
                }

                if (entryCount > 2)
                {
                    throw new InvalidDataException(
                        "Shader package root contains more than two entries.");
                }
            }

            if (entryCount != 2
                || !File.Exists(manifestPath)
                || !Directory.Exists(artifactsDirectory))
            {
                throw new InvalidDataException(
                    "Shader package root must contain exactly manifest.json and artifacts.");
            }

            FileAttributes artifactAttributes =
                File.GetAttributes(artifactsDirectory);
            if ((artifactAttributes & FileAttributes.Directory) == 0
                || (artifactAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Shader artifacts path must be a regular directory: {artifactsDirectory}");
            }
        }

        private static byte[] ReadFileBounded(
            string path,
            long maximumBytes,
            string description)
        {
            FileInfo file = new(path);
            if (file.Length <= 0
                || file.Length > maximumBytes
                || file.Length > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"{description} {path} has invalid length {file.Length}; "
                    + $"the limit is {maximumBytes} bytes.");
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
                throw new InvalidDataException(
                    $"{description} {path} changed while being read.");
            }

            return bytes;
        }

        private static long CheckedPackageAdd(
            long current,
            long additional)
        {
            if (additional < 0
                || current > MaximumPackageBytes - additional)
            {
                throw new InvalidDataException(
                    $"Shader artifact package exceeds the "
                    + $"{MaximumPackageBytes}-byte limit.");
            }

            return current + additional;
        }


        private static string GetArtifactFileName(
            ShaderArtifactIdentity artifact)
        {
            string extension = artifact.ArtifactKind switch
            {
                ShaderArtifactKind.Dxil => ".dxil",
                ShaderArtifactKind.SpirV => ".spv",
                ShaderArtifactKind.MslSource => ".metal",
                ShaderArtifactKind.MetalLibrary => ".metallib",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(artifact),
                    artifact.ArtifactKind,
                    "Shader artifact kind is not defined."),
            };
            return artifact.ContentDigest.ToLowerInvariant() + extension;
        }

        private static char GetRegisterPrefix(ShaderBindingClass bindingClass)
        {
            return bindingClass switch
            {
                ShaderBindingClass.ShaderResource => 't',
                ShaderBindingClass.Sampler => 's',
                ShaderBindingClass.ConstantBuffer => 'b',
                ShaderBindingClass.UnorderedAccess => 'u',
                _ => throw new ArgumentOutOfRangeException(
                    nameof(bindingClass),
                    bindingClass,
                    "Shader binding class is not defined."),
            };
        }

        private readonly record struct ArtifactLookupKey(
            string Variant,
            string EntryPoint,
            ShaderExecutionStage Stage,
            ShaderArtifactKind Kind);
    }
}
