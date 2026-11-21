using Npgsql;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.Queries;
using TFM.Nucleus.Data.Schema;
using static TFM.Nucleus.Data.PostgreSQL.PostgresIdentifiers;

namespace TFM.Nucleus.Data.PostgreSQL
{
    /// <summary>
    /// Reads the PostgreSQL catalog (information_schema and pg_catalog) for the configured schema,
    /// and runs schema statements outside any transaction, each on a connection of its own.
    /// Opening a connection retries a transient failure.
    /// </summary>
    internal sealed class PostgresCatalogReader(Func<PostgresConfiguration> config) : ICatalogReader
    {
        private PostgresConfiguration Config => config();

        /// <summary>The columns of the configured schema, or of one table in it.</summary>
        internal static string ColumnsQuery(string schema, string? tableName) =>
            "SELECT table_name, column_name, is_nullable, data_type, character_maximum_length, numeric_precision, numeric_scale " +
            $"FROM information_schema.columns WHERE table_schema = {Literal(schema)}" +
            (tableName == null ? "" : $" AND table_name = {Literal(tableName)}");

        // One row per column of every index carrying a Nucleus prefix (NCI_, UQ_ or NIX_), so a
        // stale multi-column column index shows as more than one row and gets rebuilt. An
        // expression index has no key column and never matches. indkey keeps its 0-based
        // subscripts when cast to an array, so the 1-based column number counts from the array's
        // lower bound; positions past indnkeyatts are INCLUDE columns, reported as ordinal 0.
        private static string IndexQuery(string schema) =>
            "SELECT t.relname, i.relname, a.attname, ix.indisunique, " +
            "coalesce(pg_index_column_has_property(i.oid, array_position(ix.indkey::int2[], a.attnum) - array_lower(ix.indkey::int2[], 1) + 1, 'desc'), false), " +
            "ix.indpred IS NOT NULL, " +
            "array_position(ix.indkey::int2[], a.attnum) - array_lower(ix.indkey::int2[], 1) + 1 AS position, ix.indnkeyatts " +
            "FROM pg_index ix " +
            "JOIN pg_class i ON i.oid = ix.indexrelid " +
            "JOIN pg_class t ON t.oid = ix.indrelid " +
            "JOIN pg_namespace n ON n.oid = t.relnamespace " +
            "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(ix.indkey) " +
            $"WHERE n.nspname = {Literal(schema)} AND NOT ix.indisprimary " +
            @"AND (i.relname LIKE 'NCI\_%' OR i.relname LIKE 'UQ\_%' OR i.relname LIKE 'NIX\_%')";

        public async ValueTask<bool> DatabaseExistsAsync(bool async, CancellationToken cancellationToken)
        {
            try
            {
                using var connection = Config.BuildMaintenanceConnection();
                await Open(connection, async, cancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name";
                command.Parameters.AddWithValue("name", Config.Database ?? throw new PostgresDataProviderException("PostgreSQL database name is missing", DataProviderErrorKind.NotConfigured));
                var result = async ? await command.ExecuteScalarAsync(cancellationToken) : command.ExecuteScalar();
                return result != null;
            }
            catch (Exception ex) when (DatabaseFailures.IsDatabaseFailure(ex))
            {
                throw DatabaseFailures.Translate($"Error checking if database '{Config.Database}' exists. Error: {ex.Message}", ex, PostgresErrors.Classify(ex), DatabaseOperation.Schema, token: cancellationToken);
            }
        }

        public async ValueTask CreateDatabaseAsync(bool async, CancellationToken cancellationToken)
        {
            try
            {
                using var connection = Config.BuildMaintenanceConnection();
                await Open(connection, async, cancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = $"CREATE DATABASE {Quote(Config.Database!)}";
                if (async)
                    await command.ExecuteNonQueryAsync(cancellationToken);
                else
                    command.ExecuteNonQuery();
            }
            catch (Exception ex) when (DatabaseFailures.IsDatabaseFailure(ex))
            {
                throw DatabaseFailures.Translate($"Error creating database '{Config.Database}'. Error: {ex.Message}", ex, PostgresErrors.Classify(ex), DatabaseOperation.Schema, token: cancellationToken);
            }
        }

        public async ValueTask<CatalogSnapshot> ReadAsync(bool async, CancellationToken cancellationToken)
        {
            var tables = await Read("table names",
                $"SELECT table_name FROM information_schema.tables WHERE table_schema = {Literal(Config.Schema)} AND table_type = 'BASE TABLE'",
                r => r.GetString(0), async, cancellationToken);
            var columns = await Read("columns", ColumnsQuery(Config.Schema, null), ReadColumn, async, cancellationToken);
            var indexes = await ReadIndexesAsync(async, cancellationToken);
            return new CatalogSnapshot(tables, columns, indexes);
        }

        public ValueTask<IReadOnlyList<CatalogIndex>> ReadIndexesAsync(bool async, CancellationToken cancellationToken) =>
            Read("column indexes", IndexQuery(Config.Schema), r => new CatalogIndex
            {
                TableName = r.GetString(0),
                IndexName = r.GetString(1),
                ColumnName = r.GetString(2),
                IsUnique = r.GetBoolean(3),
                IsDescending = r.GetBoolean(4),
                HasFilter = r.GetBoolean(5),
                KeyOrdinal = r.GetInt32(6) <= r.GetInt16(7) ? r.GetInt32(6) : 0,
                IsIncluded = r.GetInt32(6) > r.GetInt16(7),
            }, async, cancellationToken);

        /// <summary>The (table, index) names of every full-text index in the schema.</summary>
        internal ValueTask<IReadOnlyList<(string Table, string Index)>> ReadFullTextIndexesAsync(bool async, CancellationToken cancellationToken) =>
            Read("full-text indexes",
                $"SELECT tablename, indexname FROM pg_indexes WHERE schemaname = {Literal(Config.Schema)} AND indexname LIKE {Literal(PostgresSchemaDialect.FullTextIndexPrefix.Replace("_", @"\_") + "%")}",
                r => (r.GetString(0), r.GetString(1)), async, cancellationToken);

        /// <summary>The (table, trigger) names of every notification trigger in the schema.</summary>
        internal ValueTask<IReadOnlyList<(string Table, string Trigger)>> ReadNotifyTriggersAsync(bool async, CancellationToken cancellationToken) =>
            Read("notification triggers",
                "SELECT c.relname, t.tgname FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace " +
                $"WHERE n.nspname = {Literal(Config.Schema)} AND NOT t.tgisinternal AND t.tgname LIKE {Literal(PostgresSchemaDialect.NotifyTriggerPrefix.Replace("_", @"\_") + "%")}",
                r => (r.GetString(0), r.GetString(1)), async, cancellationToken);

        /// <summary>True when the schema has the notification trigger function.</summary>
        internal async ValueTask<bool> NotifyFunctionExistsAsync(bool async, CancellationToken cancellationToken)
        {
            var rows = await Read("the notification function",
                "SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace " +
                $"WHERE n.nspname = {Literal(Config.Schema)} AND p.proname = {Literal(PostgresSchemaDialect.NotifyFunction)}",
                r => r.GetInt32(0), async, cancellationToken);
            return rows.Count > 0;
        }

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
                    command.Parameters.AddWithValue(bound.Key, bound.Value ?? DBNull.Value);
                if (async)
                    await command.ExecuteNonQueryAsync(cancellationToken);
                else
                    command.ExecuteNonQuery();
            }
            catch (Exception ex) when (DatabaseFailures.IsDatabaseFailure(ex))
            {
                throw DatabaseFailures.Translate($"Error executing query in database '{Config.Database}'. Error: {ex.Message}", ex, PostgresErrors.Classify(ex), DatabaseOperation.Schema, token: cancellationToken);
            }
        }

        internal static CatalogColumn ReadColumn(DbDataReader reader) => new()
        {
            TableName = reader.GetString(0),
            Name = reader.GetString(1),
            IsNullable = reader.GetString(2) == "YES",
            TypeName = reader.GetString(3),
            DataLength = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            Precision = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            Scale = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
        };

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
                throw DatabaseFailures.Translate($"Error retrieving {what} from database '{Config.Database}'. Error: {ex.Message}", ex, PostgresErrors.Classify(ex), DatabaseOperation.Schema, token: cancellationToken);
            }
        }

        private ValueTask Open(NpgsqlConnection connection, bool async, CancellationToken cancellationToken) =>
            ConnectionOpener.OpenAsync(connection, async, Config.ConnectAttempts, PostgresErrors.IsTransient, ConnectionOpener.TimeoutOf(connection), cancellationToken);
    }
}
