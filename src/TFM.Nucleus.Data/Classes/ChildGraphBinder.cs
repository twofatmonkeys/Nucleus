using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Abstractions.Interfaces;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// Builds the child collections a select query fetched and attaches them to their parents.
    /// The rows come either from the result sets a data set read with the parent rows, or from
    /// the child queries streamed one by one; both feed the same materialisation. Children are
    /// attached as loaded: no change is staged on them or on the parent.
    /// </summary>
    public static class ChildGraphBinder
    {
        /// <summary>The children of one level, per collection property, keyed by parent ID.</summary>
        public sealed class Graph : List<(IPropertySchema Property, Dictionary<Guid, List<ICoreObject>> Children)>
        {
        }

        /// <summary>
        /// Materialises a data set's rows as <typeparamref name="T"/>, each with the child
        /// collections the query fetched attached.
        /// </summary>
        public static NucleusCollection<T> BuildCollection<T>(IQuery query, DataSet dataSet) where T : ICoreObject<T>, new() =>
            BuildCollection<T>(query, dataSet, null);

        /// <summary>
        /// Materialises a data set's rows as <typeparamref name="T"/>, each with the child
        /// collections the query fetched attached, and its lazy collections, at every depth, left
        /// to <paramref name="lazyLoader"/> to load on first read.
        /// </summary>
        public static NucleusCollection<T> BuildCollection<T>(IQuery query, DataSet dataSet, Action<INucleusChildCollection>? lazyLoader) where T : ICoreObject<T>, new() =>
            BuildCollection<T>(query, dataSet, lazyLoader, []);

        /// <summary>
        /// Materialises a data set's rows as <typeparamref name="T"/> with the child collections
        /// the query fetched attached, marks the <paramref name="excluded"/> collections unloaded,
        /// and runs every object's load hook (<see cref="Complete"/>).
        /// </summary>
        /// <param name="query">The select whose child queries the data set mirrors.</param>
        /// <param name="dataSet">The rows read for the query.</param>
        /// <param name="lazyLoader">Loads a lazy collection on first read; the ambient runtime when null.</param>
        /// <param name="excluded">The collections the load left out, which read as unloaded.</param>
        public static NucleusCollection<T> BuildCollection<T>(IQuery query, DataSet dataSet, Action<INucleusChildCollection>? lazyLoader,
            IReadOnlyList<IPropertySchema> excluded) where T : ICoreObject<T>, new()
        {
            ArgumentNullException.ThrowIfNull(excluded);
            var collection = BuildAttached<T>(query, dataSet, lazyLoader);
            foreach (var obj in collection)
                Complete(obj, excluded);
            return collection;
        }

        /// <summary>
        /// The rows bound and their children attached, but not completed: for a caller that binds
        /// the objects into a parent first and completes them once they are, as a lazy collection
        /// does when it loads.
        /// </summary>
        internal static NucleusCollection<T> BuildAttached<T>(IQuery query, DataSet dataSet, Action<INucleusChildCollection>? lazyLoader) where T : ICoreObject<T>, new()
        {
            var collection = dataSet.BuildCollection<T>();
            var children = FromDataSet(query, dataSet, lazyLoader);
            foreach (var obj in collection)
                Attach(obj, children, lazyLoader);
            return collection;
        }

        /// <summary>
        /// Finishes a loaded object: marks the <paramref name="excluded"/> collections unloaded, then
        /// runs <see cref="ICoreObject.OnAfterLoadFromDB"/> on its loaded children at every depth and
        /// then on the object, so a hook sees its parent bound and its children finished. Every
        /// load path ends here, once per object; a lazy collection's children are completed when
        /// it loads.
        /// </summary>
        public static void Complete(ICoreObject obj, IReadOnlyList<IPropertySchema> excluded)
        {
            ArgumentNullException.ThrowIfNull(obj);
            ArgumentNullException.ThrowIfNull(excluded);
            for (var i = 0; i < excluded.Count; i++)
                if (excluded[i].GetValue(obj) is INucleusChildCollection collection)
                    collection.MarkUnloaded();
            RunLoadHooks(obj);
        }

        private static void RunLoadHooks(ICoreObject obj)
        {
            if (obj.ObjectSchema.HasNucleusChildCollections)
                foreach (var collection in obj.NucleusChildCollections)
                    if (collection.IsLoaded)
                        foreach (var child in collection.ChildObjects)
                            RunLoadHooks(child);
            obj.OnAfterLoadFromDB();
        }

        /// <summary>
        /// The child graph held in a data set's linked result sets, which must mirror the query's
        /// LinkedQueries. Null when the query has no child queries.
        /// </summary>
        public static Graph? FromDataSet(IQuery query, DataSet dataSet) => FromDataSet(query, dataSet, null);

        /// <inheritdoc cref="FromDataSet(IQuery, DataSet)"/>
        /// <param name="query">The select whose child queries the data set mirrors.</param>
        /// <param name="dataSet">The rows read for the query.</param>
        /// <param name="lazyLoader">Loads a child's lazy collections on first read.</param>
        public static Graph? FromDataSet(IQuery query, DataSet dataSet, Action<INucleusChildCollection>? lazyLoader)
        {
            ArgumentNullException.ThrowIfNull(query);
            ArgumentNullException.ThrowIfNull(dataSet);
            var childQueries = query.LinkedQueries.ToList();
            if (childQueries.Count == 0)
                return null;
            if (dataSet.LinkedDataSets.Count != childQueries.Count)
                throw new InvalidOperationException(
                    $"The query has {childQueries.Count} child queries but the provider returned {dataSet.LinkedDataSets.Count} child result sets.");
            var schema = SchemaOf(query);
            var graph = new Graph();
            for (int i = 0; i < childQueries.Count; i++)
            {
                var childQuery = childQueries[i];
                var childRows = dataSet.LinkedDataSets[i];
                if (CollectionPropertyFor(schema, childQuery) is not { } property)
                    continue;
                var childSchema = SchemaOf(childQuery);
                var grandchildren = FromDataSet(childQuery, childRows, lazyLoader);
                var map = new Dictionary<Guid, List<ICoreObject>>();
                for (int row = 0; row < childRows.RowCount; row++)
                    AddChild(map, childSchema, childRows.ColumnNames, childRows[row], grandchildren, lazyLoader);
                graph.Add((property, map));
            }
            return graph;
        }

        /// <summary>
        /// The child graph of a query, fetched by streaming each child query on
        /// <paramref name="context"/>. Null when the query has no child queries.
        /// </summary>
        public static Task<Graph?> LoadAsync(IQuery query, IDataContext context, CancellationToken cancellationToken) =>
            LoadAsync(query, context, null, cancellationToken);

        /// <inheritdoc cref="LoadAsync(IQuery, IDataContext, CancellationToken)"/>
        /// <param name="query">The select whose child queries are streamed.</param>
        /// <param name="context">The context they are streamed on.</param>
        /// <param name="lazyLoader">Loads a child's lazy collections on first read.</param>
        /// <param name="cancellationToken">Cancels the reads.</param>
        public static async Task<Graph?> LoadAsync(IQuery query, IDataContext context, Action<INucleusChildCollection>? lazyLoader, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            ArgumentNullException.ThrowIfNull(context);
            var childQueries = query.LinkedQueries.ToList();
            if (childQueries.Count == 0)
                return null;
            var schema = SchemaOf(query);
            var graph = new Graph();
            foreach (var childQuery in childQueries)
            {
                if (CollectionPropertyFor(schema, childQuery) is not { } property)
                    continue;
                var childSchema = SchemaOf(childQuery);
                var grandchildren = await LoadAsync(childQuery, context, lazyLoader, cancellationToken).ConfigureAwait(false);
                var map = new Dictionary<Guid, List<ICoreObject>>();
                await foreach (var row in context.StreamAsync(childQuery, cancellationToken).ConfigureAwait(false))
                    AddChild(map, childSchema, row.ColumnNames, row.Values, grandchildren, lazyLoader);
                graph.Add((property, map));
            }
            return graph;
        }

        /// <summary>
        /// Attaches a parent's children from the graph. Every collection the graph covers is
        /// attached, empty or not, since the graph is the whole of that collection. A lazy
        /// ([RetrieveOnDemand]) collection the graph does not cover is marked to load itself
        /// when it is first read.
        /// </summary>
        public static void Attach(ICoreObject parent, Graph? graph) => Attach(parent, graph, null);

        /// <inheritdoc cref="Attach(ICoreObject, Graph?)"/>
        /// <param name="parent">The object the children belong to.</param>
        /// <param name="graph">The children read for its level.</param>
        /// <param name="lazyLoader">
        /// Loads a lazy collection on first read; the ambient runtime when null.
        /// </param>
        public static void Attach(ICoreObject parent, Graph? graph, Action<INucleusChildCollection>? lazyLoader)
        {
            ArgumentNullException.ThrowIfNull(parent);
            if (graph is not null)
                foreach (var (property, map) in graph)
                    CollectionOf(parent, property).AttachLoaded(map.TryGetValue(parent.ID, out var children) ? children : []);
            MarkLazy(parent, graph, lazyLoader);
        }

        private static void MarkLazy(ICoreObject parent, Graph? graph, Action<INucleusChildCollection>? lazyLoader)
        {
            var schema = parent.ObjectSchema;
            if (!schema.HasNucleusChildCollections)
                return;
            foreach (var property in schema.NucleusChildCollectionProperties ?? [])
            {
                if (!property.IsLazyChildCollection || (graph is not null && graph.Any(g => g.Property.Name == property.Name)))
                    continue;
                var collection = CollectionOf(parent, property);
                if (lazyLoader is null)
                    collection.MarkUnloaded(lazy: true);
                else
                    collection.MarkLazy(lazyLoader);
            }
        }

        private static INucleusChildCollection CollectionOf(ICoreObject parent, IPropertySchema property) =>
            property.Getter?.Invoke(parent, null) as INucleusChildCollection
            ?? throw new InvalidOperationException($"{parent.GetType().Name}.{property.Name} did not return a child collection.");

        /// <summary>
        /// Materialises one child row, attaches its own children, and files it under its parent.
        /// The parent column is read for the key, not bound: the parent object is bound when the
        /// child is attached, and the raw ID would otherwise sit where the object belongs.
        /// </summary>
        private static void AddChild(Dictionary<Guid, List<ICoreObject>> map, IObjectSchema childSchema,
            string[] columns, object?[] values, Graph? grandchildren, Action<INucleusChildCollection>? lazyLoader)
        {
            var parentColumn = childSchema.ParentObjectProperty?.Name
                ?? throw new InvalidOperationException($"{childSchema.Name} is used as a child collection but has no [ParentObject] property.");
            var child = (ICoreObject)Activator.CreateInstance(childSchema.Type)!;
            var parentId = Guid.Empty;
            for (int i = 0; i < columns.Length; i++)
            {
                if (columns[i] == parentColumn)
                {
                    if (values[i] is Guid id)
                        parentId = id;
                    continue;
                }
                child.BindValue(columns[i], values[i]);
            }
            if (parentId == Guid.Empty)
                return;
            Attach(child, grandchildren, lazyLoader);
            if (!map.TryGetValue(parentId, out var list))
                map[parentId] = list = [];
            list.Add(child);
        }

        private static IObjectSchema SchemaOf(IQuery query) =>
            query.DataTableSchema?.ObjectSchema
            ?? throw new InvalidOperationException("A child collection query has no table schema.");

        /// <summary>
        /// The collection property a child query fills: by name when the query records it, else
        /// the collection of the query's child type. Null when the parent has no such collection.
        /// </summary>
        private static IPropertySchema? CollectionPropertyFor(IObjectSchema parentSchema, IQuery childQuery)
        {
            var properties = parentSchema.NucleusChildCollectionProperties ?? [];
            if (childQuery.ChildCollectionProperty is { } name)
                return properties.FirstOrDefault(p => p.Name == name);
            var childType = childQuery.DataTableSchema?.ObjectSchema.Type;
            return properties.FirstOrDefault(p => p.ChildType == childType);
        }
    }
}
