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
        public static NucleusCollection<T> BuildCollection<T>(IQuery query, DataSet dataSet) where T : ICoreObject<T>, new()
        {
            var collection = dataSet.BuildCollection<T>();
            var children = FromDataSet(query, dataSet);
            foreach (var obj in collection)
                Attach(obj, children);
            return collection;
        }

        /// <summary>
        /// The child graph held in a data set's linked result sets, which must mirror the query's
        /// LinkedQueries. Null when the query has no child queries.
        /// </summary>
        public static Graph? FromDataSet(IQuery query, DataSet dataSet)
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
                var grandchildren = FromDataSet(childQuery, childRows);
                var map = new Dictionary<Guid, List<ICoreObject>>();
                for (int row = 0; row < childRows.RowCount; row++)
                    AddChild(map, childSchema, childRows.ColumnNames, childRows[row], grandchildren);
                graph.Add((property, map));
            }
            return graph;
        }

        /// <summary>
        /// The child graph of a query, fetched by streaming each child query on
        /// <paramref name="context"/>. Null when the query has no child queries.
        /// </summary>
        public static async Task<Graph?> LoadAsync(IQuery query, IDataContext context, CancellationToken cancellationToken)
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
                var grandchildren = await LoadAsync(childQuery, context, cancellationToken).ConfigureAwait(false);
                var map = new Dictionary<Guid, List<ICoreObject>>();
                await foreach (var row in context.StreamAsync(childQuery, cancellationToken).ConfigureAwait(false))
                    AddChild(map, childSchema, row.ColumnNames, row.Values, grandchildren);
                graph.Add((property, map));
            }
            return graph;
        }

        /// <summary>
        /// Attaches a parent's children from the graph. Every collection the graph covers is
        /// attached, empty or not, since the graph is the whole of that collection.
        /// </summary>
        public static void Attach(ICoreObject parent, Graph? graph)
        {
            ArgumentNullException.ThrowIfNull(parent);
            if (graph is null)
                return;
            foreach (var (property, map) in graph)
            {
                if (property.Getter?.Invoke(parent, null) is not INucleusChildCollection collection)
                    throw new InvalidOperationException(
                        $"{parent.GetType().Name}.{property.Name} did not return a child collection.");
                collection.AttachLoaded(map.TryGetValue(parent.ID, out var children) ? children : []);
            }
        }

        /// <summary>
        /// Materialises one child row, attaches its own children, and files it under its parent.
        /// The parent column is read for the key, not bound: the parent object is bound when the
        /// child is attached, and the raw ID would otherwise sit where the object belongs.
        /// </summary>
        private static void AddChild(Dictionary<Guid, List<ICoreObject>> map, IObjectSchema childSchema,
            string[] columns, object?[] values, Graph? grandchildren)
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
            Attach(child, grandchildren);
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
