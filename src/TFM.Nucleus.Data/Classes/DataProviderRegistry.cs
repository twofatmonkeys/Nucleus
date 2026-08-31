using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// Routes entity types to data providers. Each provider is registered against a root entity
    /// base type; an entity resolves to the provider whose root is its nearest ancestor. A
    /// provider registered without a root is the fallback for otherwise-unclaimed types.
    /// Resolution is memoized per entity type.
    /// </summary>
    public sealed class DataProviderRegistry : IDataProviderRegistry
    {
        private readonly IReadOnlyList<IDataProvider> _providers;
        private readonly ConcurrentDictionary<Type, IDataProvider> _resolved = [];

        public DataProviderRegistry(IEnumerable<IDataProvider> providers)
        {
            _providers = [.. providers];
            if (_providers.Count == 0)
                throw new InvalidOperationException("No data providers are registered. Call AddNucleusSQLDataProvider (or another provider registration) before building the service provider.");
        }

        public IReadOnlyList<IDataProvider> AllProviders => _providers;

        /// <summary>The fallback provider for single-provider applications (first registered when none is unrooted).</summary>
        public IDataProvider DefaultProvider =>
            _providers.FirstOrDefault(p => p.RootType == null) ?? _providers[0];

        public IDataProvider GetProvider<T>() where T : ICoreObject => GetProvider(typeof(T));

        public IDataProvider GetProvider(Type entityType) => _resolved.GetOrAdd(entityType, Resolve);

        public bool IsOwnedBy(Type entityType, IDataProvider provider) =>
            ReferenceEquals(GetProvider(entityType), provider);

        private IDataProvider Resolve(Type entityType)
        {
            IDataProvider? nearest = null;
            var nearestDepth = int.MaxValue;
            foreach (var provider in _providers)
            {
                if (provider.RootType == null)
                    continue;
                var depth = InheritanceDepth(entityType, provider.RootType);
                if (depth >= 0 && depth < nearestDepth)
                {
                    nearest = provider;
                    nearestDepth = depth;
                }
            }
            nearest ??= _providers.FirstOrDefault(p => p.RootType == null);
            return nearest ?? throw new InvalidOperationException(
                $"No data provider is registered for entity type {entityType.FullName}. " +
                "Register a provider for one of its base types, or register an unrooted default provider.");
        }

        /// <summary>True when <paramref name="entityType"/> falls under <paramref name="root"/> (null root claims everything).</summary>
        public static bool IsUnderRoot(Type entityType, Type? root) =>
            root == null || InheritanceDepth(entityType, root) >= 0;

        /// <summary>
        /// Steps from <paramref name="entityType"/> up its base chain to <paramref name="root"/>;
        /// -1 when not an ancestor. Open-generic roots (e.g. AppRoot&lt;&gt;) match any construction.
        /// </summary>
        private static int InheritanceDepth(Type entityType, Type root)
        {
            var depth = 0;
            for (var type = entityType; type != null; type = type.BaseType)
            {
                if (type == root ||
                    (root.IsGenericTypeDefinition && type.IsGenericType && type.GetGenericTypeDefinition() == root))
                    return depth;
                depth++;
            }
            return -1;
        }
    }
}
