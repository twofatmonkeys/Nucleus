using TFM.Nucleus.Data;

namespace TFM.Nucleus.S3Library;

/// <summary>
/// One TFM.S3Library asset's metadata. Every scalar on <c>S3Asset</c> has a column; the three
/// collections travel as JSON, except previous paths, which get their own indexed rows in
/// <see cref="S3AssetPreviousPath"/> because the library looks them up on the miss path.
/// <para>
/// <see cref="FullPath"/> is always set and serves every lookup, deleted rows included.
/// <see cref="LivePath"/> is the same value while the record is live and null once it is
/// soft-deleted; the filtered unique index Nucleus puts on a <c>Unique</c> column
/// (<c>WHERE [LivePath] IS NOT NULL</c>) is therefore exactly the contract's rule that no two
/// live records share a path while a deleted one may. The soft-delete flag is
/// <see cref="SoftDeleted"/>, not <c>IsDeleted</c>: Nucleus reserves that column name for its
/// own soft-delete behaviour, which this entity does not use.
/// </para>
/// </summary>
[Deletable(DeleteBehavior.Hard)]
public class S3AssetRecord : CoreObject<S3AssetRecord>
{
    /// <summary>The library's stable asset identity. The row's own ID is Nucleus's business.</summary>
    [DatabaseColumn(Unique = true)]
    public Guid AssetId { get => GetValue<Guid>(); set => SetValue(value); }

    /// <summary>The store's concurrency stamp. See <see cref="S3AssetVersionClaim"/> for how it is guarded.</summary>
    [DatabaseColumn]
    public long Version { get => GetValue<long>(); set => SetValue(value); }

    [DatabaseColumn]
    public Guid BlobId { get => GetValue<Guid>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode)]
    public string? Name { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode)]
    public string? Slug { get => GetValue<string>(); set => SetValue(value); }

    /// <summary>The normalised folder, empty string for the root, so a folder query is one equality.</summary>
    [DatabaseColumn(DataType.Unicode, Indexed = true)]
    public string? Folder { get => GetValue<string>(); set => SetValue(value); }

    /// <summary>The normalised <c>folder/slug</c>, always set, so path lookups find deleted rows too.</summary>
    [DatabaseColumn(DataType.Unicode, Indexed = true)]
    public string? FullPath { get => GetValue<string>(); set => SetValue(value); }

    /// <summary>Equal to <see cref="FullPath"/> while live, null once soft-deleted. The unique index is the path rule.</summary>
    [DatabaseColumn(DataType.Unicode, Unique = true)]
    public string? LivePath { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn]
    public string? ContentType { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn]
    public long SizeBytes { get => GetValue<long>(); set => SetValue(value); }

    /// <summary>Stored lowercase, so the hash lookup is one equality.</summary>
    [DatabaseColumn(Indexed = true)]
    public string? Sha256 { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn]
    public string? ETag { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn]
    public int? Width { get => GetValue<int?>(); set => SetValue(value); }

    [DatabaseColumn]
    public int? Height { get => GetValue<int?>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode, -1)]
    public string? AltText { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn]
    public bool IsDecorative { get => GetValue<bool>(); set => SetValue(value); }

    [DatabaseColumn]
    public int? CacheSeconds { get => GetValue<int?>(); set => SetValue(value); }

    [DatabaseColumn]
    public bool RequiresAuthorization { get => GetValue<bool>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode)]
    public string? AuthorizationScope { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn]
    public bool ServeInline { get => GetValue<bool>(); set => SetValue(value); }

    /// <summary>The variants as JSON; null when there are none.</summary>
    [DatabaseColumn(DataType.Unicode, -1)]
    public string? VariantsJson { get => GetValue<string>(); set => SetValue(value); }

    /// <summary>The superseded blobs as JSON; null when there are none.</summary>
    [DatabaseColumn(DataType.Unicode, -1)]
    public string? SupersededJson { get => GetValue<string>(); set => SetValue(value); }

    /// <summary>Denormalised from <see cref="SupersededJson"/> so the purge filter is one equality.</summary>
    [DatabaseColumn]
    public bool HasSupersededBlobs { get => GetValue<bool>(); set => SetValue(value); }

    [DatabaseColumn]
    public DateTimeOffset CreatedUtc { get => GetValue<DateTimeOffset>(); set => SetValue(value); }

    [DatabaseColumn]
    public DateTimeOffset UpdatedUtc { get => GetValue<DateTimeOffset>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode)]
    public string? CreatedBy { get => GetValue<string>(); set => SetValue(value); }

    /// <summary>The library's soft-delete flag. Not <c>IsDeleted</c>, which Nucleus reserves.</summary>
    [DatabaseColumn]
    public bool SoftDeleted { get => GetValue<bool>(); set => SetValue(value); }

    [DatabaseColumn]
    public DateTimeOffset? DeletedUtc { get => GetValue<DateTimeOffset?>(); set => SetValue(value); }

    public override void Validate()
    {
        base.Validate();
        if (AssetId == Guid.Empty)
            throw new InvalidOperationException("An S3 asset record needs an asset id.");
        if (string.IsNullOrEmpty(FullPath))
            throw new InvalidOperationException("An S3 asset record needs a full path.");
        if (SoftDeleted ? LivePath is not null : !string.Equals(LivePath, FullPath, StringComparison.Ordinal))
            throw new InvalidOperationException("An S3 asset record's live path must equal its full path while live and be null once soft-deleted.");
    }
}
