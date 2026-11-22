using System.Threading;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// The async operators a Nucleus query runs in the database rather than over every matching
    /// row. <c>ObjectDataAdaptorQueryableExtensions</c> has no entity constraint on its element
    /// type, so it reaches the adaptor through this interface, which has none either.
    /// </summary>
    internal interface INucleusQueryOperators<T>
    {
        /// <summary>The first row, read as one row, or the default when there is none.</summary>
        Task<(bool Found, T? Row)> FirstCoreAsync(CancellationToken cancellationToken);

        /// <summary>
        /// The only row, read as at most two rows: the default when there is none, and an
        /// <see cref="System.InvalidOperationException"/> when there are several.
        /// </summary>
        Task<(bool Found, T? Row)> SingleCoreAsync(CancellationToken cancellationToken);

        /// <summary>True when a row matches, read as one ID.</summary>
        Task<bool> AnyCoreAsync(CancellationToken cancellationToken);

        /// <summary>How many rows match, counted in the database.</summary>
        Task<long> CountCoreAsync(CancellationToken cancellationToken);
    }
}
