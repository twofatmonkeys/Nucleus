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
    /// Builds the child collections a select query fetched and attaches them to their parents,
    /// and binds the entities of the <c>[LinkedObject]</c> properties it included. The rows come
    /// either from the result sets a data set read with the parent rows, or from the linked
    /// queries streamed one by one; both feed the same materialisation. Everything is attached as
    /// loaded: no change is staged on a child, a referenced entity or the parent.
    /// </summary>
    public static class ChildGraphBinder
    {
        /// <summary>
        /// The children of one level, per collection property, keyed by parent ID, and the
        /// entities of the included references, per <c>[LinkedObject]</c> property, keyed by ID.
        /// </summary>
        public sealed class Graph : List<(IPropertySchema Property, Dictionary<Guid, List<ICoreObject>> Children)>
        {
            /// <summary>The entities of each included <c>[LinkedObject]</c> property, by ID.</summary>
            public List<(IPropertySchema Property, Dictionary<Guid, ICoreObject> Entities)> References { get; } = [];

            /// <summary>The members of each included linked collection, by the ID of the one their key holds.</summary>
            public List<(IPropertySchema Property, Dictionary<Guid, List<ICoreObject>> Members)> LinkedCollections { get; } = [];
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
        /// and runs every object's load hook (<see cref="Complete(ICoreObject, IReadOnlyList{IPropertySchema})"/>).
        /// </summary>
        /// <param name="query">The select whose child queries the data set mirrors.</param>
        /// <param name="dataSet">The rows read for the query.</param>
        /// <param name="lazyLoader">Loads a lazy collection on first read; the ambient runtime when null.</param>
        /// <param name="excluded">The collections the load left out, which read as unloaded.</param>
        public static NucleusCollection<T> BuildCollection<T>(IQuery query, DataSet dataSet, Action<INucleusChildCollection>? lazyLoader,
            IReadOnlyList<IPropertySchema> excluded) where T : ICoreObject<T>, new() =>
            BuildCollection<T>(query, dataSet, lazyLoader, null, excluded);

        /// <inheritdoc cref="BuildCollection{T}(IQuery, DataSet, Action{INucleusChildCollection}?, IReadOnlyList{IPropertySchema})"/>
        /// <param name="query">The select whose linked queries the data set mirrors.</param>
        /// <param name="dataSet">The rows read for the query.</param>
        /// <param name="lazyLoader">Loads a lazy collection on first read; the ambient runtime when null.</param>
        /// <param name="linkLoader">
        /// Loads a <c>[LinkedObject]</c> the load did not include, on first read; the ambient
        /// runtime when null.
        /// </param>
        /// <param name="excluded">The collections the load left out, which read as unloaded.</param>
        public static NucleusCollection<T> BuildCollection<T>(IQuery query, DataSet dataSet, Action<INucleusChildCollection>? lazyLoader,
            ILinkedObjectLoader? linkLoader, IReadOnlyList<IPropertySchema> excluded) where T : ICoreObject<T>, new()
        {
            ArgumentNullException.ThrowIfNull(excluded);
            var collection = BuildAttached<T>(query, dataSet, lazyLoader, linkLoader);
            foreach (var obj in collection)
                Complete(obj, excluded, linkLoader);
            return collection;
        }

        /// <summary>
        /// The rows bound and their children attached, but not completed: for a caller that binds
        /// the objects into a parent first and completes them once they are, as a lazy collection
        /// does when it loads.
        /// </summary>
        internal static NucleusCollection<T> BuildAttached<T>(IQuery query, DataSet dataSet, Action<INucleusChildCollection>? lazyLoader,
            ILinkedObjectLoader? linkLoader) where T : ICoreObject<T>, new()
        {
            var collection = dataSet.BuildCollection<T>();
            var children = FromDataSet(query, dataSet, lazyLoader, linkLoader);
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
        public static void Complete(ICoreObject obj, IReadOnlyList<IPropertySchema> excluded) => Complete(obj, excluded, null);

        /// <inheritdoc cref="Complete(ICoreObject, IReadOnlyList{IPropertySchema})"/>
        /// <param name="obj">The loaded object.</param>
        /// <param name="excluded">The collections the load left out.</param>
        /// <param name="linkLoader">
        /// Bound to the object and its loaded children at every depth, for the
        /// <c>[LinkedObject]</c> properties the load did not include; none is bound when null.
        /// </param>
        public static void Complete(ICoreObject obj, IReadOnlyList<IPropertySchema> excluded, ILinkedObjectLoader? linkLoader)
        {
            ArgumentNullException.ThrowIfNull(obj);
            ArgumentNullException.ThrowIfNull(excluded);
            for (var i = 0; i < excluded.Count; i++)
                if (excluded[i].GetValue(obj) is INucleusChildCollection collection)
                    collection.MarkUnloaded();
            RunLoadHooks(obj, linkLoader);
        }

        private static void RunLoadHooks(ICoreObject obj, ILinkedObjectLoader? linkLoader)
        {
            var schema = obj.ObjectSchema;
            if (schema.HasNucleusChildCollections)
                foreach (var collection in obj.NucleusChildCollections)
                    if (collection.IsLoaded)
                        foreach (var child in collection.ChildObjects)
                            RunLoadHooks(child, linkLoader);
            // Only an included collection exists yet; its members finish before their one, as
            // children do. A collection loaded later completes its own members.
            if (schema.HasLinkedCollections)
                foreach (var collection in obj.NucleusLinkedCollections)
                    if (collection.IsLoaded)
                        foreach (var member in collection.Members)
                            RunLoadHooks(member, linkLoader);
            // A child loaded on its own holds its parent's ID until the parent is read, and loads it
            // through this loader like a link; one loaded with its parent never reads through it.
            if (linkLoader is not null && (schema.HasLinkedObjects || schema.HasLinkedCollections || schema.HasParentObject))
                obj.BindLinkedObjectLoader(linkLoader);
            obj.OnAfterLoadFromDB();
        }

        /// <summary>
        /// The child graph held in a data set's linked result sets, which must mirror the query's
        /// LinkedQueries. Null when the query has no linked queries.
        /// </summary>
        public static Graph? FromDataSet(IQuery query, DataSet dataSet) => FromDataSet(query, dataSet, null);

        /// <inheritdoc cref="FromDataSet(IQuery, DataSet)"/>
        /// <param name="query">The select whose child queries the data set mirrors.</param>
        /// <param name="dataSet">The rows read for the query.</param>
        /// <param name="lazyLoader">Loads a child's lazy collections on first read.</param>
        public static Graph? FromDataSet(IQuery query, DataSet dataSet, Action<INucleusChildCollection>? lazyLoader) =>
            FromDataSet(query, dataSet, lazyLoader, null);

        /// <inheritdoc cref="FromDataSet(IQuery, DataSet)"/>
        /// <param name="query">The select whose linked queries the data set mirrors.</param>
        /// <param name="dataSet">The rows read for the query.</param>
        /// <param name="lazyLoader">Loads a child's lazy collections on first read.</param>
        /// <param name="linkLoader">Bound to the included referenced entities, for their own references.</param>
        public static Graph? FromDataSet(IQuery query, DataSet dataSet, Action<INucleusChildCollection>? lazyLoader, ILinkedObjectLoader? linkLoader)
        {
            ArgumentNullException.ThrowIfNull(query);
            ArgumentNullException.ThrowIfNull(dataSet);
            var linkedQueries = query.LinkedQueries.ToList();
            if (linkedQueries.Count == 0)
                return null;
            if (dataSet.LinkedDataSets.Count != linkedQueries.Count)
                throw new InvalidOperationException(
                    $"The query has {linkedQueries.Count} linked queries but the provider returned {dataSet.LinkedDataSets.Count} linked result sets.");
            var schema = SchemaOf(query);
            var graph = new Graph();
            for (int i = 0; i < linkedQueries.Count; i++)
            {
                var linkedQuery = linkedQueries[i];
                var reference = ReferencePropertyFor(schema, linkedQuery);
                var linked = reference is null ? LinkedCollectionPropertyFor(schema, linkedQuery) : null;
                var property = reference is null && linked is null ? CollectionPropertyFor(schema, linkedQuery) : null;
                if (reference is null && linked is null && property is null)
                    continue;
                var rows = dataSet.LinkedDataSets[i];
                var linkedSchema = SchemaOf(linkedQuery);
                var below = FromDataSet(linkedQuery, rows, lazyLoader, linkLoader);
                if (reference is not null)
                {
                    var entities = new Dictionary<Guid, ICoreObject>();
                    for (int row = 0; row < rows.RowCount; row++)
                        AddReferenced(entities, linkedSchema, rows.ColumnNames, rows[row], below, lazyLoader, linkLoader);
                    graph.References.Add((reference, entities));
                    continue;
                }
                if (linked is not null)
                {
                    var key = schema.GetLinkedCollectionKey(linked).Name;
                    var members = new Dictionary<Guid, List<ICoreObject>>();
                    for (int row = 0; row < rows.RowCount; row++)
                        AddMember(members, linkedSchema, key, rows.ColumnNames, rows[row], below, lazyLoader);
                    graph.LinkedCollections.Add((linked, members));
                    continue;
                }
                var map = new Dictionary<Guid, List<ICoreObject>>();
                for (int row = 0; row < rows.RowCount; row++)
                    AddChild(map, linkedSchema, rows.ColumnNames, rows[row], below, lazyLoader);
                graph.Add((property!, map));
            }
            return graph;
        }

        /// <summary>
        /// The child graph of a query, fetched by streaming each linked query on
        /// <paramref name="context"/>. Null when the query has no linked queries.
        /// </summary>
        public static Task<Graph?> LoadAsync(IQuery query, IDataContext context, CancellationToken cancellationToken) =>
            LoadAsync(query, context, null, cancellationToken);

        /// <inheritdoc cref="LoadAsync(IQuery, IDataContext, CancellationToken)"/>
        /// <param name="query">The select whose child queries are streamed.</param>
        /// <param name="context">The context they are streamed on.</param>
        /// <param name="lazyLoader">Loads a child's lazy collections on first read.</param>
        /// <param name="cancellationToken">Cancels the reads.</param>
        public static Task<Graph?> LoadAsync(IQuery query, IDataContext context, Action<INucleusChildCollection>? lazyLoader, CancellationToken cancellationToken) =>
            LoadAsync(query, context, lazyLoader, null, cancellationToken);

        /// <inheritdoc cref="LoadAsync(IQuery, IDataContext, CancellationToken)"/>
        /// <param name="query">The select whose linked queries are streamed.</param>
        /// <param name="context">The context they are streamed on.</param>
        /// <param name="lazyLoader">Loads a child's lazy collections on first read.</param>
        /// <param name="linkLoader">Bound to the included referenced entities, for their own references.</param>
        /// <param name="cancellationToken">Cancels the reads.</param>
        public static async Task<Graph?> LoadAsync(IQuery query, IDataContext context, Action<INucleusChildCollection>? lazyLoader,
            ILinkedObjectLoader? linkLoader, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            ArgumentNullException.ThrowIfNull(context);
            var linkedQueries = query.LinkedQueries.ToList();
            if (linkedQueries.Count == 0)
                return null;
            var schema = SchemaOf(query);
            var graph = new Graph();
            foreach (var linkedQuery in linkedQueries)
            {
                var reference = ReferencePropertyFor(schema, linkedQuery);
                var linked = reference is null ? LinkedCollectionPropertyFor(schema, linkedQuery) : null;
                var property = reference is null && linked is null ? CollectionPropertyFor(schema, linkedQuery) : null;
                if (reference is null && linked is null && property is null)
                    continue;
                var linkedSchema = SchemaOf(linkedQuery);
                var below = await LoadAsync(linkedQuery, context, lazyLoader, linkLoader, cancellationToken).ConfigureAwait(false);
                if (reference is not null)
                {
                    var entities = new Dictionary<Guid, ICoreObject>();
                    await foreach (var row in context.StreamAsync(linkedQuery, cancellationToken).ConfigureAwait(false))
                        AddReferenced(entities, linkedSchema, row.ColumnNames, row.Values, below, lazyLoader, linkLoader);
                    graph.References.Add((reference, entities));
                    continue;
                }
                if (linked is not null)
                {
                    var key = schema.GetLinkedCollectionKey(linked).Name;
                    var members = new Dictionary<Guid, List<ICoreObject>>();
                    await foreach (var row in context.StreamAsync(linkedQuery, cancellationToken).ConfigureAwait(false))
                        AddMember(members, linkedSchema, key, row.ColumnNames, row.Values, below, lazyLoader);
                    graph.LinkedCollections.Add((linked, members));
                    continue;
                }
                var map = new Dictionary<Guid, List<ICoreObject>>();
                await foreach (var row in context.StreamAsync(linkedQuery, cancellationToken).ConfigureAwait(false))
                    AddChild(map, linkedSchema, row.ColumnNames, row.Values, below, lazyLoader);
                graph.Add((property!, map));
            }
            return graph;
        }

        /// <summary>
        /// Attaches a parent's children from the graph, and binds the entities its included
        /// references hold the IDs of. Every collection the graph covers is attached, empty or
        /// not, since the graph is the whole of that collection. A lazy ([RetrieveOnDemand])
        /// collection the graph does not cover is marked to load itself when it is first read.
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
            {
                foreach (var (property, map) in graph)
                    CollectionOf(parent, property).AttachLoaded(map.TryGetValue(parent.ID, out var children) ? children : []);
                foreach (var (property, members) in graph.LinkedCollections)
                    LinkedCollectionOf(parent, property).AttachLoaded(members.TryGetValue(parent.ID, out var held) ? held : []);
                BindReferences(parent, graph);
            }
            MarkLazy(parent, graph, lazyLoader);
        }

        /// <summary>
        /// Replaces each included reference's stored ID with the entity read for it, as a stored
        /// value, so reading the property runs no query and the parent is not made dirty. An ID
        /// with no entity (a row since deleted) stays an ID and is looked for when it is read.
        /// </summary>
        private static void BindReferences(ICoreObject parent, Graph graph)
        {
            foreach (var (property, entities) in graph.References)
                if (parent.GetPropertyValue(property.Name) is Guid id && entities.TryGetValue(id, out var entity))
                    parent.BindValue(property.Name, entity);
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

        private static INucleusLinkedCollection LinkedCollectionOf(ICoreObject parent, IPropertySchema property) =>
            property.Getter?.Invoke(parent, null) as INucleusLinkedCollection
            ?? throw new InvalidOperationException($"{parent.GetType().Name}.{property.Name} did not return a linked collection.");

        /// <summary>
        /// Materialises one member row of an included linked collection, attaches its own
        /// children, and files it under the one its key holds. The key is bound as stored, an ID;
        /// the collection binds the one itself when the key is a <c>[LinkedObject]</c>.
        /// </summary>
        private static void AddMember(Dictionary<Guid, List<ICoreObject>> members, IObjectSchema memberSchema, string keyProperty,
            string[] columns, object?[] values, Graph? below, Action<INucleusChildCollection>? lazyLoader)
        {
            var member = (ICoreObject)Activator.CreateInstance(memberSchema.Type)!;
            var ownerId = Guid.Empty;
            for (int i = 0; i < columns.Length; i++)
            {
                if (columns[i] == keyProperty && values[i] is Guid id)
                    ownerId = id;
                member.BindValue(columns[i], values[i]);
            }
            if (ownerId == Guid.Empty)
                return;
            Attach(member, below, lazyLoader);
            if (!members.TryGetValue(ownerId, out var list))
                members[ownerId] = list = [];
            list.Add(member);
        }

        /// <summary>The linked collection property an included members query fills; null for any other query.</summary>
        private static IPropertySchema? LinkedCollectionPropertyFor(IObjectSchema parentSchema, IQuery linkedQuery) =>
            linkedQuery.LinkedCollectionProperty is { } name
                ? parentSchema.LinkedCollectionProperties.FirstOrDefault(p => p.Name == name)
                    ?? throw new InvalidOperationException($"{parentSchema.Name} has no linked collection {name}.")
                : null;

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

        /// <summary>
        /// Materialises one row of an included reference as a loaded entity of its own, with its
        /// children attached and its load hook run, filed by its ID. Several parents referring to
        /// it share the one instance.
        /// </summary>
        private static void AddReferenced(Dictionary<Guid, ICoreObject> entities, IObjectSchema schema,
            string[] columns, object?[] values, Graph? below, Action<INucleusChildCollection>? lazyLoader, ILinkedObjectLoader? linkLoader)
        {
            var entity = (ICoreObject)Activator.CreateInstance(schema.Type)!;
            for (int i = 0; i < columns.Length; i++)
                entity.BindValue(columns[i], values[i]);
            if (entity.ID == Guid.Empty || entities.ContainsKey(entity.ID))
                return;
            Attach(entity, below, lazyLoader);
            Complete(entity, [], linkLoader);
            entities[entity.ID] = entity;
        }

        private static IObjectSchema SchemaOf(IQuery query) =>
            query.DataTableSchema?.ObjectSchema
            ?? throw new InvalidOperationException("A linked query has no table schema.");

        /// <summary>The <c>[LinkedObject]</c> property an included reference query fills; null for any other query.</summary>
        private static IPropertySchema? ReferencePropertyFor(IObjectSchema parentSchema, IQuery linkedQuery) =>
            linkedQuery.LinkedObjectProperty is { } name
                ? (parentSchema.LinkedObjectProperties ?? []).FirstOrDefault(p => p.Name == name)
                    ?? throw new InvalidOperationException($"{parentSchema.Name} has no [LinkedObject] property {name}.")
                : null;

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
