using TFM.Nucleus.Data.Schema;

namespace TFM.Nucleus.Data.PostgreSQL
{
    /// <summary>
    /// How PostgreSQL names things. Every identifier is double-quoted, so Nucleus's PascalCase
    /// names keep their case instead of folding to lower case. PostgreSQL silently truncates an
    /// identifier longer than 63 bytes, so a longer logical name is shortened deterministically
    /// before it is ever written, and the schema check looks for the same shortened name.
    /// </summary>
    internal static class PostgresIdentifiers
    {
        /// <summary>NAMEDATALEN - 1: the longest identifier PostgreSQL keeps.</summary>
        public const int MaxBytes = 63;

        public static string Physical(string logicalName) => IdentifierShortener.Shorten(logicalName, MaxBytes);

        /// <summary>A quoted identifier, shortened when it is too long.</summary>
        public static string Quote(string name) => $"\"{Physical(name).Replace("\"", "\"\"")}\"";

        /// <summary>A schema-qualified, quoted table name.</summary>
        public static string Table(string schema, string tableName) => $"{Quote(schema)}.{Quote(tableName)}";

        /// <summary>A string literal, for the few statements that cannot bind a parameter.</summary>
        public static string Literal(string value) => $"'{value.Replace("'", "''")}'";
    }
}
