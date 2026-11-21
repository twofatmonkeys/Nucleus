using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Data;

namespace TFM.Nucleus
{
    /// <summary>
    /// The runtime surface behind the CoreObject static convenience API. Implemented by the
    /// object data manager and published through <see cref="NucleusRuntime.Current"/> when the
    /// application registers Nucleus in DI. All operations route to the provider owning each
    /// entity's registered root type.
    /// </summary>
    public interface INucleusRuntime
    {
        void Save(ICoreObject obj);
        Task SaveAsync(ICoreObject obj, CancellationToken token = default);
        void Delete(ICoreObject obj);
        Task DeleteAsync(ICoreObject obj, CancellationToken token = default);

        /// <summary>
        /// Deletes every matching row without loading any, with its child collection rows, in a
        /// transaction of its own. Returns the rows of <typeparamref name="T"/> affected.
        /// </summary>
        int DeleteWhere<T>(Expression<Func<T, bool>> predicate) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException($"{GetType().Name} does not support deleting by predicate.");

        /// <inheritdoc cref="DeleteWhere{T}(Expression{Func{T, bool}})"/>
        Task<int> DeleteWhereAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken token = default) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException($"{GetType().Name} does not support deleting by predicate.");

        /// <summary>
        /// Loads a child collection that was excluded when its parent was loaded: the rows whose
        /// parent is the collection's owner, with their own children, merged by ID with any
        /// children added since.
        /// </summary>
        Task LoadChildCollectionAsync(TFM.Nucleus.Abstractions.Interfaces.INucleusChildCollection collection, CancellationToken token = default) =>
            throw new NotSupportedException($"{GetType().Name} does not support loading a child collection on demand.");

        /// <summary>
        /// The synchronous form of <see cref="LoadChildCollectionAsync"/>, which a lazy
        /// ([RetrieveOnDemand]) collection calls the first time it is read. It reads on the
        /// synchronous path rather than blocking on the asynchronous one.
        /// </summary>
        void LoadChildCollection(TFM.Nucleus.Abstractions.Interfaces.INucleusChildCollection collection) =>
            throw new NotSupportedException($"{GetType().Name} does not support loading a child collection on demand.");
        [Obsolete("Use Save. A batch too large for ordinary statements is written by the bulk engine automatically.")]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        void BulkSave(IEnumerable<ICoreObject?> objs);

        /// <summary>
        /// Saves objects of any types, each routed to the provider that stores it, in one
        /// transaction per provider: a batch spanning providers is not atomic. Nulls are skipped.
        /// A batch too large for ordinary statements is written by the provider's bulk engine on
        /// the asynchronous path; the caller does not choose.
        /// </summary>
        void Save(IEnumerable<ICoreObject?> objs) =>
#pragma warning disable CS0618 // an implementation that predates this member has its batch save under the old name
            BulkSave(objs);
#pragma warning restore CS0618

        /// <inheritdoc cref="Save(IEnumerable{ICoreObject})"/>
        Task SaveAsync(IEnumerable<ICoreObject?> objs, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException($"{GetType().Name} does not support saving a batch.");

        NucleusCollection<T> LoadAll<T>() where T : ICoreObject<T>, new();
        T? LoadByID<T>(Guid id) where T : ICoreObject<T>, new();
        NucleusCollection<T> LoadWhere<T>(Expression expression) where T : ICoreObject<T>, new();
        Task<NucleusCollection<T>> LoadWhereAsync<T>(Expression expression, CancellationToken token = default) where T : ICoreObject<T>, new();

        /// <summary>
        /// Loads by primary key, leaving the named child collection properties unloaded: their
        /// rows are neither queried nor transferred, and reading them throws until they are loaded.
        /// </summary>
        T? LoadByID<T>(Guid id, IReadOnlyCollection<string> excludedCollections) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException($"{GetType().Name} does not support excluding child collections.");

        /// <summary>Loads the matching rows, leaving the named child collection properties unloaded.</summary>
        NucleusCollection<T> LoadWhere<T>(Expression expression, IReadOnlyCollection<string> excludedCollections) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException($"{GetType().Name} does not support excluding child collections.");

        /// <inheritdoc cref="LoadWhere{T}(Expression, IReadOnlyCollection{string})"/>
        Task<NucleusCollection<T>> LoadWhereAsync<T>(Expression expression, IReadOnlyCollection<string> excludedCollections, CancellationToken token = default) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException($"{GetType().Name} does not support excluding child collections.");

        /// <summary>
        /// Loads the rows <paramref name="predicate"/> matches as <paramref name="options"/> say:
        /// child collections left out, lazy collections and references brought in, deleted rows seen.
        /// </summary>
        NucleusCollection<T> LoadWhere<T>(Expression<Func<T, bool>> predicate, LoadOptions? options) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException($"{GetType().Name} does not support load options.");

        /// <inheritdoc cref="LoadWhere{T}(Expression{Func{T, bool}}, LoadOptions?)"/>
        Task<NucleusCollection<T>> LoadWhereAsync<T>(Expression<Func<T, bool>> predicate, LoadOptions? options, CancellationToken cancellationToken = default) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException($"{GetType().Name} does not support load options.");

        /// <summary>Loads by primary key as <paramref name="options"/> say, or null when there is no such row.</summary>
        T? LoadByID<T>(Guid id, LoadOptions? options) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException($"{GetType().Name} does not support load options.");

        /// <inheritdoc cref="LoadByID{T}(Guid, LoadOptions?)"/>
        Task<T?> LoadByIDAsync<T>(Guid id, LoadOptions? options = null, CancellationToken cancellationToken = default) where T : ICoreObject<T>, new() =>
            throw new NotSupportedException($"{GetType().Name} does not support loading by ID on the asynchronous path.");
    }

    /// <summary>
    /// Ambient access point for the runtime, set when the DI container first resolves the object
    /// data manager (or explicitly via assignment). Enables the static CoreObject API.
    /// </summary>
    public static class NucleusRuntime
    {
        private static INucleusRuntime? _current;

        public static INucleusRuntime Current
        {
            get => _current ?? throw new InvalidOperationException(
                "Nucleus is not initialized. Call services.AddNucleus().AddNucleusData() plus a provider registration, " +
                "and resolve IObjectDataManager (e.g. via InitializeDataProviderAsync) before using the static entity API.");
            set => _current = value;
        }

        public static bool IsInitialized => _current != null;
    }
}
