using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TFM.Nucleus.Data.SQL
{
    /// <summary>
    /// Deletes one object per query instance (batches chain via LinkedQueries, like inserts and
    /// updates). Hard deletes render DELETE FROM; soft deletes render an UPDATE flagging
    /// [IsDeleted]/[DeletedAt]. The WHERE is bound to the object's identity column.
    /// </summary>
    public class DeleteQuery : Query, IQuery, IDeleteQuery
    {
        private readonly bool _isSoftDelete;

        public DeleteQuery(SqlTableSchema tableSchema, ICoreObject obj)
        {
            ArgumentNullException.ThrowIfNull(tableSchema, nameof(tableSchema));
            ArgumentNullException.ThrowIfNull(obj, nameof(obj));
            Type = QueryType.DELETE;
            TableName = tableSchema.TableName;
            TableSchema = tableSchema;
            _isSoftDelete = SoftDelete.Applies(tableSchema);
            var idColumn = tableSchema.IdentityColumn ?? throw new InvalidQueryException("Table schema does not have an identity column defined");
            var id = obj.GetPropertyValue(idColumn.PropertySchema.Name) ?? throw new InvalidQueryException("Unable to retrieve identity value from ICoreObject<>");
            Where.And(idColumn.ColumnName, id);
            if (_isSoftDelete)
                SetParameters.AddRange(SoftDelete.NewSetParameters());
        }

        public static DeleteQuery? Create(SqlTableSchema tableSchema, IEnumerable<ICoreObject>? objs)
        {
            if (objs == null)
                return null;
            DeleteQuery? rootQuery = null;
            foreach (var obj in objs)
            {
                var query = new DeleteQuery(tableSchema, obj);
                if (rootQuery == null)
                    rootQuery = query;
                else
                    rootQuery.LinkedQueries.Add(query);
            }
            return rootQuery;
        }

        public override string BuildQuery()
        {
            var sb = _isSoftDelete
                ? new StringBuilder($"UPDATE {TableName} WITH (ROWLOCK) SET ")
                    .Append(string.Join(",", SetParameters.Select(x => x!.SQLSetCondition())))
                    .Append($" WHERE {Where.WhereClause()}")
                : new StringBuilder($"DELETE FROM {TableName} WHERE {Where.WhereClause()}");
            foreach (var q in LinkedQueries)
                sb.Append($";{Environment.NewLine}{q.BuildQuery()}");
            return sb.ToString();
        }
    }
}
