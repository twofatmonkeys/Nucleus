using System;
using System.Text;
using TFM.Nucleus.Data.Schema;

namespace TFM.Nucleus.Data.SQL
{
    /// <summary>The SQL Server types. DDL prints the member name, so the names must not change.</summary>
    internal enum ESqlDataType
    {
        TinyInt,
        SmallInt,
        Int,
        BigInt,
        Bit,
        Decimal,
        Numeric,
        Money,
        Smallmoney,
        Float,
        Real,
        Date,
        Time,
        DateTime2,
        DateTimeOffset,
        DateTime,
        SmallDateTime,
        Char,
        VarChar,
        Text,
        NChar,
        NVarChar,
        NText,
        Binary,
        VarBinary,
        Image,
        Cursor,
        Geography,
        Geometry,
        HierarchyId,
        Json,
        Vector,
        RowVersion,
        SqlVariant,
        Table,
        UniqueIdentifier,
        Xml
    }

    /// <summary>Maps a neutral column to its SQL Server type and writes the type in DDL.</summary>
    internal static class SqlServerTypeMapper
    {
        public static ESqlDataType SqlType(ColumnSchema column) => column.Intent switch
        {
            ColumnTypeIntent.AnsiString => ESqlDataType.VarChar,
            ColumnTypeIntent.UnicodeString => ESqlDataType.NVarChar,
            ColumnTypeIntent.Int32 => ESqlDataType.Int,
            ColumnTypeIntent.Int64 => ESqlDataType.BigInt,
            ColumnTypeIntent.Double => ESqlDataType.Float,
            ColumnTypeIntent.Boolean => ESqlDataType.Bit,
            ColumnTypeIntent.DateTime => ESqlDataType.DateTime2,
            ColumnTypeIntent.DateTimeOffset => ESqlDataType.DateTimeOffset,
            ColumnTypeIntent.Date => ESqlDataType.Date,
            ColumnTypeIntent.Guid => ESqlDataType.UniqueIdentifier,
            ColumnTypeIntent.Binary => ESqlDataType.VarBinary,
            ColumnTypeIntent.Decimal => ESqlDataType.Decimal,
            ColumnTypeIntent.Json => ESqlDataType.Json,
            _ => throw new NotSupportedException($"Column type {column.Intent} is not supported by the SQL Server provider."),
        };

        /// <summary>The size in parentheses: max, the length, or nothing for a type without one.</summary>
        public static string DataSize(ColumnSchema column) => column.DataLength switch
        {
            null => "",
            ColumnSchema.MaxLength => "max",
            var length => length.ToString()!,
        };

        public static bool HasSize(ESqlDataType type) =>
            type is ESqlDataType.VarChar or ESqlDataType.NVarChar or ESqlDataType.Decimal or ESqlDataType.NChar
                or ESqlDataType.Char or ESqlDataType.Numeric or ESqlDataType.VarBinary;

        public static bool HasPrecisionScale(ESqlDataType type) =>
            type is ESqlDataType.Decimal or ESqlDataType.Numeric;

        /// <summary>The column's type and nullability in a CREATE TABLE, followed by its separator.</summary>
        public static string Constructor(ColumnSchema column) => TypeDefinition(column) + ", ";

        /// <summary>
        /// The column's type and nullability, as CREATE TABLE and ALTER TABLE ADD both write it.
        /// Every column is created NULL.
        /// </summary>
        public static string TypeDefinition(ColumnSchema column)
        {
            var type = SqlType(column);
            var constr = $"[{type}]";
            if (HasPrecisionScale(type))
            {
                constr += $"({column.Precision},{column.Scale})";
            }
            else if (!string.IsNullOrEmpty(DataSize(column)))
                constr += $"({DataSize(column)})";
            if (IsFileStream(column))
                constr += " FILESTREAM ";
            constr += " NULL";
            return constr;
        }

        /// <summary>An externally stored binary column, which SQL Server keeps in FILESTREAM.</summary>
        public static bool IsFileStream(ColumnSchema column) =>
            column.ExternalStorage && SqlType(column) == ESqlDataType.VarBinary;

        /// <summary>
        /// The type of a bulk temp table column. Mirrors <see cref="Constructor"/> but omits
        /// FILESTREAM (invalid on temp tables) and the trailing comma.
        /// </summary>
        public static string TempTableTypeDef(ColumnSchema column)
        {
            var type = SqlType(column);
            var sb = new StringBuilder();
            sb.Append(type);

            if (HasPrecisionScale(type))
                sb.Append($"({column.Precision},{column.Scale})");
            else if (!string.IsNullOrEmpty(DataSize(column)))
                sb.Append($"({DataSize(column)})");

            sb.Append(" NULL");
            return sb.ToString();
        }

        /// <summary>
        /// True when a column found in the catalog has the type and size the schema needs. A type
        /// Nucleus does not know is a mismatch, so the table is rebuilt to the schema's type.
        /// </summary>
        public static bool Matches(ColumnSchema column, CatalogColumn existing)
        {
            var type = SqlType(column);
            if (!Enum.TryParse<ESqlDataType>(existing.TypeName, true, out var existingType) || existingType != type)
                return false;
            if (HasPrecisionScale(type))
            {
                if (existing.Precision != column.Precision)
                    return false;
                if (existing.Scale != column.Scale)
                    return false;
            }
            else if (HasSize(type) && existing.DataLength != column.DataLength)
                return false;
            return true;
        }
    }
}
