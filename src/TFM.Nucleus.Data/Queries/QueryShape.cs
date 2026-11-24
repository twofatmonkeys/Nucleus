using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace TFM.Nucleus.Data.Queries
{
    /// <summary>
    /// A query expression taken apart into the rows it filters to and the operators that order and
    /// cut them: what <see cref="ExpressionParser.Split"/> returns. A plain filter (a predicate,
    /// or a chain of Where calls) is carried as it came, with no operators, so a load that only
    /// filters translates exactly as before and allocates nothing more.
    /// </summary>
    internal readonly struct QueryShape
    {
        private QueryShape(Expression predicate, IReadOnlyList<(string Property, bool Descending)>? order, int skip, int? take)
        {
            Predicate = predicate;
            Order = order;
            Skip = skip;
            Take = take;
        }

        /// <summary>A filter with no operators: the expression as given.</summary>
        internal static QueryShape Plain(Expression predicate) => new(predicate, null, 0, null);

        internal static QueryShape WithOperators(Expression predicate, IReadOnlyList<(string Property, bool Descending)>? order, int skip, int? take) =>
            new(predicate, order is { Count: > 0 } ? order : null, skip, take);

        /// <summary>
        /// The rows to read, as any expression a load translates: the expression itself for a
        /// plain filter, else the Where bodies ANDed, or <c>true</c> when there were none.
        /// </summary>
        public Expression Predicate { get; }

        /// <summary>The properties to order by, first key first; null when the query asks for no order.</summary>
        public IReadOnlyList<(string Property, bool Descending)>? Order { get; }

        /// <summary>The rows to skip; 0 for none.</summary>
        public int Skip { get; }

        /// <summary>The most rows to return; null for no limit.</summary>
        public int? Take { get; }

        /// <summary>True when the query orders, skips or limits as well as filtering.</summary>
        public bool HasOperators => Order != null || Skip > 0 || Take != null;

        /// <summary>True when the query is cut to no rows (<c>Take(0)</c>), so nothing needs to be read.</summary>
        public bool TakesNothing => Take == 0;

        /// <summary>True when the query skips or limits, so a count or a page must allow for it.</summary>
        public bool IsCut => Skip > 0 || Take != null;

        /// <summary>
        /// Writes the order, skip and limit onto a select built from <see cref="Predicate"/>. Each
        /// ordered property must be a stored column of the query's table, or this throws rather
        /// than ordering by a name the database does not have. A limit of no rows is never
        /// written, because on a select 0 means no limit; callers return nothing first.
        /// </summary>
        public void ApplyTo(ISelectQuery query)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (TakesNothing)
                throw new InvalidOperationException("A query cut to no rows reads nothing. Check TakesNothing before building it.");
            if (Order != null)
            {
                var table = query.DataTableSchema;
                foreach (var (property, descending) in Order)
                {
                    if (table != null && table.FindByPropertyName(property) == null)
                        throw new NotSupportedException(
                            $"{table.ObjectSchema.Name}.{property} is not a stored column, so the query cannot be ordered by it in the database.");
                    query.AddOrderBy(property, descending);
                }
            }
            query.Skip = Skip;
            if (Take is { } take)
                query.Take = take;
        }

        /// <summary>
        /// How many rows a query cut by this shape returns, given how many its filter matches:
        /// what is left after the skip, capped by the limit.
        /// </summary>
        public long CountWithin(long matching)
        {
            var left = Math.Max(0, matching - Skip);
            return Take is { } take ? Math.Min(left, take) : left;
        }
    }
}
