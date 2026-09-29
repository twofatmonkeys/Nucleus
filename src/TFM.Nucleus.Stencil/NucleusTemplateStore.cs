using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TFM.Nucleus.Data;
using TFM.Stencil.Abstractions;

namespace TFM.Nucleus.Stencil;

/// <summary>
/// TFM.Stencil's template store in Nucleus tables: <see cref="StencilTemplateRecord"/> for each
/// template's metadata and file manifest, <see cref="StencilTemplateFile"/> for the bytes of each
/// file. Nucleus creates and migrates both tables on startup like any other entity.
/// <para>
/// Abstract on purpose. The store is partitioned by a collection name so one database can hold the
/// bundles of several consumers (TFM.Mailroom and TFM.Paperwork, say) without their keys colliding
/// or one manager listing the other's templates, and each consumer registers its store by its
/// concrete type. A host therefore writes one sealed subclass per consumer, each passing its own
/// collection:
/// <code>
/// public sealed class MailroomTemplates(IObjectDataManager data, ILogger&lt;MailroomTemplates&gt;? logger = null)
///     : NucleusTemplateStore(data, "mailroom", logger);
/// </code>
/// </para>
/// <para>
/// Reads run outside a transaction and writes inside one, because a Nucleus transactional context
/// closes its connection after a read. Listings and counts read record rows only, never content;
/// the name filter, ordering and paging run in memory over the collection's rows, because the
/// provider translates none of them. Deletes run by predicate, so no bytes are loaded to remove
/// them. Keys and paths match case-insensitively, as in the reference store, because both composite
/// keys are stored lowercased.
/// </para>
/// <para>
/// Content rows are tied to their record by key, not by a foreign key. Content the manifest does
/// not list (the library writes content before metadata, so a failure between the two leaves some)
/// never appears in a record or a listing, is still served, and goes when the template is deleted.
/// A content row whose record is gone, which a content save racing a delete can leave behind, is
/// invisible until the key is saved again, is overwritten by the next save of the same path, and
/// goes with the next delete of the key.
/// </para>
/// <para>
/// A storage failure is logged with the operation and key and rethrown unchanged:
/// TFM.Stencil.Abstractions defines no exception of its own to wrap it in. Nothing is retried,
/// because a write whose commit outcome is unknown cannot be safely replayed.
/// </para>
/// </summary>
public abstract class NucleusTemplateStore : ITemplateStore
{
    private readonly IObjectDataManager _data;
    private readonly ILogger _logger;

    /// <param name="data">The Nucleus data manager the host registered.</param>
    /// <param name="collection">
    /// This consumer's partition, for example "mailroom". Compared case-insensitively and stored
    /// lowercased. It cannot contain ':' or '|', which separate the parts of the composite keys.
    /// </param>
    /// <param name="logger">Receives storage failures. Null logs nothing.</param>
    protected NucleusTemplateStore(IObjectDataManager data, string collection, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        if (collection.IndexOfAny([':', '|']) >= 0)
            throw new ArgumentException("A collection name cannot contain ':' or '|'; they separate the parts of the store's keys.", nameof(collection));

        _data = data;
        Collection = collection.Trim().ToLowerInvariant();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>This store's partition, lowercased.</summary>
    protected string Collection { get; }

    // ---- reads ------------------------------------------------------------------------------

    public Task<Template?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return RunAsync("Reading the template", key, async () =>
        {
            var row = await FindRecordAsync(RecordKeyOf(key), cancellationToken).ConfigureAwait(false);
            return row is null ? null : StencilTemplateMapper.ToTemplate(row);
        });
    }

    public Task<IReadOnlyList<Template>> QueryAsync(TemplateQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return RunAsync("Querying templates", "*", async () =>
        {
            var rows = Sort(Filter(await LoadCollectionAsync(cancellationToken).ConfigureAwait(false), query), query)
                .Skip(Math.Max(0, query.Skip))
                .Take(query.Take);
            return (IReadOnlyList<Template>)rows.Select(StencilTemplateMapper.ToTemplate).ToList();
        });
    }

    public Task<int> CountAsync(TemplateQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return RunAsync("Counting templates", "*", async () =>
            Filter(await LoadCollectionAsync(cancellationToken).ConfigureAwait(false), query).Count());
    }

    public Task<byte[]?> GetFileContentAsync(string key, string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return RunAsync("Reading the file content", $"{key}/{path}", async () =>
        {
            var fileKey = StencilTemplateMapper.FileKeyFor(RecordKeyOf(key), path);
            var rows = await Query<StencilTemplateFile>().Where(f => f.FileKey == fileKey).ToListAsync(cancellationToken).ConfigureAwait(false);
            var row = rows.FirstOrDefault(f => string.Equals(f.FileKey, fileKey, StringComparison.Ordinal));
            // A zero-length file is still a file; the provider may hand an empty value back as null.
            return row is null ? null : row.Content ?? [];
        });
    }

    // ---- writes -----------------------------------------------------------------------------

    public Task SaveAsync(Template template, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(template.Key);
        return RunAsync("Saving the template", template.Key, async () =>
        {
            var recordKey = RecordKeyOf(template.Key);
            var row = await FindRecordAsync(recordKey, cancellationToken).ConfigureAwait(false) ?? new StencilTemplateRecord();
            StencilTemplateMapper.Apply(row, template, Collection, recordKey);

            await using var context = await _data.DataProvider.CreateContextAsync(ContextOptions.Transactional, cancellationToken).ConfigureAwait(false);
            try
            {
                await row.SaveAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await context.TryRollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
            return true;
        });
    }

    public async Task SaveFileContentAsync(string key, string path, byte[] content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var recordKey = RecordKeyOf(key);
        var exists = await RunAsync("Checking the template", key, async () =>
            await FindRecordAsync(recordKey, cancellationToken).ConfigureAwait(false) is not null).ConfigureAwait(false);

        // The contract's refusal, outside the logging wrapper: it is the caller's ordering error,
        // not a storage failure.
        if (!exists)
            throw new InvalidOperationException(
                $"No template with the key '{key}' in the '{Collection}' collection. Save the template record before its file content.");

        await RunAsync("Saving the file content", $"{key}/{path}", async () =>
        {
            // Replace by delete and insert on one transaction rather than update in place: finding
            // the existing row to update would load its bytes only to overwrite them.
            var fileKey = StencilTemplateMapper.FileKeyFor(recordKey, path);
            var row = new StencilTemplateFile { RecordKey = recordKey, Path = path, FileKey = fileKey, Content = content.ToArray() };

            await using var context = await _data.DataProvider.CreateContextAsync(ContextOptions.Transactional, cancellationToken).ConfigureAwait(false);
            try
            {
                await context.DeleteWhereAsync<StencilTemplateFile>(f => f.FileKey == fileKey, cancellationToken).ConfigureAwait(false);
                await row.SaveAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await context.TryRollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
            return true;
        }).ConfigureAwait(false);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return RunAsync("Deleting the template", key, async () =>
        {
            // Nothing is read first: both deletes run by predicate, so no bytes are loaded, and a
            // delete that matches nothing is a no-op, which is what makes this idempotent. The
            // files go by record key rather than through the record, so a row left behind by a
            // content save that raced an earlier delete goes too.
            var recordKey = RecordKeyOf(key);
            await using var context = await _data.DataProvider.CreateContextAsync(ContextOptions.Transactional, cancellationToken).ConfigureAwait(false);
            try
            {
                await context.DeleteWhereAsync<StencilTemplateFile>(f => f.RecordKey == recordKey, cancellationToken).ConfigureAwait(false);
                await context.DeleteWhereAsync<StencilTemplateRecord>(r => r.RecordKey == recordKey, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await context.TryRollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
            return true;
        });
    }

    public Task DeleteFileContentAsync(string key, string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return RunAsync("Deleting the file content", $"{key}/{path}", async () =>
        {
            var fileKey = StencilTemplateMapper.FileKeyFor(RecordKeyOf(key), path);
            await using var context = await _data.DataProvider.CreateContextAsync(ContextOptions.Transactional, cancellationToken).ConfigureAwait(false);
            try
            {
                await context.DeleteWhereAsync<StencilTemplateFile>(f => f.FileKey == fileKey, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await context.TryRollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
            return true;
        });
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>
    /// The adaptor as a plain queryable. It also implements IAsyncEnumerable, and on .NET 10 the
    /// BCL's async LINQ would otherwise make every ToListAsync ambiguous.
    /// </summary>
    private IQueryable<T> Query<T>() where T : CoreObject<T>, new() => _data.Get<T>();

    private string RecordKeyOf(string key) => StencilTemplateMapper.RecordKeyFor(Collection, key);

    private async Task<StencilTemplateRecord?> FindRecordAsync(string recordKey, CancellationToken ct)
    {
        var rows = await Query<StencilTemplateRecord>().Where(r => r.RecordKey == recordKey).ToListAsync(ct).ConfigureAwait(false);
        return rows.FirstOrDefault(r => string.Equals(r.RecordKey, recordKey, StringComparison.Ordinal));
    }

    /// <summary>Every record in this collection. Record rows only: file content is never read here.</summary>
    private async Task<List<StencilTemplateRecord>> LoadCollectionAsync(CancellationToken ct)
    {
        var collection = Collection;
        var rows = await Query<StencilTemplateRecord>().Where(r => r.Collection == collection).ToListAsync(ct).ConfigureAwait(false);
        return rows.Where(r => string.Equals(r.Collection, collection, StringComparison.Ordinal)).ToList();
    }

    private static IEnumerable<StencilTemplateRecord> Filter(IEnumerable<StencilTemplateRecord> rows, TemplateQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.NameContains))
            return rows;
        var term = query.NameContains;
        return rows.Where(r =>
            (r.Key ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase) ||
            (r.Name ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The requested order, with the key as the tie-break so paging is stable.</summary>
    private static IEnumerable<StencilTemplateRecord> Sort(IEnumerable<StencilTemplateRecord> rows, TemplateQuery query)
    {
        var ordered = query.SortBy switch
        {
            TemplateSortBy.Key => query.Descending
                ? rows.OrderByDescending(r => r.Key, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase),
            TemplateSortBy.CreatedUtc => query.Descending
                ? rows.OrderByDescending(r => r.CreatedUtc)
                : rows.OrderBy(r => r.CreatedUtc),
            TemplateSortBy.UpdatedUtc => query.Descending
                ? rows.OrderByDescending(r => r.UpdatedUtc)
                : rows.OrderBy(r => r.UpdatedUtc),
            _ => query.Descending
                ? rows.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        };
        return query.Descending
            ? ordered.ThenByDescending(r => r.RecordKey, StringComparer.Ordinal)
            : ordered.ThenBy(r => r.RecordKey, StringComparer.Ordinal);
    }

    /// <summary>
    /// Runs one operation and logs anything the storage layer throws, with the operation and key,
    /// before rethrowing it unchanged. Argument refusals and cancellation pass through unlogged.
    /// </summary>
    private async Task<T> RunAsync<T>(string operation, string key, Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or ArgumentException))
        {
            _logger.LogError(exception, "{Operation} failed for '{Key}' in the '{Collection}' template collection.", operation, key, Collection);
            throw;
        }
    }
}
