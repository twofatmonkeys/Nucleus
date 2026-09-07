using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TFM.Bindery;
using TFM.Nucleus.Data;

namespace TFM.Nucleus.Bindery;

/// <summary>
/// Bindery pages, block templates and version snapshots in Nucleus tables, with every optional
/// capability the editor knows: version history, direct lookups by route and page key, and a
/// change token. Both copies of a page live in one <see cref="BinderyDocument"/> row so publishing
/// is one UPDATE, and publish, unpublish and delete run inside a single Nucleus transaction with
/// the change token bump, so a reader never sees a half-published page and a replica never polls
/// a stale token against new content.
/// <para>
/// Register it with <c>services.AddBindery&lt;NucleusBinderyStore&gt;(...)</c> after the Nucleus
/// registrations; Nucleus creates and migrates the four tables on startup like any other entity.
/// Failures below the store surface as <see cref="BinderyException"/> with the original attached,
/// so a consumer is never coupled to the provider in use.
/// </para>
/// </summary>
public sealed class NucleusBinderyStore : IBinderyStore, IBinderyVersionStore, IBinderyPageLookup, IBinderyChangeTokenSource
{
    private readonly IObjectDataManager _data;
    private readonly ILogger<NucleusBinderyStore> _logger;

    public NucleusBinderyStore(IObjectDataManager data, ILogger<NucleusBinderyStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        _logger = logger ?? NullLogger<NucleusBinderyStore>.Instance;
    }

    // ---- pages ------------------------------------------------------------------------------

    public Task<BinderyPage?> GetPageAsync(string pageId, PageVersion version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        return RunAsync($"Reading the {version} page", pageId, async () =>
        {
            var row = await FindDocumentAsync(pageId, cancellationToken).ConfigureAwait(false);
            var json = version == PageVersion.Draft ? row?.DraftJson : row?.PublishedJson;
            return json is null ? null : BinderyJsonSerializer.DeserializePage(json);
        });
    }

    public Task<IReadOnlyList<BinderyPageInfo>> ListPagesAsync(CancellationToken cancellationToken = default) =>
        RunAsync("Listing pages", "*", async () =>
        {
            var rows = await Query<BinderyDocument>().ToListAsync(cancellationToken).ConfigureAwait(false);
            var infos = new List<BinderyPageInfo>(rows.Count);
            foreach (var row in rows)
            {
                if (InfoOf(row) is { } info)
                    infos.Add(info);
            }
            return (IReadOnlyList<BinderyPageInfo>)infos;
        });

    public Task SaveDraftAsync(BinderyPage page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(page.Id);
        return RunAsync("Saving the draft", page.Id, async () =>
        {
            var row = await FindDocumentAsync(page.Id, cancellationToken).ConfigureAwait(false) ?? new BinderyDocument { PageId = page.Id };
            row.DraftJson = BinderyJsonSerializer.Serialize(page, indented: false);
            row.DraftModifiedUtc = page.ModifiedUtc ?? DateTimeOffset.UtcNow;
            row.ModifiedBy = page.ModifiedBy;
            ApplyListing(row, page);
            await _data.SaveAsync(row).ConfigureAwait(false);
            return true;
        });
    }

    public Task PublishAsync(string pageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        return RunAsync("Publishing the page", pageId, async () =>
        {
            var row = await FindDocumentAsync(pageId, cancellationToken).ConfigureAwait(false);
            if (row?.DraftJson is null)
                return false;

            var draft = BinderyJsonSerializer.DeserializePage(row.DraftJson);
            var now = DateTimeOffset.UtcNow;
            var snapshot = new BinderyPageVersion
            {
                PageId = pageId,
                Json = row.DraftJson,
                CreatedUtc = now,
                CreatedBy = draft?.ModifiedBy,
                Kind = PageVersionKind.Published,
            };
            snapshot.VersionId = snapshot.ID.ToString("N");

            row.PublishedJson = row.DraftJson;
            row.PublishedUtc = now;
            if (draft is not null)
                ApplyListing(row, draft);

            var token = await LoadOrCreateTokenAsync(cancellationToken).ConfigureAwait(false);

            // Snapshot, publish and token in one transaction: an orphan snapshot after a failed
            // publish would be harmless, but a published page with no version to roll back to,
            // or a token that moved before the content did, is not.
            await using var context = await _data.DataProvider.CreateContextAsync(ContextOptions.Transactional, cancellationToken).ConfigureAwait(false);
            try
            {
                await snapshot.SaveAsync(context, cancellationToken).ConfigureAwait(false);
                await row.SaveAsync(context, cancellationToken).ConfigureAwait(false);
                await token.SaveAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await context.TryRollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
            return true;
        });
    }

    public Task UnpublishAsync(string pageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        return RunAsync("Unpublishing the page", pageId, async () =>
        {
            var row = await FindDocumentAsync(pageId, cancellationToken).ConfigureAwait(false);
            if (row?.PublishedJson is null)
                return false;
            row.PublishedJson = null;
            row.PublishedUtc = null;
            var token = await LoadOrCreateTokenAsync(cancellationToken).ConfigureAwait(false);

            await using var context = await _data.DataProvider.CreateContextAsync(ContextOptions.Transactional, cancellationToken).ConfigureAwait(false);
            try
            {
                if (row.DraftJson is null)
                    await context.DeleteAsync(row, cancellationToken).ConfigureAwait(false);
                else
                    await row.SaveAsync(context, cancellationToken).ConfigureAwait(false);
                await token.SaveAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await context.TryRollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
            return true;
        });
    }

    public Task DiscardDraftAsync(string pageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        return RunAsync("Discarding the draft", pageId, async () =>
        {
            var row = await FindDocumentAsync(pageId, cancellationToken).ConfigureAwait(false);
            if (row?.DraftJson is null)
                return false;
            if (row.PublishedJson is null)
            {
                // A draft discarded before it was ever published leaves nothing behind.
                await DeleteAsync(row, cancellationToken).ConfigureAwait(false);
                return true;
            }
            row.DraftJson = null;
            row.DraftModifiedUtc = null;
            await _data.SaveAsync(row).ConfigureAwait(false);
            return true;
        });
    }

    public Task DeletePageAsync(string pageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        return RunAsync("Deleting the page", pageId, async () =>
        {
            var versions = await VersionsOf(pageId).ToListAsync(cancellationToken).ConfigureAwait(false);
            var row = await FindDocumentAsync(pageId, cancellationToken).ConfigureAwait(false);
            if (row is null && versions.Count == 0)
                return false;
            var token = await LoadOrCreateTokenAsync(cancellationToken).ConfigureAwait(false);

            await using var context = await _data.DataProvider.CreateContextAsync(ContextOptions.Transactional, cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var version in versions)
                    await context.DeleteAsync(version, cancellationToken).ConfigureAwait(false);
                if (row is not null)
                    await context.DeleteAsync(row, cancellationToken).ConfigureAwait(false);
                await token.SaveAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await context.TryRollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
            return true;
        });
    }

    // ---- lookups ----------------------------------------------------------------------------

    public Task<BinderyPageInfo?> FindByPageKeyAsync(string pageKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageKey);
        return RunAsync("Finding the page by key", pageKey, async () =>
        {
            // The index narrows the rows; the ordinal comparison decides, because a SQL collation
            // may fold case and Bindery keys are exact.
            var rows = await Query<BinderyDocument>().Where(d => d.PageKey == pageKey).ToListAsync(cancellationToken).ConfigureAwait(false);
            return rows.Where(d => string.Equals(d.PageKey, pageKey, StringComparison.Ordinal)).Select(InfoOf).FirstOrDefault(i => i is not null);
        });
    }

    public Task<BinderyPageInfo?> FindByRouteAsync(string route, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        return RunAsync("Finding the page by route", route, async () =>
        {
            var rows = await Query<BinderyDocument>().Where(d => d.Route == route).ToListAsync(cancellationToken).ConfigureAwait(false);
            return rows.Where(d => string.Equals(d.Route, route, StringComparison.Ordinal)).Select(InfoOf).FirstOrDefault(i => i is not null);
        });
    }

    // ---- change token -----------------------------------------------------------------------

    public Task<string?> GetChangeTokenAsync(CancellationToken cancellationToken = default) =>
        RunAsync("Reading the change token", "*", async () =>
        {
            var row = await Query<BinderyChangeToken>().FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return row?.Token;
        });

    // ---- block templates --------------------------------------------------------------------

    public Task<IReadOnlyList<BlockTemplate>> ListBlockTemplatesAsync(CancellationToken cancellationToken = default) =>
        RunAsync("Listing block templates", "*", async () =>
        {
            var rows = await Query<BinderyBlockTemplate>().ToListAsync(cancellationToken).ConfigureAwait(false);
            var templates = new List<BlockTemplate>(rows.Count);
            foreach (var row in rows)
            {
                if (row.Json is null)
                    continue;
                var template = BinderyJsonSerializer.DeserializeTemplate(row.Json);
                if (template is not null)
                    templates.Add(template);
            }
            return (IReadOnlyList<BlockTemplate>)templates;
        });

    public Task<BlockTemplate?> GetBlockTemplateAsync(string templateId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        return RunAsync("Reading the block template", templateId, async () =>
        {
            var row = await FindTemplateAsync(templateId, cancellationToken).ConfigureAwait(false);
            return row?.Json is null ? null : BinderyJsonSerializer.DeserializeTemplate(row.Json);
        });
    }

    public Task SaveBlockTemplateAsync(BlockTemplate template, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(template.Id);
        return RunAsync("Saving the block template", template.Id, async () =>
        {
            var row = await FindTemplateAsync(template.Id, cancellationToken).ConfigureAwait(false)
                ?? new BinderyBlockTemplate { TemplateId = template.Id };
            row.Json = BinderyJsonSerializer.Serialize(template, indented: false);
            row.ModifiedUtc = DateTimeOffset.UtcNow;
            await _data.SaveAsync(row).ConfigureAwait(false);
            return true;
        });
    }

    public Task DeleteBlockTemplateAsync(string templateId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        return RunAsync("Deleting the block template", templateId, async () =>
        {
            var row = await FindTemplateAsync(templateId, cancellationToken).ConfigureAwait(false);
            if (row is not null)
                await DeleteAsync(row, cancellationToken).ConfigureAwait(false);
            return true;
        });
    }

    // ---- versions ---------------------------------------------------------------------------

    public Task<IReadOnlyList<BinderyPageVersionInfo>> ListVersionsAsync(string pageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        return RunAsync("Listing versions", pageId, async () =>
        {
            var rows = await VersionsOf(pageId).ToListAsync(cancellationToken).ConfigureAwait(false);
            return (IReadOnlyList<BinderyPageVersionInfo>)NewestFirst(rows)
                .Select(v => new BinderyPageVersionInfo(v.VersionId!, v.CreatedUtc, v.CreatedBy, v.Kind))
                .ToList();
        });
    }

    public Task<BinderyPage?> GetVersionAsync(string pageId, string versionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        return RunAsync("Reading the version", $"{pageId}/{versionId}", async () =>
        {
            var row = await FindVersionAsync(pageId, versionId, cancellationToken).ConfigureAwait(false);
            return row?.Json is null ? null : BinderyJsonSerializer.DeserializePage(row.Json);
        });
    }

    public Task RestoreVersionAsync(string pageId, string versionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        return RunAsync("Restoring the version", $"{pageId}/{versionId}", async () =>
        {
            var version = await FindVersionAsync(pageId, versionId, cancellationToken).ConfigureAwait(false);
            if (version?.Json is null)
                return false;
            var row = await FindDocumentAsync(pageId, cancellationToken).ConfigureAwait(false);
            if (row is null)
                return false;

            // Into the draft only. Publishing the restored copy is a separate, explicit step.
            row.DraftJson = version.Json;
            row.DraftModifiedUtc = DateTimeOffset.UtcNow;
            var page = BinderyJsonSerializer.DeserializePage(version.Json);
            if (page is not null)
                ApplyListing(row, page);
            await _data.SaveAsync(row).ConfigureAwait(false);
            return true;
        });
    }

    public Task PruneVersionsAsync(string pageId, int keep, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        ArgumentOutOfRangeException.ThrowIfNegative(keep);
        return RunAsync("Pruning versions", pageId, async () =>
        {
            var rows = await VersionsOf(pageId).ToListAsync(cancellationToken).ConfigureAwait(false);
            var surplus = NewestFirst(rows).Skip(keep).ToList();
            if (surplus.Count == 0)
                return false;

            await using var context = await _data.DataProvider.CreateContextAsync(ContextOptions.Transactional, cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var version in surplus)
                    await context.DeleteAsync(version, cancellationToken).ConfigureAwait(false);
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
    /// Every write goes through the injected manager or an explicit context, never the static
    /// entity API, which needs the ambient runtime that only <c>AddNucleusData</c> publishes. The
    /// manager has no delete, so a lone delete gets a transaction of its own.
    /// </summary>
    private async Task DeleteAsync(ICoreObject row, CancellationToken cancellationToken)
    {
        await using var context = await _data.DataProvider.CreateContextAsync(ContextOptions.Transactional, cancellationToken).ConfigureAwait(false);
        try
        {
            await context.DeleteAsync(row, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await context.TryRollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The adaptor as a plain queryable. It also implements IAsyncEnumerable, and on .NET 10 the
    /// BCL's async LINQ would otherwise make every ToListAsync and FirstOrDefaultAsync ambiguous.
    /// </summary>
    private IQueryable<T> Query<T>() where T : CoreObject<T>, new() => _data.Get<T>();

    private async Task<BinderyDocument?> FindDocumentAsync(string pageId, CancellationToken cancellationToken)
    {
        var rows = await Query<BinderyDocument>().Where(d => d.PageId == pageId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.FirstOrDefault(d => string.Equals(d.PageId, pageId, StringComparison.Ordinal));
    }

    private async Task<BinderyBlockTemplate?> FindTemplateAsync(string templateId, CancellationToken cancellationToken)
    {
        var rows = await Query<BinderyBlockTemplate>().Where(t => t.TemplateId == templateId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.FirstOrDefault(t => string.Equals(t.TemplateId, templateId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<BinderyPageVersion?> FindVersionAsync(string pageId, string versionId, CancellationToken cancellationToken)
    {
        var rows = await Query<BinderyPageVersion>().Where(v => v.PageId == pageId && v.VersionId == versionId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.FirstOrDefault(v =>
            string.Equals(v.PageId, pageId, StringComparison.Ordinal) && string.Equals(v.VersionId, versionId, StringComparison.Ordinal));
    }

    private IQueryable<BinderyPageVersion> VersionsOf(string pageId) =>
        Query<BinderyPageVersion>().Where(v => v.PageId == pageId);

    private static IEnumerable<BinderyPageVersion> NewestFirst(IEnumerable<BinderyPageVersion> rows) =>
        rows.Where(v => v.VersionId is not null)
            .OrderByDescending(v => v.CreatedUtc)
            .ThenByDescending(v => v.ID);

    private async Task<BinderyChangeToken> LoadOrCreateTokenAsync(CancellationToken cancellationToken)
    {
        var row = await Query<BinderyChangeToken>().FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? new BinderyChangeToken();
        row.Token = Guid.NewGuid().ToString("N");
        return row;
    }

    /// <summary>The listing projection, shared by the listing and the lookups; null for a row with neither copy.</summary>
    private static BinderyPageInfo? InfoOf(BinderyDocument row)
    {
        var hasDraft = row.DraftJson is not null;
        var hasPublished = row.PublishedJson is not null;
        if (!hasDraft && !hasPublished || row.PageId is null)
            return null;
        return new BinderyPageInfo(
            row.PageId,
            row.Route,
            row.PageKey,
            row.Title ?? "",
            HasDraft: hasDraft,
            HasPublished: hasPublished,
            row.IncludeInSitemap,
            PublishedUtc: row.PublishedUtc,
            ModifiedUtc: row.DraftModifiedUtc ?? row.PublishedUtc);
    }

    /// <summary>The listing columns, denormalised so the listing and the lookups never deserialise a document.</summary>
    private static void ApplyListing(BinderyDocument row, BinderyPage page)
    {
        row.Route = page.Route;
        row.PageKey = page.PageKey;
        row.Title = page.Title;
        row.IncludeInSitemap = page.Metadata.IncludeInSitemap;
    }

    /// <summary>
    /// Runs one operation and turns anything the storage layer throws into a
    /// <see cref="BinderyException"/> carrying the original, per the contract's remarks. Argument
    /// refusals, cancellation and Bindery's own exceptions pass through untouched.
    /// </summary>
    private async Task<T> RunAsync<T>(string operation, string key, Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or ArgumentException or BinderyException))
        {
            _logger.LogError(exception, "{Operation} failed for '{Key}'.", operation, key);
            throw new BinderyException($"{operation} failed for '{key}'. See the inner exception.", exception);
        }
    }
}
