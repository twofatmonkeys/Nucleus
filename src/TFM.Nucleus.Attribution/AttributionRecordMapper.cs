using TFM.Attribution;

namespace TFM.Nucleus.Attribution;

/// <summary>
/// Copies between the library's models and the three records. Every read builds a fresh model, so
/// nothing handed out is ever a row. Strings are clipped to their column sizes on the way in: the
/// library's classifier already clips, but a backfill or a host-built touch might not, and an
/// oversized value would fail the whole batch.
/// </summary>
internal static class AttributionRecordMapper
{
    public static AttributionTouchRecord ToRecord(AttributionTouch touch) => new()
    {
        TouchId = touch.ID == Guid.Empty ? Guid.NewGuid() : touch.ID,
        SessionId = touch.SessionID,
        Touched = touch.Touched,
        VisitStart = touch.VisitStart,
        Origin = touch.Origin.ToString(),
        Channel = touch.Channel.ToString(),
        Source = Clip(touch.Source, AttributionTouch.SourceMaxLength),
        Medium = Clip(touch.Medium, AttributionTouch.MediumMaxLength),
        Campaign = Clip(touch.Campaign, AttributionTouch.CampaignMaxLength),
        Term = Clip(touch.Term, AttributionTouch.TermMaxLength),
        Content = Clip(touch.Content, AttributionTouch.ContentMaxLength),
        UtmId = Clip(touch.UtmId, AttributionTouch.UtmIdMaxLength),
        SourcePlatform = Clip(touch.SourcePlatform, AttributionTouch.SourcePlatformMaxLength),
        CreativeFormat = Clip(touch.CreativeFormat, AttributionTouch.CreativeFormatMaxLength),
        MarketingTactic = Clip(touch.MarketingTactic, AttributionTouch.MarketingTacticMaxLength),
        ClickIdType = Clip(touch.ClickIdType, AttributionTouch.ClickIdTypeMaxLength),
        ClickId = Clip(touch.ClickId, AttributionTouch.ClickIdMaxLength),
        GadSource = Clip(touch.GadSource, AttributionTouch.GadSourceMaxLength),
        GadCampaignId = Clip(touch.GadCampaignId, AttributionTouch.GadCampaignIdMaxLength),
        PartnerRef = Clip(touch.PartnerRef, AttributionTouch.PartnerRefMaxLength),
        ReferrerHost = Clip(touch.ReferrerHost, AttributionTouch.ReferrerHostMaxLength),
        LandingPath = Clip(touch.LandingPath, AttributionTouch.LandingPathMaxLength),
    };

    public static AttributionTouch ToTouch(AttributionTouchRecord row) => new()
    {
        ID = row.TouchId,
        SessionID = row.SessionId,
        Touched = row.Touched,
        VisitStart = row.VisitStart,
        Origin = Enum.TryParse(row.Origin, ignoreCase: true, out TouchOrigin origin) ? origin : TouchOrigin.Live,
        // A name this version does not know (a channel renamed or added by a newer library) reads
        // as Other rather than failing the whole history.
        Channel = AttributionChannels.TryParse(row.Channel, out var channel) ? channel : AttributionChannel.Other,
        Source = row.Source,
        Medium = row.Medium,
        Campaign = row.Campaign,
        Term = row.Term,
        Content = row.Content,
        UtmId = row.UtmId,
        SourcePlatform = row.SourcePlatform,
        CreativeFormat = row.CreativeFormat,
        MarketingTactic = row.MarketingTactic,
        ClickIdType = row.ClickIdType,
        ClickId = row.ClickId,
        GadSource = row.GadSource,
        GadCampaignId = row.GadCampaignId,
        PartnerRef = row.PartnerRef,
        ReferrerHost = row.ReferrerHost,
        LandingPath = row.LandingPath,
    };

    public static AttributionSaleRecord ToRecord(AttributionSale sale) => new()
    {
        SaleId = sale.SaleID,
        SaleType = Clip(sale.SaleType, AttributionSale.SaleTypeMaxLength) ?? string.Empty,
        SessionIds = string.Join(',', sale.SessionIDs.Select(id => id.ToString("N"))),
        Completed = sale.Completed,
        SaleValue = sale.SaleValue,
        AmountPaid = sale.AmountPaid,
        WindowDays = sale.WindowDays,
        Model = Clip(sale.Model, AttributionSale.ModelMaxLength) ?? string.Empty,
        TouchesInWindow = sale.TouchesInWindow,
        TouchesTotal = sale.TouchesTotal,
        Recorded = sale.Recorded,
    };

    public static AttributionShareRecord ToRecord(Guid saleId, AttributionShare share) => new()
    {
        ShareId = share.ID == Guid.Empty ? Guid.NewGuid() : share.ID,
        SaleId = saleId,
        TouchId = share.TouchID,
        SortOrder = share.SortOrder,
        Position = share.Position.ToString(),
        Weight = share.Weight,
        SaleValueShare = share.SaleValueShare,
        AmountPaidShare = share.AmountPaidShare,
        Channel = share.Channel.ToString(),
        Source = Clip(share.Source, AttributionTouch.SourceMaxLength),
        Campaign = Clip(share.Campaign, AttributionTouch.CampaignMaxLength),
    };

    public static AttributionSale ToSale(AttributionSaleRecord row, IEnumerable<AttributionShareRecord> shares) => new()
    {
        SaleID = row.SaleId,
        SaleType = row.SaleType ?? string.Empty,
        SessionIDs = (row.SessionIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => Guid.TryParse(id, out var parsed) ? parsed : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToList(),
        Completed = row.Completed,
        SaleValue = row.SaleValue,
        AmountPaid = row.AmountPaid,
        WindowDays = row.WindowDays,
        Model = row.Model ?? string.Empty,
        TouchesInWindow = row.TouchesInWindow,
        TouchesTotal = row.TouchesTotal,
        Recorded = row.Recorded,
        Shares = shares.OrderBy(s => s.SortOrder).Select(s => new AttributionShare
        {
            ID = s.ShareId,
            SaleID = s.SaleId,
            TouchID = s.TouchId,
            SortOrder = s.SortOrder,
            Position = Enum.TryParse(s.Position, ignoreCase: true, out SharePosition position) ? position : SharePosition.None,
            Weight = s.Weight,
            SaleValueShare = s.SaleValueShare,
            AmountPaidShare = s.AmountPaidShare,
            Channel = AttributionChannels.TryParse(s.Channel, out var channel) ? channel : AttributionChannel.Other,
            Source = s.Source,
            Campaign = s.Campaign,
        }).ToList(),
    };

    private static string? Clip(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
