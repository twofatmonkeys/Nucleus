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

        private static string NoLock(Query q) => q.WithNoLock ? " WITH(NOLOCK)" : "";

        // ---- Select

        public string Select(SelectQuery q, bool includeChildQueries)
        {
            if (q.IsEmptyProjection)
                return ("SELECT TOP 0 NULL");
            var sb = q.IsPaged ? PagedSelect(q) : PlainSelect(q);
            if (q.WithTotalCount)
            {
                // Same filter, unpaged: the count arrives as the second result set of the batch.
                sb.Append($"; {Environment.NewLine}SELECT COUNT_BIG(*) FROM {q.TableName}{NoLock(q)}");
                var where = SelectWhere(q);
                if (where != null)
                    sb.Append(' ').Append(where);
            }
            if (includeChildQueries)
                foreach (var link in q.LinkedQueries)
                    sb.Append($"; {Environment.NewLine}").Append(link.BuildQuery());
            return sb.ToString();
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

        private StringBuilder PlainSelect(SelectQuery q)
        {
            var sb = new StringBuilder(q.Take == 0 ? $"SELECT " : $"SELECT TOP {q.Take} ");
            for (int i = 0; i < q.Columns.Count; i++)
                sb.Append($"{(i == 0 ? "" : " ,")}[{q.Columns[i]}]");
            sb.Append($" FROM {q.TableName}{NoLock(q)}");
            sb.Append(' ').Append(SelectWhere(q));
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
            var where = SelectWhere(q);
            if (where != null)
                sb.Append(' ').Append(where);
            sb.Append($") AS [__paged] WHERE [__paged].[__RowNumber] > {q.Skip}");
            if (q.Take > 0)
                sb.Append($" AND [__paged].[__RowNumber] <= {q.Skip + q.Take}");
            return sb;
        }

        /// <summary>
        /// Selects the IDs of exactly the rows a parent query returns, for a child query to select
        /// its rows by. A page or a TOP applies here too, or the children of every matching
        /// parent would be fetched rather than those of the parents returned.
        /// </summary>
        private string ParentIdSelect(SelectQuery q)
        {
            if (q.IsRoot && q.IsPaged)
                return $"SELECT [ID] FROM {PagedWindow(q, "[ID]")}";
            var top = q.IsRoot && q.Take > 0;
            var sb = new StringBuilder(top ? $"SELECT TOP {q.Take} [ID] FROM {q.TableName}" : $"SELECT [ID] FROM {q.TableName}");
            if (SelectWhere(q) is { } where)
                sb.Append(' ').Append(where);
            if (top && RootOrderBy(q) is { } orderBy)
                sb.Append(' ').Append(orderBy);
            return sb.ToString();
        }

        private StringBuilder? SelectWhere(SelectQuery q)
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
                    .Append(ParentIdSelect(parent)).Append(')');
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
            if (q.SetParameters.HasOutputParameters)
            {
                var outputParams = q.SetParameters.OutputParameters;
                sb.Append(" OUTPUT ");
                for (int i = 0; i < outputParams.Count; i++)
                {
                    sb.Append($"INSERTED.{LeftOperand(outputParams[i])} AS [{outputParams[i].Variable}]")
                        .Append(i < outputParams.Count - 1 ? " ," : "");
                }
            }
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
            if (q.SetParameters.HasOutputParameters)
            {
                var outputParams = q.SetParameters.OutputParameters;
                sb.Append(" OUTPUT ");
                for (int i = 0; i < outputParams.Count; i++)
                {
                    sb.Append(i < 1 ? $"({LeftOperand(outputParams[i])}" : $"{SetValue(outputParams[i])}")
                        .Append(i < q.SetParameters.Count - 1 ? "," : ")");
                }
            }
            if (!q.Where.IsEmpty)
            {
                sb.Append($" WHERE {WhereRenderer.Render(q.Where, this)}");
            }
            foreach (var l in q.LinkedQueries)
                sb.Append($";{Environment.NewLine}{l.BuildQuery()}");
            return sb.ToString();
        }

        // ---- Delete

        /// <summary>
        /// The children deepest first, then the root, each through its parent's condition, then
        /// SELECT @@ROWCOUNT straight after the root statement for the rows of the root type.
        /// </summary>
        public string DeleteWhere(DeleteWhereQuery q)
        {
            q.EnsureHasCondition();
            var sb = new StringBuilder();
            foreach (var target in q.TargetsChildrenFirst())
                sb.Append(DeleteStatement(q, target)).Append(';').Append(Environment.NewLine);
            sb.Append(DeleteStatement(q, q.Root)).Append(';').Append(Environment.NewLine);
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

        private string DeleteStatement(DeleteWhereQuery q, DeleteTarget target) => target.IsSoft
            ? $"UPDATE {target.Table.TableName} WITH (ROWLOCK) SET {string.Join(",", target.SetParameters.Select(SetCondition))} WHERE {DeleteFilter(q, target)}"
            : $"DELETE FROM {target.Table.TableName} WHERE {DeleteFilter(q, target)}";

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

        internal static string WhereCondition(Parameter p)
        {
            switch (p.Operator)
            {
                case Operator.Contains:
                    return $"CONTAINS({LeftOperand(p)}, {VariableReference(p)})";
                case Operator.ContainsCaseSensitive:
                    return $"{LeftOperand(p)} COLLATE {CaseSensitiveCollation} LIKE '%' + {VariableReference(p)} + '%'";
                case Operator.Like:
                    return $"{LeftOperand(p)} {OperatorString(p)} '%' + {VariableReference(p)} + '%'";
                case Operator.LikeCaseSensitive:
                    return $"{LeftOperand(p)} COLLATE {CaseSensitiveCollation} {OperatorString(p)} '%' + {VariableReference(p)} + '%'";
                case Operator.StartsWith:
                    return $"{LeftOperand(p)} {OperatorString(p)} {VariableReference(p)} + '%'";
                case Operator.EndsWith:
                    return $"{LeftOperand(p)} {OperatorString(p)} '%' + {VariableReference(p)}";
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
                ServerFunctionKind.SysDateTime => "SYSDATETIME()",
                ServerFunctionKind.SysDateTimeOffset => "SYSDATETIMEOFFSET()",
                ServerFunctionKind.UtcNow => "GETUTCDATE()",
                ServerFunctionKind.NewGuid => "NEWID()",
                _ => throw new NotSupportedException($"Server function {function.Kind} is not supported by the SQL Server provider."),
            };
    }
}
