using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// The layered in-memory cache for [Cacheable] entity types. Each entry holds the type's
    /// whole unfiltered rows plus a Guid index over them, so LoadAll and LoadByID are both
    /// O(1). Population is single-flight (concurrent misses share one load), on the synchronous
    /// or the asynchronous path. CacheLifetime is a hard expiry; CacheRefreshInterval serves the
    /// stale entry while a background load replaces it, and a failed refresh waits one interval
    /// before the next. The rows are an array only the cache holds: every list it hands out is a
    /// copy over the shared objects. Hand-rolled, with no external caching dependency.
    /// </summary>
    public sealed class NucleusCache
    {
        private readonly ConcurrentDictionary<Type, Lazy<Task<CacheEntry>>> _entries = [];
        private readonly Func<DateTimeOffset> _clock;

        public NucleusCache(Func<DateTimeOffset>? clock = null)
        {
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Where a failed background refresh is reported. The manager sets it to its own logger
        /// when it has one and the cache has none, so a cache shared by several managers keeps
        /// the first one's.
        /// </summary>
        internal ILogger? Logger { get; set; }

        private sealed class CacheEntry
        {
            /// <summary>The rows, a <c>T[]</c> no caller holds, so nothing outside can change it.</summary>
            public required object Rows { get; init; }
            public required ConcurrentDictionary<Guid, ICoreObject> Index { get; init; }
            public required DateTimeOffset PopulatedAt { get; init; }
            public int RefreshScheduled;

            /// <summary>
            /// The earliest time, in UTC ticks, a refresh may start: the population time, and one
            /// refresh interval after a failed refresh, so a database that is down sees at most
            /// one background load per interval.
            /// </summary>
            public long NextRefreshTicks;
        }

        /// <summary>
        /// Returns the cached rows of <typeparamref name="T"/> as a list of the caller's own over
        /// the shared objects, loading them (single-flight) on a miss or hard expiry. Past the
        /// refresh interval the stale rows are returned immediately and a background load replaces
        /// them. A fill already running on the asynchronous path is waited for.
        /// </summary>
        public NucleusCollection<T> GetOrLoad<T>(IObjectSchema schema, Func<NucleusCollection<T>> loader, bool forceRefresh = false)
            where T : ICoreObject<T>, new()
        {
            ArgumentNullException.ThrowIfNull(loader);
            // A synchronous caller waits on the database anyway. An asynchronous fill runs on the
            // thread pool, so waiting for it here cannot block its own continuation.
            return CopyOf(RowsOf<T>(EntryTask<T>(schema, loader, null, forceRefresh).GetAwaiter().GetResult()));
        }

        /// <summary>
        /// The asynchronous form of <see cref="GetOrLoad{T}"/>: a miss is filled by
        /// <paramref name="loader"/> without blocking a thread. The fill is shared by every caller
        /// that misses meanwhile, so it runs without a caller's token and each caller stops
        /// waiting on its own; a caller that cancels leaves the fill to finish for the others.
        /// </summary>
        internal async Task<NucleusCollection<T>> GetOrLoadAsync<T>(IObjectSchema schema, Func<CancellationToken, Task<NucleusCollection<T>>> loader,
            bool forceRefresh, CancellationToken cancellationToken)
            where T : ICoreObject<T>, new() =>
            CopyOf(await RowsAsync<T>(schema, loader, forceRefresh, cancellationToken).ConfigureAwait(false));

        /// <summary>
        /// The cached rows themselves, filled as <see cref="GetOrLoadAsync{T}"/> fills them, with
        /// no copy: for a reader that only reads, such as a stream. Nothing changes the array once
        /// it is built.
        /// </summary>
        internal async Task<IReadOnlyList<T>> RowsAsync<T>(IObjectSchema schema, Func<CancellationToken, Task<NucleusCollection<T>>> loader,
            bool forceRefresh, CancellationToken cancellationToken)
            where T : ICoreObject<T>, new()
        {
            ArgumentNullException.ThrowIfNull(loader);
            return RowsOf<T>(await EntryTask<T>(schema, null, loader, forceRefresh).WaitAsync(cancellationToken).ConfigureAwait(false));
        }

        private static T[] RowsOf<T>(CacheEntry entry) => (T[])entry.Rows;

        /// <summary>
        /// A list of the caller's own over the cached objects, so adding to or removing from it
        /// changes nothing for any other reader. The objects are shared.
        /// </summary>
        private static NucleusCollection<T> CopyOf<T>(IReadOnlyList<T> rows) where T : ICoreObject<T>, new()
        {
            var copy = new NucleusCollection<T>();
            // The rows are always the cache's array, which List<T>.AddRange copies in one step;
            // the collection's own Add raises an event per object.
            ((List<T>)copy).AddRange(rows);
            return copy;
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
            var now = _clock();
            var age = now - entry.PopulatedAt;
            if (schema.CacheLifetime > 0 && age >= TimeSpan.FromSeconds(schema.CacheLifetime))
            {
                // Hard expiry: reload in-line (single-flight via the replacing Lazy).
                _entries.TryRemove(new KeyValuePair<Type, Lazy<Task<CacheEntry>>>(type, lazy));
                return EntryTask(schema, load, loadAsync, forceRefresh: false);
            }

            if (schema.CacheRefreshInterval > 0)
            {
                var interval = TimeSpan.FromSeconds(schema.CacheRefreshInterval);
                if (age >= interval && now.UtcTicks >= Volatile.Read(ref entry.NextRefreshTicks) &&
                    Interlocked.CompareExchange(ref entry.RefreshScheduled, 1, 0) == 0)
                    Refresh(type, lazy, entry, interval, load, loadAsync);
            }

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
        /// what the save changed. A failed refresh keeps the stale entry, is logged, and lets the
        /// next refresh start only one <paramref name="interval"/> later: retried on the next
        /// read, a database that was down took a background load per cached read.
        /// </summary>
        private void Refresh<T>(Type type, Lazy<Task<CacheEntry>> stale, CacheEntry entry, TimeSpan interval, Func<NucleusCollection<T>>? load,
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
                catch (Exception ex)
                {
                    // The wait is written before the flag is reset, so no reader sees the reset
                    // without it.
                    Volatile.Write(ref entry.NextRefreshTicks, (_clock() + interval).UtcTicks);
                    Interlocked.Exchange(ref entry.RefreshScheduled, 0);
                    Logger?.LogWarning(ex,
                        "Refreshing the cached {EntityType} rows failed. The cached rows are still served, and the next refresh starts in {Seconds} s.",
                        type.FullName, interval.TotalSeconds);
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
            // The loader's list is not kept: the cache reads only an array no caller holds.
            T[] rows = [.. collection];
            var index = new ConcurrentDictionary<Guid, ICoreObject>();
            foreach (var obj in rows)
                index[obj.ID] = obj;
            var populatedAt = _clock();
            return new CacheEntry
            {
                Rows = rows,
                Index = index,
                PopulatedAt = populatedAt,
                NextRefreshTicks = populatedAt.UtcTicks,
            };
        }
    }
}
