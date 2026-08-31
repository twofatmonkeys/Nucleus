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
