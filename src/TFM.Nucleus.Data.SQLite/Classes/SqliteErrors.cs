using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Data.SQLite
{
    /// <summary>
    /// How the SQLite provider reads the driver's errors. SQLite reports a unique violation by
    /// the columns it broke (<c>UNIQUE constraint failed: Table.A, Table.B</c>), not by the index,
    /// so the provider maps those columns back to the index name; an index over an expression is
    /// reported by name (<c>UNIQUE constraint failed: index 'Name'</c>).
    /// </summary>
    internal static class SqliteErrors
    {
        /// <summary>SQLITE_CONSTRAINT_UNIQUE: a unique index was violated.</summary>
        public const int UniqueViolation = 2067;

        /// <summary>SQLITE_CONSTRAINT_PRIMARYKEY: the primary key was violated.</summary>
        public const int PrimaryKeyViolation = 1555;

        /// <summary>
        /// What the first SQLite error in the chain is. The driver does not say whether an error
        /// is transient (<c>SqliteException.IsTransient</c> is always false), so it is read from the
        /// result code: SQLITE_BUSY and SQLITE_LOCKED are a wait for another connection's lock
        /// that ran out of <c>BusyTimeoutSeconds</c>, which another attempt can pass.
        /// </summary>
        public static DatabaseErrorClass Classify(Exception exception)
        {
            for (Exception? e = exception; e != null; e = e.InnerException)
                if (e is SqliteException sqlite)
                    return Classify(sqlite);
            return DatabaseFailures.ClassifyDefault(exception);
        }

        private static DatabaseErrorClass Classify(SqliteException exception) => exception.SqliteExtendedErrorCode switch
        {
            UniqueViolation or PrimaryKeyViolation => new(DatabaseErrorKind.UniqueViolation, false),
            // SQLITE_CONSTRAINT_FOREIGNKEY and SQLITE_CONSTRAINT_NOTNULL.
            787 => new(DatabaseErrorKind.ForeignKeyViolation, false),
            1299 => new(DatabaseErrorKind.NotNullViolation, false),
            _ => exception.SqliteErrorCode switch
            {
                // SQLITE_BUSY and SQLITE_LOCKED, with every extended code of each.
                5 or 6 => new(DatabaseErrorKind.Timeout, true),
                // SQLITE_TOOBIG: a value over the engine's own size limit.
                18 => new(DatabaseErrorKind.ValueTooLong, false),
                // SQLITE_CANTOPEN and SQLITE_NOTADB: the file is missing, unreadable or not a database.
                14 or 26 => new(DatabaseErrorKind.ConnectionFailed, false),
                _ => DatabaseErrorClass.Other,
            },
        };

        private const string Marker = "constraint failed: ";
        private const string IndexMarker = "index '";

        /// <summary>What a unique violation names: the table and its columns in key order, or an index by name.</summary>
        internal readonly record struct UniqueViolationTarget(string? TableName, IReadOnlyList<string> Columns, string? IndexName, bool IsPrimaryKey);

        /// <summary>The unique or primary key violation anywhere in the chain, with what it names.</summary>
        public static bool TryGetUniqueViolation(Exception exception, out UniqueViolationTarget target)
        {
            for (var e = exception; e != null; e = e.InnerException)
            {
                if (e is SqliteException { SqliteExtendedErrorCode: UniqueViolation or PrimaryKeyViolation } sqlite)
                {
                    target = Parse(sqlite.Message, sqlite.SqliteExtendedErrorCode == PrimaryKeyViolation);
                    return true;
                }
            }
            target = default;
            return false;
        }

        /// <summary>
        /// Reads the constraint's target out of the driver's message. A table name can hold dots
        /// (a full-name table), a column name cannot, so each column is split at its last dot.
        /// </summary>
        internal static UniqueViolationTarget Parse(string message, bool isPrimaryKey)
        {
            var start = message.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return new UniqueViolationTarget(null, [], null, isPrimaryKey);
            var text = message[(start + Marker.Length)..];
            // The driver wraps the engine's text as: SQLite Error 19: '...'.
            var end = text.IndexOf("'.", StringComparison.Ordinal);
            if (text.StartsWith(IndexMarker, StringComparison.OrdinalIgnoreCase))
            {
                var name = text[IndexMarker.Length..];
                var close = name.IndexOf('\'');
                return new UniqueViolationTarget(null, [], close < 0 ? name : name[..close], isPrimaryKey);
            }
            if (end >= 0)
                text = text[..end];
            text = text.TrimEnd('\'', '.', ' ');
            string? table = null;
            List<string> columns = [];
            foreach (var part in text.Split(", ", StringSplitOptions.RemoveEmptyEntries))
            {
                var dot = part.LastIndexOf('.');
                if (dot <= 0)
                    continue;
                table ??= part[..dot];
                columns.Add(part[(dot + 1)..]);
            }
            return new UniqueViolationTarget(table, columns, null, isPrimaryKey);
        }

        /// <summary>True when the columns name the same key, in order, as SQLite compares identifiers.</summary>
        internal static bool SameColumns(IReadOnlyList<string> reported, IEnumerable<string> keys) =>
            reported.SequenceEqual(keys, SqliteIdentifiers.Comparer);
    }
}
