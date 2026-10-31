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
}
