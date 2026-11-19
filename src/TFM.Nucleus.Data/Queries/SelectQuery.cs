using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Data.Queries
{
    public class SelectQuery : Query, IQuery, ISelectQuery
    {
        public SelectQuery(IQueryRenderer renderer, Type objectType) : base(renderer)
        {
            if (!typeof(ICoreObject).IsAssignableFrom(objectType))
                throw new Exception("Object type must implement ICoreObject");
            Type = QueryType.SELECT;
            ObjectType = objectType;

        }
        public SelectQuery(IQueryRenderer renderer, string tablename, Type objectType) : this(renderer, tablename, null, objectType)
        {
        }
        public SelectQuery(IQueryRenderer renderer, string tablename, string[]? columnConstraints, Type objectType) : base(renderer)
        {
            Type = QueryType.SELECT;
            TableName = tablename;
            if (columnConstraints != null)
                Constraints = columnConstraints;
            Columns = columnConstraints?.ToList() ?? [];
            ObjectType = objectType;
        }

        /// <summary>Finds a child collection type's table; null when the provider does not store it.</summary>
        private readonly Func<Type, IDataTableSchema?>? _resolveTable;

        /// <summary>
        /// Selects every column of a table, with a child query per child collection of its type,
        /// except lazy ([RetrieveOnDemand]) ones, which <see cref="IncludeChildCollection"/> adds back.
        /// </summary>
        /// <param name="renderer">The provider's renderer.</param>
        /// <param name="tableSchema">The table to select from.</param>
        /// <param name="resolveTable">
        /// Finds the table of a child collection's type, or null when the provider does not store
        /// it, in which case that collection is left out. Required when the type has child collections.
        /// </param>
        public SelectQuery(IQueryRenderer renderer, IDataTableSchema tableSchema, Func<Type, IDataTableSchema?>? resolveTable = null) : base(renderer)
        {
            Type = QueryType.SELECT;
            TableName = tableSchema.TableName;
            Columns = [.. tableSchema.Columns.Select(c => c.ColumnName)];
            ObjectType = tableSchema.ObjectSchema.Type;
            TableSchema = tableSchema;
            _resolveTable = resolveTable;
            if(tableSchema.ObjectSchema.HasNucleusChildCollections)
            {
                if (resolveTable == null)
                    throw new InvalidOperationException(
                        $"{tableSchema.ObjectSchema.Name} has child collections; a table resolver is needed to build their queries.");
                foreach(var childColl in tableSchema.ObjectSchema.NucleusChildCollectionProperties!)
                {
                    // A lazy collection's rows are left out; it loads them when it is first read.
                    if (childColl.IsLazyChildCollection)
                        continue;
                    AddChildCollectionQuery(childColl);
                }
            }
        }

        /// <summary>
        /// Adds the query that loads one child collection, with its own non-lazy collections
        /// below it. False when the provider does not store the child type.
        /// </summary>
        private bool AddChildCollectionQuery(IPropertySchema childColl)
        {
            var childType = childColl.ChildType ?? childColl.Type.GetGenericArguments()[0];
            var childTableSchema = _resolveTable!(childType);
            if (childTableSchema == null)
                return false;
            var childObjSchema = TableSchema!.ObjectSchema.GetObjectSchema(childType);
            var childQuery = new SelectQuery(Renderer, childTableSchema, _resolveTable) { ChildCollectionProperty = childColl.Name };
            var parentProperty = childObjSchema.ParentObjectProperty
                ?? throw new InvalidQueryException(
                    $"{childObjSchema.Name} is used as a child collection but has no [ParentObject] property.");
            AddChildQuery(childQuery, childTableSchema.FindByPropertyName(parentProperty.Name)?.ColumnName ?? parentProperty.Name);
            return true;
        }

        public SelectQuery Parse(Expression expression)
        {
            return (SelectQuery)ExpressionParser.ParseExpressionToQuery(this, expression);
        }

        public override string BuildQuery() => BuildQuery(includeChildQueries: true);

        /// <summary>
        /// The batch: this query's rows, the unpaged count when requested, then, when
        /// <paramref name="includeChildQueries"/> is set, each child collection query in
        /// LinkedQueries pre-order, which is the order a reader meets their result sets. A
        /// streamed read leaves the children out and fetches them itself.
        /// </summary>
        public string BuildQuery(bool includeChildQueries) => Renderer.Select(this, includeChildQueries);

        // ---- The rules every renderer applies. They decide what a select means; the renderer
        // ---- only writes it.

        /// <summary>No columns and no measures to select: the query returns no rows and no result sets follow.</summary>
        public bool IsEmptyProjection => Columns.Count == 0 && Measures.Count == 0;

        /// <summary>
        /// The columns a grouped read groups by, written in this order. On a grouped read
        /// <see cref="Query.Columns"/> holds the same columns, the group keys being what it selects.
        /// </summary>
        public List<string> GroupBy = [];

        /// <summary>The figures a grouped read returns after its group keys, in this order.</summary>
        public List<Measure> Measures = [];

        /// <summary>
        /// A grouped read: the rows are one per distinct combination of <see cref="GroupBy"/>
        /// (one row in all when there are no keys), each with its <see cref="Measures"/>.
        /// </summary>
        public bool IsAggregate => GroupBy.Count > 0 || Measures.Count > 0;

        /// <summary>
        /// Set on a row read: the name and conversion of each value of a row, in the order the
        /// query selects them. A reader labels and converts by this instead of the table's whole
        /// column list, which is what lets a read select only some columns, or figures that are
        /// not columns at all. Null on an entity read.
        /// </summary>
        public IReadOnlyList<ResultColumn>? ResultColumns { get; set; }

        /// <summary>
        /// Throws when a grouped read is also asked to order or page: a grouped read returns its
        /// groups in no particular order, so it is ordered in memory, and is small enough to be.
        /// </summary>
        public void EnsureAggregateShape()
        {
            if (!IsAggregate)
                return;
            if (!IsRoot || LinkedQueries.Count > 0)
                throw new InvalidQueryException("A grouped read has no child queries.");
            if (!OrderBy.IsEmpty || Skip > 0 || Take > 0 || WithTotalCount)
                throw new InvalidQueryException("A grouped read is not ordered or paged; order its groups in memory.");
            if (!GroupBy.SequenceEqual(Columns))
                throw new InvalidQueryException("A grouped read selects exactly its group keys.");
        }

        /// <summary>The root of a select batch, as opposed to a child collection query.</summary>
        public bool IsRoot => ParentQuery == null;

        /// <summary>Rows are skipped, so the query pages through an order.</summary>
        public bool IsPaged => Skip > 0;

        /// <summary>The parent of a child collection query.</summary>
        public SelectQuery? ParentSelect => ParentQuery as SelectQuery;

        /// <summary>
        /// The root rows need an order by ID: a limit without an order would let the server pick
        /// one set of parents for the rows and another for the children's subquery, so a root
        /// with child queries and a Take is ordered by ID when no order was asked for.
        /// </summary>
        public bool NeedsIdentityOrder => OrderBy.IsEmpty && Take > 0 && LinkedQueries.Count > 0;

        /// <summary>Paging applies to a root query only; throws for a child query.</summary>
        public void EnsurePageable()
        {
            if (ParentQuery != null)
                throw new InvalidQueryException("Paging is not supported on child queries.");
        }

        /// <summary>
        /// Soft-deletable types are filtered to live rows unless IncludeDeleted is set on this
        /// query or any query above it: a read that includes deleted parents includes their
        /// deleted children too.
        /// </summary>
        public bool AppliesLiveRowFilter => !IncludesDeleted && TableSchema != null && SoftDelete.Applies(TableSchema);

        /// <summary>IncludeDeleted is set on this query or on any query above it.</summary>
        public bool IncludesDeleted => IncludeDeleted || ParentSelect?.IncludesDeleted == true;

        ISelectQuery ISelectQuery.Create(string tablename, Type objectType) => new SelectQuery(Renderer, tablename, objectType);

        ISelectQuery ISelectQuery.Create(string tablename, string[]? columnConstraints, Type objectType) => new SelectQuery(Renderer, tablename, columnConstraints, objectType);

        int ISelectQuery.Skip { get => Skip; set => Skip = value; }
        int ISelectQuery.Take { get => Take; set => Take = value; }
        public bool WithTotalCount { get; set; }
        public bool IncludeDeleted { get; set; }
        /// <summary>Orders by a property, written as the column the property maps to.</summary>
        public void AddOrderBy(string propertyName, bool descending = false) =>
            OrderBy.Add(new OrderBy(TableSchema?.FindByPropertyName(propertyName)?.ColumnName ?? propertyName,
                descending ? OrderByType.DESCENDING : OrderByType.ASCENDING));

        public bool ExcludeChildCollection(string propertyName) =>
            LinkedQueries.RemoveAll(q => q.ChildCollectionProperty == propertyName) > 0;

        /// <inheritdoc cref="ISelectQuery.IncludeChildCollection"/>
        public bool IncludeChildCollection(string propertyName)
        {
            if (LinkedQueries.Any(q => q.ChildCollectionProperty == propertyName))
                return true;
            if (_resolveTable == null || TableSchema?.ObjectSchema.NucleusChildCollectionProperties?.FirstOrDefault(p => p.Name == propertyName) is not { } property)
                return false;
            return AddChildCollectionQuery(property);
        }
    }
}
