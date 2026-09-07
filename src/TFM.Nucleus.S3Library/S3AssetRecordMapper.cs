using System.Text.Json;
using TFM.S3Library.Abstractions;

namespace TFM.Nucleus.S3Library;

/// <summary>
/// Copies between the library's <see cref="S3Asset"/> and the three entities. <see cref="ToAsset"/>
/// builds a fresh instance on every call, which is how the store meets the contract's detachment
/// rule by construction: nothing handed out is ever the row itself.
/// </summary>
internal static class S3AssetRecordMapper
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Writes every column from the asset, with the version the save is moving to.</summary>
    public static void Apply(S3AssetRecord row, S3Asset asset, long newVersion)
    {
        var fullPath = asset.FullPath;
        row.AssetId = asset.Id;
        row.Version = newVersion;
        row.BlobId = asset.BlobId;
        row.Name = asset.Name;
        row.Slug = asset.Slug;
        row.Folder = S3Asset.NormaliseFolder(asset.Folder);
        row.FullPath = fullPath;
        row.LivePath = asset.IsDeleted ? null : fullPath;
        row.ContentType = asset.ContentType;
        row.SizeBytes = asset.SizeBytes;
        row.Sha256 = NormaliseSha(asset.Sha256);
        row.ETag = asset.ETag;
        row.Width = asset.Width;
        row.Height = asset.Height;
        row.AltText = asset.AltText;
        row.IsDecorative = asset.IsDecorative;
        row.CacheSeconds = asset.CacheSeconds;
        row.RequiresAuthorization = asset.RequiresAuthorization;
        row.AuthorizationScope = asset.AuthorizationScope;
        row.ServeInline = asset.ServeInline;
        row.VariantsJson = asset.Variants.Count == 0 ? null : JsonSerializer.Serialize(asset.Variants, Json);
        row.SupersededJson = asset.SupersededBlobs.Count == 0 ? null : JsonSerializer.Serialize(asset.SupersededBlobs, Json);
        row.HasSupersededBlobs = asset.SupersededBlobs.Count > 0;
        row.CreatedUtc = asset.CreatedUtc;
        row.UpdatedUtc = asset.UpdatedUtc;
        row.CreatedBy = asset.CreatedBy;
        row.SoftDeleted = asset.IsDeleted;
        row.DeletedUtc = asset.DeletedUtc;
    }

    /// <summary>A new asset from the row and its previous-path rows. Folder is null for the root.</summary>
    public static S3Asset ToAsset(S3AssetRecord row, IEnumerable<S3AssetPreviousPath> previousPaths)
    {
        var asset = new S3Asset
        {
            Id = row.AssetId,
            Version = row.Version,
            BlobId = row.BlobId,
            Name = row.Name ?? string.Empty,
            Slug = row.Slug ?? string.Empty,
            Folder = string.IsNullOrEmpty(row.Folder) ? null : row.Folder,
            ContentType = row.ContentType ?? string.Empty,
            SizeBytes = row.SizeBytes,
            Sha256 = row.Sha256 ?? string.Empty,
            ETag = row.ETag ?? string.Empty,
            Width = row.Width,
            Height = row.Height,
            AltText = row.AltText,
            IsDecorative = row.IsDecorative,
            CacheSeconds = row.CacheSeconds,
            RequiresAuthorization = row.RequiresAuthorization,
            AuthorizationScope = row.AuthorizationScope,
            ServeInline = row.ServeInline,
            CreatedUtc = row.CreatedUtc,
            UpdatedUtc = row.UpdatedUtc,
            CreatedBy = row.CreatedBy,
            IsDeleted = row.SoftDeleted,
            DeletedUtc = row.DeletedUtc,
        };

        if (row.VariantsJson is { Length: > 0 } variants)
            asset.Variants.AddRange(JsonSerializer.Deserialize<List<S3AssetVariant>>(variants, Json) ?? []);
        if (row.SupersededJson is { Length: > 0 } superseded)
            asset.SupersededBlobs.AddRange(JsonSerializer.Deserialize<List<S3SupersededBlob>>(superseded, Json) ?? []);
        foreach (var previous in previousPaths)
        {
            if (previous.Path is { Length: > 0 } path)
                asset.PreviousPaths.Add(path);
        }

        return asset;
    }

    /// <summary>The form a previous path is stored and asked in: no surrounding slashes, lowercase.</summary>
    public static string NormalisePath(string path) => path.Trim('/').ToLowerInvariant();

    /// <summary>Hashes are compared case-insensitively by the contract; storing them lowercase makes that one equality.</summary>
    public static string NormaliseSha(string? sha256) => (sha256 ?? string.Empty).ToLowerInvariant();

    /// <summary>The claim value for one version of one asset.</summary>
    public static string ClaimFor(Guid assetId, long version) => $"{assetId:N}:{version}";
}
