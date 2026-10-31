using TFM.Nucleus.Data;

namespace TFM.Nucleus.S3Library;

/// <summary>
/// One path an asset used to answer to, one row per (asset, path). The library consults these on
/// the miss path, ahead of answering 404, and its contract requires that to be an indexed lookup
/// rather than a scan over JSON, which is why previous paths are rows and not a column.
/// </summary>
[Deletable(DeleteBehavior.Hard)]
public class S3AssetPreviousPath : CoreObject<S3AssetPreviousPath>
{
    [DatabaseColumn(Indexed = true)]
    public Guid AssetId { get => GetValue<Guid>(); set => SetValue(value); }

    /// <summary>The path trimmed of slashes and lowercased, the form the library asks with.</summary>
    [DatabaseColumn(DataType.Unicode, Indexed = true)]
    public string? Path { get => GetValue<string>(); set => SetValue(value); }

    public override void Validate()
    {
        base.Validate();
        if (AssetId == Guid.Empty)
            throw new InvalidOperationException("A previous path needs an asset id.");
        if (string.IsNullOrEmpty(Path))
            throw new InvalidOperationException("A previous path needs a path.");
    }
}
