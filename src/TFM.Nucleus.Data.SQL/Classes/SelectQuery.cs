using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data.SQL
{
    public class SelectQuery : Query, IQuery, ISelectQuery
    {
        //new readonly QueryType Type = QueryType.SELECT;
        private readonly string? queryText;

        public SelectQuery(string queryText)
        {
            Type = QueryType.SELECT;
            this.queryText = queryText;
        }
        public SelectQuery(Type objectType)
        {
            if (!typeof(ICoreObject).IsAssignableFrom(objectType))
                throw new Exception("Object type must implement ICoreObject");
            Type = QueryType.SELECT;
            ObjectType = objectType;

        }
        public SelectQuery(string tablename, Type objectType) : this(tablename, null, objectType)
        {
        }
        public SelectQuery(string tablename, string[]? columnConstraints, Type objectType)
        {
            Type = QueryType.SELECT;
            TableName = tablename;
            if (columnConstraints != null)
                Constraints = columnConstraints;
            Columns = columnConstraints?.ToList() ?? [];
            ObjectType = objectType;
        }
        public SelectQuery(SqlTableSchema tableSchema)
        {
            Type = QueryType.SELECT;
            TableName = tableSchema.TableName;
            Columns = [.. tableSchema.Columns.Select(c => c.ColumnName)];
            ObjectType = tableSchema.ObjectSchema.Type;
            TableSchema = tableSchema;
            if(tableSchema.ObjectSchema.HasNucleusChildCollections)
            {
                foreach(var childColl in tableSchema.ObjectSchema.NucleusChildCollectionProperties!)
                {
                    var childTableSchema = tableSchema.ParentSchema.GetTableSchema(childColl.Type.GetGenericArguments()[0]);
                    var childObjSchema = tableSchema.ObjectSchema.GetObjectSchema(childColl.Type.GetGenericArguments()[0]);
                    if (childTableSchema != null)
                    {
                        var childQuery = new SelectQuery(childTableSchema);
                        var parentProperty = childObjSchema.ParentObjectProperty
                            ?? throw new InvalidQueryException(
                                $"{childObjSchema.Name} is used as a child collection but has no [ParentObject] property.");
                        AddChildQuery(childQuery, parentProperty.Name);
                    }
                }
            }
        }




        public SelectQuery Parse(Expression expression)
        {
            return (SelectQuery)ExpressionParser.ParseExpressionToQuery(this, expression);
        }




        public override string BuildQuery()
        {
            if (queryText != null)
                return queryText;
            if (Columns.Count == 0)
                return ("SELECT TOP 0 NULL");
            var sb = Skip > 0 ? BuildPagedSelect() : BuildPlainSelect();
            if (WithTotalCount)
            {
                // Same filter, unpaged: the count arrives as the second result set of the batch.
                sb.Append($"; {Environment.NewLine}SELECT COUNT_BIG(*) FROM {TableName}{(WithNoLock ? "" : " WITH(NOLOCK)")}");
                var where = BuildWhere();
                if (where != null)
                    sb.Append(' ').Append(where);
            }
            foreach (var link in LinkedQueries)
                sb.Append($"; {Environment.NewLine}").Append(link.BuildQuery());
            return sb.ToString();
        }

        private StringBuilder BuildPlainSelect()
        {
            var sb = new StringBuilder(Take == 0 ? $"SELECT " : $"SELECT TOP {Take} ");
            for (int i = 0; i < Columns.Count; i++)
                sb.Append($"{(i == 0 ? "" : " ,")}[{Columns[i]}]");
            sb.Append($" FROM {TableName}{(WithNoLock ? "" : " WITH(NOLOCK)")}");
            sb.Append(' ').Append(BuildWhere());
            if (ParentQuery == null && !OrderBy.IsEmpty)
                sb.Append(' ').Append(OrderBy.BuildOrderByStatement());
            return sb;
        }

        /// <summary>
        /// Skip paging via a ROW_NUMBER window — valid at every supported SQL Server
        /// compatibility level, unlike OFFSET/FETCH (110+). Unordered queries page by [ID].
        /// </summary>
        private StringBuilder BuildPagedSelect()
        {
            if (ParentQuery != null)
                throw new InvalidQueryException("Paging is not supported on child queries.");
            var columnList = string.Join(" ,", Columns.Select(c => $"[{c}]"));
            var orderBy = OrderBy.IsEmpty ? "ORDER BY [ID]" : OrderBy.BuildOrderByStatement()?.ToString() ?? "ORDER BY [ID]";
            var sb = new StringBuilder(
                $"SELECT {columnList} FROM (SELECT {columnList}, ROW_NUMBER() OVER ({orderBy}) AS [__RowNumber] " +
                $"FROM {TableName}{(WithNoLock ? "" : " WITH(NOLOCK)")}");
            var where = BuildWhere();
            if (where != null)
                sb.Append(' ').Append(where);
            sb.Append($") AS [__paged] WHERE [__paged].[__RowNumber] > {Skip}");
            if (Take > 0)
                sb.Append($" AND [__paged].[__RowNumber] <= {Skip + Take}");
            sb.Append(" ORDER BY [__paged].[__RowNumber]");
            return sb;
        }
        private StringBuilder? BuildWhere()
        {
            var softDeleteFilter = SoftDeleteFilter();
            if (ParentQuery == null)
            {
                if (Where.IsEmpty && softDeleteFilter == null)
                    return null;
                var sb = new StringBuilder("WHERE ");
                if (!Where.IsEmpty)
                {
                    sb.Append($"{Where.WhereClause()}");
                    if (softDeleteFilter != null)
                        sb.Append($" AND {softDeleteFilter}");
                }
                else
                    sb.Append(softDeleteFilter);
                return sb;
            }
            else
            {
                var sb = new StringBuilder($"WHERE {LinkedColumn} IN (SELECT ID FROM {ParentQuery.TableName} ");
                sb.Append(((SelectQuery)ParentQuery).BuildWhere()).Append(')');
                if (softDeleteFilter != null)
                    sb.Append($" AND {softDeleteFilter}");
                return sb;
            }
        }

        /// <summary>Soft-deletable types are filtered to live rows unless IncludeDeleted is set.</summary>
        private string? SoftDeleteFilter() =>
            !IncludeDeleted && TableSchema?.ObjectSchema is { IsDeletable: true, DeleteBehavior: DeleteBehavior.Soft }
                ? "[IsDeleted] = 0"
                : null;

        //public override Task<DbDataReader?> ExecuteAsync() => throw new Exception("A Data.Context must be supplied");
        //public override Task<DbDataReader?> ExecuteAsync(Context dataContext) => ExecuteAsync(dataContext, CancellationToken.None);
        //public override Task<DbDataReader?> ExecuteAsync(Context dataContext, CancellationToken token) => dataContext.GetObjectDataAsync(this, token);

        ISelectQuery ISelectQuery.Create(string tablename, Type objectType) => new SelectQuery(tablename, objectType);

        ISelectQuery ISelectQuery.Create(string tablename, string[]? columnConstraints, Type objectType) => new SelectQuery(tablename, columnConstraints, objectType);

        int ISelectQuery.Skip { get => Skip; set => Skip = value; }
        int ISelectQuery.Take { get => Take; set => Take = value; }
        public bool WithTotalCount { get; set; }
        public bool IncludeDeleted { get; set; }
        public void AddOrderBy(string propertyName, bool descending = false) =>
            OrderBy.Add(new OrderBy(propertyName, descending ? OrderByType.DESCENDING : OrderByType.ASCENDING));
    }
}
