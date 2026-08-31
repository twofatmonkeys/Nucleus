using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data
{
    public interface IDataConnection
    {
        public IDataContext CreateContext();
        public IDataContext CreateContext(bool useTransaction);
        public IDataContext CreateContext(IDataContext parent);
        public IDataContext CreateContext(IDataContext parent, bool useTransaction);

        public Task<IDataContext> CreateContextAsync();
        public Task<IDataContext> CreateContextAsync(CancellationToken token);
        public Task<IDataContext> CreateContextAsync(bool useTransaction);
        public Task<IDataContext> CreateContextAsync(bool useTransaction, CancellationToken token);
        public Task<IDataContext> CreateContextAsync(IDataContext parent);
        public Task<IDataContext> CreateContextAsync(IDataContext parent, CancellationToken token);
        public Task<IDataContext> CreateContextAsync(IDataContext parent, bool useTransaction);
        public Task<IDataContext> CreateContextAsync(IDataContext parent, bool useTransaction, CancellationToken token);
        public Task<IDataContext> CreateContextAsync(IDataContext parent, bool useTransaction, bool useDependency);
        public Task<IDataContext> CreateContextAsync(IDataContext parent, bool useTransaction, bool useDependency, CancellationToken token);
    }
}
