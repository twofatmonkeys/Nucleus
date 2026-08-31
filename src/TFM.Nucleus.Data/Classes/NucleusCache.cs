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
    /// O(1). Population is single-flight (concurrent misses share one load). CacheLifetime is a
    /// hard expiry; CacheRefreshInterval serves stale data instantly while refreshing in the
    /// background. Hand-rolled — no external caching dependency.
    /// </summary>
    public sealed class NucleusCache
    {
        private readonly ConcurrentDictionary<Type, Lazy<CacheEntry>> _entries = [];
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
        /// immediately and a background refresh is kicked off.
        /// </summary>
        public NucleusCollection<T> GetOrLoad<T>(IObjectSchema schema, Func<NucleusCollection<T>> loader, bool forceRefresh = false)
            where T : ICoreObject<T>, new()
        {
            if (forceRefresh)
                Evict(typeof(T));

            var lazy = _entries.GetOrAdd(typeof(T), _ => NewEntry(loader));
            CacheEntry entry;
            try
            {
                entry = lazy.Value;
            }
            catch
            {
                // A failed load must not poison the cache.
                _entries.TryRemove(new KeyValuePair<Type, Lazy<CacheEntry>>(typeof(T), lazy));
                throw;
            }

            var age = _clock() - entry.PopulatedAt;
            if (schema.CacheLifetime > 0 && age >= TimeSpan.FromSeconds(schema.CacheLifetime))
            {
                // Hard expiry: reload in-line (single-flight via the replacing Lazy).
                _entries.TryRemove(new KeyValuePair<Type, Lazy<CacheEntry>>(typeof(T), lazy));
                return GetOrLoad(schema, loader);
            }

            if (schema.CacheRefreshInterval > 0 && age >= TimeSpan.FromSeconds(schema.CacheRefreshInterval) &&
                Interlocked.CompareExchange(ref entry.RefreshScheduled, 1, 0) == 0)
            {
                // Serve stale immediately; refresh in the background.
                _ = Task.Run(() =>
                {
                    try
                    {
                        _entries[typeof(T)] = NewEntry(loader);
                    }
                    catch
                    {
                        Interlocked.Exchange(ref entry.RefreshScheduled, 0);
                    }
                });
            }

            return (NucleusCollection<T>)entry.Collection;
        }

        /// <summary>O(1) lookup by ID over the cached collection; false when the type is not cached.</summary>
        public bool TryGetById<T>(Guid id, out T? obj) where T : ICoreObject<T>, new()
        {
            obj = default;
            if (!_entries.TryGetValue(typeof(T), out var lazy) || !lazy.IsValueCreated)
                return false;
            if (lazy.Value.Index.TryGetValue(id, out var found) && found is T typed)
            {
                obj = typed;
                return true;
            }
            return false;
        }

        public bool IsCached(Type entityType) =>
            _entries.TryGetValue(entityType, out var lazy) && lazy.IsValueCreated;

        public void Evict(Type entityType) => _entries.TryRemove(entityType, out _);

        public void Clear() => _entries.Clear();

        private Lazy<CacheEntry> NewEntry<T>(Func<NucleusCollection<T>> loader) where T : ICoreObject<T>, new() =>
            new(() =>
            {
                var collection = loader();
                var index = new ConcurrentDictionary<Guid, ICoreObject>();
                foreach (var obj in collection)
                    index[obj.ID] = obj;
                return new CacheEntry
                {
                    Collection = collection,
                    Index = index,
                    PopulatedAt = _clock(),
                };
            });
    }
}
