using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data
{
    public static class ObjectDataAdaptorQueryableExtensions
    {
        /// <summary>
        /// Leaves a child collection out of a Nucleus query, so <c>.Where(..).Exclude(b =&gt; b.Files)</c>
        /// works as well as <c>.Exclude(..).Where(..)</c>. Only queries from IObjectDataAdaptor support it.
        /// </summary>
        public static IQueryable<TSource> Exclude<TSource>(this IQueryable<TSource> source,
            Expression<Func<TSource, TFM.Nucleus.Abstractions.Interfaces.INucleusChildCollection>> collection)
            where TSource : ICoreObject<TSource>, new() =>
            source is ObjectDataAdaptor<TSource> adaptor
                ? adaptor.Exclude(collection)
                : throw new InvalidOperationException(
                    $"Exclude applies to Nucleus queries only; this source is a {source.GetType().Name}.");

        /// <summary>
        /// Also returns the soft-deleted rows of a Nucleus query, so
        /// <c>.Where(..).IncludeDeleted()</c> works as well as <c>.IncludeDeleted().Where(..)</c>.
        /// Only queries from IObjectDataAdaptor support it.
        /// </summary>
        public static IQueryable<TSource> IncludeDeleted<TSource>(this IQueryable<TSource> source)
            where TSource : ICoreObject<TSource>, new() =>
            source is ObjectDataAdaptor<TSource> adaptor
                ? adaptor.IncludeDeleted()
                : throw new InvalidOperationException(
                    $"IncludeDeleted applies to Nucleus queries only; this source is a {source.GetType().Name}.");

        /// <summary>
        /// Loads a lazy ([RetrieveOnDemand]) child collection with a Nucleus query's rows, so
        /// <c>.Where(..).Include(m =&gt; m.Naps)</c> works as well as <c>.Include(..).Where(..)</c>.
        /// Only queries from IObjectDataAdaptor support it.
        /// </summary>
        public static IQueryable<TSource> Include<TSource>(this IQueryable<TSource> source,
            Expression<Func<TSource, TFM.Nucleus.Abstractions.Interfaces.INucleusChildCollection>> collection)
            where TSource : ICoreObject<TSource>, new() =>
            source is ObjectDataAdaptor<TSource> adaptor
                ? adaptor.Include(collection)
                : throw new InvalidOperationException(
                    $"Include applies to Nucleus queries only; this source is a {source.GetType().Name}.");

        /// <summary>
        /// Loads the entities a <c>[LinkedObject]</c> property refers to with a Nucleus query's
        /// rows, so <c>.Where(..).Include(m =&gt; m.Keeper)</c> works as well as
        /// <c>.Include(..).Where(..)</c>. Only queries from IObjectDataAdaptor support it.
        /// </summary>
        public static IQueryable<TSource> Include<TSource>(this IQueryable<TSource> source,
            Expression<Func<TSource, ICoreObject?>> reference)
            where TSource : ICoreObject<TSource>, new() =>
            source is ObjectDataAdaptor<TSource> adaptor
                ? adaptor.Include(reference)
                : throw new InvalidOperationException(
                    $"Include applies to Nucleus queries only; this source is a {source.GetType().Name}.");

        /// <summary>
        /// Loads a linked collection's members with a Nucleus query's rows, so
        /// <c>.Where(..).Include(t =&gt; t.Monkeys)</c> works as well as <c>.Include(..).Where(..)</c>.
        /// Only queries from IObjectDataAdaptor support it.
        /// </summary>
        public static IQueryable<TSource> Include<TSource>(this IQueryable<TSource> source,
            Expression<Func<TSource, TFM.Nucleus.Abstractions.Interfaces.INucleusLinkedCollection>> collection)
            where TSource : ICoreObject<TSource>, new() =>
            source is ObjectDataAdaptor<TSource> adaptor
                ? adaptor.Include(collection)
                : throw new InvalidOperationException(
                    $"Include applies to Nucleus queries only; this source is a {source.GetType().Name}.");

        /// <summary>
        /// One page of a Nucleus query's rows plus the unpaged total count, in a single round trip,
        /// so <c>.Where(..).ToPagedResultAsync(0, 20)</c> works as well as on the adaptor itself.
        /// Only queries from IObjectDataAdaptor support it.
        /// </summary>
        public static Task<PagedResult<TSource>> ToPagedResultAsync<TSource>(this IQueryable<TSource> source,
            int skip, int take, CancellationToken cancellationToken = default)
            where TSource : ICoreObject<TSource>, new() =>
            source is ObjectDataAdaptor<TSource> adaptor
                ? adaptor.ToPagedResultAsync(skip, take, cancellationToken)
                : throw new InvalidOperationException(
                    $"ToPagedResultAsync applies to Nucleus queries only; this source is a {source.GetType().Name}.");

        public static async Task<List<TSource>> ToListAsync<TSource>(this IQueryable<TSource> source, CancellationToken cancellationToken = default)
        {
            var list = new List<TSource>();
            await foreach (var element in source.AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                list.Add(element);
            }
            return list;
        }

        public static async Task<TSource[]> ToArrayAsync<TSource>(this IQueryable<TSource> source, CancellationToken cancellationToken = default)
        {
            var list = await source.ToListAsync(cancellationToken).ConfigureAwait(false);
            return [.. list];
        }

        // First, Single, Any and Count on a Nucleus query run in the database: First reads one row,
        // Single two, Any one ID and Count a count, with no child query for Any and Count. Any other
        // source goes through AsAsyncEnumerable, which refuses it.

        /// <summary>The first row, or the default when there is none. A Nucleus query reads one row.</summary>
        public static async Task<TSource?> FirstOrDefaultAsync<TSource>(this IQueryable<TSource> source, CancellationToken cancellationToken = default)
        {
            if (source is INucleusQueryOperators<TSource> nucleus)
                return (await nucleus.FirstCoreAsync(cancellationToken).ConfigureAwait(false)).Row;
            await foreach (var element in source.AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                return element;
            }
            return default;
        }

        public static async Task<TSource?> FirstOrDefaultAsync<TSource>(this IQueryable<TSource> source, Expression<Func<TSource, bool>> predicate, CancellationToken cancellationToken = default)
        {
            return await source.Where(predicate).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>The first row; throws when there is none. A Nucleus query reads one row.</summary>
        public static async Task<TSource> FirstAsync<TSource>(this IQueryable<TSource> source, CancellationToken cancellationToken = default)
        {
            if (source is INucleusQueryOperators<TSource> nucleus)
            {
                var (found, row) = await nucleus.FirstCoreAsync(cancellationToken).ConfigureAwait(false);
                return found ? row! : throw new InvalidOperationException("Sequence contains no elements.");
            }
            await foreach (var element in source.AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                return element;
            }
            throw new InvalidOperationException("Sequence contains no elements.");
        }

        public static async Task<TSource> FirstAsync<TSource>(this IQueryable<TSource> source, Expression<Func<TSource, bool>> predicate, CancellationToken cancellationToken = default)
        {
            return await source.Where(predicate).FirstAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>The only row, or the default when there is none; throws when there are several. A Nucleus query reads two rows at most.</summary>
        public static async Task<TSource?> SingleOrDefaultAsync<TSource>(this IQueryable<TSource> source, CancellationToken cancellationToken = default)
        {
            if (source is INucleusQueryOperators<TSource> nucleus)
                return (await nucleus.SingleCoreAsync(cancellationToken).ConfigureAwait(false)).Row;
            TSource? result = default;
            bool found = false;
            await foreach (var element in source.AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (found)
                    throw new InvalidOperationException("Sequence contains more than one element.");
                result = element;
                found = true;
            }
            return result;
        }

        public static async Task<TSource?> SingleOrDefaultAsync<TSource>(this IQueryable<TSource> source, Expression<Func<TSource, bool>> predicate, CancellationToken cancellationToken = default)
        {
            return await source.Where(predicate).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>The only row; throws when there is none or there are several. A Nucleus query reads two rows at most.</summary>
        public static async Task<TSource> SingleAsync<TSource>(this IQueryable<TSource> source, CancellationToken cancellationToken = default)
        {
            if (source is INucleusQueryOperators<TSource> nucleus)
            {
                var single = await nucleus.SingleCoreAsync(cancellationToken).ConfigureAwait(false);
                return single.Found ? single.Row! : throw new InvalidOperationException("Sequence contains no elements.");
            }
            TSource? result = default;
            bool found = false;
            await foreach (var element in source.AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (found)
                    throw new InvalidOperationException("Sequence contains more than one element.");
                result = element;
                found = true;
            }
            if (!found)
                throw new InvalidOperationException("Sequence contains no elements.");
            return result!;
        }

        public static async Task<TSource> SingleAsync<TSource>(this IQueryable<TSource> source, Expression<Func<TSource, bool>> predicate, CancellationToken cancellationToken = default)
        {
            return await source.Where(predicate).SingleAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>True when the query has a row. A Nucleus query reads one ID and runs no child query.</summary>
        public static async Task<bool> AnyAsync<TSource>(this IQueryable<TSource> source, CancellationToken cancellationToken = default)
        {
            if (source is INucleusQueryOperators<TSource> nucleus)
                return await nucleus.AnyCoreAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var _ in source.AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
            return false;
        }

        public static async Task<bool> AnyAsync<TSource>(this IQueryable<TSource> source, Expression<Func<TSource, bool>> predicate, CancellationToken cancellationToken = default)
        {
            return await source.Where(predicate).AnyAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>The number of rows. A Nucleus query counts in the database and runs no child query.</summary>
        public static async Task<int> CountAsync<TSource>(this IQueryable<TSource> source, CancellationToken cancellationToken = default)
        {
            if (source is INucleusQueryOperators<TSource> nucleus)
                return checked((int)await nucleus.CountCoreAsync(cancellationToken).ConfigureAwait(false));
            int count = 0;
            await foreach (var _ in source.AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                checked { count++; }
            }
            return count;
        }

        public static async Task<int> CountAsync<TSource>(this IQueryable<TSource> source, Expression<Func<TSource, bool>> predicate, CancellationToken cancellationToken = default)
        {
            return await source.Where(predicate).CountAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>The number of rows. A Nucleus query counts in the database and runs no child query.</summary>
        public static async Task<long> LongCountAsync<TSource>(this IQueryable<TSource> source, CancellationToken cancellationToken = default)
        {
            if (source is INucleusQueryOperators<TSource> nucleus)
                return await nucleus.CountCoreAsync(cancellationToken).ConfigureAwait(false);
            long count = 0;
            await foreach (var _ in source.AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                checked { count++; }
            }
            return count;
        }

        public static IAsyncEnumerable<TSource> AsAsyncEnumerable<TSource>(this IQueryable<TSource> source)
        {
            if (source is IAsyncEnumerable<TSource> asyncEnumerable)
                return asyncEnumerable;

            throw new InvalidOperationException(
                $"The source IQueryable does not implement IAsyncEnumerable<{typeof(TSource).Name}>. " +
                $"Only queries from IObjectDataAdaptor support async enumeration.");
        }
    }
}
