using System;
using System.Globalization;
using TFM.Nucleus.Data.Schema;

namespace TFM.Nucleus.Data.SQLite
{
    /// <summary>
    /// The one place values are turned into what SQLite stores and back. SQLite keeps only
    /// integers, reals, text and blobs, so everything else has a fixed text form, written the same
    /// way by a bound parameter and by the server functions Nucleus registers:
    /// <list type="bullet">
    /// <item>a Guid as 36 lower-case characters, so one ID always has one spelling;</item>
    /// <item>a DateTime as <c>yyyy-MM-dd HH:mm:ss.fffffff</c>, its kind dropped (a wall time, as on PostgreSQL);</item>
    /// <item>a DateTimeOffset in UTC as <c>yyyy-MM-dd HH:mm:ss.fffffff+00:00</c> (the offset is not kept, as on PostgreSQL);</item>
    /// <item>a date as <c>yyyy-MM-dd</c>;</item>
    /// <item>a bool as 0 or 1, an enum as its integer, and a decimal as a real.</item>
    /// </list>
    /// The date and time forms are fixed width, so comparing and ordering their text compares and
    /// orders the instants. Reads convert back to the type the column's property stores.
    /// </summary>
    internal static class SqliteValues
    {
        private const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss.fffffff";
        private const string DateFormat = "yyyy-MM-dd";
        private const string UtcSuffix = "+00:00";

        // What a read accepts: Nucleus's own forms first, then the ISO forms other tools write.
        private static readonly string[] DateTimeReadFormats =
            ["yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm", DateFormat];

        private static readonly string[] DateTimeOffsetReadFormats =
            ["yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
             "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"];

        public static string FormatGuid(Guid value) => value.ToString("D");

        public static string FormatDateTime(DateTime value) => value.ToString(DateTimeFormat, CultureInfo.InvariantCulture);

        public static string FormatDate(DateTime value) => value.ToString(DateFormat, CultureInfo.InvariantCulture);

        public static string FormatDateTimeOffset(DateTimeOffset value) =>
            value.UtcDateTime.ToString(DateTimeFormat, CultureInfo.InvariantCulture) + UtcSuffix;

        /// <summary>
        /// A value as it is bound: in the form its column stores when the column is known, and
        /// otherwise in the form a value of its .NET type is stored in.
        /// </summary>
        public static object? ToDb(object? value, ColumnSchema? column)
        {
            switch (value)
            {
                case null:
                    return DBNull.Value;
                case DBNull:
                    return value;
                case Guid g:
                    return FormatGuid(g);
                case ICoreObject entity:
                    return FormatGuid(entity.ID);
                case IDatabaseStringValue text:
                    return text.DatabaseStringValue ?? (object)DBNull.Value;
                case DateTimeOffset dto:
                    return column?.Intent switch
                    {
                        ColumnTypeIntent.DateTime => FormatDateTime(dto.DateTime),
                        ColumnTypeIntent.Date => FormatDate(dto.Date),
                        _ => FormatDateTimeOffset(dto),
                    };
                case DateTime dt:
                    return column?.Intent switch
                    {
                        // A wall time against an instant column means the local time it reads as, as on PostgreSQL.
                        ColumnTypeIntent.DateTimeOffset => FormatDateTimeOffset(dt.Kind == DateTimeKind.Utc ? new DateTimeOffset(dt) : new DateTimeOffset(dt.ToUniversalTime())),
                        ColumnTypeIntent.Date => FormatDate(dt),
                        _ => FormatDateTime(dt),
                    };
                case bool b:
                    return b ? 1L : 0L;
                case Enum e:
                    return Convert.ToInt64(e, CultureInfo.InvariantCulture);
                case decimal m:
                    return (double)m;
                default:
                    return value;
            }
        }

        /// <summary>
        /// A value read back as the column's property stores it. SQLite hands back a long, a
        /// double, a string or a byte array; anything else is converted here. A column of another
        /// assembly's schema converts by its property type.
        /// </summary>
        public static object? FromDb(object? value, IDataColumnSchema? column)
        {
            if (value is null or DBNull || column == null)
                return value;
            var schema = column as ColumnSchema;
            var target = schema?.ClrType ?? StorageType(column.DataType);
            return FromDb(value, target, schema?.Scale);
        }

        /// <summary>A value read back as <paramref name="target"/>.</summary>
        public static object? FromDb(object value, Type target, int? scale = null)
        {
            // An enum is read as its integer, which the entity widens into the enum, as on every engine.
            // A nullable enum property stores as the enum type itself, so this is where it narrows.
            if (target.IsEnum)
                target = Enum.GetUnderlyingType(target);
            if (target.IsInstanceOfType(value))
                return value;
            try
            {
                if (target == typeof(Guid))
                    return value switch
                    {
                        string s => Guid.Parse(s),
                        byte[] { Length: 16 } bytes => new Guid(bytes),
                        _ => Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!),
                    };
                if (target == typeof(DateTimeOffset))
                    return ReadDateTimeOffset(value);
                if (target == typeof(DateTime))
                    return ReadDateTime(value);
                if (target == typeof(bool))
                    return Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
                if (target == typeof(decimal))
                {
                    var number = value is double d ? (decimal)d : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    return scale is { } digits and >= 0 and <= 28 ? Math.Round(number, digits, MidpointRounding.ToEven) : number;
                }
                if (target == typeof(string))
                    return value is byte[] text ? System.Text.Encoding.UTF8.GetString(text) : Convert.ToString(value, CultureInfo.InvariantCulture);
                if (target == typeof(byte[]))
                    return value is string s ? System.Text.Encoding.UTF8.GetBytes(s) : value;
                return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                throw new FormatException($"SQLite returned '{value}' ({value.GetType().Name}), which cannot be read as {target.Name}.", ex);
            }
        }

        private static DateTime ReadDateTime(object value) => value switch
        {
            string s => DateTime.ParseExact(s, DateTimeReadFormats, CultureInfo.InvariantCulture, DateTimeStyles.None),
            // A Julian day number or Unix time written by another tool is not a form Nucleus writes.
            _ => throw new FormatException($"'{value}' is not a date and time Nucleus wrote."),
        };

        private static DateTimeOffset ReadDateTimeOffset(object value) => value switch
        {
            string s => DateTimeOffset.ParseExact(s, DateTimeOffsetReadFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime(),
            _ => throw new FormatException($"'{value}' is not a date and time with an offset Nucleus wrote."),
        };

        /// <summary>What a property of <paramref name="propertyType"/> is stored as, as <see cref="ColumnSchema.ClrType"/> decides it.</summary>
        private static Type StorageType(Type propertyType)
        {
            var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            if (type.IsEnum)
                return typeof(int);
            if (typeof(ICoreObject).IsAssignableFrom(type))
                return typeof(Guid);
            if (typeof(IDatabaseStringValue).IsAssignableFrom(type))
                return typeof(string);
            return type;
        }
    }
}
