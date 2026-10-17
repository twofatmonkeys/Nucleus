namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// Minimal entity used by schema, binding and SQL-generation tests. Not database-backed.
    /// </summary>
    public class TestWidget : CoreObject<TestWidget>
    {
        [DatabaseColumn]
        public string? Name
        {
            get => GetValue<string>();
            set => SetValue(value);
        }

        [DatabaseColumn]
        public int Quantity
        {
            get => GetValue<int>();
            set => SetValue(value);
        }
    }
}

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// Fixture for the column index tests: one Unique column, one Unique + NonClusteredIndex + DESC
    /// column, and the inherited Timestamp column which carries a plain descending index.
    /// </summary>
    public class TestUniqueWidget : CoreObject<TestUniqueWidget>
    {
        /// <summary>A decimal with an explicit scale, for the scale test.</summary>
        [DatabaseColumn(Scale = 6)]
        public decimal Weight
        {
            get => GetValue<decimal>();
            set => SetValue(value);
        }

        /// <summary>A decimal on the provider's default scale.</summary>
        [DatabaseColumn]
        public decimal Amount
        {
            get => GetValue<decimal>();
            set => SetValue(value);
        }

        [DatabaseColumn(Unique = true)]
        public string? Code
        {
            get => GetValue<string>();
            set => SetValue(value);
        }

        [DatabaseColumn(Unique = true, NonClusteredIndex = true, IndexSortDESC = true)]
        public string? Sku
        {
            get => GetValue<string>();
            set => SetValue(value);
        }
    }

    /// <summary>
    /// Unique on a max-length column, which SQL Server cannot key. Must fail at schema build.
    /// A byte[] column defaults to varbinary(max); the int? constructor cannot be used in attribute syntax.
    /// </summary>
    public class TestUniqueMaxWidget : CoreObject<TestUniqueMaxWidget>
    {
        [DatabaseColumn(Unique = true)]
        public byte[]? Body
        {
            get => GetValue<byte[]>();
            set => SetValue(value);
        }
    }

    /// <summary>A second PrimaryKey next to the inherited ID. Must fail at schema build.</summary>
    public class TestDoublePrimaryKeyWidget : CoreObject<TestDoublePrimaryKeyWidget>
    {
        [DatabaseColumn(PrimaryKey = true)]
        public string? Code
        {
            get => GetValue<string>();
            set => SetValue(value);
        }
    }
}
