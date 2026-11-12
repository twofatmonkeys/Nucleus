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

    /// <summary>Columns named apart from their properties, with a unique one and a child collection.</summary>
    [Deletable]
    public class IntRenamedItem : CoreObject<IntRenamedItem>
    {
        [DatabaseColumn(ColumnName = "item_title", Indexed = true)]
        public string? Title { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn("item_code", Unique = true)]
        public string? Code { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(ColumnName = "item_count")]
        public int Count { get => GetValue<int>(); set => SetValue(value); }

        public NucleusChildCollection<IntRenamedNote, IntRenamedItem> Notes => GetChildCollection<IntRenamedNote, IntRenamedItem>();
    }

    [Deletable]
    public class IntRenamedNote : CoreChildObject<IntRenamedNote, IntRenamedItem>
    {
        [DatabaseColumn(ColumnName = "note_text")]
        public string? Text { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>Renamed columns without a child collection, for the bulk engine.</summary>
    [Deletable]
    public class IntRenamedRow : CoreObject<IntRenamedRow>
    {
        [DatabaseColumn(ColumnName = "row_title")]
        public string? Title { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(ColumnName = "row_code")]
        public string? Code { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(ColumnName = "row_count")]
        public int Count { get => GetValue<int>(); set => SetValue(value); }
    }

    /// <summary>A column the server stamps on every write and the entity reads back, with a child collection.</summary>
    [Deletable]
    public class IntStampedItem : CoreObject<IntStampedItem>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(SetValueOverride = eServerFunction.CurrentDateTimeOffset, ReturnOutputToProperty = true)]
        public DateTimeOffset StampedAt => GetValue<DateTimeOffset>();

        public NucleusChildCollection<IntStampedLine, IntStampedItem> Lines => GetChildCollection<IntStampedLine, IntStampedItem>();
    }

    [Deletable]
    public class IntStampedLine : CoreChildObject<IntStampedLine, IntStampedItem>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(SetValueOverride = eServerFunction.CurrentDateTimeOffset, ReturnOutputToProperty = true)]
        public DateTimeOffset StampedAt => GetValue<DateTimeOffset>();
    }

    /// <summary>A stamped row without children, for the split and bulk write paths.</summary>
    [Deletable]
    public class IntStampedRow : CoreObject<IntStampedRow>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(SetValueOverride = eServerFunction.CurrentDateTimeOffset, ReturnOutputToProperty = true)]
        public DateTimeOffset StampedAt => GetValue<DateTimeOffset>();
    }

    /// <summary>A stamped row in a table of its own, which a test puts a trigger on.</summary>
    [Deletable]
    public class IntTriggeredRow : CoreObject<IntTriggeredRow>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(SetValueOverride = eServerFunction.CurrentDateTimeOffset, ReturnOutputToProperty = true)]
        public DateTimeOffset StampedAt => GetValue<DateTimeOffset>();
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
    /// <summary>Columns named with SQL Server keywords: created bracketed, so a rebuild must copy them bracketed too.</summary>
    public class IntKeywordItem : CoreObject<IntKeywordItem>
    {
        [DatabaseColumn(DataType.Unicode)]
        public string? By { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public int Order { get => GetValue<int>(); set => SetValue(value); }

        [DatabaseColumn(DataType.Unicode)]
        public string? Note { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>
    /// A table that gains columns: the add-column suite drops Key (indexed, and named with a
    /// keyword) and Extra from a populated table, then starts the stack again.
    /// </summary>
    public class IntGrowingItem : CoreObject<IntGrowingItem>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(Indexed = true)]
        public string? Key { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public int? Extra { get => GetValue<int?>(); set => SetValue(value); }
    }

    /// <summary>A soft-deletable table that loses its soft-delete columns in the add-column suite.</summary>
    [Deletable(DeleteBehavior.Soft)]
    public class IntGrowingSoftItem : CoreObject<IntGrowingSoftItem>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

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
