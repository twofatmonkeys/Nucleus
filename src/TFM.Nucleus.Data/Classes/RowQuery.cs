using System.Globalization;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using TFM.Nucleus.Data.Queries;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// A read that returns rows of values rather than entities: some columns of each matching row,
    /// ordered and paged in the database (<see cref="Select"/>, <see cref="OrderBy"/>,
    /// <see cref="ToPageAsync"/>), or one row per group with figures worked out in the database
    /// (<see cref="GroupBy"/>, <see cref="Count(string)"/>, <see cref="Max"/> and the rest). Only what
    /// is asked for is read, so a column that is never selected is never transferred. The rows
    /// cannot be saved: a partial entity would write nulls over what it never read.
    /// </summary>
    /// <remarks>
    /// The filter is the same single Where every load translates. Build a read, then run it once
    /// with one of the materialisers; the builder methods change this instance and return it.
    /// </remarks>
    public sealed class RowQuery<T> where T : ICoreObject<T>, new()
    {
        private readonly IDataProvider _provider;
        private readonly Expression _predicate;
        private readonly List<string> _select = [];
        private readonly List<(string Property, bool Descending)> _order = [];
        private readonly List<string> _groupBy = [];
        private readonly List<(MeasureKind Kind, string? Property, string Alias)> _measures = [];

        /// <param name="provider">The provider that stores <typeparamref name="T"/>.</param>
        /// <param name="predicate">The rows to read, or null for every row.</param>
        public RowQuery(IDataProvider provider, Expression<Func<T, bool>>? predicate)
            : this(provider, predicate?.Body ?? Expression.Constant(true))
        {
        }

        private RowQuery(IDataProvider provider, Expression predicateBody)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _predicate = predicateBody;
        }

        private bool IsAggregate => _groupBy.Count > 0 || _measures.Count > 0;

        /// <summary>The properties each row returns, named by property. Not with <see cref="GroupBy"/> or a measure.</summary>
        public RowQuery<T> Select(params Expression<Func<T, object?>>[] properties)
        {
            ArgumentNullException.ThrowIfNull(properties);
            foreach (var property in properties)
                _select.Add(PropertyName(property));
            return this;
        }

        /// <summary>Orders the rows by <paramref name="property"/>, smallest first, after any order already given.</summary>
        public RowQuery<T> OrderBy(Expression<Func<T, object?>> property) => Order(property, descending: false);

        /// <summary>Orders the rows by <paramref name="property"/>, largest first, after any order already given.</summary>
        public RowQuery<T> OrderByDescending(Expression<Func<T, object?>> property) => Order(property, descending: true);

        /// <summary>The same as <see cref="OrderBy"/>: a further order for rows the earlier ones tie.</summary>
        public RowQuery<T> ThenBy(Expression<Func<T, object?>> property) => Order(property, descending: false);

        /// <summary>The same as <see cref="OrderByDescending"/>: a further order for rows the earlier ones tie.</summary>
        public RowQuery<T> ThenByDescending(Expression<Func<T, object?>> property) => Order(property, descending: true);

        /// <summary>
        /// Returns one row per distinct combination of <paramref name="properties"/>, each named by
        /// its property, with the measures asked for. The groups come back in no particular order.
        /// </summary>
        public RowQuery<T> GroupBy(params Expression<Func<T, object?>>[] properties)
        {
            ArgumentNullException.ThrowIfNull(properties);
            foreach (var property in properties)
                _groupBy.Add(PropertyName(property));
            return this;
        }

        /// <summary>How many rows each group has, as a long under <paramref name="alias"/>.</summary>
        public RowQuery<T> Count(string alias) => Add(MeasureKind.Count, null, alias);

        /// <summary>How many different non-null values of <paramref name="property"/> each group has, as a long under <paramref name="alias"/>.</summary>
        public RowQuery<T> CountDistinct(Expression<Func<T, object?>> property, string alias) => Add(MeasureKind.CountDistinct, property, alias);

        /// <summary>The smallest value of <paramref name="property"/> in each group, as the property's type, under <paramref name="alias"/>.</summary>
        public RowQuery<T> Min(Expression<Func<T, object?>> property, string alias) => Add(MeasureKind.Min, property, alias);

        /// <summary>The largest value of <paramref name="property"/> in each group, as the property's type, under <paramref name="alias"/>.</summary>
        public RowQuery<T> Max(Expression<Func<T, object?>> property, string alias) => Add(MeasureKind.Max, property, alias);

        /// <summary>The total of <paramref name="property"/> in each group, as the number the engine returns, under <paramref name="alias"/>.</summary>
        public RowQuery<T> Sum(Expression<Func<T, object?>> property, string alias) => Add(MeasureKind.Sum, property, alias);

        /// <summary>Every row, read into memory.</summary>
        public async Task<IReadOnlyList<ResultRow>> ToListAsync(CancellationToken cancellationToken = default)
        {
            var rows = new List<ResultRow>();
            await foreach (var row in StreamAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(row);
            return rows;
        }

        /// <summary>Every row, one at a time as the database returns it. Each row is its own object and can be kept.</summary>
        public async IAsyncEnumerable<ResultRow> StreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (ExpressionParser.Classify(_predicate) == PredicateShape.MatchesNone)
                yield break;
            var query = Build();
            var names = ResultRow.IndexOf(query.ResultColumns!);
            await using var context = await _provider.CreateContextAsync(token: cancellationToken).ConfigureAwait(false);
            await foreach (var row in context.StreamAsync(query, cancellationToken).ConfigureAwait(false))
                yield return new ResultRow(names, [.. row.Values]);
        }

        /// <summary>
        /// One page of rows and how many rows match in all, in one round trip. The read must be
        /// ordered, so every page comes from the same order, and must not be grouped.
        /// </summary>
        public async Task<RowPage> ToPageAsync(int skip, int take, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(skip);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
            if (IsAggregate)
                throw new InvalidOperationException("A grouped read is not paged; order and page its groups in memory.");
            if (_order.Count == 0)
                throw new InvalidOperationException("A paged read needs an order, or its pages could overlap. Call OrderBy first.");
            if (ExpressionParser.Classify(_predicate) == PredicateShape.MatchesNone)
                return new RowPage([], 0, skip, take);
            var query = Build();
            query.Skip = skip;
            query.Take = take;
            query.WithTotalCount = true;
            var names = ResultRow.IndexOf(query.ResultColumns!);
            await using var context = await _provider.CreateContextAsync(token: cancellationToken).ConfigureAwait(false);
            using var dataSet = await context.GetDataSetAsync(query, cancellationToken).ConfigureAwait(false);
            var rows = new List<ResultRow>(dataSet.RowCount);
            for (var i = 0; i < dataSet.RowCount; i++)
                rows.Add(new ResultRow(names, [.. dataSet[i]]));
            return new RowPage(rows, dataSet.TotalRowCount ?? rows.Count, skip, take);
        }

        /// <summary>How many rows match, counted in the database. Ignores any columns, order or groups asked for.</summary>
        public async Task<long> CountAsync(CancellationToken cancellationToken = default)
        {
            if (ExpressionParser.Classify(_predicate) == PredicateShape.MatchesNone)
                return 0;
            var counting = new RowQuery<T>(_provider, _predicate).Count("Rows");
            await foreach (var row in counting.StreamAsync(cancellationToken).ConfigureAwait(false))
                return row.Get<long>("Rows");
            return 0;
        }

        /// <summary>The query this read runs, built against the provider's table for <typeparamref name="T"/>.</summary>
        internal SelectQuery Build()
        {
            if (_provider.ParseSelectExpression<T>(_predicate) is not SelectQuery query)
                throw new NotSupportedException($"The provider for {typeof(T).Name} does not support row reads.");
            var table = query.TableSchema
                ?? throw new InvalidOperationException($"{typeof(T).Name} has no table to read rows from.");

            // A row read never fills child collections.
            query.LinkedQueries.Clear();

            if (IsAggregate)
            {
                if (_select.Count > 0)
                    throw new InvalidOperationException("A grouped read returns its group keys and measures; do not Select as well.");
                if (_order.Count > 0)
                    throw new InvalidOperationException("A grouped read is not ordered; order its groups in memory.");
                var keys = _groupBy.Select(p => Column(table, p)).ToList();
                var measures = _measures.Select(m => new Measure(m.Kind, m.Property == null ? null : Column(table, m.Property), m.Alias)).ToList();
                var names = _groupBy.Concat(measures.Select(m => m.Alias)).ToList();
                if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
                    throw new InvalidOperationException($"Every group key and measure needs its own name; {string.Join(", ", names)} repeats one.");
                query.Columns = [.. keys.Select(k => k.ColumnName)];
                query.GroupBy = [.. keys.Select(k => k.ColumnName)];
                query.Measures = measures;
                query.ResultColumns = [.. _groupBy.Select((p, i) => new ResultColumn(p, keys[i])), .. measures.Select(m => new ResultColumn(m.Alias, m.ValueColumn))];
            }
            else
            {
                if (_select.Count == 0)
                    throw new InvalidOperationException("A row read needs at least one column. Call Select, or GroupBy with a measure.");
                if (_select.Distinct(StringComparer.Ordinal).Count() != _select.Count)
                    throw new InvalidOperationException("A column is selected twice.");
                var columns = _select.Select(p => Column(table, p)).ToList();
                query.Columns = [.. columns.Select(c => c.ColumnName)];
                query.ResultColumns = [.. _select.Select((p, i) => new ResultColumn(p, columns[i]))];
                foreach (var (property, descending) in _order)
                    query.AddOrderBy(property, descending);
            }
            return query;
        }

        private RowQuery<T> Order(Expression<Func<T, object?>> property, bool descending)
        {
            _order.Add((PropertyName(property), descending));
            return this;
        }

        private RowQuery<T> Add(MeasureKind kind, Expression<Func<T, object?>>? property, string alias)
        {
            if (!Measure.IsAlias(alias))
                throw new ArgumentException($"'{alias}' is not a usable name for a measure: start with a letter or underscore, then letters, digits or underscores, at most 63 in all.", nameof(alias));
            _measures.Add((kind, property == null ? null : PropertyName(property), alias));
            return this;
        }

        private static IDataColumnSchema Column(IDataTableSchema table, string property) =>
            table.FindByPropertyName(property)
            ?? throw new ArgumentException($"{typeof(T).Name}.{property} is not a stored column.", nameof(property));

        /// <summary>The property a selector names: <c>e => e.Name</c>, boxed or not.</summary>
        private static string PropertyName(Expression<Func<T, object?>> selector)
        {
            ArgumentNullException.ThrowIfNull(selector);
            var body = selector.Body;
            while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
                body = convert.Operand;
            if (body is MemberExpression { Expression: ParameterExpression } member)
                return member.Member.Name;
            throw new ArgumentException($"'{selector}' does not name a property of {typeof(T).Name}; use e => e.Property.", nameof(selector));
        }
    }

    /// <summary>One row of a <see cref="RowQuery{T}"/>: its values by name, in the order the read asked for them.</summary>
    public sealed class ResultRow
    {
        private readonly IReadOnlyDictionary<string, int> _index;
        private readonly object?[] _values;

        internal ResultRow(IReadOnlyDictionary<string, int> index, object?[] values)
        {
            _index = index;
            _values = values;
        }

        internal static IReadOnlyDictionary<string, int> IndexOf(IReadOnlyList<ResultColumn> columns)
        {
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < columns.Count; i++)
                index[columns[i].Name] = i;
            return index;
        }

        /// <summary>The names of the values, in order.</summary>
        public IEnumerable<string> Names => _index.OrderBy(p => p.Value).Select(p => p.Key);

        /// <summary>A value as read, or null for a database null.</summary>
        public object? this[string name]
        {
            get
            {
                var value = Raw(name);
                return value is DBNull ? null : value;
            }
        }

        /// <summary>
        /// A value converted to <typeparamref name="TValue"/>: a database null is the type's default
        /// (null for a nullable or reference type), an integer becomes an enum, and a number becomes
        /// the numeric type asked for.
        /// </summary>
        public TValue? Get<TValue>(string name)
        {
            var value = this[name];
            if (value == null)
                return default;
            var target = Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue);
            if (value.GetType() == target || target.IsInstanceOfType(value))
                return (TValue)value;
            if (target.IsEnum)
                return (TValue)Enum.ToObject(target, value);
            return (TValue)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }

        private object? Raw(string name) =>
            _index.TryGetValue(name, out var i) ? _values[i] : throw new ArgumentException($"The row has no value named '{name}'.", nameof(name));
    }

    /// <summary>One page of a paged <see cref="RowQuery{T}"/>, with how many rows match in all.</summary>
    public sealed record RowPage(IReadOnlyList<ResultRow> Rows, long TotalCount, int Skip, int Take)
    {
        /// <summary>True when rows remain after this page, as <c>PagedResult.HasMore</c> says for entities.</summary>
        public bool HasMore => Skip + Rows.Count < TotalCount;
    }
}
