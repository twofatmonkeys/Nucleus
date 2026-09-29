using System.Linq.Expressions;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TFM.Nucleus.Data;
using TFM.S3Library.Abstractions;

namespace TFM.Nucleus.S3Library;

/// <summary>
/// TFM.S3Library's asset metadata in Nucleus tables: <see cref="S3AssetRecord"/> for the record,
/// <see cref="S3AssetPreviousPath"/> for the indexed previous-path lookup and
/// <see cref="S3AssetVersionClaim"/> as the version gate. Register it with
/// <c>services.AddS3Library&lt;NucleusS3AssetStore&gt;(...)</c> after the Nucleus registrations;
/// Nucleus creates and migrates the three tables on startup like any other entity.
/// <para>
/// Reads run outside a transaction and writes inside one, because a Nucleus transactional context
/// closes its connection after a read. Every save inserts the claim for the version it moves to
/// first, then writes the record, then reconciles the previous-path rows, then removes the old
/// claim, all on one <c>ContextOptions.Transactional</c> context; a unique violation on the claim
/// or on the record's asset id surfaces as <see cref="S3AssetConcurrencyException"/>, one on the
/// live path as <see cref="S3AssetConflictException"/>. Any other storage failure is rethrown as
/// <see cref="S3LibraryException"/> with the original attached, so a consumer is never coupled to
/// the provider in use.
/// </para>
/// <para>
/// Known limit: Nucleus cannot report rows affected, so a save that races a hard delete of the
/// same record can commit its claim against a row that is no longer there. The library only hard
/// deletes after it has removed the bytes, which makes that a metadata orphan rather than a
/// dangling reference; the next hard delete of the id clears it.
/// </para>
/// </summary>
public sealed class NucleusS3AssetStore : IS3AssetStore
{
    private readonly IObjectDataManager _data;
    private readonly ILogger<NucleusS3AssetStore> _logger;

    public NucleusS3AssetStore(IObjectDataManager data, ILogger<NucleusS3AssetStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        _logger = logger ?? NullLogger<NucleusS3AssetStore>.Instance;
    }

    // ---- reads ------------------------------------------------------------------------------

    public Task<S3Asset?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        RunAsync("Reading the asset", id.ToString("N"), async () =>
        {
            var row = await FindRecordAsync(id, ct).ConfigureAwait(false);
            return row is null ? null : await ToAssetAsync(row, ct).ConfigureAwait(false);
        });

    public Task<S3Asset?> GetByPathAsync(string? folder, string slug, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(slug);
        var fullPath = FullPathOf(folder, slug);
        return RunAsync("Reading the asset by path", fullPath, async () =>
        {
            // The index narrows the rows; the ordinal comparison decides, because a SQL collation
            // may fold more than case. A live row wins over a deleted one at the same path, and
            // the most recently deleted wins among deleted ones.
            var rows = await Query<S3AssetRecord>().Where(r => r.FullPath == fullPath).ToListAsync(ct).ConfigureAwait(false);
            var row = rows
                .Where(r => string.Equals(r.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.SoftDeleted)
                .ThenByDescending(r => r.DeletedUtc ?? DateTimeOffset.MinValue)
                .ThenByDescending(r => r.UpdatedUtc)
                .FirstOrDefault();
            return row is null ? null : await ToAssetAsync(row, ct).ConfigureAwait(false);
        });
    }

    public Task<S3Asset?> GetByPreviousPathAsync(string fullPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        var wanted = S3AssetRecordMapper.NormalisePath(fullPath);
        return RunAsync("Reading the asset by previous path", wanted, async () =>
        {
            var paths = await Query<S3AssetPreviousPath>().Where(p => p.Path == wanted).ToListAsync(ct).ConfigureAwait(false);
            foreach (var assetId in paths
                .Where(p => string.Equals(p.Path, wanted, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.AssetId)
                .Distinct())
            {
                var rows = await Query<S3AssetRecord>()
                    .Where(r => r.AssetId == assetId && r.SoftDeleted == false)
                    .ToListAsync(ct).ConfigureAwait(false);
                var row = rows.FirstOrDefault(r => !r.SoftDeleted);
                if (row is not null)
                    return await ToAssetAsync(row, ct).ConfigureAwait(false);
            }
            return null;
        });
    }

    public Task<S3Asset?> GetBySha256Async(string sha256, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        var wanted = S3AssetRecordMapper.NormaliseSha(sha256);
        return RunAsync("Reading the asset by hash", wanted, async () =>
        {
            var rows = await Query<S3AssetRecord>()
                .Where(r => r.Sha256 == wanted && r.SoftDeleted == false)
                .ToListAsync(ct).ConfigureAwait(false);
            var row = rows.FirstOrDefault(r => !r.SoftDeleted && string.Equals(r.Sha256, wanted, StringComparison.OrdinalIgnoreCase));
            return row is null ? null : await ToAssetAsync(row, ct).ConfigureAwait(false);
        });
    }

    public Task<IReadOnlyList<S3Asset>> QueryAsync(S3AssetQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return RunAsync("Querying assets", "*", async () =>
        {
            // The SQL side does what Nucleus translates (equalities); the name filter, ordering
            // and paging happen here because the provider translates none of them. Fine at the
            // scale a media library reaches; see the package notes.
            var page = Filter(await LoadAsync(query, ct).ConfigureAwait(false), query)
                .OrderBy(r => r.FullPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.AssetId)
                .Skip(Math.Max(0, query.Skip))
                .Take(query.EffectiveTake)
                .ToList();
            if (page.Count == 0)
                return (IReadOnlyList<S3Asset>)Array.Empty<S3Asset>();

            var ids = page.Select(r => r.AssetId).ToList();
            var previous = await Query<S3AssetPreviousPath>().Where(p => ids.Contains(p.AssetId)).ToListAsync(ct).ConfigureAwait(false);
            var byAsset = previous.ToLookup(p => p.AssetId);
            return (IReadOnlyList<S3Asset>)page.Select(r => S3AssetRecordMapper.ToAsset(r, byAsset[r.AssetId])).ToList();
        });
    }

    public Task<int> CountAsync(S3AssetQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return RunAsync("Counting assets", "*", async () =>
            Filter(await LoadAsync(query, ct).ConfigureAwait(false), query).Count());
    }

    // ---- writes -----------------------------------------------------------------------------

    public Task SaveAsync(S3Asset asset, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Id == Guid.Empty)
            throw new ArgumentException("An asset needs an id before it can be saved.", nameof(asset));

        return RunAsync("Saving the asset", asset.Id.ToString("N"), async () =>
        {
            var id = asset.Id;
            var expected = asset.Version;
            var fullPath = asset.FullPath;

            // Read phase, no transaction. The version check here covers a stale save and a
            // never-stored record that claims a version; the claim insert below covers the race
            // the check cannot see. The path check here is the fast path; the live-path index is
            // the guard.
            var existing = await FindRecordAsync(id, ct).ConfigureAwait(false);
            var stored = existing?.Version ?? 0;
            if (expected != stored)
                throw new S3AssetConcurrencyException(id, expected, stored);

            if (!asset.IsDeleted)
            {
                var holders = await Query<S3AssetRecord>().Where(r => r.LivePath == fullPath).ToListAsync(ct).ConfigureAwait(false);
                if (holders.Any(r => r.AssetId != id && string.Equals(r.LivePath, fullPath, StringComparison.OrdinalIgnoreCase)))
                    throw new S3AssetConflictException(fullPath);
            }

            var oldPaths = await PreviousPathsOf(id, ct).ConfigureAwait(false);
            var oldClaims = await ClaimsOf(id, ct).ConfigureAwait(false);
            var wantedPaths = asset.PreviousPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(S3AssetRecordMapper.NormalisePath)
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var pathsToDelete = oldPaths.Where(p => p.Path is null || !wantedPaths.Contains(p.Path, StringComparer.Ordinal)).ToList();
            var pathsToInsert = wantedPaths
                .Where(p => !oldPaths.Any(o => string.Equals(o.Path, p, StringComparison.Ordinal)))
                .Select(p => new S3AssetPreviousPath { AssetId = id, Path = p })
                .ToList();

            var newVersion = expected + 1;
            var row = existing ?? new S3AssetRecord();
            S3AssetRecordMapper.Apply(row, asset, newVersion);
            var newClaim = new S3AssetVersionClaim { AssetId = id, Claim = S3AssetRecordMapper.ClaimFor(id, newVersion) };

            // Write phase, one transaction. The claim goes first: it is the gate, and a loser
            // fails here before touching the record.
            try
            {
                await using var context = await _data.DataProvider.CreateContextAsync(ContextOptions.Transactional, ct).ConfigureAwait(false);
                try
                {
                    await newClaim.SaveAsync(context, ct).ConfigureAwait(false);
                    await row.SaveAsync(context, ct).ConfigureAwait(false);
                    foreach (var path in pathsToDelete)
                        await context.DeleteAsync(path, ct).ConfigureAwait(false);
                    foreach (var path in pathsToInsert)
                        await path.SaveAsync(context, ct).ConfigureAwait(false);
                    foreach (var claim in oldClaims)
                        await context.DeleteAsync(claim, ct).ConfigureAwait(false);
                }
                catch
                {
                    await context.TryRollbackAsync(ct).ConfigureAwait(false);
                    throw;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException && FindUniqueViolation(exception) is { } violation)
            {
                var translated = await TranslateUniqueViolationAsync(violation, id, expected, fullPath, ct).ConfigureAwait(false);
                if (translated is null)
                    ExceptionDispatchInfo.Capture(exception).Throw();
                throw translated!;
            }

            // Only once the transaction has committed: a caller that sees the new version can
            // rely on the store holding it.
            asset.Version = newVersion;
            return true;
        });
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default) =>
        RunAsync("Deleting the asset", id.ToString("N"), async () =>
        {
            var row = await FindRecordAsync(id, ct).ConfigureAwait(false);
            var paths = await PreviousPathsOf(id, ct).ConfigureAwait(false);
            var claims = await ClaimsOf(id, ct).ConfigureAwait(false);
            if (row is null && paths.Count == 0 && claims.Count == 0)
                return false;

            await using var context = await _data.DataProvider.CreateContextAsync(ContextOptions.Transactional, ct).ConfigureAwait(false);
            try
            {
                foreach (var path in paths)
                    await context.DeleteAsync(path, ct).ConfigureAwait(false);
                foreach (var claim in claims)
                    await context.DeleteAsync(claim, ct).ConfigureAwait(false);
                if (row is not null)
                    await context.DeleteAsync(row, ct).ConfigureAwait(false);
            }
            catch
            {
                await context.TryRollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
            return true;
        });

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>
    /// The adaptor as a plain queryable. It also implements IAsyncEnumerable, and on .NET 10 the
    /// BCL's async LINQ would otherwise make every ToListAsync ambiguous.
    /// </summary>
    private IQueryable<T> Query<T>() where T : CoreObject<T>, new() => _data.Get<T>();

    private async Task<S3AssetRecord?> FindRecordAsync(Guid id, CancellationToken ct)
    {
        var rows = await Query<S3AssetRecord>().Where(r => r.AssetId == id).ToListAsync(ct).ConfigureAwait(false);
        return rows.FirstOrDefault(r => r.AssetId == id);
    }

    private async Task<List<S3AssetPreviousPath>> PreviousPathsOf(Guid id, CancellationToken ct)
    {
        var rows = await Query<S3AssetPreviousPath>().Where(p => p.AssetId == id).ToListAsync(ct).ConfigureAwait(false);
        return rows.Where(p => p.AssetId == id).ToList();
    }

    private async Task<List<S3AssetVersionClaim>> ClaimsOf(Guid id, CancellationToken ct)
    {
        var rows = await Query<S3AssetVersionClaim>().Where(c => c.AssetId == id).ToListAsync(ct).ConfigureAwait(false);
        return rows.Where(c => c.AssetId == id).ToList();
    }

    private async Task<S3Asset> ToAssetAsync(S3AssetRecord row, CancellationToken ct) =>
        S3AssetRecordMapper.ToAsset(row, await PreviousPathsOf(row.AssetId, ct).ConfigureAwait(false));

    /// <summary>The rows the SQL side of a query selects, as one composed predicate.</summary>
    private Task<List<S3AssetRecord>> LoadAsync(S3AssetQuery query, CancellationToken ct)
    {
        var predicate = SqlPredicateFor(query);
        var source = predicate is null ? Query<S3AssetRecord>() : Query<S3AssetRecord>().Where(predicate);
        return source.ToListAsync(ct);
    }

    /// <summary>
    /// The equalities Nucleus translates, combined with AndAlso into one predicate. Null when the
    /// query has no SQL-side clause at all.
    /// </summary>
    internal static Expression<Func<S3AssetRecord, bool>>? SqlPredicateFor(S3AssetQuery query)
    {
        var row = Expression.Parameter(typeof(S3AssetRecord), "r");
        Expression? body = null;

        void And(string property, object? value, Type type)
        {
            var clause = Expression.Equal(Expression.Property(row, property), Expression.Constant(value, type));
            body = body is null ? clause : Expression.AndAlso(body, clause);
        }

        if (!query.IncludeDeleted)
            And(nameof(S3AssetRecord.SoftDeleted), false, typeof(bool));
        if (query.Folder is not null)
            And(nameof(S3AssetRecord.Folder), S3Asset.NormaliseFolder(query.Folder), typeof(string));
        if (!string.IsNullOrEmpty(query.Sha256))
            And(nameof(S3AssetRecord.Sha256), S3AssetRecordMapper.NormaliseSha(query.Sha256), typeof(string));
        if (query.HasSupersededBlobs is { } hasSuperseded)
            And(nameof(S3AssetRecord.HasSupersededBlobs), hasSuperseded, typeof(bool));

        return body is null ? null : Expression.Lambda<Func<S3AssetRecord, bool>>(body, row);
    }

    /// <summary>The in-memory side: the SQL clauses re-checked ordinally, plus the name filter.</summary>
    private static IEnumerable<S3AssetRecord> Filter(IEnumerable<S3AssetRecord> rows, S3AssetQuery query)
    {
        if (!query.IncludeDeleted)
            rows = rows.Where(r => !r.SoftDeleted);
        if (query.Folder is not null)
        {
            var folder = S3Asset.NormaliseFolder(query.Folder);
            rows = rows.Where(r => string.Equals(r.Folder ?? string.Empty, folder, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrEmpty(query.Sha256))
        {
            var sha = S3AssetRecordMapper.NormaliseSha(query.Sha256);
            rows = rows.Where(r => string.Equals(r.Sha256, sha, StringComparison.OrdinalIgnoreCase));
        }
        if (query.HasSupersededBlobs is { } hasSuperseded)
            rows = rows.Where(r => r.HasSupersededBlobs == hasSuperseded);
        if (!string.IsNullOrWhiteSpace(query.NameContains))
        {
            var term = query.NameContains.Trim();
            rows = rows.Where(r =>
                (r.Name ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (r.Slug ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase));
        }
        return rows;
    }

    private static string FullPathOf(string? folder, string slug)
    {
        var normalisedFolder = S3Asset.NormaliseFolder(folder);
        var normalisedSlug = slug.Trim('/').ToLowerInvariant();
        return normalisedFolder.Length == 0 ? normalisedSlug : $"{normalisedFolder}/{normalisedSlug}";
    }

    /// <summary>
    /// The message of a SQL Server unique violation (error 2601 or 2627) anywhere in the chain,
    /// or null. Found by type name and an int <c>Number</c> property rather than a type, so the
    /// package stays provider neutral and never references SqlClient.
    /// </summary>
    internal static string? FindUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!string.Equals(current.GetType().Name, "SqlException", StringComparison.Ordinal))
                continue;
            if (current.GetType().GetProperty("Number")?.GetValue(current) is int number && number is 2601 or 2627)
                return current.Message;
        }
        return null;
    }

    /// <summary>
    /// Which rule the index in the violation message guards: the claim or the asset id means a
    /// racing writer won the version, the live path means a racing writer won the path. Null for
    /// any other index, which is then an ordinary storage failure.
    /// </summary>
    internal static Exception? ClassifyUniqueViolation(string violation, Guid id, long expected, long actual, string fullPath)
    {
        if (violation.Contains($"{nameof(S3AssetVersionClaim)}_{nameof(S3AssetVersionClaim.Claim)}", StringComparison.OrdinalIgnoreCase) ||
            violation.Contains($"{nameof(S3AssetRecord)}_{nameof(S3AssetRecord.AssetId)}", StringComparison.OrdinalIgnoreCase))
            return new S3AssetConcurrencyException(id, expected, actual);
        if (violation.Contains($"{nameof(S3AssetRecord)}_{nameof(S3AssetRecord.LivePath)}", StringComparison.OrdinalIgnoreCase))
            return new S3AssetConflictException(fullPath);
        return null;
    }

    private async Task<Exception?> TranslateUniqueViolationAsync(string violation, Guid id, long expected, string fullPath, CancellationToken ct)
    {
        // The stored version is re-read after the failed transaction has been disposed, so the
        // number in the exception is what the winner left behind.
        var isVersionRace = ClassifyUniqueViolation(violation, id, expected, expected, fullPath) is S3AssetConcurrencyException;
        var actual = isVersionRace ? (await FindRecordAsync(id, ct).ConfigureAwait(false))?.Version ?? 0 : 0;
        return ClassifyUniqueViolation(violation, id, expected, actual, fullPath);
    }

    /// <summary>
    /// Runs one operation and turns anything the storage layer throws into an
    /// <see cref="S3LibraryException"/> carrying the original, per the contract's remarks.
    /// Argument refusals, cancellation and the library's own exceptions pass through untouched.
    /// </summary>
    private async Task<T> RunAsync<T>(string operation, string key, Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or ArgumentException or S3LibraryException))
        {
            _logger.LogError(exception, "{Operation} failed for '{Key}'.", operation, key);
            throw new S3LibraryException($"{operation} failed for '{key}'. See the inner exception.", exception);
        }
    }
}
