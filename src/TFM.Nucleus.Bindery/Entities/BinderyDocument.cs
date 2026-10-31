using TFM.Nucleus.Data;

namespace TFM.Nucleus.Bindery;

/// <summary>
/// One Bindery page: both copies of the document in one row, so publishing (copy draft to
/// published) is a single UPDATE and a reader never sees a half-published page. The listing
/// columns are denormalised from the document on every save so the route map and the lookups
/// never deserialise a document. The table is named after the class, the same name the first
/// consumer used before this package existed, so its rows carry over unchanged.
/// </summary>
[Deletable(DeleteBehavior.Hard)]
public class BinderyDocument : CoreObject<BinderyDocument>
{
    /// <summary>Bindery's immutable page id.</summary>
    [DatabaseColumn(DataType.Unicode, 64, Unique = true)]
    public string? PageId { get => GetValue<string>(); set => SetValue(value); }

    /// <summary>Normalised route for editor-created pages; null for zone sets on code pages.</summary>
    [DatabaseColumn(DataType.Unicode, Indexed = true)]
    public string? Route { get => GetValue<string>(); set => SetValue(value); }

    /// <summary>Developer-given key for a zone set on a code page; null for routed pages.</summary>
    [DatabaseColumn(DataType.Unicode, Indexed = true)]
    public string? PageKey { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode)]
    public string? Title { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn]
    public bool IncludeInSitemap { get => GetValue<bool>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode, -1)]
    public string? DraftJson { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode, -1)]
    public string? PublishedJson { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn]
    public DateTimeOffset? DraftModifiedUtc { get => GetValue<DateTimeOffset?>(); set => SetValue(value); }

    [DatabaseColumn]
    public DateTimeOffset? PublishedUtc { get => GetValue<DateTimeOffset?>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode)]
    public string? ModifiedBy { get => GetValue<string>(); set => SetValue(value); }

    public override void Validate()
    {
        base.Validate();
        if (string.IsNullOrWhiteSpace(PageId))
            throw new InvalidOperationException("A Bindery document needs a page id.");
    }
}
