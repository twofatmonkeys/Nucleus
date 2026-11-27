using NpgsqlTypes;
using System;
using TFM.Nucleus.Data.Schema;

namespace TFM.Nucleus.Data.PostgreSQL
{
    /// <summary>
    /// Maps a neutral column to its PostgreSQL type, and converts values on their way in and
    /// out. PostgreSQL types every parameter, so a value is bound as the type its column stores:
    /// a DateTimeOffset as timestamptz in UTC (the only offset PostgreSQL keeps), a DateTime as a
    /// timestamp without a kind, an enum as its integer, and JSON as jsonb.
    /// </summary>
    internal static class PostgresTypes
    {
        /// <summary>The column's type in a CREATE TABLE.</summary>
        public static string TypeName(ColumnSchema column) => column.Intent switch
        {
            ColumnTypeIntent.AnsiString or ColumnTypeIntent.UnicodeString => column.IsMax ? "text" : $"varchar({column.DataLength})",
            ColumnTypeIntent.Int32 => "integer",
            ColumnTypeIntent.Int64 => "bigint",
            ColumnTypeIntent.Double => "double precision",
            ColumnTypeIntent.Boolean => "boolean",
            ColumnTypeIntent.DateTime => "timestamp",
            ColumnTypeIntent.DateTimeOffset => "timestamptz",
            ColumnTypeIntent.Date => "date",
            ColumnTypeIntent.Guid => "uuid",
            ColumnTypeIntent.Binary => "bytea",
            ColumnTypeIntent.Decimal => $"numeric({column.Precision},{column.Scale})",
            ColumnTypeIntent.Json => "jsonb",
            _ => throw new NotSupportedException($"Column type {column.Intent} is not supported by the PostgreSQL provider."),
        };

        /// <summary>The type as information_schema.columns.data_type reports it.</summary>
        public static string CatalogTypeName(ColumnSchema column) => column.Intent switch
        {
            ColumnTypeIntent.AnsiString or ColumnTypeIntent.UnicodeString => column.IsMax ? "text" : "character varying",
            ColumnTypeIntent.Int32 => "integer",
            ColumnTypeIntent.Int64 => "bigint",
            ColumnTypeIntent.Double => "double precision",
            ColumnTypeIntent.Boolean => "boolean",
            ColumnTypeIntent.DateTime => "timestamp without time zone",
            ColumnTypeIntent.DateTimeOffset => "timestamp with time zone",
            ColumnTypeIntent.Date => "date",
            ColumnTypeIntent.Guid => "uuid",
            ColumnTypeIntent.Binary => "bytea",
            ColumnTypeIntent.Decimal => "numeric",
            ColumnTypeIntent.Json => "jsonb",
            _ => throw new NotSupportedException($"Column type {column.Intent} is not supported by the PostgreSQL provider."),
        };

        /// <summary>True when a column found in the catalog has the type and size the schema needs.</summary>
        public static bool Matches(ColumnSchema column, CatalogColumn existing)
        {
            if (!string.Equals(existing.TypeName, CatalogTypeName(column), StringComparison.Ordinal))
                return false;
            return column.Intent switch
            {
                ColumnTypeIntent.AnsiString or ColumnTypeIntent.UnicodeString when !column.IsMax => existing.DataLength == column.DataLength,
                ColumnTypeIntent.Decimal => existing.Precision == column.Precision && existing.Scale == column.Scale,
                _ => true,
            };
        }

        /// <summary>The driver type a column's values are written as.</summary>
        public static NpgsqlDbType DbType(ColumnSchema column) => column.Intent switch
        {
            ColumnTypeIntent.AnsiString or ColumnTypeIntent.UnicodeString => column.IsMax ? NpgsqlDbType.Text : NpgsqlDbType.Varchar,
            ColumnTypeIntent.Int32 => NpgsqlDbType.Integer,
            ColumnTypeIntent.Int64 => NpgsqlDbType.Bigint,
            ColumnTypeIntent.Double => NpgsqlDbType.Double,
            ColumnTypeIntent.Boolean => NpgsqlDbType.Boolean,
            ColumnTypeIntent.DateTime => NpgsqlDbType.Timestamp,
            ColumnTypeIntent.DateTimeOffset => NpgsqlDbType.TimestampTz,
            ColumnTypeIntent.Date => NpgsqlDbType.Date,
            ColumnTypeIntent.Guid => NpgsqlDbType.Uuid,
            ColumnTypeIntent.Binary => NpgsqlDbType.Bytea,
            ColumnTypeIntent.Decimal => NpgsqlDbType.Numeric,
            ColumnTypeIntent.Json => NpgsqlDbType.Jsonb,
            _ => throw new NotSupportedException($"Column type {column.Intent} is not supported by the PostgreSQL provider."),
        };

        /// <summary>
        /// A value as the driver should send it, with the type to send it as when the value alone
        /// would mislead the driver: the column's type when the column is known, otherwise the
        /// type a .NET value means on its own (a UTC DateTime is an instant, any other a wall time).
        /// </summary>
        public static object? ToDb(object? value, ColumnSchema? column, out NpgsqlDbType? type)
        {
            type = column != null ? DbType(column) : null;
            switch (value)
            {
                case null:
                case DBNull:
                    return value;
                case DateTimeOffset dto:
                    type ??= NpgsqlDbType.TimestampTz;
                    return type == NpgsqlDbType.TimestampTz ? dto.UtcDateTime : DateTime.SpecifyKind(dto.DateTime, DateTimeKind.Unspecified);
                case DateTime dt when type is NpgsqlDbType.Timestamp or NpgsqlDbType.Date:
                    return DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
                case DateTime dt when type is NpgsqlDbType.TimestampTz:
                    return dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
                case DateTime dt:
                    type = dt.Kind == DateTimeKind.Utc ? NpgsqlDbType.TimestampTz : NpgsqlDbType.Timestamp;
                    return dt;
                case Enum e:
                    type ??= NpgsqlDbType.Integer;
                    return Convert.ToInt32(e);
                default:
                    return value;
            }
        }

        /// <summary>
        /// Whether a list of <paramref name="elementType"/> is bound as one array. The types here
        /// each map to one array type whatever the values, which a date does not (its kind picks
        /// timestamp or timestamptz) and a byte does not (a byte array is a bytea).
        /// </summary>
        public static bool HasArray(Type elementType) =>
            elementType.IsEnum || elementType == typeof(Guid) || elementType == typeof(string) || elementType == typeof(int) ||
            elementType == typeof(long) || elementType == typeof(short) || elementType == typeof(decimal) ||
            elementType == typeof(double) || elementType == typeof(bool);

        /// <summary>
        /// The values of a list as one typed array, each converted as <see cref="ToDb"/> converts
        /// a single value, so the driver sends the array of that type (a Guid array as uuid[]).
        /// </summary>
        public static Array ToDbArray(IReadOnlyList<object> values, ColumnSchema? column)
        {
            var first = ToDb(values[0], column, out _)!;
            var array = Array.CreateInstance(first.GetType(), values.Count);
            array.SetValue(first, 0);
            for (var i = 1; i < values.Count; i++)
                array.SetValue(ToDb(values[i], column, out _), i);
            return array;
        }

        /// <summary>
        /// A value read back as the property expects it: a timestamptz arrives as a UTC DateTime
        /// and becomes a DateTimeOffset at offset zero.
        /// </summary>
        public static object? FromDb(object? value, Type propertyType)
        {
            if (value is DateTime dt && (Nullable.GetUnderlyingType(propertyType) ?? propertyType) == typeof(DateTimeOffset))
                return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
            return value;
        }
    }
}
