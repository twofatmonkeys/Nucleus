using System.Collections;
using System.Collections.ObjectModel;
using System.Data.Common;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// In-memory DbDataReader over a column list and row array, for testing materialization
    /// without a database.
    /// </summary>
    public sealed class FakeDataReader(string[] columns, object?[][] rows) : DbDataReader, IDbColumnSchemaGenerator
    {
        private sealed class FakeColumn : DbColumn
        {
            public FakeColumn(string name, Type type)
            {
                ColumnName = name;
                DataType = type;
            }
        }

        private int _index = -1;

        public override int FieldCount => columns.Length;
        public override bool HasRows => rows.Length > 0;
        public override bool IsClosed => false;
        public override int Depth => 0;
        public override int RecordsAffected => -1;

        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));

        public override bool Read() => ++_index < rows.Length;
        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
        public override bool NextResult() => false;

        public override string GetName(int ordinal) => columns[ordinal];
        public override int GetOrdinal(string name) => Array.IndexOf(columns, name);
        public override object GetValue(int ordinal) => rows[_index][ordinal]!;
        public override bool IsDBNull(int ordinal) => rows[_index][ordinal] is null or DBNull;
        public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) =>
            Task.FromResult(IsDBNull(ordinal));

        public override int GetValues(object[] values)
        {
            var row = rows[_index];
            var count = Math.Min(values.Length, row.Length);
            for (int i = 0; i < count; i++)
                values[i] = row[i] ?? DBNull.Value;
            return count;
        }

        public override Type GetFieldType(int ordinal) =>
            rows.Select(r => r[ordinal]).FirstOrDefault(v => v is not null)?.GetType() ?? typeof(object);

        public ReadOnlyCollection<DbColumn> GetColumnSchema() =>
            new([.. columns.Select((c, i) => (DbColumn)new FakeColumn(c, GetFieldType(i)))]);

        public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;
        public override IEnumerator GetEnumerator() => rows.GetEnumerator();

        public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
        public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override char GetChar(int ordinal) => (char)GetValue(ordinal);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
        public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
        public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
        public override float GetFloat(int ordinal) => (float)GetValue(ordinal);
        public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
        public override short GetInt16(int ordinal) => (short)GetValue(ordinal);
        public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
        public override long GetInt64(int ordinal) => (long)GetValue(ordinal);
        public override string GetString(int ordinal) => (string)GetValue(ordinal);
    }
}
