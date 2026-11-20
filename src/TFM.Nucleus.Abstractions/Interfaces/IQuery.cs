using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data
{
    public interface ISelectQuery : IQuery
    {
        public ISelectQuery Create(string tablename, Type objectType);
        public ISelectQuery Create(string tablename, string[]? columnConstraints, Type objectType);

        /// <summary>Rows to skip before returning results. Provider paging support required.</summary>
        public int Skip { get; set; }

        /// <summary>Maximum rows to return; 0 means unlimited.</summary>
        public int Take { get; set; }

        /// <summary>When true, the provider also computes the unpaged total row count in the same round trip.</summary>
        public bool WithTotalCount { get; set; }

        /// <summary>When true, soft-deleted rows are included for soft-deletable types.</summary>
        public bool IncludeDeleted { get; set; }

        /// <summary>Appends an ordering term by property/column name.</summary>
        public void AddOrderBy(string propertyName, bool descending = false);

        /// <summary>
        /// Leaves a child collection out of the load: its query, and the queries of its own
        /// children, are removed. Returns false when the query has no child query for that
        /// collection property.
        /// </summary>
        public bool ExcludeChildCollection(string propertyName) =>
            throw new NotSupportedException($"{GetType().Name} cannot exclude child collections.");

        /// <summary>
        /// Puts a lazy ([RetrieveOnDemand]) child collection back into the load, so its rows come
        /// with the parents' in the same round trip. Returns false when the query's type has no
        /// such collection, or it is stored by another provider.
        /// </summary>
        public bool IncludeChildCollection(string propertyName) =>
            throw new NotSupportedException($"{GetType().Name} cannot include lazy child collections.");

        /// <summary>
        /// Loads the entities a <c>[LinkedObject]</c> property refers to with the rows, in the
        /// same round trip, so reading the property on each row runs no query of its own. Returns
        /// false when the query's type has no such property. Throws when the referenced type is
        /// stored by another provider, since leaving it out would bring back a query per row.
        /// </summary>
        public bool IncludeLinkedObject(string propertyName) =>
            throw new NotSupportedException($"{GetType().Name} cannot include linked objects.");

        /// <summary>
        /// Loads the members of a linked collection with the rows, in the same round trip, so
        /// reading the collection on each row runs no query of its own. Returns false when the
        /// query's type has no such collection. Throws when the many type is stored by another
        /// provider, since leaving it out would bring back a query per row.
        /// </summary>
        public bool IncludeLinkedCollection(string propertyName) =>
            throw new NotSupportedException($"{GetType().Name} cannot include linked collections.");
    }

    public interface IInsertQuery : IQuery
    {
        public IInsertQuery Create(string tablename);

    }

    public interface IUpdateQuery : IQuery
    {
        public IUpdateQuery Create(string tablename, string idColumn, Guid idValue);
        public IUpdateQuery Create(string tablename, string idColumn, int idValue);

    }

    /// <summary>
    /// A delete statement for one or more objects of a [Deletable] type. Hard deletes remove
    /// rows; soft deletes are rendered by the provider as flag updates.
    /// </summary>
    public interface IDeleteQuery : IQuery
    {
    }

    public interface IQuery
    {
        public string BuildQuery();
        public IDataTableSchema? DataTableSchema { get; }
        public IEnumerable<IQueryParameter?>? SetParameters { get; }
        public IEnumerable<IQueryParameter?>? AllQueryParameters { get; }
        public IEnumerable<IQueryParameter?>? AllQueryOutputParameters { get; }
        public IEnumerable<IQuery> Split(int maxParameters);
        public bool TryAddWhere(IQueryParameter parameter);
        public void AddWhere(IQueryParameter parameter);
        public void AddWhere(IWhereCollection whereCollection);
        public void AddWhereOr(IQueryParameter parameter);
        public void AddWhereOr(IWhereCollection whereCollection);
        public void AddChildQuery(IQuery childQuery, string linkedColumn);
        public void AddLinkedQuery(IQuery? query);
        public IEnumerable<IQuery> FlattenLinkedQueries();
        public IEnumerable<IQuery> LinkedQueries { get; }
        public int CountLinkedQueries();
        public IWhereCollection NewWhereCollection();

        /// <summary>
        /// For a query that loads a child collection, the name of the parent's collection
        /// property it fills; null for any other query.
        /// </summary>
        public string? ChildCollectionProperty => null;

        /// <summary>
        /// For a query that loads the entities a parent query's <c>[LinkedObject]</c> property
        /// refers to (an included reference), the name of that property; null for any other query.
        /// </summary>
        public string? LinkedObjectProperty => null;

        /// <summary>
        /// For a query that loads the members of a parent query's linked collection (an included
        /// one), the name of that collection property; null for any other query.
        /// </summary>
        public string? LinkedCollectionProperty => null;

    }
}
