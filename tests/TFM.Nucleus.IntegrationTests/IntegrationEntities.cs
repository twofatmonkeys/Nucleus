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
