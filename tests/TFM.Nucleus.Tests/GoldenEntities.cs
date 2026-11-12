namespace TFM.Nucleus.Tests
{
    /// <summary>One column of every type the providers map, for the column type goldens.</summary>
    public class TestAllTypesWidget : CoreObject<TestAllTypesWidget>
    {
        [DatabaseColumn]
        public string? Ansi { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(DataType.Unicode, 64)]
        public string? UnicodeSized { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(DataType.Unicode, -1)]
        public string? UnicodeMax { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public int Count { get => GetValue<int>(); set => SetValue(value); }

        [DatabaseColumn]
        public long Big { get => GetValue<long>(); set => SetValue(value); }

        [DatabaseColumn]
        public double Ratio { get => GetValue<double>(); set => SetValue(value); }

        [DatabaseColumn]
        public bool Flag { get => GetValue<bool>(); set => SetValue(value); }

        [DatabaseColumn]
        public DateTime When { get => GetValue<DateTime>(); set => SetValue(value); }

        [DatabaseColumn(DataType.Date)]
        public DateTime Day { get => GetValue<DateTime>(); set => SetValue(value); }

        [DatabaseColumn]
        public DateTimeOffset At { get => GetValue<DateTimeOffset>(); set => SetValue(value); }

        [DatabaseColumn]
        public Guid Reference { get => GetValue<Guid>(); set => SetValue(value); }

        [DatabaseColumn]
        public byte[]? Blob { get => GetValue<byte[]>(); set => SetValue(value); }

        [DatabaseColumn(Scale = 2)]
        public decimal Money { get => GetValue<decimal>(); set => SetValue(value); }

        [DatabaseColumn]
        public WidgetKind Kind { get => GetValue<WidgetKind>(); set => SetValue(value); }

        [DatabaseColumn]
        public TestWidget? Linked { get => GetValue<TestWidget>(); set => SetValue(value); }

        [DatabaseColumn(FullTextSearch = true)]
        public string? Searchable { get => GetValue<string>(); set => SetValue(value); }
    }

    [Deletable(DeleteBehavior.Soft)]
    public class TestSoftParent : CoreObject<TestSoftParent>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        public NucleusChildCollection<TestSoftChild, TestSoftParent> Children => GetChildCollection<TestSoftChild, TestSoftParent>();
    }

    [Deletable(DeleteBehavior.Soft)]
    public class TestSoftChild : CoreChildObject<TestSoftChild, TestSoftParent>
    {
        [DatabaseColumn]
        public int Sequence { get => GetValue<int>(); set => SetValue(value); }
    }

    /// <summary>Columns named apart from their properties, by both spellings of the attribute.</summary>
    public class TestRenamedWidget : CoreObject<TestRenamedWidget>
    {
        [DatabaseColumn(ColumnName = "title_text", Indexed = true)]
        public string? Title { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn("item_code", Unique = true)]
        public string? Code { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public int Quantity { get => GetValue<int>(); set => SetValue(value); }

        public NucleusChildCollection<TestRenamedChild, TestRenamedWidget> Children => GetChildCollection<TestRenamedChild, TestRenamedWidget>();
    }

    public class TestRenamedChild : CoreChildObject<TestRenamedChild, TestRenamedWidget>
    {
        [DatabaseColumn(ColumnName = "seq")]
        public int Sequence { get => GetValue<int>(); set => SetValue(value); }
    }

    /// <summary>
    /// Columns a table gains in place, for the add-column goldens and the update plan tests: a
    /// keyword name with an index, a max-length string, a decimal, a nullable int and an
    /// externally stored binary. Soft-deletable, so its system columns can be missing too.
    /// </summary>
    [Deletable(DeleteBehavior.Soft)]
    public class TestGrowingWidget : CoreObject<TestGrowingWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(Indexed = true)]
        public string? Key { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(DataType.Unicode, -1)]
        public string? Notes { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(Scale = 2)]
        public decimal Price { get => GetValue<decimal>(); set => SetValue(value); }

        [DatabaseColumn]
        public int? Extra { get => GetValue<int?>(); set => SetValue(value); }

        [DatabaseColumn(ExternalStorage = true)]
        public byte[]? Blob { get => GetValue<byte[]>(); set => SetValue(value); }
    }

    /// <summary>A column the server sets on every write and the entity reads back.</summary>
    public class TestServerValueWidget : CoreObject<TestServerValueWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(SetValueOverride = eServerFunction.CurrentDateTimeOffset, ReturnOutputToProperty = true)]
        public DateTimeOffset StampedAt => GetValue<DateTimeOffset>();
    }
}
