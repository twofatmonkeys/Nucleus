using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data
{
    public interface IObjectDataAdaptor<TICoreObject>  : IEnumerable<TICoreObject>,
        IAsyncEnumerable<TICoreObject>, IQueryable, IQueryable<TICoreObject>,
        IAsyncQueryProvider, IOrderedQueryable<TICoreObject>, IDisposable, IAsyncDisposable where TICoreObject : ICoreObject<TICoreObject>, new()
    {
        public TICoreObject Add(TICoreObject obj); 
        public IEnumerable<TICoreObject> AddRange(IEnumerable<TICoreObject> objs);
        public TICoreObject[] AddRange(params TICoreObject[] objs);

        /// <summary>
        /// Filters the set. Declared on the interface rather than relying on Queryable.Where
        /// because this type is both IQueryable&lt;T&gt; and IAsyncEnumerable&lt;T&gt;: from .NET 10
        /// the BCL adds System.Linq.AsyncEnumerable, and an unqualified .Where(...) would be
        /// ambiguous between the two extension methods. An instance method beats both.
        /// </summary>
        public IQueryable<TICoreObject> Where(System.Linq.Expressions.Expression<Func<TICoreObject, bool>> predicate);

        // The async operators, declared here for the reason Where is: from .NET 10 each one called
        // straight on Get<T>() matched both ObjectDataAdaptorQueryableExtensions and
        // System.Linq.AsyncEnumerable and did not compile (CS0121). An instance member beats both.
        // Each runs the extension, so the behaviour is the one a query after Where already has.

        /// <summary>Runs the query and returns its rows, on the asynchronous path.</summary>
        public Task<List<TICoreObject>> ToListAsync(CancellationToken cancellationToken = default) =>
            ObjectDataAdaptorQueryableExtensions.ToListAsync(this, cancellationToken);

        /// <summary>Runs the query and returns its rows, on the asynchronous path.</summary>
        public Task<TICoreObject[]> ToArrayAsync(CancellationToken cancellationToken = default) =>
            ObjectDataAdaptorQueryableExtensions.ToArrayAsync(this, cancellationToken);

        /// <summary>The first row, or null when there is none.</summary>
        public Task<TICoreObject?> FirstOrDefaultAsync(CancellationToken cancellationToken = default) =>
            ObjectDataAdaptorQueryableExtensions.FirstOrDefaultAsync(this, cancellationToken);

        /// <summary>The first row matching the predicate, or null when there is none.</summary>
        public Task<TICoreObject?> FirstOrDefaultAsync(System.Linq.Expressions.Expression<Func<TICoreObject, bool>> predicate, CancellationToken cancellationToken = default) =>
            ObjectDataAdaptorQueryableExtensions.FirstOrDefaultAsync(this, predicate, cancellationToken);

        /// <summary>The first row; throws when there is none.</summary>
        public Task<TICoreObject> FirstAsync(CancellationToken cancellationToken = default) =>
            ObjectDataAdaptorQueryableExtensions.FirstAsync(this, cancellationToken);

        /// <summary>The first row matching the predicate; throws when there is none.</summary>
        public Task<TICoreObject> FirstAsync(System.Linq.Expressions.Expression<Func<TICoreObject, bool>> predicate, CancellationToken cancellationToken = default) =>
            ObjectDataAdaptorQueryableExtensions.FirstAsync(this, predicate, cancellationToken);

        /// <summary>The only row, or null when there is none; throws when there are several.</summary>
        public Task<TICoreObject?> SingleOrDefaultAsync(CancellationToken cancellationToken = default) =>
            ObjectDataAdaptorQueryableExtensions.SingleOrDefaultAsync(this, cancellationToken);

        /// <summary>The only row matching the predicate, or null when there is none; throws when there are several.</summary>
        public Task<TICoreObject?> SingleOrDefaultAsync(System.Linq.Expressions.Expression<Func<TICoreObject, bool>> predicate, CancellationToken cancellationToken = default) =>
            ObjectDataAdaptorQueryableExtensions.SingleOrDefaultAsync(this, predicate, cancellationToken);

        /// <summary>The only row; throws when there is none or there are several.</summary>
        public Task<TICoreObject> SingleAsync(CancellationToken cancellationToken = default) =>
            ObjectDataAdaptorQueryableExtensions.SingleAsync(this, cancellationToken);

        /// <summary>The only row matching the predicate; throws when there is none or there are several.</summary>
        public Task<TICoreObject> SingleAsync(System.Linq.Expressions.Expression<Func<TICoreObject, bool>> predicate, CancellationToken cancellationToken = default) =>
            ObjectDataAdaptorQueryableExtensions.SingleAsync(this, predicate, cancellationToken);

        /// <summary>True when the query has a row.</summary>
        public Task<bool> AnyAsync(CancellationToken cancellationToken = default) =>
            ObjectDataAdaptorQueryableExtensions.AnyAsync(this, cancellationToken);

        /// <summary>True when a row matches the predicate.</summary>
        public Task<bool> AnyAsync(System.Linq.Expressions.Expression<Func<TICoreObject, bool>> predicate, CancellationToken cancellationToken = default) =>
            ObjectDataAdaptorQueryableExtensions.AnyAsync(this, predicate, cancellationToken);

        /// <summary>The number of rows the query returns.</summary>
        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            ObjectDataAdaptorQueryableExtensions.CountAsync(this, cancellationToken);

        /// <summary>The number of rows matching the predicate.</summary>
        public Task<int> CountAsync(System.Linq.Expressions.Expression<Func<TICoreObject, bool>> predicate, CancellationToken cancellationToken = default) =>
            ObjectDataAdaptorQueryableExtensions.CountAsync(this, predicate, cancellationToken);

        /// <summary>The number of rows the query returns.</summary>
        public Task<long> LongCountAsync(CancellationToken cancellationToken = default) =>
            ObjectDataAdaptorQueryableExtensions.LongCountAsync(this, cancellationToken);

        /// <summary>
        /// The same query with a child collection left out, as in <c>.Exclude(b =&gt; b.Files)</c>:
        /// its rows are neither queried nor transferred, and the collection comes back unloaded.
        /// </summary>
        public IQueryable<TICoreObject> Exclude(System.Linq.Expressions.Expression<Func<TICoreObject, TFM.Nucleus.Abstractions.Interfaces.INucleusChildCollection>> collection) =>
            throw new NotSupportedException($"{GetType().Name} cannot exclude child collections.");

        /// <summary>
        /// The same query with a lazy ([RetrieveOnDemand]) child collection loaded with its rows,
        /// as in <c>.Include(m =&gt; m.Naps)</c>.
        /// </summary>
        public IQueryable<TICoreObject> Include(System.Linq.Expressions.Expression<Func<TICoreObject, TFM.Nucleus.Abstractions.Interfaces.INucleusChildCollection>> collection) =>
            throw new NotSupportedException($"{GetType().Name} cannot include child collections.");

        /// <summary>
        /// The same query with the entities a <c>[LinkedObject]</c> property refers to loaded with
        /// its rows, as in <c>.Include(m =&gt; m.Keeper)</c>.
        /// </summary>
        public IQueryable<TICoreObject> Include(System.Linq.Expressions.Expression<Func<TICoreObject, ICoreObject?>> reference) =>
            throw new NotSupportedException($"{GetType().Name} cannot include linked objects.");

        /// <summary>
        /// The same query with a linked collection's members loaded with its rows, as in
        /// <c>.Include(t =&gt; t.Monkeys)</c>.
        /// </summary>
        public IQueryable<TICoreObject> Include(System.Linq.Expressions.Expression<Func<TICoreObject, TFM.Nucleus.Abstractions.Interfaces.INucleusLinkedCollection>> collection) =>
            throw new NotSupportedException($"{GetType().Name} cannot include linked collections.");

        /// <summary>
        /// The same query also returning the rows of a soft-deletable type that are marked
        /// deleted, and those of its child collections.
        /// </summary>
        public IQueryable<TICoreObject> IncludeDeleted() =>
            throw new NotSupportedException($"{GetType().Name} cannot include deleted rows.");

        public TICoreObject? Find(Guid id);
        public Task<TICoreObject?> FindAsync(Guid id);

        /// <summary>
        /// Loads by primary key on the asynchronous path, with this query's excluded and included
        /// child collections. Null when no row has the ID.
        /// </summary>
        public Task<TICoreObject?> FindAsync(Guid id, CancellationToken token) =>
            throw new NotSupportedException($"{GetType().Name} does not support a cancellable find.");

        public IEnumerable<TICoreObject>? Save();
        public Task<IEnumerable<TICoreObject>?> SaveAsync();

        /// <summary>
        /// Saves every object added with <see cref="Add"/> or <c>AddRange</c> in one transaction,
        /// as the manager's batch save does, then clears them. A failure keeps them for a retry.
        /// </summary>
        public Task<IEnumerable<TICoreObject>?> SaveAsync(CancellationToken token) =>
            throw new NotSupportedException($"{GetType().Name} does not support a cancellable save.");
    }
}
