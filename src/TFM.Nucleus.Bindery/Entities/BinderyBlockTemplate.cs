using TFM.Nucleus.Data;

namespace TFM.Nucleus.Bindery;

/// <summary>An editor-saved Bindery block template, stored as the JSON <c>BinderyJsonSerializer</c> produces.</summary>
[Deletable(DeleteBehavior.Hard)]
public class BinderyBlockTemplate : CoreObject<BinderyBlockTemplate>
{
    [DatabaseColumn(DataType.Unicode, Unique = true)]
    public string? TemplateId { get => GetValue<string>(); set => SetValue(value); }

    [TextColumn(-1)]
    public string? Json { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn]
    public DateTimeOffset ModifiedUtc { get => GetValue<DateTimeOffset>(); set => SetValue(value); }

    public override void Validate()
    {
        base.Validate();
        if (string.IsNullOrWhiteSpace(TemplateId))
            throw new InvalidOperationException("A block template needs an id.");
        if (string.IsNullOrWhiteSpace(Json))
            throw new InvalidOperationException("A block template cannot be empty.");
    }
}
