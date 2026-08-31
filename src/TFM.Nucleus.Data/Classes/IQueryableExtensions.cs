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
