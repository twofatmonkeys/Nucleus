namespace TFM.Nucleus.IntegrationTests.Benchmarks
{
    // The benchmarks' own entities, apart from the test entities, so a test that changes its
    // entities never moves a benchmark. IntBenchWidget (in IntegrationEntities.cs) is the flat
    // entity; it is older than these and shared with a few tests.

    /// <summary>A parent with one level of children, deleted with its lines.</summary>
    [Deletable]
    public class IntBenchOrder : CoreObject<IntBenchOrder>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        public NucleusChildCollection<IntBenchOrderLine, IntBenchOrder> Lines => GetChildCollection<IntBenchOrderLine, IntBenchOrder>();
    }

    public class IntBenchOrderLine : CoreChildObject<IntBenchOrderLine, IntBenchOrder>
    {
        [DatabaseColumn]
        public int Sequence { get => GetValue<int>(); set => SetValue(value); }

        [DatabaseColumn]
        public int Quantity { get => GetValue<int>(); set => SetValue(value); }
    }

    /// <summary>A parent whose collection is lazy: left out of loads until read or included.</summary>
    [Deletable]
    public class IntBenchLazyOwner : CoreObject<IntBenchLazyOwner>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [RetrieveOnDemand]
        public NucleusChildCollection<IntBenchLazyItem, IntBenchLazyOwner> Items => GetChildCollection<IntBenchLazyItem, IntBenchLazyOwner>();
    }

    public class IntBenchLazyItem : CoreChildObject<IntBenchLazyItem, IntBenchLazyOwner>
    {
        [DatabaseColumn]
        public int Sequence { get => GetValue<int>(); set => SetValue(value); }
    }

    /// <summary>Three levels: an order, its lines, and each line's notes.</summary>
    [Deletable]
    public class IntBenchNestOrder : CoreObject<IntBenchNestOrder>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        public NucleusChildCollection<IntBenchNestLine, IntBenchNestOrder> Lines => GetChildCollection<IntBenchNestLine, IntBenchNestOrder>();
    }

    public class IntBenchNestLine : CoreChildObject<IntBenchNestLine, IntBenchNestOrder>
    {
        [DatabaseColumn]
        public int Sequence { get => GetValue<int>(); set => SetValue(value); }

        [DatabaseColumn]
        public int Quantity { get => GetValue<int>(); set => SetValue(value); }

        public NucleusChildCollection<IntBenchNestNote, IntBenchNestLine> Notes => GetChildCollection<IntBenchNestNote, IntBenchNestLine>();
    }

    public class IntBenchNestNote : CoreChildObject<IntBenchNestNote, IntBenchNestLine>
    {
        [DatabaseColumn]
        public string? Text { get => GetValue<string>(); set => SetValue(value); }
    }
}
