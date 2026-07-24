using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SharpShader.Compilation.Internal;

namespace SharpShader.Compilation
{
    public sealed class ShaderProgramCompiler
    {
        private readonly ShaderProgramCompilerOptions m_Options;
        private readonly ShaderProgramCompilerExecutionContext? m_ExecutionContext;
        private readonly ShaderProgramPersistentCache? m_PersistentCache;
        private readonly object m_MemoryCacheSync = new();
        private readonly Dictionary<string, MemoryCacheEntry> m_MemoryCache =
            new(StringComparer.Ordinal);
        private readonly LinkedList<MemoryCacheEntry> m_MemoryInsertionOrder = new();
        private readonly ConcurrentDictionary<string, Lazy<Task<ShaderProgramCompilation>>> m_Flights = new();
        private readonly Dictionary<string, MemoryDependencyEntry>
            m_DependencyIndex = new(StringComparer.Ordinal);
        private readonly LinkedList<MemoryDependencyEntry> m_DependencyInsertionOrder =
            new();
        private long m_MemoryCacheBytes;
        private long m_InsertionSequence;

        public ShaderProgramCompiler(ShaderProgramCompilerOptions? options = null)
            : this(options, null)
        {
        }

        internal ShaderProgramCompiler(
            ShaderProgramCompilerOptions? options,
            ShaderProgramCompilerExecutionContext? executionContext)
        {
            m_Options = options ?? new ShaderProgramCompilerOptions();
            m_ExecutionContext = executionContext;
            if (m_Options.PersistentCacheDirectory is not null)
            {
                m_PersistentCache = new ShaderProgramPersistentCache(
                    m_Options.PersistentCacheDirectory,
                    m_Options.CacheLimits);
            }
        }

        public ShaderProgramCompilation Compile(
            ShaderProgramCompileRequest request,
            CancellationToken cancellationToken = default)
        {
            return CompileAsync(request, cancellationToken)
                .GetAwaiter()
                .GetResult();
        }

        public async Task<ShaderProgramCompilation> CompileAsync(
            ShaderProgramCompileRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            ShaderProgramInputSnapshot snapshot = ShaderProgramInputSnapshot.Create(
                request,
                m_Options.CacheLimits,
                m_ExecutionContext);
            cancellationToken.ThrowIfCancellationRequested();

            if (TryGetWarmCached(
                    snapshot,
                    out ShaderProgramCompilation cached))
            {
                return cached;
            }

            Lazy<Task<ShaderProgramCompilation>> flight = m_Flights.GetOrAdd(
                snapshot.ProvisionalKey,
                _ => new Lazy<Task<ShaderProgramCompilation>>(
                    () => Task.Run(
                        () => LoadOrCompile(snapshot),
                        CancellationToken.None),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            Task<ShaderProgramCompilation> task = flight.Value;
            _ = task.ContinueWith(
                (_, state) =>
                {
                    FlightRemoval removal = (FlightRemoval)state!;
                    removal.Compiler.m_Flights.TryRemove(
                        new KeyValuePair<string, Lazy<Task<ShaderProgramCompilation>>>(
                            removal.CacheKey,
                            removal.Flight));
                },
                new FlightRemoval(this, snapshot.ProvisionalKey, flight),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private ShaderProgramCompilation LoadOrCompile(
            ShaderProgramInputSnapshot snapshot)
        {
            if (TryGetWarmCached(
                    snapshot,
                    out ShaderProgramCompilation warmCached))
            {
                return warmCached;
            }

            ShaderProgramCompilationOutput output =
                ShaderProgramCompilationPipeline.Compile(
                    snapshot,
                    m_ExecutionContext,
                    CancellationToken.None);
            if (output.Dependencies.IsWarmable)
            {
                m_PersistentCache?.Store(
                    output.Compilation,
                    output.Dependencies);
                StoreMemoryPair(output.Compilation, output.Dependencies);
            }

            return output.Compilation;
        }

        private bool TryGetWarmCached(
            ShaderProgramInputSnapshot snapshot,
            out ShaderProgramCompilation compilation)
        {
            ShaderProgramDependencySnapshot? dependencies =
                TryGetMemoryDependencies(snapshot.ProvisionalKey);
            if (dependencies is not null)
            {
                if (!dependencies.ValidateWarmState(m_Options.CacheLimits))
                {
                    compilation = null!;
                    return false;
                }

                if (TryGetMemoryCached(
                        dependencies.FinalKey,
                        out compilation))
                {
                    return true;
                }
            }

            return TryGetPersistentWarmPair(snapshot, out compilation);
        }

        private bool TryGetPersistentWarmPair(
            ShaderProgramInputSnapshot snapshot,
            out ShaderProgramCompilation compilation)
        {
            if (m_PersistentCache is null
                || !m_PersistentCache.TryLoadPair(
                    snapshot.ProvisionalKey,
                    out ShaderProgramDependencySnapshot? dependencies,
                    out ShaderProgramCompilation? persistentCompilation)
                || dependencies is null
                || persistentCompilation is null
                || !dependencies.ValidateWarmState(m_Options.CacheLimits))
            {
                compilation = null!;
                return false;
            }

            StoreMemoryPair(persistentCompilation, dependencies);
            compilation = persistentCompilation;
            return true;
        }

        private ShaderProgramDependencySnapshot? TryGetMemoryDependencies(
            string provisionalKey)
        {
            lock (m_MemoryCacheSync)
            {
                return m_DependencyIndex.TryGetValue(
                    provisionalKey,
                    out MemoryDependencyEntry? entry)
                    ? entry.Dependencies
                    : null;
            }
        }

        private void StoreMemoryPair(
            ShaderProgramCompilation compilation,
            ShaderProgramDependencySnapshot dependencies)
        {
            long compilationSize = EstimateMemorySize(compilation);
            long dependencySize = EstimateDependencyMemorySize(dependencies);
            long pairSize;
            try
            {
                pairSize = checked(compilationSize + dependencySize);
            }
            catch (OverflowException)
            {
                return;
            }

            if (pairSize > m_Options.CacheLimits.MaximumMemoryCacheBytes)
            {
                return;
            }

            lock (m_MemoryCacheSync)
            {
                RemoveMemoryArtifact(compilation.CacheKey);
                RemoveMemoryDependency(dependencies.ProvisionalKey);

                while (m_MemoryCache.Count
                       >= m_Options.CacheLimits.MaximumMemoryCacheEntries)
                {
                    if (!EvictOldestMemoryArtifact())
                    {
                        throw new InvalidOperationException(
                            "The shader artifact memory-cache order is inconsistent.");
                    }
                }

                while (m_DependencyIndex.Count
                       >= m_Options.CacheLimits.MaximumMemoryCacheEntries)
                {
                    if (!EvictOldestMemoryDependency())
                    {
                        throw new InvalidOperationException(
                            "The shader dependency memory-cache order is inconsistent.");
                    }
                }

                while (m_MemoryCacheBytes
                       > m_Options.CacheLimits.MaximumMemoryCacheBytes - pairSize)
                {
                    if (!EvictOldestMemoryEntry())
                    {
                        throw new InvalidOperationException(
                            "The shader memory-cache byte accounting is inconsistent.");
                    }
                }

                MemoryCacheEntry artifactEntry = new(
                    compilation,
                    compilationSize,
                    ++m_InsertionSequence);
                MemoryDependencyEntry dependencyEntry = new(
                    dependencies,
                    dependencySize,
                    ++m_InsertionSequence);
                m_MemoryCache.Add(compilation.CacheKey, artifactEntry);
                artifactEntry.OrderNode =
                    m_MemoryInsertionOrder.AddLast(artifactEntry);
                m_DependencyIndex.Add(
                    dependencies.ProvisionalKey,
                    dependencyEntry);
                dependencyEntry.OrderNode =
                    m_DependencyInsertionOrder.AddLast(dependencyEntry);
                m_MemoryCacheBytes += pairSize;
            }
        }

        private bool TryGetMemoryCached(
            string cacheKey,
            out ShaderProgramCompilation compilation)
        {
            lock (m_MemoryCacheSync)
            {
                if (m_MemoryCache.TryGetValue(
                        cacheKey,
                        out MemoryCacheEntry? entry))
                {
                    compilation = entry.Compilation;
                    return true;
                }
            }

            compilation = null!;
            return false;
        }

        private bool RemoveMemoryArtifact(string cacheKey)
        {
            if (!m_MemoryCache.Remove(
                    cacheKey,
                    out MemoryCacheEntry? entry))
            {
                return false;
            }

            LinkedListNode<MemoryCacheEntry> node =
                entry.OrderNode
                ?? throw new InvalidOperationException(
                    "The shader artifact memory-cache order is inconsistent.");
            m_MemoryInsertionOrder.Remove(node);
            entry.OrderNode = null;
            m_MemoryCacheBytes -= entry.Size;
            return true;
        }

        private bool RemoveMemoryDependency(string provisionalKey)
        {
            if (!m_DependencyIndex.Remove(
                    provisionalKey,
                    out MemoryDependencyEntry? entry))
            {
                return false;
            }

            LinkedListNode<MemoryDependencyEntry> node =
                entry.OrderNode
                ?? throw new InvalidOperationException(
                    "The shader dependency memory-cache order is inconsistent.");
            m_DependencyInsertionOrder.Remove(node);
            entry.OrderNode = null;
            m_MemoryCacheBytes -= entry.Size;
            return true;
        }

        private bool EvictOldestMemoryEntry()
        {
            MemoryCacheEntry? artifact = PeekOldestMemoryArtifact();
            MemoryDependencyEntry? dependency = PeekOldestMemoryDependency();
            if (artifact is null)
            {
                return dependency is not null
                    && EvictOldestMemoryDependency();
            }

            if (dependency is null
                || artifact.Sequence <= dependency.Sequence)
            {
                return EvictOldestMemoryArtifact();
            }

            return EvictOldestMemoryDependency();
        }

        private MemoryCacheEntry? PeekOldestMemoryArtifact()
        {
            return m_MemoryInsertionOrder.First?.Value;
        }

        private MemoryDependencyEntry? PeekOldestMemoryDependency()
        {
            return m_DependencyInsertionOrder.First?.Value;
        }

        private bool EvictOldestMemoryArtifact()
        {
            MemoryCacheEntry? entry = PeekOldestMemoryArtifact();
            if (entry is null)
            {
                return false;
            }

            return RemoveMemoryArtifact(entry.Compilation.CacheKey);
        }

        private bool EvictOldestMemoryDependency()
        {
            MemoryDependencyEntry? entry = PeekOldestMemoryDependency();
            if (entry is null)
            {
                return false;
            }

            return RemoveMemoryDependency(entry.Dependencies.ProvisionalKey);
        }

        private static long EstimateMemorySize(
            ShaderProgramCompilation compilation)
        {
            try
            {
                long size = ShaderInterfaceManifestSerializer
                    .SerializeToUtf8Bytes(compilation.Manifest)
                    .LongLength;
                foreach (ShaderProgramArtifact artifact in compilation.Artifacts)
                {
                    size = checked(size + checked((long)artifact.Identity.ByteLength));
                }

                return size;
            }
            catch (OverflowException)
            {
                return long.MaxValue;
            }
        }

        private static long EstimateDependencyMemorySize(
            ShaderProgramDependencySnapshot dependencies)
        {
            try
            {
                long size = checked(
                    256L
                    + EstimateStringMemory(dependencies.ProvisionalKey)
                    + EstimateStringMemory(dependencies.FinalKey));
                foreach (ShaderProgramDependencyFile file in dependencies.Files)
                {
                    size = checked(
                        size
                        + 96L
                        + EstimateStringMemory(file.Path)
                        + EstimateStringMemory(file.ContentDigest));
                }

                foreach (ShaderProgramDirectoryTopology topology
                         in dependencies.Topologies)
                {
                    size = checked(
                        size
                        + 96L
                        + EstimateStringMemory(topology.RootPath)
                        + EstimateStringMemory(topology.Digest));
                }

                return size;
            }
            catch (OverflowException)
            {
                return long.MaxValue;
            }
        }

        private static long EstimateStringMemory(string value) =>
            checked(24L + checked(value.Length * 2L));

        private sealed class MemoryCacheEntry
        {
            public ShaderProgramCompilation Compilation { get; }
            public long Size { get; }
            public long Sequence { get; }
            public LinkedListNode<MemoryCacheEntry>? OrderNode { get; set; }

            public MemoryCacheEntry(
                ShaderProgramCompilation compilation,
                long size,
                long sequence)
            {
                Compilation = compilation;
                Size = size;
                Sequence = sequence;
            }
        }

        private sealed class MemoryDependencyEntry
        {
            public ShaderProgramDependencySnapshot Dependencies { get; }
            public long Size { get; }
            public long Sequence { get; }
            public LinkedListNode<MemoryDependencyEntry>? OrderNode { get; set; }

            public MemoryDependencyEntry(
                ShaderProgramDependencySnapshot dependencies,
                long size,
                long sequence)
            {
                Dependencies = dependencies;
                Size = size;
                Sequence = sequence;
            }
        }

        private sealed class FlightRemoval
        {
            public ShaderProgramCompiler Compiler { get; }
            public string CacheKey { get; }
            public Lazy<Task<ShaderProgramCompilation>> Flight { get; }

            public FlightRemoval(
                ShaderProgramCompiler compiler,
                string cacheKey,
                Lazy<Task<ShaderProgramCompilation>> flight)
            {
                Compiler = compiler;
                CacheKey = cacheKey;
                Flight = flight;
            }
        }
    }
}
