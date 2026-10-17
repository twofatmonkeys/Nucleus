using TFM.Attribution;
using TFM.Nucleus.Data;

namespace TFM.Nucleus.Attribution;

/// <summary>
/// One <see cref="AttributionTouch"/>: every property of the library's touch has a column, sized by
/// its <c>MaxLength</c> constant. <see cref="TouchId"/> carries the library's own id, because a
/// Nucleus entity's <c>ID</c> is assigned by Nucleus and cannot be set; its unique index is what
/// makes a retried batch safe.
/// <para>
/// <see cref="Origin"/> and <see cref="Channel"/> are stored by name, as the library asks: its enum
/// members may be reordered, and a rename is a data migration either way.
/// </para>
/// <para>
/// Hard deletable so a host can purge old touches or a backfill run (<c>Origin = Backfill</c>) by
/// predicate. The store itself never deletes a touch: the library treats them as immutable.
/// </para>
/// </summary>
[Deletable(DeleteBehavior.Hard)]
public class AttributionTouchRecord : CoreObject<AttributionTouchRecord>
{
    /// <summary>The library's <see cref="AttributionTouch.ID"/>.</summary>
    [DatabaseColumn(Unique = true)]
    public Guid TouchId { get => GetValue<Guid>(); set => SetValue(value); }

    /// <summary>The visitor's long lived web session id. Every read selects by it.</summary>
    [DatabaseColumn(NonClusteredIndex = true)]
    public Guid SessionId { get => GetValue<Guid>(); set => SetValue(value); }

    [DatabaseColumn(NonClusteredIndex = true)]
    public DateTimeOffset Touched { get => GetValue<DateTimeOffset>(); set => SetValue(value); }

    [DatabaseColumn]
    public bool VisitStart { get => GetValue<bool?>() ?? false; set => SetValue(value); }

    /// <summary><see cref="TouchOrigin"/> by name.</summary>
    [DatabaseColumn(DataType.NotSpecified, AttributionTouch.OriginMaxLength)]
    public string? Origin { get => GetValue<string>(); set => SetValue(value); }

    /// <summary><see cref="AttributionChannel"/> by name.</summary>
    [DatabaseColumn(DataType.NotSpecified, AttributionChannels.NameMaxLength)]
    public string? Channel { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode, AttributionTouch.SourceMaxLength)]
    public string? Source { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode, AttributionTouch.MediumMaxLength)]
    public string? Medium { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode, AttributionTouch.CampaignMaxLength)]
    public string? Campaign { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode, AttributionTouch.TermMaxLength)]
    public string? Term { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode, AttributionTouch.ContentMaxLength)]
    public string? Content { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode, AttributionTouch.UtmIdMaxLength)]
    public string? UtmId { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode, AttributionTouch.SourcePlatformMaxLength)]
    public string? SourcePlatform { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode, AttributionTouch.CreativeFormatMaxLength)]
    public string? CreativeFormat { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode, AttributionTouch.MarketingTacticMaxLength)]
    public string? MarketingTactic { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.NotSpecified, AttributionTouch.ClickIdTypeMaxLength)]
    public string? ClickIdType { get => GetValue<string>(); set => SetValue(value); }

    /// <summary>The raw click id. Kept for offline conversion uploads; never logged.</summary>
    [DatabaseColumn(DataType.NotSpecified, AttributionTouch.ClickIdMaxLength)]
    public string? ClickId { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.NotSpecified, AttributionTouch.GadSourceMaxLength)]
    public string? GadSource { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.NotSpecified, AttributionTouch.GadCampaignIdMaxLength)]
    public string? GadCampaignId { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode, AttributionTouch.PartnerRefMaxLength)]
    public string? PartnerRef { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode, AttributionTouch.ReferrerHostMaxLength)]
    public string? ReferrerHost { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode, AttributionTouch.LandingPathMaxLength)]
    public string? LandingPath { get => GetValue<string>(); set => SetValue(value); }

    public override void Validate()
    {
        base.Validate();
        if (TouchId == Guid.Empty)
            throw new InvalidOperationException("An attribution touch needs a touch id.");
        if (SessionId == Guid.Empty)
            throw new InvalidOperationException("An attribution touch needs a session id.");
    }
}
