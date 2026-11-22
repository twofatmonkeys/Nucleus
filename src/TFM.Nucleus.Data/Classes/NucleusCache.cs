using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// The layered in-memory cache for [Cacheable] entity types. Each entry holds the type's
    /// whole unfiltered collection plus a Guid index over it, so LoadAll and LoadByID are both
    /// O(1). Population is single-flight (concurrent misses share one load), on the synchronous
    /// or the asynchronous path. CacheLifetime is a hard expiry; CacheRefreshInterval serves the
    /// stale entry while a background load replaces it. Hand-rolled, with no external caching
    /// dependency.
    /// </summary>
    public sealed class NucleusCache
    {
        private readonly ConcurrentDictionary<Type, Lazy<Task<CacheEntry>>> _entries = [];
        private readonly Func<DateTimeOffset> _clock;

        public NucleusCache(Func<DateTimeOffset>? clock = null)
        {
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        private sealed class CacheEntry
        {
            public required object Collection { get; init; }
            public required ConcurrentDictionary<Guid, ICoreObject> Index { get; init; }
            public required DateTimeOffset PopulatedAt { get; init; }
            public int RefreshScheduled;
        }

        /// <summary>
        /// Returns the cached collection for <typeparamref name="T"/>, loading it (single-flight)
        /// on a miss or hard expiry. Past the refresh interval the stale collection is returned
        /// immediately and a background load replaces it. A fill already running on the
        /// asynchronous path is waited for.
        /// </summary>
        public NucleusCollection<T> GetOrLoad<T>(IObjectSchema schema, Func<NucleusCollection<T>> loader, bool forceRefresh = false)
            where T : ICoreObject<T>, new()
        {
            ArgumentNullException.ThrowIfNull(loader);
            // A synchronous caller waits on the database anyway. An asynchronous fill runs on the
            // thread pool, so waiting for it here cannot block its own continuation.
            return (NucleusCollection<T>)EntryTask<T>(schema, loader, null, forceRefresh).GetAwaiter().GetResult().Collection;
        }

        /// <summary>
        /// The asynchronous form of <see cref="GetOrLoad{T}"/>: a miss is filled by
        /// <paramref name="loader"/> without blocking a thread. The fill is shared by every caller
        /// that misses meanwhile, so it runs without a caller's token and each caller stops
        /// waiting on its own; a caller that cancels leaves the fill to finish for the others.
        /// </summary>
        internal async Task<NucleusCollection<T>> GetOrLoadAsync<T>(IObjectSchema schema, Func<CancellationToken, Task<NucleusCollection<T>>> loader,
            bool forceRefresh, CancellationToken cancellationToken)
            where T : ICoreObject<T>, new()
        {
            ArgumentNullException.ThrowIfNull(loader);
            var entry = await EntryTask<T>(schema, null, loader, forceRefresh).WaitAsync(cancellationToken).ConfigureAwait(false);
            return (NucleusCollection<T>)entry.Collection;
        }

        /// <summary>
        /// The cached object with <paramref name="id"/>, filling the cache first on a miss as
        /// <see cref="GetOrLoad{T}"/> does, or the default when no row has it. The lookup reads the
        /// entry the fill returned, so an eviction between the two cannot make a stored row read
        /// as missing.
        /// </summary>
        internal T? GetOrLoadById<T>(IObjectSchema schema, Guid id, Func<NucleusCollection<T>> loader) where T : ICoreObject<T>, new()
        {
            ArgumentNullException.ThrowIfNull(loader);
            return ById<T>(EntryTask<T>(schema, loader, null, forceRefresh: false).GetAwaiter().GetResult(), id);
        }

        /// <summary>The asynchronous form of <see cref="GetOrLoadById{T}"/>, filling as <see cref="GetOrLoadAsync{T}"/> does.</summary>
        internal async Task<T?> GetOrLoadByIdAsync<T>(IObjectSchema schema, Guid id, Func<CancellationToken, Task<NucleusCollection<T>>> loader,
            CancellationToken cancellationToken) where T : ICoreObject<T>, new()
        {
            ArgumentNullException.ThrowIfNull(loader);
            return ById<T>(await EntryTask<T>(schema, null, loader, forceRefresh: false).WaitAsync(cancellationToken).ConfigureAwait(false), id);
        }

        private static T? ById<T>(CacheEntry entry, Guid id) where T : ICoreObject<T>, new() =>
            entry.Index.TryGetValue(id, out var found) && found is T typed ? typed : default;

        /// <summary>O(1) lookup by ID over the cached collection; false when the type is not cached.</summary>
        public bool TryGetById<T>(Guid id, out T? obj) where T : ICoreObject<T>, new()
        {
            obj = default;
            if (Loaded(typeof(T)) is not { } entry)
                return false;
            if (entry.Index.TryGetValue(id, out var found) && found is T typed)
            {
                obj = typed;
                return true;
            }
            return false;
        }

        public bool IsCached(Type entityType) => Loaded(entityType) != null;

        public void Evict(Type entityType) => _entries.TryRemove(entityType, out _);

        public void Clear() => _entries.Clear();

        /// <summary>The entry for a type once its load has succeeded, or null.</summary>
        private CacheEntry? Loaded(Type entityType) =>
            _entries.TryGetValue(entityType, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully
                ? lazy.Value.Result
                : null;

        /// <summary>
        /// The one lookup both paths share: the type's entry, loading it on a miss with whichever
        /// loader the caller gave, applying the hard expiry and scheduling the refresh.
        /// </summary>
        private Task<CacheEntry> EntryTask<T>(IObjectSchema schema, Func<NucleusCollection<T>>? load,
            Func<CancellationToken, Task<NucleusCollection<T>>>? loadAsync, bool forceRefresh)
            where T : ICoreObject<T>, new()
        {
            ArgumentNullException.ThrowIfNull(schema);
            var type = typeof(T);
            if (forceRefresh)
                Evict(type);

            var lazy = _entries.GetOrAdd(type, static (_, s) => s.cache.NewEntry(s.load, s.loadAsync), (cache: this, load, loadAsync));
            var task = lazy.Value;
            if (!task.IsCompleted)
                return WhenLoaded(type, lazy, task);
            if (!task.IsCompletedSuccessfully)
            {
                // A failed load must not poison the cache.
                _entries.TryRemove(new KeyValuePair<Type, Lazy<Task<CacheEntry>>>(type, lazy));
                return task;
            }

            var entry = task.Result;
            var age = _clock() - entry.PopulatedAt;
            if (schema.CacheLifetime > 0 && age >= TimeSpan.FromSeconds(schema.CacheLifetime))
            {
                // Hard expiry: reload in-line (single-flight via the replacing Lazy).
                _entries.TryRemove(new KeyValuePair<Type, Lazy<Task<CacheEntry>>>(type, lazy));
                return EntryTask(schema, load, loadAsync, forceRefresh: false);
            }

            if (schema.CacheRefreshInterval > 0 && age >= TimeSpan.FromSeconds(schema.CacheRefreshInterval) &&
                Interlocked.CompareExchange(ref entry.RefreshScheduled, 1, 0) == 0)
                Refresh(type, lazy, entry, load, loadAsync);

            return task;
        }

        /// <summary>A load still running, which removes its entry if it fails.</summary>
        private async Task<CacheEntry> WhenLoaded(Type type, Lazy<Task<CacheEntry>> lazy, Task<CacheEntry> task)
        {
            try
            {
                return await task.ConfigureAwait(false);
            }
            catch
            {
                _entries.TryRemove(new KeyValuePair<Type, Lazy<Task<CacheEntry>>>(type, lazy));
                throw;
            }
        }

        /// <summary>
        /// Loads the type again in the background while readers keep the stale entry, then swaps
        /// the new entry in, but only over the entry it refreshes: an eviction meanwhile (a save or
        /// a delete) or a newer load wins, so a refresh that read before a save cannot bring back
        /// what the save changed.
        /// </summary>
        private void Refresh<T>(Type type, Lazy<Task<CacheEntry>> stale, CacheEntry entry, Func<NucleusCollection<T>>? load,
            Func<CancellationToken, Task<NucleusCollection<T>>>? loadAsync)
            where T : ICoreObject<T>, new()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var collection = loadAsync != null ? await loadAsync(CancellationToken.None).ConfigureAwait(false) : load!();
                    _entries.TryUpdate(type, new Lazy<Task<CacheEntry>>(Task.FromResult(Build(collection))), stale);
                }
                catch
                {
                    // The stale entry stays; the next read past the interval tries again.
                    Interlocked.Exchange(ref entry.RefreshScheduled, 0);
                }
            });
        }

        /// <summary>
        /// A new, unstarted entry. A synchronous load runs on the thread of the caller that starts
        /// it; an asynchronous one on the thread pool, so it captures no caller's context and a
        /// synchronous caller can wait for it.
        /// </summary>
        private Lazy<Task<CacheEntry>> NewEntry<T>(Func<NucleusCollection<T>>? load, Func<CancellationToken, Task<NucleusCollection<T>>>? loadAsync)
            where T : ICoreObject<T>, new() =>
            new(() =>
            {
                if (loadAsync != null)
                    return Task.Run(async () => Build(await loadAsync(CancellationToken.None).ConfigureAwait(false)));
                try
                {
                    return Task.FromResult(Build(load!()));
                }
                catch (Exception ex)
                {
                    return Task.FromException<CacheEntry>(ex);
                }
            });

        private CacheEntry Build<T>(NucleusCollection<T> collection) where T : ICoreObject<T>, new()
        {
            var index = new ConcurrentDictionary<Guid, ICoreObject>();
            foreach (var obj in collection)
                index[obj.ID] = obj;
            return new CacheEntry
            {
                Collection = collection,
                Index = index,
                PopulatedAt = _clock(),
            };
        }
    }
}
