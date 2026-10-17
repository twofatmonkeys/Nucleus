using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TFM.Attribution;
using TFM.Nucleus.Data;

namespace TFM.Nucleus.Attribution;

/// <summary>
/// TFM.Attribution's store in Nucleus tables: <see cref="AttributionTouchRecord"/> for touches,
/// <see cref="AttributionSaleRecord"/> and <see cref="AttributionShareRecord"/> for each sale's
/// frozen credit. Nucleus creates and migrates all three on startup like any other entity. A host
/// registers it with <c>services.AddAttribution&lt;NucleusAttributionStore&gt;()</c>.
/// <para>
/// Only the library's background worker calls the store, and it retries an attempt that fails or
/// runs past its timeout. Touch writes are therefore made safe to repeat: the batch skips any touch
/// id already stored, and the unique index on <see cref="AttributionTouchRecord.TouchId"/> turns a
/// race between two attempts into a failure the worker retries, after which the skip applies. A
/// sale is replaced by delete and insert on one transaction, so a reader sees the old record or the
/// new one, never a mixture.
/// </para>
/// <para>
/// Reads run outside a transaction and writes inside one, because a Nucleus transactional context
/// closes its connection after a read. Every member honours its <see cref="CancellationToken"/>.
/// Failures are logged with the operation and rethrown unchanged: the library's worker decides
/// what is worth a retry.
/// </para>
/// </summary>
public class NucleusAttributionStore : IAttributionStore
{
    /// <summary>Session ids per IN list, well under SQL Server's 2,100 parameter limit.</summary>
    internal const int SessionChunkSize = 1000;

    private readonly IObjectDataManager _data;
    private readonly ILogger _logger;

    /// <param name="data">The Nucleus data manager the host registered.</param>
    /// <param name="logger">Receives storage failures. Null logs nothing.</param>
    public NucleusAttributionStore(IObjectDataManager data, ILogger<NucleusAttributionStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public Task AddTouchesAsync(IReadOnlyList<AttributionTouch> touches, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(touches);
        if (touches.Count == 0)
            return Task.CompletedTask;

        return RunAsync("Adding attribution touches", touches.Count, async () =>
        {
            var records = touches
                .Select(AttributionRecordMapper.ToRecord)
                .GroupBy(r => r.TouchId)
                .Select(g => g.First())
                .ToList();

            // A retry of a batch whose first attempt committed: drop what is already there.
            var ids = records.Select(r => r.TouchId).ToList();
            var existing = new HashSet<Guid>();
            foreach (var chunk in ids.Chunk(SessionChunkSize))
            {
                var found = await Query<AttributionTouchRecord>().Where(t => chunk.Contains(t.TouchId)).ToListAsync(cancellationToken).ConfigureAwait(false);
                existing.UnionWith(found.Select(t => t.TouchId));
            }
            var fresh = records.Where(r => !existing.Contains(r.TouchId)).ToList();
            if (fresh.Count == 0)
                return true;

            await using var context = await _data.DataProvider.CreateContextAsync(ContextOptions.Transactional, cancellationToken).ConfigureAwait(false);
            try
            {
                await context.BulkSaveAsync(fresh, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await context.TryRollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
            return true;
        });
    }

    public Task<IReadOnlyList<AttributionTouch>> GetTouchesAsync(IReadOnlyCollection<Guid> sessionIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        var ids = sessionIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return Task.FromResult<IReadOnlyList<AttributionTouch>>([]);

        return RunAsync("Reading attribution touches", ids.Count, async () =>
        {
            var touches = new List<AttributionTouch>();
            foreach (var chunk in ids.Chunk(SessionChunkSize))
            {
                var rows = await Query<AttributionTouchRecord>().Where(t => chunk.Contains(t.SessionId)).ToListAsync(cancellationToken).ConfigureAwait(false);
                touches.AddRange(rows.Select(AttributionRecordMapper.ToTouch));
            }
            return (IReadOnlyList<AttributionTouch>)touches.OrderBy(t => t.Touched).ThenBy(t => t.ID).ToList();
        });
    }

    public Task ReplaceSaleAsync(AttributionSale sale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sale);
        if (sale.SaleID == Guid.Empty)
            throw new ArgumentException("A sale needs a sale id.", nameof(sale));

        return RunAsync("Replacing an attribution sale", 1, async () =>
        {
            var saleId = sale.SaleID;
            var record = AttributionRecordMapper.ToRecord(sale);
            var shares = sale.Shares.Select(s => AttributionRecordMapper.ToRecord(saleId, s)).ToList();

            await using var context = await _data.DataProvider.CreateContextAsync(ContextOptions.Transactional, cancellationToken).ConfigureAwait(false);
            try
            {
                await context.DeleteWhereAsync<AttributionShareRecord>(s => s.SaleId == saleId, cancellationToken).ConfigureAwait(false);
                await context.DeleteWhereAsync<AttributionSaleRecord>(s => s.SaleId == saleId, cancellationToken).ConfigureAwait(false);
                await record.SaveAsync(context, cancellationToken).ConfigureAwait(false);
                if (shares.Count > 0)
                    await context.BulkSaveAsync(shares, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await context.TryRollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
            return true;
        });
    }

    /// <summary>
    /// A frozen sale with its shares in path order, or null. Not part of the library's contract:
    /// the host's reports read it.
    /// </summary>
    public Task<AttributionSale?> GetSaleAsync(Guid saleId, CancellationToken cancellationToken = default) =>
        RunAsync("Reading an attribution sale", 1, async () =>
        {
            var rows = await Query<AttributionSaleRecord>().Where(s => s.SaleId == saleId).ToListAsync(cancellationToken).ConfigureAwait(false);
            var row = rows.FirstOrDefault(s => s.SaleId == saleId);
            if (row is null)
                return null;
            var shares = await Query<AttributionShareRecord>().Where(s => s.SaleId == saleId).ToListAsync(cancellationToken).ConfigureAwait(false);
            return (AttributionSale?)AttributionRecordMapper.ToSale(row, shares);
        });

    /// <summary>
    /// The adaptor as a plain queryable. It also implements IAsyncEnumerable, and on .NET 10 the
    /// BCL's async LINQ would otherwise make every ToListAsync ambiguous.
    /// </summary>
    private IQueryable<T> Query<T>() where T : CoreObject<T>, new() => _data.Get<T>();

    /// <summary>
    /// Runs one operation and logs anything the storage layer throws before rethrowing it
    /// unchanged. Cancellation and argument refusals pass through unlogged.
    /// </summary>
    private async Task<T> RunAsync<T>(string operation, int count, Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or ArgumentException))
        {
            _logger.LogError(exception, "{Operation} failed ({Count} item(s)).", operation, count);
            throw;
        }
    }
}
