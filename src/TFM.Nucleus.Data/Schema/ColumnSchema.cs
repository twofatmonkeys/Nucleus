using System;

namespace TFM.Nucleus.Data.Schema
{
    /// <summary>
    /// One column of a <see cref="TableSchema"/>, built from a [DatabaseColumn] property: its
    /// name, what it stores (<see cref="Intent"/>), its size facets and its index. Providers map
    /// the intent and facets to their own types; the defaults below are shared so every provider
    /// sizes a column the same way.
    /// </summary>
    public class ColumnSchema : IDataColumnSchema
    {
        /// <summary>Length meaning "no limit".</summary>
        public const int MaxLength = -1;

        /// <summary>
        /// The physical column: [DatabaseColumn]'s ColumnName, which defaults to the property's
        /// name. Every statement names the column by it; values bind to the entity by
        /// <see cref="PropertyName"/>.
        /// </summary>
        public readonly string ColumnName;
        public readonly IPropertySchema PropertySchema;

        /// <summary>The entity property the column stores.</summary>
        public string PropertyName => PropertySchema.Name;
        public readonly ColumnTypeIntent Intent;

        /// <summary>The CLR type a value of this column is written as.</summary>
        public Type ClrType { get; }

        /// <summary>
        /// The declared length, or the intent's default: 255 for a string, 510 for a Unicode
        /// string, 7 (fractional second digits) for a date and time, unlimited for binary, and
        /// 20 for a decimal, where it is precision plus scale. Null for a type without a length.
        /// </summary>
        public readonly int? DataLength;

        /// <summary>Digits before the point of a decimal: <see cref="DataLength"/> minus <see cref="Scale"/>.</summary>
        public readonly int? Precision;

        /// <summary>Digits after the point of a decimal; 5 unless declared.</summary>
        public readonly int? Scale;

        public readonly bool IsNullable = true;

        /// <summary>The value is kept outside the row by the engine where it supports that.</summary>
        public readonly bool ExternalStorage;

        public readonly bool PrimaryKey;

        /// <summary>The column's text is indexed for full-text search where the provider supports it.</summary>
        public readonly bool FullTextSearch;

        public readonly bool Unique;

        /// <summary>The column gets an index of its own.</summary>
        public readonly bool Indexed;

        public readonly bool IndexSortDescending;

        /// <summary>True when the column gets an index of its own (plain or unique).</summary>
        public bool HasColumnIndex => Indexed || Unique;

        /// <summary>The length is unlimited.</summary>
        public bool IsMax => DataLength == MaxLength;

        /// <summary>A value must be converted on its way to the database (enum, linked object, string value type).</summary>
        public bool RequiresCast { get; }

        public bool ExistsInDatabase { get; internal set; } = false;
        public bool SchemaMatched { get; internal set; } = false;
        public bool IsCurrent => ExistsInDatabase && SchemaMatched;

        #region IDataColumnSchema
        string IDataColumnSchema.ColumnName => ColumnName;
        bool IDataColumnSchema.IsNullable => IsNullable;
        Type IDataColumnSchema.DataType => PropertySchema.Type;
        IPropertySchema IDataColumnSchema.PropertySchema => PropertySchema;
        #endregion

        public ColumnSchema(IPropertySchema propertySchema, IObjectSchema owner)
        {
            var attr = propertySchema.DatabaseColumnAttribute ?? throw new Exception("PropertySchema does not have a DatabaseColumnAttribute defined.");
            PropertySchema = propertySchema;
            ColumnName = string.IsNullOrWhiteSpace(attr.ColumnName) ? propertySchema.Name : attr.ColumnName;
            Intent = IntentOf(propertySchema);
            DataLength = attr.DataLength ?? DefaultLength(Intent);
            Scale = attr.DecimalPlaces ?? DefaultScale(Intent);
            Precision = DataLength - Scale;
            if (DataLength is not null and not MaxLength and <= 0)
                throw new Exception($"Unable to determine the data size of column '{ColumnName}'.");
            ExternalStorage = attr.ExternalStorage;
            PrimaryKey = attr.PrimaryKey;
            Unique = attr.Unique;
            FullTextSearch = attr.FullTextSearch;
            Indexed = attr.Indexed;
            ClrType = GetClrType(propertySchema);
            RequiresCast = WillRequireCast(propertySchema);
            IndexSortDescending = attr.IndexDescending;
            // A max-length or externally stored value cannot be an index key on every engine.
            // Fail at schema build with a readable message instead of a raw error inside Update.
            if (HasColumnIndex && (IsMax || ExternalStorage))
                throw new InvalidOperationException(
                    $"Property '{propertySchema.Name}' on '{owner.FullName}' cannot carry Indexed or Unique: " +
                    "a max-length or externally stored column cannot be an index key.");
        }

        public object? GetClrValue(object? value)
        {
            if (!RequiresCast)
                return value;
            if (value == null)
                return null;

            var type = PropertySchema.Type;
            if (type.IsEnum)
                return Enum.ToObject(type, value);
            if (ClrType == typeof(Guid) && value is ICoreObject icoreObj)
                return icoreObj.ID;
            if (ClrType == typeof(string) && value is IDatabaseStringValue dbStr)
                return dbStr.DatabaseStringValue;
            return Convert.ChangeType(value, Nullable.GetUnderlyingType(type) ?? type);
        }

        private static Type GetClrType(IPropertySchema propertySchema)
        {
            var type = propertySchema.Type;
            if (type.IsEnum) return typeof(int);
            if (typeof(ICoreObject).IsAssignableFrom(type)) return typeof(Guid);
            if (typeof(IDatabaseStringValue).IsAssignableFrom(type)) return typeof(string);

            return Nullable.GetUnderlyingType(type) ?? type;
        }

        private static bool WillRequireCast(IPropertySchema propertySchema)
        {
            var propType = propertySchema.Type;
            if (propType.IsEnum)
                return propType != typeof(int);
            if (typeof(ICoreObject).IsAssignableFrom(propType))
                return propType != typeof(Guid);
            if (typeof(IDatabaseStringValue).IsAssignableFrom(propType))
                return propType != typeof(string);
            return false;
        }

        /// <summary>What a property stores: its declared DataType, or else its CLR type.</summary>
        public static ColumnTypeIntent IntentOf(IPropertySchema propertySchema)
        {
            var dt = propertySchema.DatabaseColumnAttribute!.DataType;
            return dt switch
            {
                DataType.NotSpecified => IntentOf(propertySchema.Type),
                DataType.Date => ColumnTypeIntent.Date,
                DataType.Integer => ColumnTypeIntent.Int32,
                DataType.Unicode => ColumnTypeIntent.UnicodeString,
                _ => throw new Exception("Unknown property type '" + dt.ToString() + "' unable to select a  matching sql datatype."),
            };
        }

        private static ColumnTypeIntent IntentOf(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(string))
                return ColumnTypeIntent.AnsiString;
            else if (type == typeof(int))
                return ColumnTypeIntent.Int32;
            else if (type == typeof(double))
                return ColumnTypeIntent.Double;
            else if (type == typeof(bool))
                return ColumnTypeIntent.Boolean;
            else if (type == typeof(DateTime))
                return ColumnTypeIntent.DateTime;
            else if (type == typeof(DateTimeOffset))
                return ColumnTypeIntent.DateTimeOffset;
            else if (type == typeof(Guid))
                return ColumnTypeIntent.Guid;
            else if (type == typeof(byte[]))
                return ColumnTypeIntent.Binary;
            else if (type == typeof(decimal))
                return ColumnTypeIntent.Decimal;
            else if (type == typeof(long))
                return ColumnTypeIntent.Int64;
            else if (type.Name.Contains("TFMJsonString"))
                return ColumnTypeIntent.Json;
            else if (type.IsEnum)
                return ColumnTypeIntent.Int32;
            else if (typeof(ICoreObject).IsAssignableFrom(type))
                return ColumnTypeIntent.Guid;
            else if (typeof(IDatabaseStringValue).IsAssignableFrom(type))
                return ColumnTypeIntent.AnsiString;
            throw new Exception("Unknown property type '" + type.Namespace + "." + type.Name + "' unable to select a  matching sql datatype.");
        }

        private static int? DefaultLength(ColumnTypeIntent intent) => intent switch
        {
            ColumnTypeIntent.AnsiString => 255,
            ColumnTypeIntent.UnicodeString => 510,
            ColumnTypeIntent.DateTimeOffset => 7,
            ColumnTypeIntent.DateTime => 7,
            ColumnTypeIntent.Binary => MaxLength,
            ColumnTypeIntent.Decimal => 20,
            _ => null,
        };

        private static int? DefaultScale(ColumnTypeIntent intent) =>
            intent == ColumnTypeIntent.Decimal ? 5 : null;

        public override string ToString()
        {
            return ColumnName ?? "null";
        }
    }
}
