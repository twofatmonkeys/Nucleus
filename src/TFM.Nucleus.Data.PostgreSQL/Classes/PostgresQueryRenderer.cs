using System;
using System.Linq;
using System.Text;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.Queries;
using TFM.Nucleus.Data.Schema;
using static TFM.Nucleus.Data.PostgreSQL.PostgresIdentifiers;

namespace TFM.Nucleus.Data.PostgreSQL
{
    /// <summary>
    /// Writes the neutral query model as PostgreSQL. The model decides what a query means
    /// (paging, identity order, the live-row filter, the children-first delete order); this
    /// class only writes it. Pattern terms use ILIKE, so they match without regard to case as they
    /// do under SQL Server's default collation; equality is exact, PostgreSQL's native rule.
    /// </summary>
    internal sealed class PostgresQueryRenderer(string schema) : IQueryRenderer
    {
        /// <summary>The schema every table lives in.</summary>
        public string Schema { get; } = schema;

        private const string LiveRowFilter = "\"IsDeleted\" = false";
        private const string FullTextConfiguration = "english";

        private string TableOf(Query q) => Table(Schema, q.TableName ?? throw new InvalidQueryException("The query has no table."));

        private string TableOf(IDataTableSchema table) => Table(Schema, table.TableName);

        // ---- Select

        public string Select(SelectQuery q, bool includeChildQueries)
        {
            if (q.IsAggregate)
                return AggregateSelect(q);
            if (q.IsEmptyProjection)
                return "SELECT NULL LIMIT 0";
            var sb = new StringBuilder(RowsSelect(q));
            if (q.WithTotalCount)
            {
                // Same filter, unpaged: the count arrives as the second result set of the batch.
                sb.Append($";{Environment.NewLine}SELECT count(*) FROM {TableOf(q)}");
                if (SelectWhere(q) is { } where)
                    sb.Append(' ').Append(where);
            }
            if (includeChildQueries)
                foreach (var link in q.LinkedQueries)
                    sb.Append($";{Environment.NewLine}").Append(link.BuildQuery());
            return sb.ToString();
        }

        /// <summary>
        /// A grouped read: the group keys, then each measure under its alias, grouped by the keys.
        /// With no keys it is one row over every matching row.
        /// </summary>
        private string AggregateSelect(SelectQuery q)
        {
            q.EnsureAggregateShape();
            var terms = q.GroupBy.Select(Quote).Concat(q.Measures.Select(MeasureTerm));
            var sb = new StringBuilder($"SELECT {string.Join(", ", terms)} FROM {TableOf(q)}");
            if (SelectWhere(q) is { } where)
                sb.Append(' ').Append(where);
            if (q.GroupBy.Count > 0)
                sb.Append(" GROUP BY ").Append(string.Join(", ", q.GroupBy.Select(Quote)));
            return sb.ToString();
        }

        private static string MeasureTerm(Measure m)
        {
            var column = m.Column == null ? null : Quote(m.Column.ColumnName);
            var figure = m.Kind switch
            {
                MeasureKind.Count => "count(*)",
                MeasureKind.CountDistinct => $"count(DISTINCT {column})",
                MeasureKind.Min => $"min({column})",
                MeasureKind.Max => $"max({column})",
                MeasureKind.Sum => $"sum({column})",
                _ => throw new NotSupportedException($"Measure {m.Kind} is not supported by the PostgreSQL provider."),
            };
            return $"{figure} AS {Quote(m.Alias)}";
        }

        private string RowsSelect(SelectQuery q)
        {
            if (q.IsPaged)
                q.EnsurePageable();
            var sb = new StringBuilder($"SELECT {string.Join(", ", q.Columns.Select(Quote))} FROM {TableOf(q)}");
            if (SelectWhere(q) is { } where)
                sb.Append(' ').Append(where);
            if (q.IsRoot)
                AppendOrderAndPage(sb, q);
            return sb.ToString();
        }

        /// <summary>
        /// The order of the root rows, then the page. A page is ordered by ID when no order was
        /// asked for, as is a limit on a root with child queries, so the rows and the children's
        /// subquery see the same parents.
        /// </summary>
        private void AppendOrderAndPage(StringBuilder sb, SelectQuery q)
        {
            if (OrderBy(q) is { } orderBy)
                sb.Append(' ').Append(orderBy);
            if (q.Take > 0)
                sb.Append($" LIMIT {q.Take}");
            if (q.IsPaged)
                sb.Append($" OFFSET {q.Skip}");
        }

        private static string? OrderBy(SelectQuery q)
        {
            if (!q.OrderBy.IsEmpty)
                return "ORDER BY " + string.Join(", ", q.OrderBy.Where(o => !o.IsEmpty).Select(o => $"{Quote(o.Column)} {(o.IsDescending ? "DESC" : "ASC")}"));
            return q.IsPaged || q.NeedsIdentityOrder ? "ORDER BY \"ID\"" : null;
        }

        /// <summary>
        /// The IDs of exactly the rows a parent query returns, for a child query to select its
        /// rows by: its page or its limit applies here too.
        /// </summary>
        private string ParentIdSelect(SelectQuery q)
        {
            var sb = new StringBuilder($"SELECT \"ID\" FROM {TableOf(q)}");
            if (SelectWhere(q) is { } where)
                sb.Append(' ').Append(where);
            if (q.IsRoot)
                AppendOrderAndPage(sb, q);
            return sb.ToString();
        }

        /// <summary>
        /// A root selects by its own condition; a child collection query selects by the IDs of
        /// the parent rows returned, as on every provider. Soft-deleted rows are left out unless
        /// the read includes them.
        /// </summary>
        private string? SelectWhere(SelectQuery q)
        {
            var conditions = new System.Collections.Generic.List<string>(2);
            if (q.IsRoot)
            {
                if (!q.Where.IsEmpty)
                    conditions.Add(q.AppliesLiveRowFilter ? $"({Condition(q)})" : Condition(q));
            }
            else
            {
                var parent = q.ParentSelect
                    ?? throw new InvalidQueryException("A child collection query must hang off a select.");
                conditions.Add($"{Quote(q.LinkedColumn!)} IN ({ParentIdSelect(parent)})");
            }
            if (q.AppliesLiveRowFilter)
                conditions.Add(LiveRowFilter);
            return conditions.Count == 0 ? null : "WHERE " + string.Join(" AND ", conditions);
        }

        private static string Condition(Query q) => WhereRenderer.Render(q.Where, ConditionWriter.Instance);

        // ---- Insert and update

        public string Insert(InsertQuery q)
        {
            var sb = new StringBuilder($"INSERT INTO {TableOf(q)} ");
            sb.Append($"({string.Join(", ", q.SetParameters.Select(p => Quote(p!.Name!)))})");
            sb.Append($" VALUES ({string.Join(", ", q.SetParameters.Select(p => SetValue(p!)))})");
            sb.Append(ReturningClause(q));
            foreach (var l in q.LinkedQueries)
                sb.Append($";{Environment.NewLine}{l.BuildQuery()}");
            return sb.ToString();
        }

        public string Update(UpdateQuery q)
        {
            var sb = new StringBuilder($"UPDATE {TableOf(q)} SET ");
            sb.Append(string.Join(", ", q.SetParameters.Select(p => SetCondition(p!))));
            if (!q.Where.IsEmpty)
                sb.Append($" WHERE {Condition(q)}");
            sb.Append(ReturningClause(q));
            foreach (var l in q.LinkedQueries)
                sb.Append($";{Environment.NewLine}{l.BuildQuery()}");
            return sb.ToString();
        }

        /// <summary>
        /// Returns the stored value of each output term, aliased by the term's variable so the
        /// context can hand it back. Empty when the write has no output terms.
        /// </summary>
        private static string ReturningClause(Query q) =>
            q.SetParameters.HasOutputParameters
                ? " RETURNING " + string.Join(", ", q.SetParameters.OutputParameters.Select(o => $"{Quote(o.Name!)} AS {Quote(o.Variable!)}"))
                : "";

        // ---- Delete

        /// <summary>
        /// The children deepest first, then the root, each through its parent's condition. The
        /// root statement returns one row per row it affects, counted, so the command's scalar
        /// result is the number of rows of the root type affected.
        /// </summary>
        public string DeleteWhere(DeleteWhereQuery q)
        {
            q.EnsureHasCondition();
            var sb = new StringBuilder();
            foreach (var target in q.TargetsChildrenFirst())
                sb.Append(DeleteStatement(q, target, returning: false)).Append(';').Append(Environment.NewLine);
            sb.Append($"WITH affected AS ({DeleteStatement(q, q.Root, returning: true)}) SELECT count(*) FROM affected");
            return sb.ToString();
        }

        private string DeleteFilter(DeleteWhereQuery q, DeleteTarget target)
        {
            string condition;
            if (target.Parent == null)
                condition = $"({Condition(q)})";
            else
            {
                var parentId = target.Parent.Table.IdentityColumn!.ColumnName;
                condition = $"{Quote(target.ParentColumn!)} IN (SELECT {Quote(parentId)} FROM {TableOf(target.Parent.Table)} WHERE {DeleteFilter(q, target.Parent)})";
            }
            return target.IsSoft ? $"{condition} AND {LiveRowFilter}" : condition;
        }

        private string DeleteStatement(DeleteWhereQuery q, DeleteTarget target, bool returning)
        {
            var statement = target.IsSoft
                ? $"UPDATE {TableOf(target.Table)} SET {string.Join(", ", target.SetParameters.Select(SetCondition))} WHERE {DeleteFilter(q, target)}"
                : $"DELETE FROM {TableOf(target.Table)} WHERE {DeleteFilter(q, target)}";
            return returning ? $"{statement} RETURNING 1" : statement;
        }

        // ---- Terms

        /// <summary>Writes the terms of a condition.</summary>
        private sealed class ConditionWriter : IConditionWriter
        {
            public static readonly ConditionWriter Instance = new();

            public string Term(Parameter term) => WhereCondition(term);
            public string And => " AND ";
            public string Or => " OR ";
            public string Group(string condition) => $"({condition})";
        }

        private static string Variable(Parameter p) => $"@{p.Variable}";

        // A pattern term's value is bound escaped by LikePattern, so its wildcards are literal.
        private static string Pattern(string column, string op, string pattern) => $"{column} {op} {pattern} ESCAPE '{LikePattern.EscapeCharacter}'";

        internal static string WhereCondition(Parameter p)
        {
            var column = Quote(p.Name!);
            switch (p.Operator)
            {
                // A full-text search: the parser only produces it for a FullTextSearch column.
                case Operator.Contains:
                    return $"to_tsvector('{FullTextConfiguration}', {column}) @@ plainto_tsquery('{FullTextConfiguration}', {Variable(p)})";
                case Operator.ContainsCaseSensitive:
                case Operator.LikeCaseSensitive:
                    return Pattern(column, "LIKE", $"'%' || {Variable(p)} || '%'");
                case Operator.Like:
                    return Pattern(column, "ILIKE", $"'%' || {Variable(p)} || '%'");
                case Operator.StartsWith:
                    return Pattern(column, "ILIKE", $"{Variable(p)} || '%'");
                case Operator.EndsWith:
                    return Pattern(column, "ILIKE", $"'%' || {Variable(p)}");
                case Operator.EqualsIgnoreCase:
                    return $"lower({column}) = lower({Variable(p)})";
                case Operator.In:
                    return InCondition(p, column);
            }
            if (!p.UsesVariable && p.Value == null)
            {
                return p.Operator switch
                {
                    Operator.Equals => $"{column} IS NULL",
                    Operator.NotEquals => $"{column} IS NOT NULL",
                    _ => throw new NotSupportedException($"Operator {p.Operator} with a null value is not supported in a condition."),
                };
            }
            var op = p.Operator switch
            {
                Operator.Equals => "=",
                Operator.NotEquals => "<>",
                Operator.GreaterThan => ">",
                Operator.LessThan => "<",
                Operator.GreaterThanOrEqualTo => ">=",
                Operator.LessThanOrEqualTo => "<=",
                _ => throw new NotSupportedException($"Unsupported operator ({p.Operator}) found when writing a condition."),
            };
            return $"{column} {op} {RightOperand(p)}";
        }

        /// <summary>IN over the term's values, one parameter per element; an empty list matches nothing.</summary>
        private static string InCondition(Parameter p, string column)
        {
            var count = ParameterBinding.MultiValues(p).Count;
            if (count == 0)
                return "1 = 0";
            var references = Enumerable.Range(0, count).Select(i => $"@{ParameterBinding.ElementName(p, i)}");
            return $"{column} IN ({string.Join(", ", references)})";
        }

        private static string RightOperand(Parameter p)
        {
            if (p.UsesVariable)
                return Variable(p);
            if (p.Value is IServerFunction function)
                return ServerFunctionSql(function);
            throw new NotSupportedException($"Unsupported operator ({p.Operator}) with Null Value for use with a Database Parameter");
        }

        internal static string SetCondition(Parameter p) => $"{Quote(p.Name!)} = {SetValue(p)}";

        internal static string SetValue(Parameter p)
        {
            if (p.UsesVariable)
                return Variable(p);
            if (p.Value == null)
                return "NULL";
            if (p.Value is IServerFunction function)
                return ServerFunctionSql(function);
            throw new Exception("Unknown Value Type to set.");
        }

        /// <summary>
        /// Maps a neutral server-function token to PostgreSQL. clock_timestamp() rather than now(),
        /// which is fixed at the start of the transaction, so each statement stamps its own time.
        /// </summary>
        internal static string ServerFunctionSql(IServerFunction function) =>
            function.Kind switch
            {
                ServerFunctionKind.CurrentDateTime => "clock_timestamp()::timestamp",
                ServerFunctionKind.CurrentDateTimeOffset => "clock_timestamp()",
                ServerFunctionKind.UtcNow => "(clock_timestamp() AT TIME ZONE 'UTC')",
                ServerFunctionKind.NewGuid => "gen_random_uuid()",
                _ => throw new NotSupportedException($"Server function {function.Kind} is not supported by the PostgreSQL provider."),
            };
    }
}
