using TFM.Nucleus.Data;

namespace TFM.Nucleus
{
    public class StreamingRow
    {
        public readonly string[] ColumnNames;
        public readonly object?[] Values;

        internal StreamingRow(IDataTableSchema schema)
        {
            ColumnNames = [.. schema.Columns.Select(x => x.ColumnName)];
            Values = new object?[ColumnNames.Length];
        }

        public StreamingRow(string[] columnNames)
        {
            ColumnNames = columnNames;
            Values = new object?[columnNames.Length];
        }

        public object? this[int i] => Values[i];

        public object? this[string columnName]
        {
            get
            {
                var index = Array.IndexOf(ColumnNames, columnName);
                return index >= 0 ? Values[index] : throw new ArgumentException($"Column '{columnName}' not found.");
            }
        }
    }
}
