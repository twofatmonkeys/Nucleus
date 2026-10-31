using TFM.Bindery;
using TFM.Nucleus.Data;

namespace TFM.Nucleus.Bindery;

/// <summary>
/// A snapshot of a Bindery page taken when it was published, so the editor's Versions panel can
/// restore an earlier copy into the draft. <see cref="VersionId"/> is the row's own id, which
/// makes it unique without a count; rows written by an earlier consumer with "v0001" style ids
/// keep working because every lookup is by page id and version id string.
/// </summary>
[Deletable(DeleteBehavior.Hard)]
public class BinderyPageVersion : CoreObject<BinderyPageVersion>
{
    [DatabaseColumn(DataType.Unicode, Indexed = true)]
    public string? PageId { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode)]
    public string? VersionId { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode, -1)]
    public string? Json { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn]
    public DateTimeOffset CreatedUtc { get => GetValue<DateTimeOffset>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode)]
    public string? CreatedBy { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn]
    public PageVersionKind Kind { get => GetValue<PageVersionKind>(); set => SetValue(value); }

    public override void Validate()
    {
        base.Validate();
        if (string.IsNullOrWhiteSpace(PageId) || string.IsNullOrWhiteSpace(VersionId))
            throw new InvalidOperationException("A page version needs a page id and a version id.");
    }
}
