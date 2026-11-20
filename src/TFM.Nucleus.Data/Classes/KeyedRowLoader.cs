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
    /// The one way Nucleus reads rows a load left for later: the rows of a type whose key column
    /// holds a value (a child collection's rows by their parent, a linked collection's members by
    /// their key, a linked object by its ID), each with its own non-lazy child collections,
    /// attached but not completed, on a context of its own through one provider. The objects read
    /// are bound to <c>linkLoader</c> when they are completed, and their lazy collections load
    /// through the same provider and loader, so everything below them reads from the same stack.
    /// Retries and timeouts are the provider's: a connection open retries a transient failure, and
    /// the command runs under the provider's command timeout.
    /// </summary>
    internal static class KeyedRowLoader
    {
        private static readonly ConcurrentDictionary<Type, MethodInfo> _sync = [];
        private static readonly ConcurrentDictionary<Type, MethodInfo> _async = [];

        /// <summary>The rows of <paramref name="type"/> whose <paramref name="keyProperty"/> is <paramref name="key"/>, on the synchronous path.</summary>
        public static List<ICoreObject> Rows(IDataProvider provider, Type type, string keyProperty, Guid key, ILinkedObjectLoader linkLoader)
        {
            var rows = _sync.GetOrAdd(type, static t =>
                typeof(KeyedRowLoader).GetMethod(nameof(RowsOf), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(t));
            return (List<ICoreObject>)rows.Invoke(null, BindingFlags.DoNotWrapExceptions, binder: null, [provider, keyProperty, key, linkLoader], culture: null)!;
        }

        /// <inheritdoc cref="Rows"/>
        public static Task<List<ICoreObject>> RowsAsync(IDataProvider provider, Type type, string keyProperty, Guid key, ILinkedObjectLoader linkLoader,
            CancellationToken token)
        {
            var rows = _async.GetOrAdd(type, static t =>
                typeof(KeyedRowLoader).GetMethod(nameof(RowsOfAsync), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(t));
            return (Task<List<ICoreObject>>)rows.Invoke(null, BindingFlags.DoNotWrapExceptions, binder: null, [provider, keyProperty, key, linkLoader, token], culture: null)!;
        }

        /// <summary>
        /// Runs the load hooks of the loaded rows a holder took, now that it has bound them. A row
        /// whose ID the holder already held (one added since) was not taken, and is not a loaded
        /// object of the holder, so its hook does not run.
        /// </summary>
        public static void CompleteTaken(IEnumerable<ICoreObject> held, List<ICoreObject> loaded, ILinkedObjectLoader linkLoader)
        {
            if (loaded.Count == 0)
                return;
            var taken = new HashSet<ICoreObject>(held, ReferenceEqualityComparer.Instance);
            foreach (var row in loaded)
                if (taken.Contains(row))
                    ChildGraphBinder.Complete(row, [], linkLoader);
        }

        private static List<ICoreObject> RowsOf<T>(IDataProvider provider, string keyProperty, Guid key, ILinkedObjectLoader linkLoader)
            where T : ICoreObject<T>, new()
        {
            var query = Query<T>(provider, keyProperty, key);
            using var context = provider.CreateContext();
            var loaded = ChildGraphBinder.BuildAttached<T>(query, context.GetDataSet(query), c => ChildCollectionLoader.Load(provider, c, linkLoader), linkLoader);
            return [.. loaded.Cast<ICoreObject>()];
        }

        private static async Task<List<ICoreObject>> RowsOfAsync<T>(IDataProvider provider, string keyProperty, Guid key, ILinkedObjectLoader linkLoader,
            CancellationToken token) where T : ICoreObject<T>, new()
        {
            var query = Query<T>(provider, keyProperty, key);
            using var context = await provider.CreateContextAsync(token: token).ConfigureAwait(false);
            var loaded = ChildGraphBinder.BuildAttached<T>(query, await context.GetDataSetAsync(query, token).ConfigureAwait(false),
                c => ChildCollectionLoader.Load(provider, c, linkLoader), linkLoader);
            return [.. loaded.Cast<ICoreObject>()];
        }

        /// <summary>The select of the rows by their key, with their own non-lazy collections.</summary>
        private static ISelectQuery Query<T>(IDataProvider provider, string keyProperty, Guid key) where T : ICoreObject<T>, new()
        {
            var query = provider.ParseSelectExpression<T>(Expression.Constant(true));
            var column = query.DataTableSchema?.FindByPropertyName(keyProperty)?.ColumnName ?? keyProperty;
            query.AddWhere(new Parameter(column, key));
            return query;
        }
    }
}
