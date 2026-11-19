using System;

namespace TFM.Nucleus.Data.SQLite
{
    /// <summary>
    /// How SQLite names things. Every identifier is double-quoted, so a name that is also a
    /// keyword, or holds the dots of a full-name table, is still one identifier. SQLite keeps an
    /// identifier of any length and compares identifiers without regard to case.
    /// </summary>
    internal static class SqliteIdentifiers
    {
        /// <summary>How SQLite compares identifiers: ASCII case does not matter.</summary>
        public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

        /// <summary>A quoted identifier.</summary>
        public static string Quote(string name) => $"\"{name.Replace("\"", "\"\"")}\"";

        /// <summary>A string literal, for the catalog queries that name a table or an index.</summary>
        public static string Literal(string value) => $"'{value.Replace("'", "''")}'";
    }
}
