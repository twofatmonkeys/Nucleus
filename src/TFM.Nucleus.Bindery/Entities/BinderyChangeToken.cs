using TFM.Nucleus.Data;

namespace TFM.Nucleus.Bindery;

/// <summary>
/// The one row Bindery's runtime cache polls: its <see cref="Token"/> is replaced, inside the same
/// transaction, whenever published content changes (a publish, an unpublish, a page delete), so
/// every replica converges on the next request after another replica's publish.
/// </summary>
[Deletable(DeleteBehavior.Hard)]
public class BinderyChangeToken : CoreObject<BinderyChangeToken>
{
    [DatabaseColumn(DataType.Unicode)]
    public string? Token { get => GetValue<string>(); set => SetValue(value); }
}
