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

    /// <summary>The save benchmarks' rows, in a table of their own.</summary>
    [Deletable]
    public class IntBenchWidget : CoreObject<IntBenchWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public int Quantity { get => GetValue<int>(); set => SetValue(value); }
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

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>A parent whose child rows carry the content, like a bundle and its files.</summary>
    [Deletable]
    public class IntBundle : CoreObject<IntBundle>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        public NucleusChildCollection<IntBundleFile, IntBundle> Files => GetChildCollection<IntBundleFile, IntBundle>();
    }

    [Deletable]
    public class IntBundleFile : CoreChildObject<IntBundleFile, IntBundle>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public string? Content { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>A soft-deleted parent over hard-deleted children.</summary>
    [Deletable(DeleteBehavior.Soft)]
    public class IntSoftBundle : CoreObject<IntSoftBundle>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        public NucleusChildCollection<IntSoftBundleNote, IntSoftBundle> Notes => GetChildCollection<IntSoftBundleNote, IntSoftBundle>();
    }

    [Deletable]
    public class IntSoftBundleNote : CoreChildObject<IntSoftBundleNote, IntSoftBundle>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }
}
