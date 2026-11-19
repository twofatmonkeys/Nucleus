using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Abstractions.Interfaces;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// Loads a child collection that its parent's load left out (excluded, or lazy): the rows of
    /// the child type whose parent is the collection's owner, with their own non-lazy children,
    /// attached as loaded and merged by ID with any children added since. The manager and the
    /// data context both load through here, each with its own provider, so a lazy collection
    /// reads from the stack that loaded its parent rather than from the ambient runtime.
    /// </summary>
    public static class ChildCollectionLoader
    {
        private static readonly ConcurrentDictionary<Type, MethodInfo> _sync = [];
        private static readonly ConcurrentDictionary<Type, MethodInfo> _async = [];

        /// <summary>Loads the collection on the synchronous path; does nothing when it is loaded.</summary>
        public static void Load(IDataProvider provider, INucleusChildCollection collection)
        {
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentNullException.ThrowIfNull(collection);
            if (collection.IsLoaded)
                return;
            var loader = _sync.GetOrAdd(collection.ChildType, static t =>
                typeof(ChildCollectionLoader).GetMethod(nameof(ChildrenOf), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(t));
            List<ICoreObject> children;
            try
            {
                children = (List<ICoreObject>)loader.Invoke(null, [provider, collection.ParentObject])!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
            collection.AttachLoaded(children);
            CompleteAttached(collection, children);
        }

        /// <inheritdoc cref="Load(IDataProvider, INucleusChildCollection)"/>
        public static async Task LoadAsync(IDataProvider provider, INucleusChildCollection collection, CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentNullException.ThrowIfNull(collection);
            if (collection.IsLoaded)
                return;
            var loader = _async.GetOrAdd(collection.ChildType, static t =>
                typeof(ChildCollectionLoader).GetMethod(nameof(ChildrenOfAsync), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(t));
            var children = await ((Task<List<ICoreObject>>)loader.Invoke(null, [provider, collection.ParentObject, token])!).ConfigureAwait(false);
            collection.AttachLoaded(children);
            CompleteAttached(collection, children);
        }

        /// <summary>
        /// Runs the load hooks of the loaded children the collection took, now that their parent
        /// is bound. A row whose ID the collection already held (a child added since) was not
        /// taken, and is not a loaded object of the collection, so its hook does not run.
        /// </summary>
        private static void CompleteAttached(INucleusChildCollection collection, List<ICoreObject> loaded)
        {
            if (loaded.Count == 0)
                return;
            var held = new HashSet<ICoreObject>(collection.ChildObjects, ReferenceEqualityComparer.Instance);
            foreach (var child in loaded)
                if (held.Contains(child))
                    ChildGraphBinder.Complete(child, []);
        }

        private static List<ICoreObject> ChildrenOf<TChild>(IDataProvider provider, ICoreObject parent) where TChild : ICoreObject<TChild>, new()
        {
            var query = ChildrenOfQuery<TChild>(provider, parent);
            using var context = provider.CreateContext();
            var loaded = ChildGraphBinder.BuildAttached<TChild>(query, context.GetDataSet(query), c => Load(provider, c));
            return [.. loaded.Cast<ICoreObject>()];
        }

        private static async Task<List<ICoreObject>> ChildrenOfAsync<TChild>(IDataProvider provider, ICoreObject parent, CancellationToken token) where TChild : ICoreObject<TChild>, new()
        {
            var query = ChildrenOfQuery<TChild>(provider, parent);
            using var context = await provider.CreateContextAsync(token: token).ConfigureAwait(false);
            var loaded = ChildGraphBinder.BuildAttached<TChild>(query, await context.GetDataSetAsync(query, token).ConfigureAwait(false), c => Load(provider, c));
            return [.. loaded.Cast<ICoreObject>()];
        }

        /// <summary>The select of one parent's children, with their own non-lazy collections.</summary>
        private static ISelectQuery ChildrenOfQuery<TChild>(IDataProvider provider, ICoreObject parent) where TChild : ICoreObject<TChild>, new()
        {
            var query = provider.ParseSelectExpression<TChild>(Expression.Constant(true));
            var childSchema = query.DataTableSchema?.ObjectSchema ?? parent.ObjectSchema.GetObjectSchema<TChild>();
            var parentProperty = childSchema.ParentObjectProperty
                ?? throw new InvalidOperationException($"{childSchema.Name} is used as a child collection but has no [ParentObject] property.");
            var parentColumn = query.DataTableSchema?.FindByPropertyName(parentProperty.Name)?.ColumnName ?? parentProperty.Name;
            query.AddWhere(new Parameter(parentColumn, parent.ID));
            return query;
        }
    }
}
