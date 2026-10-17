using TFM.Attribution;
using TFM.Nucleus.Data;

namespace TFM.Nucleus.Attribution;

/// <summary>
/// One <see cref="AttributionShare"/> of a frozen sale. <see cref="Position"/> and
/// <see cref="Channel"/> are stored by name, like the touch's channel. <see cref="Weight"/> keeps
/// six places, the precision the library allocates to.
/// </summary>
[Deletable(DeleteBehavior.Hard)]
public class AttributionShareRecord : CoreObject<AttributionShareRecord>
{
    /// <summary>The library's <see cref="AttributionShare.ID"/>.</summary>
    [DatabaseColumn(Unique = true)]
    public Guid ShareId { get => GetValue<Guid>(); set => SetValue(value); }

    /// <summary>The owning sale's <see cref="AttributionSaleRecord.SaleId"/>. Replacement deletes by it.</summary>
    [DatabaseColumn(NonClusteredIndex = true)]
    public Guid SaleId { get => GetValue<Guid>(); set => SetValue(value); }

    /// <summary>Null on the single Unattributed share of a sale that had no touches.</summary>
    [DatabaseColumn(NonClusteredIndex = true)]
    public Guid? TouchId { get => GetValue<Guid?>(); set => SetValue(value); }

    [DatabaseColumn]
    public int SortOrder { get => GetValue<int?>() ?? 0; set => SetValue(value); }

    /// <summary><see cref="SharePosition"/> by name.</summary>
    [DatabaseColumn(DataType.NotSpecified, 10)]
    public string? Position { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(Scale = 6)]
    public decimal Weight { get => GetValue<decimal?>() ?? 0m; set => SetValue(value); }

    [DatabaseColumn]
    public decimal SaleValueShare { get => GetValue<decimal?>() ?? 0m; set => SetValue(value); }

    [DatabaseColumn]
    public decimal AmountPaidShare { get => GetValue<decimal?>() ?? 0m; set => SetValue(value); }

    /// <summary><see cref="AttributionChannel"/> by name.</summary>
    [DatabaseColumn(DataType.NotSpecified, AttributionChannels.NameMaxLength)]
    public string? Channel { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode, AttributionTouch.SourceMaxLength)]
    public string? Source { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(DataType.Unicode, AttributionTouch.CampaignMaxLength)]
    public string? Campaign { get => GetValue<string>(); set => SetValue(value); }

    public override void Validate()
    {
        base.Validate();
        if (ShareId == Guid.Empty)
            throw new InvalidOperationException("An attribution share needs a share id.");
        if (SaleId == Guid.Empty)
            throw new InvalidOperationException("An attribution share needs a sale id.");
    }
}
