using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.Schema;

namespace TFM.Nucleus.Data.SQL
{
    /// <summary>
    /// Reads the SQL Server catalog (sys.* and INFORMATION_SCHEMA) and runs schema statements
    /// outside any transaction, each on a connection of its own. Every read and write a schema
    /// change makes outside its rebuild transaction comes through here.
    /// </summary>
    internal sealed class SqlServerCatalogReader(Func<SQLConfiguration> config) : ICatalogReader
    {
        private SQLConfiguration Config => config();

        /// <summary>One column of a full-text index.</summary>
        internal sealed record FullTextIndexColumn(string TableName, string CatalogName, string UniqueIndexName, string ColumnName);

        private const string TableNamesQuery = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'";

        private const string ColumnsSql = @"
                    SELECT TABLE_NAME, COLUMN_NAME, IS_NULLABLE, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE
                    FROM INFORMATION_SCHEMA.COLUMNS";

        /// <summary>The columns of every table, or of one table, its name bound as a parameter.</summary>
        internal static SchemaStatement ColumnsQuery(string? tableName) =>
            string.IsNullOrEmpty(tableName)
                ? ColumnsSql
                : new SchemaStatement($"{ColumnsSql} WHERE TABLE_NAME = @tableName", new ParameterCollection(new Parameter("tableName", tableName, false)));

        // Reads every Nucleus-named nonclustered index: the key columns of each column index
        // (NCI_* and UQ_*), and the key and included columns of each [TableIndex] index (NIX_*).
        // Only those prefixes are returned so the schema compare and sync never see, drop or
        // recreate an index a DBA added by hand. [_] is a literal underscore inside LIKE.
        private const string ColumnIndexQuery = @"
                SELECT t.name AS TableName, i.name AS IndexName, c.name AS ColumnName, i.is_unique, ic.is_descending_key, i.has_filter, ic.key_ordinal, ic.is_included_column
                FROM sys.indexes i
                JOIN sys.tables t ON t.object_id = i.object_id
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.type = 2 AND i.is_primary_key = 0
                  AND ((ic.is_included_column = 0 AND (i.name LIKE 'NCI[_]%' OR i.name LIKE 'UQ[_]%')) OR i.name LIKE 'NIX[_]%')";

        private const string FullTextIndexQuery = @"
                SELECT t.name AS TableName, fc.name AS FullTextCatalogName, i.name AS UniqueIndexName, c.name AS ColumnName
                FROM sys.fulltext_indexes fti
                JOIN sys.tables t ON fti.object_id = t.object_id
                JOIN sys.fulltext_catalogs fc ON fti.fulltext_catalog_id = fc.fulltext_catalog_id
                JOIN sys.indexes i ON fti.unique_index_id = i.index_id AND fti.object_id = i.object_id
                JOIN sys.fulltext_index_columns ftic ON fti.object_id = ftic.object_id
                JOIN sys.columns c ON ftic.column_id = c.column_id AND ftic.object_id = c.object_id";

        public async ValueTask<bool> DatabaseExistsAsync(bool async, CancellationToken cancellationToken)
        {
            try
            {
                using var connection = Config.BuildSystemSQLConnection();
                await Open(connection, async, cancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT database_id FROM sys.databases WHERE name = @dbName";
                command.Parameters.AddWithValue("@dbName", Config.Database);
                var result = async ? await command.ExecuteScalarAsync(cancellationToken) : command.ExecuteScalar();
                return result != null && (int)result > 0;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error checking if database '{Config.Database}' exists. Error: {ex.Message}", ex);
            }
        }

        public async ValueTask CreateDatabaseAsync(bool async, CancellationToken cancellationToken)
        {
            try
            {
                using var connection = Config.BuildSystemSQLConnection();
                await Open(connection, async, cancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = $"CREATE DATABASE [{Config.Database}]";
                if (async)
                    await command.ExecuteNonQueryAsync(cancellationToken);
                else
                    command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                throw new Exception($"Error creating database '{Config.Database}'. Error: {ex.Message}", ex);
            }
        }

        public async ValueTask<CatalogSnapshot> ReadAsync(bool async, CancellationToken cancellationToken)
        {
            var tables = await ReadTableNamesAsync(async, cancellationToken);
            var columns = await ReadColumnsAsync(async, cancellationToken);
            var indexes = await ReadIndexesAsync(async, cancellationToken);
            return new CatalogSnapshot(tables, columns, indexes);
        }

        private async ValueTask<IReadOnlyList<string>> ReadTableNamesAsync(bool async, CancellationToken cancellationToken)
        {
            try
            {
                return await ReadAll(TableNamesQuery, r => r.GetString(0), async, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error retrieving table names from database '{Config.Database}'. Error: {ex.Message}", ex);
            }
        }

        private async ValueTask<IReadOnlyList<CatalogColumn>> ReadColumnsAsync(bool async, CancellationToken cancellationToken)
        {
            try
            {
                return await ReadAll(ColumnsSql, ReadColumn, async, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error retrieving columns from database '{Config.Database}'. Error: {ex.Message}", ex);
            }
        }

        public async ValueTask<IReadOnlyList<CatalogIndex>> ReadIndexesAsync(bool async, CancellationToken cancellationToken)
        {
            try
            {
                return await ReadAll(ColumnIndexQuery, ReadColumnIndex, async, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error retrieving column indexes from database '{Config.Database}'. Error: {ex.Message}", ex);
            }
        }

        public async ValueTask ExecuteAsync(SchemaStatement statement, bool async, CancellationToken cancellationToken)
        {
            try
            {
                using var connection = Config.BuildNewSQLConnection(false);
                await Open(connection, async, cancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = statement.Text;
                command.CommandTimeout = Config.SchemaCommandTimeoutSeconds;
                command.Parameters.TryAddRange(statement.Parameters.GetSQLParams());
                if (async)
                    await command.ExecuteNonQueryAsync(cancellationToken);
                else
                    command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                throw new Exception($"Error executing query in database '{Config.Database}'. Error: {ex.Message}", ex);
            }
        }

        internal async ValueTask<IReadOnlyList<FullTextIndexColumn>> ReadFullTextIndexesAsync(bool async, CancellationToken cancellationToken)
        {
            try
            {
                return await ReadAll(FullTextIndexQuery,
                    r => new FullTextIndexColumn(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)),
                    async, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error retrieving full-text indexes from database '{Config.Database}'. Error: {ex.Message}", ex);
            }
        }

        internal async ValueTask<IReadOnlyList<string>> ReadFullTextCatalogsAsync(bool async, CancellationToken cancellationToken)
        {
            try
            {
                return await ReadAll("SELECT name FROM sys.fulltext_catalogs", r => r.GetString(0), async, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error retrieving full-text catalogs from database '{Config.Database}'. Error: {ex.Message}", ex);
            }
        }

        internal async ValueTask CreateFullTextCatalogAsync(string catalogName, bool async, CancellationToken cancellationToken)
        {
            try
            {
                using var connection = Config.BuildNewSQLConnection(false);
                await Open(connection, async, cancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = SqlServerSchemaDialect.CreateFullTextCatalogSql(catalogName);
                command.CommandTimeout = Config.SchemaCommandTimeoutSeconds;
                if (async)
                    await command.ExecuteNonQueryAsync(cancellationToken);
                else
                    command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                throw new Exception($"Error creating full-text catalog '{catalogName}' in database '{Config.Database}'. Error: {ex.Message}", ex);
            }
        }

        internal async ValueTask<bool> TableHasFullTextIndexAsync(string tableName, bool async, CancellationToken cancellationToken)
        {
            try
            {
                using var connection = Config.BuildNewSQLConnection(false);
                await Open(connection, async, cancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = @"
                SELECT COUNT(*)
                FROM sys.fulltext_indexes fti
                JOIN sys.tables t ON fti.object_id = t.object_id
                WHERE t.name = @tableName";
                command.Parameters.AddWithValue("@tableName", tableName);
                var count = (int)(async ? await command.ExecuteScalarAsync(cancellationToken) : command.ExecuteScalar())!;
                return count > 0;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error checking full-text index on table '{tableName}' in database '{Config.Database}'. Error: {ex.Message}", ex);
            }
        }

        internal static CatalogColumn ReadColumn(DbDataReader reader) => new()
        {
            TableName = reader.GetString(0),
            Name = reader.GetString(1),
            IsNullable = reader.GetString(2) == "YES",
            // Kept as the catalog spells it. A type Nucleus does not know only matters when a
            // Nucleus column has it, which the comparison treats as a mismatch.
            TypeName = reader.GetString(3),
            DataLength = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            Precision = reader.IsDBNull(5) ? 0 : reader.GetByte(5),
            Scale = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
        };

        private static CatalogIndex ReadColumnIndex(DbDataReader reader) => new()
        {
            TableName = reader.GetString(0),
            IndexName = reader.GetString(1),
            ColumnName = reader.GetString(2),
            IsUnique = reader.GetBoolean(3),
            IsDescending = reader.GetBoolean(4),
            HasFilter = reader.GetBoolean(5),
            KeyOrdinal = reader.GetByte(6),
            IsIncluded = reader.GetBoolean(7),
        };

        private async ValueTask<IReadOnlyList<T>> ReadAll<T>(string query, Func<DbDataReader, T> read, bool async, CancellationToken cancellationToken)
        {
            List<T> rows = [];
            using var connection = Config.BuildNewSQLConnection(false);
            await Open(connection, async, cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = query;
            using var reader = async ? await command.ExecuteReaderAsync(cancellationToken) : command.ExecuteReader();
            while (async ? await reader.ReadAsync(cancellationToken) : reader.Read())
                rows.Add(read(reader));
            return rows;
        }

        private static async ValueTask Open(SqlConnection connection, bool async, CancellationToken cancellationToken)
        {
            if (async)
                await connection.OpenAsync(cancellationToken);
            else
                connection.Open();
        }
    }
}
