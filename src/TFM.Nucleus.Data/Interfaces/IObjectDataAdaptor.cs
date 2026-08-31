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

        public TICoreObject? Find(Guid id);
        public Task<TICoreObject?> FindAsync(Guid id);

        public IEnumerable<TICoreObject>? Save();
        public Task<IEnumerable<TICoreObject>?> SaveAsync();
    }
}
