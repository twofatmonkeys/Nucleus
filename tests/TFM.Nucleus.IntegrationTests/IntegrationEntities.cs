namespace TFM.Nucleus.IntegrationTests
{
    public class IntWidget : CoreObject<IntWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public int Quantity { get => GetValue<int>(); set => SetValue(value); }
    }

    [Deletable]
    public class IntHardItem : CoreObject<IntHardItem>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    [Deletable(DeleteBehavior.Soft)]
    public class IntSoftItem : CoreObject<IntSoftItem>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }
}

namespace TFM.Nucleus.IntegrationTests
{
    public class IntUniqueItem : CoreObject<IntUniqueItem>
    {
        [DatabaseColumn(Unique = true)]
        public string? Code { get => GetValue<string>(); set => SetValue(value); }
    }

    public class IntKindWidget : CoreObject<IntKindWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public IntWidgetKind Kind { get => GetValue<IntWidgetKind>(); set => SetValue(value); }

        [DatabaseColumn]
        public IntWidgetKind? OptionalKind { get => GetValue<IntWidgetKind?>(); set => SetValue(value); }
    }

    public enum IntWidgetKind
    {
        Unknown = 0,
        Standard = 1,
        Premium = 2,
    }
}
