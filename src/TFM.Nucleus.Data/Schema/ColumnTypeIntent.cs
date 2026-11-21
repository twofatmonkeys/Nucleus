namespace TFM.Nucleus.Data.Schema
{
    /// <summary>
    /// What a column stores, independent of any engine. Each provider maps an intent to its own
    /// type, together with the column's <see cref="ColumnSchema.DataLength"/>,
    /// <see cref="ColumnSchema.Precision"/> and <see cref="ColumnSchema.Scale"/>.
    /// </summary>
    public enum ColumnTypeIntent
    {
        /// <summary>A string declared without <see cref="ColumnDataType.Unicode"/>.</summary>
        AnsiString,
        /// <summary>A string declared with <see cref="ColumnDataType.Unicode"/>.</summary>
        UnicodeString,
        Int32,
        Int64,
        Double,
        Boolean,
        DateTime,
        DateTimeOffset,
        /// <summary>A date without a time of day, declared with <see cref="ColumnDataType.Date"/>.</summary>
        Date,
        Guid,
        Binary,
        Decimal,
        Json,
    }
}
