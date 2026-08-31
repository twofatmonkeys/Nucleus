using System;
using System.Collections.Generic;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// Resolves the data provider responsible for an entity type. Applications may register
    /// multiple providers, each rooted at an abstract entity base type; an entity resolves to
    /// the provider of its nearest registered ancestor.
    /// </summary>
    public interface IDataProviderRegistry
    {
        /// <summary>
        /// The provider owning <paramref name="entityType"/> (nearest registered ancestor wins).
        /// Throws when no registered root covers the type.
        /// </summary>
        IDataProvider GetProvider(Type entityType);

        IDataProvider GetProvider<T>() where T : ICoreObject;

        /// <summary>True when <paramref name="entityType"/> resolves to <paramref name="provider"/>.</summary>
        bool IsOwnedBy(Type entityType, IDataProvider provider);

        IReadOnlyList<IDataProvider> AllProviders { get; }
    }
}
