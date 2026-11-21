using System;
using System.Linq;
using System.Text;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.Queries;

namespace TFM.Nucleus.Data.SQL
{
    /// <summary>
    /// The single home of SQL Server rendering. The neutral query model decides what a query
    /// means (<see cref="SelectQuery.IsPaged"/>, <see cref="SelectQuery.NeedsIdentityOrder"/>,
    /// <see cref="DeleteWhereQuery.TargetsChildrenFirst"/> and the rest); this class only writes
    /// it as T-SQL. The output is pinned byte for byte by the raw SQL Server goldens.
    /// </summary>
    internal sealed class SqlServerQueryRenderer : IQueryRenderer, IConditionWriter
    {
        public static readonly SqlServerQueryRenderer Instance = new();

        private SqlServerQueryRenderer() { }

        private const string CaseSensitiveCollation = "Latin1_General_CS_AS";

        /// <summary>The condition that selects rows which have not been soft deleted.</summary>
        internal const string LiveRowFilter = "[IsDeleted] = 0";

        private static string NoLock(Query q) => q.AllowDirtyRead ? " WITH(NOLOCK)" : "";

        // ---- Select

        /// <summary>
        /// A query and, when <paramref name="includeChildQueries"/> is set, its linked queries.
        /// Every linked query, in the root's batch or written on its own (a streamed load fetches
        /// each child query separately, and a delete checks a linked collection for members),
        /// selects by its parent's key values kept in a table variable (<see cref="KeptIds"/>).
        /// </summary>
        public string Select(SelectQuery q, bool includeChildQueries)
        {
            var sb = new StringBuilder();
            var kept = new KeptIds(this, sb, "@__parents");
            var keys = kept.KeysFor(q);
            AppendSelect(sb, q, keys);
            if (includeChildQueries)
                AppendLinked(sb, q, kept);
            return sb.ToString();
        }

        /// <summary>Each linked query of <paramref name="q"/> in pre-order, the order a reader meets their result sets.</summary>
        private void AppendLinked(StringBuilder sb, SelectQuery q, KeptIds kept)
        {
            foreach (var link in q.LinkedQueries)
            {
                sb.Append($"; {Environment.NewLine}");
                if (link is SelectQuery linked)
                {
                    var keys = kept.KeysFor(linked);
                    AppendSelect(sb, linked, keys);
                    AppendLinked(sb, linked, kept);
                }
                else
                    sb.Append(link.BuildQuery());
            }
        }

        /// <summary>Declares a table variable of IDs and fills it from <paramref name="select"/>.</summary>
        private static string CaptureIds(string variable, string column, string select) =>
            $"DECLARE {variable} TABLE ([{column}] uniqueidentifier PRIMARY KEY);{Environment.NewLine}" +
            $"INSERT INTO {variable} ([{column}]) {select};{Environment.NewLine}";

        /// <summary>
        /// The key values a batch keeps, one table variable per parent query and key column,
        /// declared where first needed. A linked query that reached its parents through a subquery
        /// over the parent table ran from the child table when the tables were small: it held a
        /// lock on a child row of some other parent while it read that parent's row, the opposite
        /// order to a save of that parent, and the two deadlocked (SQL Server 2008 R2 did it a
        /// level down too, holding a grandchild while it read the child). Here every statement
        /// reads one table and table variables: a parent's keys are read from the parent's table
        /// alone, by its own condition (a root) or by its parent's kept keys (a linked query).
        /// </summary>
        private sealed class KeptIds(SqlServerQueryRenderer renderer, StringBuilder sb, string prefix)
        {
            private readonly Dictionary<(SelectQuery Owner, string Column), string?> _kept = [];

            /// <summary>
            /// The variable holding the values <paramref name="q"/> matches its linked column
            /// against; null for a root, or when the parent's keys cannot be kept.
            /// </summary>
            public string? KeysFor(SelectQuery q) =>
                q.ParentSelect is { } parent ? Keys(parent, q.ParentKeyColumn ?? "ID") : null;

            private string? Keys(SelectQuery owner, string column)
            {
                if (_kept.TryGetValue((owner, column), out var variable))
                    return variable;
                variable = null;
                if (CanKeep(owner))
                {
                    var source = owner.IsRoot
                        ? renderer.ParentKeySelect(owner, column)
                        : $"SELECT [{column}] FROM {owner.TableName} {renderer.SelectWhere(owner, KeysFor(owner))}";
                    // IDs are unique and never null; a reference column is neither.
                    var select = column == "ID"
                        ? source
                        : $"SELECT DISTINCT [{column}] FROM ({source}) AS [__keys] WHERE [{column}] IS NOT NULL";
                    variable = _kept.Count == 0 ? prefix : $"{prefix}{_kept.Count}";
                    sb.Append(CaptureIds(variable, "ID", select));
                }
                _kept[(owner, column)] = variable;
                return variable;
            }

            private static bool CanKeep(SelectQuery owner) =>
                !owner.IsAggregate && !owner.IsEmptyProjection &&
                (owner.IsRoot || owner.ParentSelect != null) &&
                owner.TableSchema?.IdentityColumn?.DataType == typeof(Guid);
        }

        private void AppendSelect(StringBuilder sb, SelectQuery q, string? keys)
        {
            if (q.IsAggregate)
            {
                sb.Append(AggregateSelect(q));
                return;
            }
            if (q.IsEmptyProjection)
            {
                sb.Append("SELECT TOP 0 NULL");
                return;
            }
            sb.Append(q.IsPaged ? PagedSelect(q) : PlainSelect(q, keys));
            if (q.WithTotalCount)
            {
                // Same filter, unpaged: the count arrives as the second result set of the batch.
                sb.Append($"; {Environment.NewLine}SELECT COUNT_BIG(*) FROM {q.TableName}{NoLock(q)}");
                var where = SelectWhere(q, keys);
                if (where != null)
                    sb.Append(' ').Append(where);
            }
        }

        /// <summary>
        /// A grouped read: the group keys, then each measure under its alias, grouped by the keys.
        /// With no keys it is one row over every matching row.
        /// </summary>
        private string AggregateSelect(SelectQuery q)
        {
            q.EnsureAggregateShape();
            var terms = q.GroupBy.Select(c => $"[{c}]").Concat(q.Measures.Select(MeasureTerm));
            var sb = new StringBuilder($"SELECT {string.Join(" ,", terms)} FROM {q.TableName}{NoLock(q)}");
            if (SelectWhere(q, null) is { } where)
                sb.Append(' ').Append(where);
            if (q.GroupBy.Count > 0)
                sb.Append(" GROUP BY ").Append(string.Join(" ,", q.GroupBy.Select(c => $"[{c}]")));
            return sb.ToString();
        }

        private static string MeasureTerm(Measure m)
        {
            var column = m.Column == null ? null : $"[{m.Column.ColumnName}]";
            var figure = m.Kind switch
            {
                MeasureKind.Count => "COUNT_BIG(*)",
                MeasureKind.CountDistinct => $"COUNT_BIG(DISTINCT {column})",
                MeasureKind.Min => $"MIN({column})",
                MeasureKind.Max => $"MAX({column})",
                MeasureKind.Sum => $"SUM({column})",
                _ => throw new NotSupportedException($"Measure {m.Kind} is not supported by the SQL Server provider."),
            };
            return $"{figure} AS [{m.Alias}]";
        }

        private static string? OrderByClause(OrderByCollection orderBy)
        {
            if (orderBy.IsEmpty)
                return null;
            var sb = new StringBuilder("ORDER BY ");
            var i = 0;
            foreach (var column in orderBy)
                sb.Append($"{(i++ == 0 ? string.Empty : " ,")}{OrderByTerm(column)}");
            return sb.ToString();
        }

        private static string OrderByTerm(OrderBy column)
        {
            if (column.IsEmpty)
                return string.Empty;
            return column.IsDescending ? $"[{column.Column}] DESC" : $"[{column.Column}] ASC";
        }

        /// <summary>The order of the root rows: the one asked for, or [ID] when the query needs one.</summary>
        private static string? RootOrderBy(SelectQuery q)
        {
            if (!q.OrderBy.IsEmpty)
                return OrderByClause(q.OrderBy);
            return q.NeedsIdentityOrder ? "ORDER BY [ID]" : null;
        }

        private StringBuilder PlainSelect(SelectQuery q, string? keys)
        {
            var sb = new StringBuilder(q.Take == 0 ? $"SELECT " : $"SELECT TOP {q.Take} ");
            for (int i = 0; i < q.Columns.Count; i++)
                sb.Append($"{(i == 0 ? "" : " ,")}[{q.Columns[i]}]");
            sb.Append($" FROM {q.TableName}{NoLock(q)}");
            sb.Append(' ').Append(SelectWhere(q, keys));
            if (q.IsRoot && RootOrderBy(q) is { } orderBy)
                sb.Append(' ').Append(orderBy);
            return sb;
        }

        /// <summary>
        /// Skip paging via a ROW_NUMBER window, valid at every supported SQL Server compatibility
        /// level, unlike OFFSET/FETCH (110+). Unordered queries page by [ID].
        /// </summary>
        private StringBuilder PagedSelect(SelectQuery q)
        {
            q.EnsurePageable();
            var columnList = string.Join(" ,", q.Columns.Select(c => $"[{c}]"));
            var sb = new StringBuilder($"SELECT {columnList} FROM ").Append(PagedWindow(q, columnList));
            sb.Append(" ORDER BY [__paged].[__RowNumber]");
            return sb;
        }

        /// <summary>The ROW_NUMBER window over a query's rows, cut to the requested page.</summary>
        private StringBuilder PagedWindow(SelectQuery q, string columnList)
        {
            var orderBy = q.OrderBy.IsEmpty ? "ORDER BY [ID]" : OrderByClause(q.OrderBy) ?? "ORDER BY [ID]";
            var sb = new StringBuilder(
                $"(SELECT {columnList}, ROW_NUMBER() OVER ({orderBy}) AS [__RowNumber] " +
                $"FROM {q.TableName}{NoLock(q)}");
            var where = SelectWhere(q, null);
            if (where != null)
                sb.Append(' ').Append(where);
            sb.Append($") AS [__paged] WHERE [__paged].[__RowNumber] > {q.Skip}");
            if (q.Take > 0)
                sb.Append($" AND [__paged].[__RowNumber] <= {q.Skip + q.Take}");
            return sb;
        }

        /// <summary>
        /// Selects one column (the ID, or for an included reference the reference column) of
        /// exactly the rows a parent query returns, for a linked query to select its rows by. A
        /// page or a TOP applies here too, or the rows of every matching parent would be fetched
        /// rather than those of the parents returned. Without kept keys a linked parent reaches
        /// its own parents through this select in turn.
        /// </summary>
        private string ParentKeySelect(SelectQuery q, string column)
        {
            if (q.IsRoot && q.IsPaged)
                return $"SELECT [{column}] FROM {PagedWindow(q, $"[{column}]")}";
            var top = q.IsRoot && q.Take > 0;
            var sb = new StringBuilder(top ? $"SELECT TOP {q.Take} [{column}] FROM {q.TableName}" : $"SELECT [{column}] FROM {q.TableName}");
            if (SelectWhere(q, null) is { } where)
                sb.Append(' ').Append(where);
            if (top && RootOrderBy(q) is { } orderBy)
                sb.Append(' ').Append(orderBy);
            return sb.ToString();
        }

        /// <summary>
        /// A root's own condition; a linked query's match on its parent's keys, from
        /// <paramref name="keys"/> when they are kept, else through the parent's select.
        /// </summary>
        private StringBuilder? SelectWhere(SelectQuery q, string? keys)
        {
            var softDeleteFilter = q.AppliesLiveRowFilter ? LiveRowFilter : null;
            if (q.IsRoot)
            {
                if (q.Where.IsEmpty && softDeleteFilter == null)
                    return null;
                var sb = new StringBuilder("WHERE ");
                if (!q.Where.IsEmpty)
                {
                    sb.Append($"{WhereRenderer.Render(q.Where, this)}");
                    if (softDeleteFilter != null)
                        sb.Append($" AND {softDeleteFilter}");
                }
                else
                    sb.Append(softDeleteFilter);
                return sb;
            }
            else
            {
                var parent = q.ParentSelect
                    ?? throw new InvalidQueryException("A child collection query must hang off a select.");
                var sb = new StringBuilder($"WHERE [{q.LinkedColumn}] IN (")
                    .Append(keys != null ? $"SELECT [ID] FROM {keys}" : ParentKeySelect(parent, q.ParentKeyColumn ?? "ID")).Append(')');
                if (softDeleteFilter != null)
                    sb.Append($" AND {softDeleteFilter}");
                return sb;
            }
        }

        // ---- Insert and update

        public string Insert(InsertQuery q)
        {
            var sb = new StringBuilder($"INSERT INTO {q.TableName}");
            sb.Append($"({string.Join(",", q.SetParameters.Select(x => LeftOperand(x!)))})");
            sb.Append(OutputClause(q));
            sb.Append(" VALUES ");
            sb.Append($"({string.Join(",", q.SetParameters.Select(x => SetValue(x!)))})");
            foreach (var l in q.LinkedQueries)
                sb.Append($";{Environment.NewLine}{l.BuildQuery()}");
            return sb.ToString();
        }

        public string Update(UpdateQuery q)
        {
            var sb = new StringBuilder($"UPDATE {q.TableName} WITH (ROWLOCK) SET ");
            sb.Append(string.Join(",", q.SetParameters.Select(x => SetCondition(x!))));
            sb.Append(OutputClause(q));
            if (!q.Where.IsEmpty)
            {
                sb.Append($" WHERE {WhereRenderer.Render(q.Where, this)}");
            }
            foreach (var l in q.LinkedQueries)
                sb.Append($";{Environment.NewLine}{l.BuildQuery()}");
            return sb.ToString();
        }

        /// <summary>
        /// Returns the stored value of each output term, aliased by the term's variable so the
        /// context can hand it back. Empty when the write has no output terms.
        /// </summary>
        private static string OutputClause(Query q) =>
            q.SetParameters.HasOutputParameters
                ? " OUTPUT " + string.Join(" ,", q.SetParameters.OutputParameters.Select(o => $"INSERTED.{LeftOperand(o)} AS [{o.Variable}]"))
                : "";

        // ---- Delete

        /// <summary>
        /// The children deepest first, then the root, then SELECT @@ROWCOUNT straight after the
        /// root statement for the rows of the root type. A delete with children keeps the IDs of
        /// each level it deletes from, each read from that level's table alone (the root's by the
        /// delete's condition, once), and deletes every level by its parent's kept IDs. No
        /// statement reads a parent's table through a child's, which deadlocked against a save of
        /// the parent (see <see cref="KeptIds"/>), and the condition is not evaluated again per
        /// statement.
        /// </summary>
        public string DeleteWhere(DeleteWhereQuery q)
        {
            q.EnsureHasCondition();
            var sb = new StringBuilder();
            var children = q.TargetsChildrenFirst().ToList();
            var keeps = children.Count > 0 && children.Append(q.Root).All(t => t.Table.IdentityColumn?.DataType == typeof(Guid));
            var kept = new Dictionary<DeleteTarget, string>();

            // The IDs of a target's rows, kept in a table variable declared on first need.
            string IdsOf(DeleteTarget target)
            {
                if (kept.TryGetValue(target, out var variable))
                    return variable;
                var id = target.Table.IdentityColumn!.ColumnName;
                var select = $"SELECT [{id}] FROM {target.Table.TableName} WHERE {KeptFilter(target)}";
                variable = kept.Count == 0 ? "@__targets" : $"@__targets{kept.Count}";
                sb.Append(CaptureIds(variable, id, select));
                kept[target] = variable;
                return variable;
            }

            // A target's rows by the delete's condition (the root) or its parent's kept IDs.
            string KeptFilter(DeleteTarget target)
            {
                var condition = target.Parent == null
                    ? $"({WhereRenderer.Render(q.Where, this)})"
                    : $"[{target.ParentColumn}] IN (SELECT [{target.Parent.Table.IdentityColumn!.ColumnName}] FROM {IdsOf(target.Parent)})";
                return target.IsSoft ? $"{condition} AND {LiveRowFilter}" : condition;
            }

            string Filter(DeleteTarget target)
            {
                if (!keeps)
                    return DeleteFilter(q, target);
                if (target.Parent != null)
                    return KeptFilter(target);
                var id = target.Table.IdentityColumn!.ColumnName;
                var rows = $"[{id}] IN (SELECT [{id}] FROM {IdsOf(target)})";
                return target.IsSoft ? $"{rows} AND {LiveRowFilter}" : rows;
            }

            foreach (var target in children.Append(q.Root))
            {
                var filter = Filter(target);
                sb.Append(DeleteStatement(target, filter)).Append(';').Append(Environment.NewLine);
            }
            sb.Append("SELECT @@ROWCOUNT");
            return sb.ToString();
        }

        /// <summary>The condition selecting a target's rows; a child selects by its parent's.</summary>
        private string DeleteFilter(DeleteWhereQuery q, DeleteTarget target)
        {
            string condition;
            if (target.Parent == null)
            {
                condition = $"({WhereRenderer.Render(q.Where, this)})";
            }
            else
            {
                var parentId = target.Parent.Table.IdentityColumn!.ColumnName;
                condition = $"[{target.ParentColumn}] IN (SELECT [{parentId}] FROM {target.Parent.Table.TableName} WHERE {DeleteFilter(q, target.Parent)})";
            }
            return target.IsSoft ? $"{condition} AND {LiveRowFilter}" : condition;
        }

        private static string DeleteStatement(DeleteTarget target, string filter) => target.IsSoft
            ? $"UPDATE {target.Table.TableName} WITH (ROWLOCK) SET {string.Join(",", target.SetParameters.Select(SetCondition))} WHERE {filter}"
            : $"DELETE FROM {target.Table.TableName} WHERE {filter}";

        // ---- Terms

        string IConditionWriter.Term(Parameter term) => WhereCondition(term);
        string IConditionWriter.And => " AND ";
        string IConditionWriter.Or => " OR ";
        string IConditionWriter.Group(string condition) => $"({condition})";

        internal static string LeftOperand(Parameter p) => $"[{p.Name}]";

        /// <summary>The @variable reference for a bound term.</summary>
        private static string VariableReference(Parameter p) => $"@{p.Variable}";

        private static string OperatorString(Parameter p)
        {
            if (!p.NeedsOperator)
                return "";
            switch (p.Operator)
            {
                case Operator.Equals:
                case Operator.EqualsIgnoreCase:
                    return "=";
                case Operator.Like:
                case Operator.LikeCaseSensitive:
                case Operator.StartsWith:
                case Operator.EndsWith:
                    return "LIKE";
                case Operator.GreaterThan:
                    return ">";
                case Operator.LessThan:
                    return "<";
                case Operator.GreaterThanOrEqualTo:
                    return ">=";
                case Operator.LessThanOrEqualTo:
                    return "<=";
                case Operator.NotEquals:
                    return "<>";
                default:
                    throw new NotSupportedException($"Unsupported operator ({p.Operator}) found when generating SQL Operator String");
            }
        }

        // A pattern term's value is bound escaped by LikePattern, so its wildcards are literal.
        private static readonly string PatternEscape = $" ESCAPE '{LikePattern.EscapeCharacter}'";

        internal static string WhereCondition(Parameter p)
        {
            switch (p.Operator)
            {
                case Operator.Contains:
                    return $"CONTAINS({LeftOperand(p)}, {VariableReference(p)})";
                case Operator.ContainsCaseSensitive:
                    return $"{LeftOperand(p)} COLLATE {CaseSensitiveCollation} LIKE '%' + {VariableReference(p)} + '%'{PatternEscape}";
                case Operator.Like:
                    return $"{LeftOperand(p)} {OperatorString(p)} '%' + {VariableReference(p)} + '%'{PatternEscape}";
                case Operator.LikeCaseSensitive:
                    return $"{LeftOperand(p)} COLLATE {CaseSensitiveCollation} {OperatorString(p)} '%' + {VariableReference(p)} + '%'{PatternEscape}";
                case Operator.StartsWith:
                    return $"{LeftOperand(p)} {OperatorString(p)} {VariableReference(p)} + '%'{PatternEscape}";
                case Operator.EndsWith:
                    return $"{LeftOperand(p)} {OperatorString(p)} '%' + {VariableReference(p)}{PatternEscape}";
                case Operator.EqualsIgnoreCase:
                    return $"LOWER({LeftOperand(p)}) {OperatorString(p)} LOWER({VariableReference(p)})";
                case Operator.In:
                    return InCondition(p);
                default:
                    break;
            }
            if (!p.UsesVariable && p.Value == null)
            {
                if (p.Operator == Operator.Equals)
                    return $"{LeftOperand(p)} IS NULL";
                else if (p.Operator == Operator.NotEquals)
                    return $"{LeftOperand(p)} IS NOT NULL";
                else
                    throw new NotSupportedException($"Operator {p.Operator} with a null value is not supported in SQLWhereCondition.");
            }
            return $"{LeftOperand(p)}{OperatorString(p)}{RightOperandExpression(p)}";
        }

        /// <summary>
        /// IN over the term's values, one command parameter per element. An empty list renders
        /// as a no-match predicate.
        /// </summary>
        private static string InCondition(Parameter p)
        {
            var count = ParameterBinding.MultiValues(p).Count;
            if (count == 0)
                return "1 = 0";
            var references = Enumerable.Range(0, count).Select(i => $"@{ParameterBinding.ElementName(p, i)}");
            return $"{LeftOperand(p)} IN ({string.Join(",", references)})";
        }

        private static string RightOperandExpression(Parameter p)
        {
            if (p.UsesVariable)
                return VariableReference(p);
            if (p.Value is IServerFunction function)
                return ServerFunctionSql(function);
            throw new NotSupportedException($"Unsupported operator ({p.Operator}) with Null Value for use with a Database Parameter");
        }

        internal static string SetCondition(Parameter p) =>
            $"{LeftOperand(p)} = {SetValue(p)}";

        internal static string SetValue(Parameter p)
        {
            if (p.UsesVariable)
                return VariableReference(p);
            else if (p.Value == null)
                return "NULL";
            else if (p.Value is IServerFunction function)
                return ServerFunctionSql(function);
            else
                throw new Exception("Unknown Value Type to set.");
        }

        /// <summary>Maps a neutral server-function token to its SQL Server dialect.</summary>
        internal static string ServerFunctionSql(IServerFunction function) =>
            function.Kind switch
            {
                ServerFunctionKind.CurrentDateTime => "SYSDATETIME()",
                ServerFunctionKind.CurrentDateTimeOffset => "SYSDATETIMEOFFSET()",
                ServerFunctionKind.UtcNow => "GETUTCDATE()",
                ServerFunctionKind.NewGuid => "NEWID()",
                _ => throw new NotSupportedException($"Server function {function.Kind} is not supported by the SQL Server provider."),
            };
    }
}
