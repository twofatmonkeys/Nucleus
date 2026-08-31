using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// Extends IQueryProvider with async execution support, enabling true async LINQ operations.
    /// Implementations should stream results where possible rather than materializing entire result sets.
    /// </summary>
    public interface IAsyncQueryProvider : IQueryProvider
    {
        /// <summary>
        /// Asynchronously executes the query represented by the specified expression tree.
        /// For IEnumerable results, implementations should defer enumeration to support streaming.
        /// </summary>
        Task<TResult> ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default);
    }
}
