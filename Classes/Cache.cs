using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus
{
    public static class Cache
    {
        internal static readonly ConcurrentDictionary<Type, object> _Cache =[];

        public static bool TryAddToCache<T>(T obj) where T : CoreObject => TryAddToCache(obj, 1000);
        public static bool TryAddToCache<T>(T obj, int waitTimeout) where T : CoreObject
        {
            if (!obj.IsCacheable())
                return false;
            else
                return _tryAddToCache(obj, waitTimeout);
        }
        private static bool _tryAddToCache<T>(T obj, int waitTimeout) where T : CoreObject
        {
            if (_Cache.TryGetValue(obj.GetType(), out var cache))
                return ((ICache<T>)cache).TryAddToCache(obj, waitTimeout);
            Debug.WriteLine($"No Cache object exists for {obj.GetType()} at {DateTime.Now.ToString("H:mm:ss.ff")}", "Cache");
            return false;
        }
        public static Task<bool> TryAddToCacheAsync<T>(T obj) where T : CoreObject
        {
            if (!obj.ObjectSchema.IsCacheable)
                return Task.FromResult(false);
            return Task.Run(() => TryAddToCache(obj));
        }
        public static bool TryDeleteFromCache<T>(T obj) where T : CoreObject => TryDeleteFromCache(obj, 1000);
        public static bool TryDeleteFromCache<T>(T obj, int waitTimeout) where T : CoreObject
        {
            if (_Cache.TryGetValue(obj.GetType(), out var cache))
                return ((ICache<T>)cache).TryDeleteFromCache(obj, waitTimeout);
            Debug.WriteLine($"No Cache object exists for {obj.GetType()} at {DateTime.Now.ToString("H:mm:ss.ff")}", "Cache");
            return false;
        }




        internal static bool ContainsType(Type type) => _Cache.ContainsKey(type);
    }
}
