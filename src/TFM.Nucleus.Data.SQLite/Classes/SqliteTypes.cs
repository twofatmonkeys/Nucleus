using System;
using System.Globalization;
using TFM.Nucleus.Data.Schema;

namespace TFM.Nucleus.Data.SQLite
{
    /// <summary>
    /// Maps a neutral column to the type SQLite declares it as. SQLite stores any value in any
    /// column and only gives each column an affinity, taken from words in its declared type, so
    /// the declared type does two jobs: it carries the size facets the schema check compares (SQLite
    /// keeps the declaration as written), and its affinity must leave the stored form alone.
    /// <list type="bullet">
    /// <item>VARCHAR, TEXT and JSON_TEXT have TEXT affinity: text stays text, so a JSON value of 123 is not turned into a number.</item>
    /// <item>INTEGER and BIGINT have INTEGER affinity, REAL has REAL affinity.</item>
    /// <item>BOOLEAN, DATETIME, DATETIMEOFFSET, DATE, UUID and DECIMAL have NUMERIC affinity, which converts
    /// only text that reads as a number: Nucleus's date, time and Guid forms never do, and a decimal is bound as a real.</item>
    /// </list>
    /// A string's length is not enforced by SQLite; it is kept so the schema check sees a changed length.
    /// </summary>
    internal static class SqliteTypes
    {
        /// <summary>The column's type in a CREATE TABLE.</summary>
        public static string TypeName(ColumnSchema column) => column.Intent switch
        {
            ColumnTypeIntent.AnsiString or ColumnTypeIntent.UnicodeString => column.IsMax ? "TEXT" : $"VARCHAR({column.DataLength})",
            ColumnTypeIntent.Decimal => $"DECIMAL({column.Precision},{column.Scale})",
            _ => CatalogTypeName(column),
        };

        /// <summary>The declared type's name without its facets, as <see cref="ParseDeclaredType"/> reads it from the catalog.</summary>
        public static string CatalogTypeName(ColumnSchema column) => column.Intent switch
        {
            ColumnTypeIntent.AnsiString or ColumnTypeIntent.UnicodeString => column.IsMax ? "TEXT" : "VARCHAR",
            ColumnTypeIntent.Int32 => "INTEGER",
            ColumnTypeIntent.Int64 => "BIGINT",
            ColumnTypeIntent.Double => "REAL",
            ColumnTypeIntent.Boolean => "BOOLEAN",
            ColumnTypeIntent.DateTime => "DATETIME",
            ColumnTypeIntent.DateTimeOffset => "DATETIMEOFFSET",
            ColumnTypeIntent.Date => "DATE",
            ColumnTypeIntent.Guid => "UUID",
            ColumnTypeIntent.Binary => "BLOB",
            ColumnTypeIntent.Decimal => "DECIMAL",
            ColumnTypeIntent.Json => "JSON_TEXT",
            _ => throw new NotSupportedException($"Column type {column.Intent} is not supported by the SQLite provider."),
        };

        /// <summary>True when a column found in the catalog has the type and size the schema needs.</summary>
        public static bool Matches(ColumnSchema column, CatalogColumn existing)
        {
            if (!string.Equals(existing.TypeName, CatalogTypeName(column), StringComparison.OrdinalIgnoreCase))
                return false;
            return column.Intent switch
            {
                ColumnTypeIntent.AnsiString or ColumnTypeIntent.UnicodeString when !column.IsMax => existing.DataLength == column.DataLength,
                ColumnTypeIntent.Decimal => existing.Precision == column.Precision && existing.Scale == column.Scale,
                _ => true,
            };
        }

        /// <summary>
        /// Splits a declared type as SQLite keeps it, such as <c>VARCHAR(255)</c> or
        /// <c>DECIMAL(20,5)</c>, into its upper-case name and its facets. One number is a string's
        /// length; two are a decimal's precision and scale. A type without facets has zeros.
        /// </summary>
        public static (string Name, int Length, int Precision, int Scale) ParseDeclaredType(string? declared)
        {
            if (string.IsNullOrWhiteSpace(declared))
                return ("", 0, 0, 0);
            var open = declared.IndexOf('(');
            if (open < 0)
                return (declared.Trim().ToUpperInvariant(), 0, 0, 0);
            var name = declared[..open].Trim().ToUpperInvariant();
            var close = declared.IndexOf(')', open);
            var facets = declared[(open + 1)..(close < 0 ? declared.Length : close)].Split(',');
            static int Number(string text) => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            return facets.Length switch
            {
                1 => (name, Number(facets[0]), 0, 0),
                _ => (name, 0, Number(facets[0]), Number(facets[1])),
            };
        }
    }
}
