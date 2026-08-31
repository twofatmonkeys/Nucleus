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

        public TICoreObject? Find(Guid id);
        public Task<TICoreObject?> FindAsync(Guid id);

        public IEnumerable<TICoreObject>? Save();
        public Task<IEnumerable<TICoreObject>?> SaveAsync();
    }
}
