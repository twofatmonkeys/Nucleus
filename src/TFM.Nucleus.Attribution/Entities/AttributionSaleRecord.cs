using TFM.Attribution;
using TFM.Nucleus.Data;

namespace TFM.Nucleus.Attribution;

/// <summary>
/// One frozen <see cref="AttributionSale"/>. <see cref="SaleId"/> carries the unique index, so a
/// sale has one record however often it is recorded: the store replaces it whole. The shares are
/// <see cref="AttributionShareRecord"/> rows tied by <see cref="SaleId"/> rather than a Nucleus
/// child collection, so a replacement deletes them by predicate without loading them.
/// </summary>
[Deletable(DeleteBehavior.Hard)]
public class AttributionSaleRecord : CoreObject<AttributionSaleRecord>
{
    /// <summary>The host's sale id; on a cart based site, the cart or checkout id.</summary>
    [DatabaseColumn(Unique = true)]
    public Guid SaleId { get => GetValue<Guid>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode, AttributionSale.SaleTypeMaxLength)]
    public string? SaleType { get => GetValue<string>(); set => SetValue(value); }

    /// <summary>Every session id considered, comma separated. Read whole, never queried into.</summary>
    [DatabaseColumn(DataType.NotSpecified, -1)]
    public string? SessionIds { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(NonClusteredIndex = true)]
    public DateTimeOffset Completed { get => GetValue<DateTimeOffset>(); set => SetValue(value); }

    [DatabaseColumn]
    public decimal SaleValue { get => GetValue<decimal?>() ?? 0m; set => SetValue(value); }

    [DatabaseColumn]
    public decimal AmountPaid { get => GetValue<decimal?>() ?? 0m; set => SetValue(value); }

    [DatabaseColumn]
    public int WindowDays { get => GetValue<int?>() ?? 0; set => SetValue(value); }

    [DatabaseColumn(DataType.NotSpecified, AttributionSale.ModelMaxLength)]
    public string? Model { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn]
    public int TouchesInWindow { get => GetValue<int?>() ?? 0; set => SetValue(value); }

    [DatabaseColumn]
    public int TouchesTotal { get => GetValue<int?>() ?? 0; set => SetValue(value); }

    [DatabaseColumn]
    public DateTimeOffset Recorded { get => GetValue<DateTimeOffset>(); set => SetValue(value); }

    public override void Validate()
    {
        base.Validate();
        if (SaleId == Guid.Empty)
            throw new InvalidOperationException("An attribution sale needs a sale id.");
    }
}
