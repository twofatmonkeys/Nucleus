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

        public static async Task<TSource?> FirstOrDefaultAsync<TSource>(this IQueryable<TSource> source, CancellationToken cancellationToken = default)
        {
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

        public static async Task<TSource> FirstAsync<TSource>(this IQueryable<TSource> source, CancellationToken cancellationToken = default)
        {
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

        public static async Task<TSource?> SingleOrDefaultAsync<TSource>(this IQueryable<TSource> source, CancellationToken cancellationToken = default)
        {
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

        public static async Task<TSource> SingleAsync<TSource>(this IQueryable<TSource> source, CancellationToken cancellationToken = default)
        {
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

        public static async Task<bool> AnyAsync<TSource>(this IQueryable<TSource> source, CancellationToken cancellationToken = default)
        {
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

        public static async Task<int> CountAsync<TSource>(this IQueryable<TSource> source, CancellationToken cancellationToken = default)
        {
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

        public static async Task<long> LongCountAsync<TSource>(this IQueryable<TSource> source, CancellationToken cancellationToken = default)
        {
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
