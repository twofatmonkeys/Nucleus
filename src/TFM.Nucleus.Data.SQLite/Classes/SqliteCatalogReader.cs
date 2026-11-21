using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.Queries;
using TFM.Nucleus.Data.Schema;
using static TFM.Nucleus.Data.SQLite.SqliteIdentifiers;

namespace TFM.Nucleus.Data.SQLite
{
    /// <summary>
    /// Reads the SQLite catalog (sqlite_schema and the table-valued pragma functions), and runs
    /// schema statements outside any transaction, each on a connection of its own. The database
    /// exists when its file does; creating it creates the file and its folder.
    /// </summary>
    internal sealed class SqliteCatalogReader(Func<SqliteConfiguration> config) : ICatalogReader
    {
        /// <summary>RETURNING (3.35) and ALTER TABLE DROP COLUMN (3.35) are the newest features Nucleus uses.</summary>
        internal static readonly Version MinimumVersion = new(3, 35, 0);

        private SqliteConfiguration Config => config();

        private int _prepared;

        // Every table but SQLite's own. LIKE folds ASCII case here, which is what SQLite does with names too.
        private const string UserTables = "m.type = 'table' AND m.name NOT LIKE 'sqlite\\_%' ESCAPE '\\'";

        /// <summary>The columns of every table, or of one table, with each declared type as written.</summary>
        internal static string ColumnsQuery(string? tableName) =>
            "SELECT m.name, p.name, p.\"notnull\", p.type FROM sqlite_schema m JOIN pragma_table_info(m.name) p " +
            $"WHERE {UserTables}" +
            (tableName == null ? "" : $" AND m.name = {Literal(tableName)} COLLATE NOCASE") +
            " ORDER BY m.name, p.cid";

        // One row per key column of every index carrying a Nucleus prefix (NCI_, UQ_ or NIX_), so a
        // stale multi-column column index shows as more than one row and gets rebuilt. A key over
        // an expression has no column name and never matches. seqno counts the keys from 0.
        private const string IndexQuery =
            "SELECT m.name, il.name, ix.name, il.\"unique\", ix.\"desc\", il.partial, ix.seqno + 1 " +
            "FROM sqlite_schema m JOIN pragma_index_list(m.name) il JOIN pragma_index_xinfo(il.name) ix " +
            "WHERE " + UserTables + " AND ix.key = 1 AND ix.name IS NOT NULL " +
            "AND (il.name LIKE 'NCI\\_%' ESCAPE '\\' OR il.name LIKE 'UQ\\_%' ESCAPE '\\' OR il.name LIKE 'NIX\\_%' ESCAPE '\\') " +
            "ORDER BY m.name, il.name, ix.seqno";

        public async ValueTask<bool> DatabaseExistsAsync(bool async, CancellationToken cancellationToken)
        {
            string path;
            try
            {
                path = Config.FilePath;
            }
            catch (ArgumentException ex)
            {
                throw new SqliteDataProviderException($"'{Config.Database}' is not a usable file path. {ex.Message}", DataProviderErrorKind.NotConfigured);
            }
            if (!File.Exists(path))
                return false;
            await PrepareAsync(async, cancellationToken);
            return true;
        }

        public async ValueTask CreateDatabaseAsync(bool async, CancellationToken cancellationToken)
        {
            var path = Config.FilePath;
            try
            {
                if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
                    Directory.CreateDirectory(folder);
                using var connection = Config.BuildConnection(create: true);
                await Open(connection, async, cancellationToken);
            }
            catch (Exception ex) when (DatabaseFailures.IsDatabaseFailure(ex))
            {
                throw DatabaseFailures.Translate($"Error creating SQLite database '{path}'. Error: {ex.Message}", ex, SqliteErrors.Classify(ex), DatabaseOperation.Schema, token: cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SqliteDataProviderException($"Error creating SQLite database '{path}'. Error: {ex.Message}", DataProviderErrorKind.Error, ex);
            }
            await PrepareAsync(async, cancellationToken);
        }

        /// <summary>
        /// Once per provider: refuses a SQLite older than <see cref="MinimumVersion"/>, which can
        /// happen when the application swaps the bundled native library for the system's, and puts
        /// the file in write-ahead log mode when asked. The mode is kept in the file.
        /// </summary>
        private async ValueTask PrepareAsync(bool async, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _prepared) == 1)
                return;
            var version = await Scalar("the SQLite version", "SELECT sqlite_version()", async, cancellationToken);
            if (!Version.TryParse(version as string, out var parsed) || parsed < MinimumVersion)
                throw new SqliteDataProviderException(
                    $"SQLite {version} is too old: Nucleus needs {MinimumVersion} or later. Use the bundled native library (SQLitePCLRaw.bundle_e_sqlite3).",
                    DataProviderErrorKind.NotConfigured);
            if (Config.UseWriteAheadLog)
            {
                var mode = await Scalar("the journal mode", "PRAGMA journal_mode = WAL", async, cancellationToken);
                if (!string.Equals(mode as string, "wal", StringComparison.OrdinalIgnoreCase))
                    throw new SqliteDataProviderException($"SQLite database '{Config.FilePath}' could not be put in write-ahead log mode; it is in {mode} mode.",
                        DataProviderErrorKind.Error);
            }
            Volatile.Write(ref _prepared, 1);
        }

        public async ValueTask<CatalogSnapshot> ReadAsync(bool async, CancellationToken cancellationToken)
        {
            var tables = await Read("table names", $"SELECT m.name FROM sqlite_schema m WHERE {UserTables} ORDER BY m.name",
                r => r.GetString(0), async, cancellationToken);
            var columns = await Read("columns", ColumnsQuery(null), ReadColumn, async, cancellationToken);
            var indexes = await ReadIndexesAsync(async, cancellationToken);
            return new CatalogSnapshot(tables, columns, indexes);
        }

        public ValueTask<IReadOnlyList<CatalogIndex>> ReadIndexesAsync(bool async, CancellationToken cancellationToken) =>
            Read("column indexes", IndexQuery, r => new CatalogIndex
            {
                TableName = r.GetString(0),
                IndexName = r.GetString(1),
                ColumnName = r.GetString(2),
                IsUnique = r.GetInt64(3) != 0,
                IsDescending = r.GetInt64(4) != 0,
                HasFilter = r.GetInt64(5) != 0,
                KeyOrdinal = r.GetInt32(6),
                IsIncluded = false,
            }, async, cancellationToken);

        public async ValueTask ExecuteAsync(SchemaStatement statement, bool async, CancellationToken cancellationToken)
        {
            try
            {
                using var connection = Config.BuildConnection();
                await Open(connection, async, cancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = statement.Text;
                command.CommandTimeout = Config.SchemaCommandTimeoutSeconds;
                foreach (var bound in ParameterBinding.Bind(statement.Parameters))
                    command.Parameters.AddWithValue($"@{bound.Key}", SqliteValues.ToDb(bound.Value, null));
                if (async)
                    await command.ExecuteNonQueryAsync(cancellationToken);
                else
                    command.ExecuteNonQuery();
            }
            catch (Exception ex) when (DatabaseFailures.IsDatabaseFailure(ex))
            {
                throw DatabaseFailures.Translate($"Error executing query in SQLite database '{Config.Database}'. Error: {ex.Message}", ex, SqliteErrors.Classify(ex), DatabaseOperation.Schema, token: cancellationToken);
            }
        }

        internal static CatalogColumn ReadColumn(DbDataReader reader)
        {
            var (name, length, precision, scale) = SqliteTypes.ParseDeclaredType(reader.IsDBNull(3) ? null : reader.GetString(3));
            return new CatalogColumn
            {
                TableName = reader.GetString(0),
                Name = reader.GetString(1),
                IsNullable = reader.GetInt64(2) == 0,
                TypeName = name,
                DataLength = length,
                Precision = precision,
                Scale = scale,
            };
        }

        private async ValueTask<object?> Scalar(string what, string query, bool async, CancellationToken cancellationToken)
        {
            try
            {
                using var connection = Config.BuildConnection();
                await Open(connection, async, cancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = query;
                return async ? await command.ExecuteScalarAsync(cancellationToken) : command.ExecuteScalar();
            }
            catch (Exception ex) when (DatabaseFailures.IsDatabaseFailure(ex))
            {
                throw DatabaseFailures.Translate($"Error reading {what} of SQLite database '{Config.Database}'. Error: {ex.Message}", ex, SqliteErrors.Classify(ex), DatabaseOperation.Schema, token: cancellationToken);
            }
        }

        private async ValueTask<IReadOnlyList<T>> Read<T>(string what, string query, Func<DbDataReader, T> read, bool async, CancellationToken cancellationToken)
        {
            try
            {
                List<T> rows = [];
                using var connection = Config.BuildConnection();
                await Open(connection, async, cancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = query;
                using var reader = async ? await command.ExecuteReaderAsync(cancellationToken) : command.ExecuteReader();
                while (async ? await reader.ReadAsync(cancellationToken) : reader.Read())
                    rows.Add(read(reader));
                return rows;
            }
            catch (Exception ex) when (DatabaseFailures.IsDatabaseFailure(ex))
            {
                throw DatabaseFailures.Translate($"Error retrieving {what} from SQLite database '{Config.Database}'. Error: {ex.Message}", ex, SqliteErrors.Classify(ex), DatabaseOperation.Schema, token: cancellationToken);
            }
        }

        private static async ValueTask Open(SqliteConnection connection, bool async, CancellationToken cancellationToken)
        {
            if (async)
                await connection.OpenAsync(cancellationToken);
            else
                connection.Open();
        }
    }
}
