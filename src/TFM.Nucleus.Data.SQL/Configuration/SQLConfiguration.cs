using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Tasks;


namespace TFM.Nucleus.Data.SQL
{
    public class SQLConfiguration
    {
        public readonly Guid ID = Guid.NewGuid();

        public string? Server { get; set; }
        public string? Database { get; set; }
        public string? Username { get; set; }
        public string? Password { protected internal get; set; }
        public string? AppName { get; set; }

        /// <summary>Use Windows authentication instead of SQL authentication (Username/Password ignored).</summary>
        public bool UseIntegratedSecurity { get; set; }

        /// <summary>
        /// A raw connection string that bypasses the builder entirely (also enables Azure AD /
        /// managed-identity scenarios). Server/Database/Username/Password are ignored when set,
        /// except Database, which is still used for schema introspection queries.
        /// </summary>
        public string? ConnectionString { get; set; }

        public int MaxConnectionPoolSize { get; set; } = 100;
        public int SQLPacketSize { get; set; } = 1500;
        public int MaxThreadsForLargeQuery { get; set; } = 12;
        public int MaxParametersPerQuery { get; set; } = 2000;
        public int CommandTimeoutSeconds { get; set; } = 10;
        public int BulkCopyTimeoutSeconds { get; set; } = 60;
        public int BulkCopyBatchSize { get; set; } = 50000;


        protected internal SqlConnection BuildNewSQLConnection(bool useTransaction)
        {
            if (ConnectionString != null)
                return BuildFromRawConnectionString(ConnectionString, useTransaction);
            var builder = BuildConnectionStringBuilder();
            builder.InitialCatalog = Database ?? throw new Exception("SQL database name is missing");
            return useTransaction ? new SqlConnection($"{builder.ConnectionString}") :
                new SqlConnection($"{builder.ConnectionString}; Enlist = false");
        }
        private SqlConnection BuildSystemSQLConnection()
        {
            if (ConnectionString != null)
            {
                var raw = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
                return BuildFromRawConnectionString(raw.ConnectionString, useTransaction: false);
            }
            var builder = BuildConnectionStringBuilder();
            builder.InitialCatalog = "master";
            return new SqlConnection($"{builder.ConnectionString}; Enlist = false");
        }

        /// <summary>Shared builder for the SQL-auth / integrated-auth paths.</summary>
        private SqlConnectionStringBuilder BuildConnectionStringBuilder()
        {
            SqlConnectionStringBuilder builder = [];
            var server = Server;
            if (server?.Contains(':') ?? throw new Exception("SQL server name is missing"))
            {
                var srv = server.Split(':')[0];
                var port = server.Split(':')[1];
                server = $"tcp:{srv},{port}";
            }
            builder.DataSource = server;
            if (UseIntegratedSecurity)
            {
                builder.IntegratedSecurity = true;
            }
            else
            {
                builder.IntegratedSecurity = false;
                builder.UserID = Username ?? throw new Exception("SQL user name is missing");
                builder.Password = Password ?? throw new Exception("SQL password is missing");
            }
            builder.ApplicationName = AppName ?? throw new Exception("SQL application name is missing");
            builder.ConnectTimeout = 3;
            builder.ConnectRetryCount = 4;
            builder.ConnectRetryInterval = 5;
            builder.TrustServerCertificate = true;
            builder.MultipleActiveResultSets = true;
            builder.IPAddressPreference = SqlConnectionIPAddressPreference.UsePlatformDefault;
            builder.PacketSize = SQLPacketSize;
            builder.MaxPoolSize = MaxConnectionPoolSize;
            return builder;
        }

        private static SqlConnection BuildFromRawConnectionString(string connectionString, bool useTransaction)
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            if (!useTransaction)
                builder.Enlist = false;
            return new SqlConnection(builder.ConnectionString);
        }

        protected internal bool DatabaseExists()
        {
            try
            {
                using var connection = BuildSystemSQLConnection();
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT database_id FROM sys.databases WHERE name = @dbName";
                command.Parameters.AddWithValue("@dbName", Database);
                var result = command.ExecuteScalar();
                return result != null && (int)result > 0;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error checking if database '{Database}' exists. Error: {ex.Message}", ex);
            }
        }
        protected internal async Task<bool> DatabaseExistsAsync(CancellationToken? cancellationToken = null)
        {
            try
            {
                using var connection = BuildSystemSQLConnection();
                await connection.OpenAsync(cancellationToken ?? CancellationToken.None);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT database_id FROM sys.databases WHERE name = @dbName";
                command.Parameters.AddWithValue("@dbName", Database);
                var result = await command.ExecuteScalarAsync(cancellationToken ?? CancellationToken.None);
                return result != null && (int)result > 0;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error checking if database '{Database}' exists. Error: {ex.Message}", ex);
            }
        }

        protected internal void CreateDatabase()
        {
            try
            {
                using var connection = BuildSystemSQLConnection();
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"CREATE DATABASE [{Database}]";
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                throw new Exception($"Error creating database '{Database}'. Error: {ex.Message}", ex);
            }
        }
        protected internal async Task CreateDatabaseAsync(CancellationToken? cancellationToken = null)
        {
            try
            {
                using var connection = BuildSystemSQLConnection();
                await connection.OpenAsync(cancellationToken ?? CancellationToken.None);
                using var command = connection.CreateCommand();
                command.CommandText = $"CREATE DATABASE [{Database}]";
                await command.ExecuteNonQueryAsync(cancellationToken ?? CancellationToken.None);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error creating database '{Database}'. Error: {ex.Message}", ex);
            }

        }

        protected internal string[] GetDatabaseTableNames()
        {
            List<string> tableNames = [];
            try
            {
                using var connection = BuildNewSQLConnection(false);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    tableNames.Add(reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error retrieving table names from database '{Database}'. Error: {ex.Message}", ex);
            }
            return [.. tableNames];
        }
        protected internal async Task<string[]> GetDatabaseTableNamesAsync(CancellationToken? cancellationToken = null)
        {
            List<string> tableNames = [];
            try
            {
                using var connection = BuildNewSQLConnection(false);
                await connection.OpenAsync(cancellationToken ?? CancellationToken.None);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'";
                using var reader = await command.ExecuteReaderAsync(cancellationToken ?? CancellationToken.None);
                while (await reader.ReadAsync(cancellationToken ?? CancellationToken.None))
                {
                    tableNames.Add(reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error retrieving table names from database '{Database}'. Error: {ex.Message}", ex);
            }
            return [.. tableNames];
        }

        protected internal SqlColumn[] GetDatabaseColumns(string? tableName = null, SqlDataContext? context = null)
        {
            List<SqlColumn> columns = [];
            try
            {
                var qry = $@"
                    SELECT TABLE_NAME, COLUMN_NAME, IS_NULLABLE, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE
                    FROM INFORMATION_SCHEMA.COLUMNS{(!string.IsNullOrEmpty(tableName) ? $" WHERE TABLE_NAME = '{tableName}'" :"")}";
                if (context == null)
                {
                    using var connection = BuildNewSQLConnection(false);
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = qry;
                    using var reader = command.ExecuteReader();
                    return ReadColumnsFromReader(reader);
                }
                else
                { 
                    var sqlQry = new SelectQuery(qry);
                    using var reader = context.GetObjectDataAsync(sqlQry).GetAwaiter().GetResult() 
                        ?? throw new Exception("Sql Datareader was null");
                    return ReadColumnsFromReader(reader);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error retrieving columns from database '{Database}'. Error: {ex.Message}", ex);
            }
        }
        protected internal async Task<SqlColumn[]> GetDatabaseColumnsAsync(string? tableName = null, SqlDataContext? context = null, CancellationToken? cancellationToken = null)
        {
            List<SqlColumn> columns = [];
            try
            {
                var qry = $@"
                    SELECT TABLE_NAME, COLUMN_NAME, IS_NULLABLE, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE
                    FROM INFORMATION_SCHEMA.COLUMNS{(!string.IsNullOrEmpty(tableName) ? $" WHERE TABLE_NAME = '{tableName}'" :"")}";
                if (context == null)
                {
                    using var connection = BuildNewSQLConnection(false);
                    await connection.OpenAsync(cancellationToken ?? CancellationToken.None);
                    using var command = connection.CreateCommand();
                    command.CommandText = qry;
                    using var reader = await command.ExecuteReaderAsync(cancellationToken ?? CancellationToken.None);
                    return await ReadColumnsFromReaderAsync(reader, cancellationToken);
                }
                else
                { 
                    var sqlQry = new SelectQuery(qry);
                    using var reader = await context.GetObjectDataAsync(sqlQry, cancellationToken ?? CancellationToken.None) 
                        ?? throw new Exception("Sql Datareader was null");
                    return await ReadColumnsFromReaderAsync(reader, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error retrieving columns from database '{Database}'. Error: {ex.Message}", ex);
            }
        }
        
        private static SqlColumn[] ReadColumnsFromReader(DbDataReader reader)
        {
            List<SqlColumn> columns = [];
            while (reader.Read())
            {
                columns.Add(new SqlColumn()
                {
                    TableName = reader.GetString(0),
                    Name = reader.GetString(1),
                    IsNullable = reader.GetString(2) == "YES",
                    DataType = ParseSqlDataType(reader.GetString(3)),
                    DataLength = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    Precision = reader.IsDBNull(5) ? 0 : reader.GetByte(5),
                    Scale = reader.IsDBNull(6) ? 0 : reader.GetInt32(6)
                });
            }
            return [.. columns];
        }
        private static async Task<SqlColumn[]> ReadColumnsFromReaderAsync(DbDataReader reader, CancellationToken? cancellationToken = null)
        {
            List<SqlColumn> columns = [];
            while (await reader.ReadAsync(cancellationToken ?? CancellationToken.None))
            {
                columns.Add(new SqlColumn()
                {
                    TableName = reader.GetString(0),
                    Name = reader.GetString(1),
                    IsNullable = reader.GetString(2) == "YES",
                    DataType = ParseSqlDataType(reader.GetString(3)),
                    DataLength = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    Precision = reader.IsDBNull(5) ? 0 : reader.GetByte(5),
                    Scale = reader.IsDBNull(6) ? 0 : reader.GetInt32(6)
                });
            }
            return [.. columns];    
        }

        protected internal bool TableHasFtIndex(string tableName, SqlDataContext? context = null)
        {
            try
            {
                var qry = $@"
                SELECT COUNT(*) 
                FROM sys.fulltext_indexes fti
                JOIN sys.tables t ON fti.object_id = t.object_id
                WHERE t.name = '{tableName}'";
                int count = 0;
                if (context == null)
                {
                    using var connection = BuildNewSQLConnection(false);
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = qry;
                    count = (int)command.ExecuteScalar();
                }
                else
                {
                    var sqlQry = new SelectQuery(qry);
                    using var reader = context.GetObjectDataAsync(sqlQry).GetAwaiter().GetResult() 
                        ?? throw new Exception("Sql Datareader was null");
                    if (reader.Read())
                    {
                        count = reader.GetInt32(0);
                    }
                }
                return count > 0;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error checking full-text index on table '{tableName}' in database '{Database}'. Error: {ex.Message}", ex);
            }
        }
        protected internal async Task<bool> TableHasFtIndexAsync(string tableName, SqlDataContext? context = null, CancellationToken? cancellationToken = null)
        {
            try
            {
                var qry = $@"
                SELECT COUNT(*) 
                FROM sys.fulltext_indexes fti
                JOIN sys.tables t ON fti.object_id = t.object_id
                WHERE t.name = '{tableName}'";
                int count = 0;
                if (context == null)
                {
                    using var connection = BuildNewSQLConnection(false);
                    await connection.OpenAsync(cancellationToken ?? CancellationToken.None);
                    using var command = connection.CreateCommand();
                    command.CommandText = qry;
                    count = (int)await command.ExecuteScalarAsync(cancellationToken ?? CancellationToken.None);
                }
                else
                {
                    var sqlQry = new SelectQuery(qry);
                    using var reader = await context.GetObjectDataAsync(sqlQry, cancellationToken ?? CancellationToken.None)
                        ?? throw new Exception("Sql Datareader was null");
                    if (await reader.ReadAsync(cancellationToken ?? CancellationToken.None))
                    {
                        count = reader.GetInt32(0);
                    }
                }
                return count > 0;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error checking full-text index on table '{tableName}' in database '{Database}'. Error: {ex.Message}", ex);
            }
        }

        protected internal List<string> GetFullTextCatalogs(SqlDataContext? context = null)
        { 
            List<string> catalogs = [];
            try
            {
                var qry = "SELECT name FROM sys.fulltext_catalogs";
                if (context == null)
                {
                    using var connection = BuildNewSQLConnection(false);
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = qry;
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        catalogs.Add(reader.GetString(0));
                    }
                }
                else
                {
                    var sqlQry = new SelectQuery(qry);
                    using var reader = context.GetObjectDataAsync(sqlQry).GetAwaiter().GetResult() 
                        ?? throw new Exception("Sql Datareader was null");
                    while (reader.Read())
                    {
                        catalogs.Add(reader.GetString(0));
                    }
                }
                return catalogs;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error retrieving full-text catalogs from database '{Database}'. Error: {ex.Message}", ex);
            }
        }
        protected internal async Task<List<string>> GetFullTextCatalogsAsync(SqlDataContext? context = null, CancellationToken? cancellationToken = null)
        { 
            List<string> catalogs = [];
            try
            {
                var qry = "SELECT name FROM sys.fulltext_catalogs";
                if (context == null)
                {
                    using var connection = BuildNewSQLConnection(false);
                    await connection.OpenAsync(cancellationToken ?? CancellationToken.None);
                    using var command = connection.CreateCommand();
                    command.CommandText = qry;
                    using var reader = await command.ExecuteReaderAsync(cancellationToken ?? CancellationToken.None);
                    while (await reader.ReadAsync(cancellationToken ?? CancellationToken.None))
                    {
                        catalogs.Add(reader.GetString(0));
                    }
                }
                else
                {
                    var sqlQry = new SelectQuery(qry);
                    using var reader = await context.GetObjectDataAsync(sqlQry, cancellationToken ?? CancellationToken.None)
                        ?? throw new Exception("Sql Datareader was null");
                    while (await reader.ReadAsync(cancellationToken ?? CancellationToken.None))
                    {
                        catalogs.Add(reader.GetString(0));
                    }
                }
                return catalogs;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error retrieving full-text catalogs from database '{Database}'. Error: {ex.Message}", ex);
            }
        }

        protected internal void CreateFullTextCatalog(string catalogName)
        {
            try
            {
                var qry = $"CREATE FULLTEXT CATALOG [{catalogName}]";
                    using var connection = BuildNewSQLConnection(false);
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = qry;
                    command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                throw new Exception($"Error creating full-text catalog '{catalogName}' in database '{Database}'. Error: {ex.Message}", ex);
            }
        }
        protected internal async Task CreateFullTextCatalogAsync(string catalogName, CancellationToken? cancellationToken = null)
        {
            try
            {
                var qry = $"CREATE FULLTEXT CATALOG [{catalogName}]";
                    using var connection = BuildNewSQLConnection(false);
                    await connection.OpenAsync(cancellationToken ?? CancellationToken.None);
                    using var command = connection.CreateCommand();
                    command.CommandText = qry;
                    await command.ExecuteNonQueryAsync(cancellationToken ?? CancellationToken.None);

            }
            catch (Exception ex)
            {
                throw new Exception($"Error creating full-text catalog '{catalogName}' in database '{Database}'. Error: {ex.Message}", ex);
            }
        }

        protected internal void ExecuteQuery(string query, SqlDataContext? context = null)
        {
            try
            {
                if (context == null)
                {
                    using var connection = BuildNewSQLConnection(false);
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = query;
                    command.ExecuteNonQuery();
                }
                else
                {
                    context.ExecuteSQLAsync(query).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error executing query in database '{Database}'. Error: {ex.Message}", ex);
            }
        }
        protected internal async Task ExecuteQueryAsync(string query, SqlDataContext? context = null, CancellationToken? cancellationToken = null)
        {
            try
            {
                if (context == null)
                {
                    using var connection = BuildNewSQLConnection(false);
                    await connection.OpenAsync(cancellationToken ?? CancellationToken.None);
                    using var command = connection.CreateCommand();
                    command.CommandText = query;
                    await command.ExecuteNonQueryAsync(cancellationToken ?? CancellationToken.None);
                }
                else
                {
                    await context.ExecuteSQLAsync(query, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error executing query in database '{Database}'. Error: {ex.Message}", ex);
            }
        }

        protected internal List<(string tableName, string ftCatalogName, string UniqueIdxName, string columnName)> 
            GetFullTextIndexes(SqlDataContext? context = null)
        {
            try
            {
                List<(string tableName, string ftCatalogName, string UniqueIdxName, string columnName)> idxs = [];
                var qry = @"
                SELECT t.name AS TableName, fc.name AS FullTextCatalogName, i.name AS UniqueIndexName, c.name AS ColumnName
                FROM sys.fulltext_indexes fti
                JOIN sys.tables t ON fti.object_id = t.object_id
                JOIN sys.fulltext_catalogs fc ON fti.fulltext_catalog_id = fc.fulltext_catalog_id
                JOIN sys.indexes i ON fti.unique_index_id = i.index_id AND fti.object_id = i.object_id
                JOIN sys.fulltext_index_columns ftic ON fti.object_id = ftic.object_id
                JOIN sys.columns c ON ftic.column_id = c.column_id AND ftic.object_id = c.object_id";
                if (context == null)
                {
                    using var connection = BuildNewSQLConnection(false);
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = qry;
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        idxs.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
                    }
                }
                else
                {
                    var sqlQry = new SelectQuery(qry);
                    using var reader = context.GetObjectDataAsync(sqlQry).GetAwaiter().GetResult() 
                        ?? throw new Exception("Sql Datareader was null");
                    while (reader.Read())
                    {
                        idxs.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
                    }
                }
                return idxs;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error retrieving full-text indexes from database '{Database}'. Error: {ex.Message}", ex);
            }
        }
        protected internal async Task<List<(string tableName, string ftCatalogName, string UniqueIdxName, string columnName)>> 
            GetFullTextIndexesAsync(SqlDataContext? context = null, CancellationToken? cancellationToken = null)
        {
            try
            {
                List<(string tableName, string ftCatalogName, string UniqueIdxName, string columnName)> idxs = [];
                var qry = @"
                SELECT t.name AS TableName, fc.name AS FullTextCatalogName, i.name AS UniqueIndexName, c.name AS ColumnName
                FROM sys.fulltext_indexes fti
                JOIN sys.tables t ON fti.object_id = t.object_id
                JOIN sys.fulltext_catalogs fc ON fti.fulltext_catalog_id = fc.fulltext_catalog_id
                JOIN sys.indexes i ON fti.unique_index_id = i.index_id AND fti.object_id = i.object_id
                JOIN sys.fulltext_index_columns ftic ON fti.object_id = ftic.object_id
                JOIN sys.columns c ON ftic.column_id = c.column_id AND ftic.object_id = c.object_id";
                if (context == null)
                {
                    using var connection = BuildNewSQLConnection(false);
                    await connection.OpenAsync(cancellationToken ?? CancellationToken.None);
                    using var command = connection.CreateCommand();
                    command.CommandText = qry;
                    using var reader = await command.ExecuteReaderAsync(cancellationToken ?? CancellationToken.None);
                    while (await reader.ReadAsync(cancellationToken ?? CancellationToken.None))
                    {
                        idxs.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
                    }
                }
                else
                {
                    var sqlQry = new SelectQuery(qry);
                    using var reader = await context.GetObjectDataAsync(sqlQry, cancellationToken ?? CancellationToken.None)
                        ?? throw new Exception("Sql Datareader was null");
                    while (await reader.ReadAsync(cancellationToken ?? CancellationToken.None))
                    {
                        idxs.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
                    }
                }
                return idxs;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error retrieving full-text indexes from database '{Database}'. Error: {ex.Message}", ex);
            }
        }

        // Reads the key columns of every Nucleus-named nonclustered index (NCI_* and UQ_*). Only
        // those prefixes are returned so the schema compare and sync never see, drop or recreate
        // an index a DBA added by hand. [_] is a literal underscore inside LIKE.
        private const string ColumnIndexQuery = @"
                SELECT t.name AS TableName, i.name AS IndexName, c.name AS ColumnName, i.is_unique, ic.is_descending_key, i.has_filter
                FROM sys.indexes i
                JOIN sys.tables t ON t.object_id = i.object_id
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.type = 2 AND i.is_primary_key = 0 AND ic.is_included_column = 0
                  AND (i.name LIKE 'NCI[_]%' OR i.name LIKE 'UQ[_]%')";

        private static SqlColumnIndex ReadColumnIndex(DbDataReader reader) => new()
        {
            TableName = reader.GetString(0),
            IndexName = reader.GetString(1),
            ColumnName = reader.GetString(2),
            IsUnique = reader.GetBoolean(3),
            IsDescending = reader.GetBoolean(4),
            HasFilter = reader.GetBoolean(5),
        };

        protected internal List<SqlColumnIndex> GetColumnIndexes(SqlDataContext? context = null)
        {
            try
            {
                List<SqlColumnIndex> idxs = [];
                if (context == null)
                {
                    using var connection = BuildNewSQLConnection(false);
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = ColumnIndexQuery;
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                        idxs.Add(ReadColumnIndex(reader));
                }
                else
                {
                    var sqlQry = new SelectQuery(ColumnIndexQuery);
                    using var reader = context.GetObjectDataAsync(sqlQry).GetAwaiter().GetResult()
                        ?? throw new Exception("Sql Datareader was null");
                    while (reader.Read())
                        idxs.Add(ReadColumnIndex(reader));
                }
                return idxs;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error retrieving column indexes from database '{Database}'. Error: {ex.Message}", ex);
            }
        }
        protected internal async Task<List<SqlColumnIndex>> GetColumnIndexesAsync(SqlDataContext? context = null, CancellationToken? cancellationToken = null)
        {
            try
            {
                List<SqlColumnIndex> idxs = [];
                if (context == null)
                {
                    using var connection = BuildNewSQLConnection(false);
                    await connection.OpenAsync(cancellationToken ?? CancellationToken.None);
                    using var command = connection.CreateCommand();
                    command.CommandText = ColumnIndexQuery;
                    using var reader = await command.ExecuteReaderAsync(cancellationToken ?? CancellationToken.None);
                    while (await reader.ReadAsync(cancellationToken ?? CancellationToken.None))
                        idxs.Add(ReadColumnIndex(reader));
                }
                else
                {
                    var sqlQry = new SelectQuery(ColumnIndexQuery);
                    using var reader = await context.GetObjectDataAsync(sqlQry, cancellationToken ?? CancellationToken.None)
                        ?? throw new Exception("Sql Datareader was null");
                    while (await reader.ReadAsync(cancellationToken ?? CancellationToken.None))
                        idxs.Add(ReadColumnIndex(reader));
                }
                return idxs;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error retrieving column indexes from database '{Database}'. Error: {ex.Message}", ex);
            }
        }

        private static SqlColumnSchema.ESqlDataType ParseSqlDataType(string? dataTypeStr)
        {
            if(dataTypeStr != null && Enum.TryParse<SqlColumnSchema.ESqlDataType>(dataTypeStr, true, out var dataType))
            {
                return dataType;
            }
            throw new Exception($"Unknown SQL data type '{dataTypeStr}'");

        }

        //public void Configure(NucleusOptions options)
        //{
        //    AppName = options.AppName ?? throw new ArgumentNullException(nameof(options.AppName), "AppName must be set in NucleusOptions");
        //}
        //public SQLConfiguration(IOptions<NucleusOptions> nucleusOptions, ILogger<SQLConfiguration>? logger = null)
        //{
        //    if (nucleusOptions == null)
        //        throw new ArgumentNullException(nameof(nucleusOptions), "NucleusOptions cannot be null");
        //    if (nucleusOptions.Value == null)
        //        throw new ArgumentNullException(nameof(nucleusOptions.Value), "NucleusOptions value cannot be null");
        //    AppName = nucleusOptions.Value.AppName ?? throw new ArgumentNullException(nameof(nucleusOptions.Value.AppName), "AppName must be set in NucleusOptions");
        //    Configure(nucleusOptions.Value);
        //    _logger = logger;
        //}
    }

    /// <summary>Copies AppName from NucleusOptions into every (named) SQLConfiguration instance.</summary>
    public class ConfigureSQLOptions : IConfigureNamedOptions<SQLConfiguration>
    {
        private readonly NucleusOptions _nucleusOptions;
        public ConfigureSQLOptions(IOptions<NucleusOptions> nucleusOptions)
        {
            _nucleusOptions = nucleusOptions?.Value ?? throw new ArgumentNullException(nameof(nucleusOptions), "NucleusOptions cannot be null");
        }
        public void Configure(SQLConfiguration options) => Configure(Options.DefaultName, options);
        public void Configure(string? name, SQLConfiguration options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options), "SQLConfiguration cannot be null");
            options.AppName = _nucleusOptions.AppName ?? throw new ArgumentNullException(nameof(_nucleusOptions.AppName), "AppName must be set in NucleusOptions");
        }
    }
}
