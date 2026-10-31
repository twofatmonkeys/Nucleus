using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus;
using TFM.Nucleus.Data;

using TFM.Nucleus.Data.Queries;

namespace TFM.Nucleus.Data.SQL
{
    public class SqlDataSchema(SQLDataProvider sqlDataProvider, IObjectSchemaProvider objectSchemaProvider, ILogger<SqlDataSchema>? logger) : IDataSchema
    {
        private bool Initialized => Status != EDataSchemaStatus.NotInitialized;
        private bool IsCurrent => Status == EDataSchemaStatus.Ok;
        private readonly SQLDataProvider SqlDataProvider = sqlDataProvider;
        private readonly IObjectSchemaProvider ObjectSchemaProvider = objectSchemaProvider;
        private readonly ILogger? _logger = logger;

        public ConcurrentDictionary<Type, SqlTableSchema> TableCache = [];

        public EDataSchemaStatus Status { get; private set; } = EDataSchemaStatus.NotInitialized;

        public SqlTableSchema? GetTableSchema<T>() => GetTableSchema(typeof(T));
        public SqlTableSchema? GetTableSchema(Type objectType)
        {
            if(Status == EDataSchemaStatus.Ok)
            {
                return TableCache.TryGetValue(objectType, out SqlTableSchema? value) ? value : null;
            }
            if (Status == EDataSchemaStatus.NotInitialized)
                if (Initialize() == EDataSchemaStatus.Ok)
                    return TableCache.TryGetValue(objectType, out SqlTableSchema? value) ? value : null;
            
            throw new Exception("Cannot get SQL table schema. Schema error.");
        }


        public EDataSchemaStatus Initialize()
        {
            //try
            //{
                if (Status != EDataSchemaStatus.NotInitialized)
                    return Status;
                BuildSchema();
                if (!SqlDataProvider.SqlConfig.DatabaseExists())
                {
                    if (SqlDataProvider.NucleusOptions.ReadOnlySchema)
                    {
                        _logger?.LogError("Database does not exist and the schema is read-only. Cannot initialize.");
                        Status = EDataSchemaStatus.DatasourceNotFound;
                        return Status;
                    }
                    _logger?.LogInformation("Database does not exist. Creating database...");
                    SqlDataProvider.SqlConfig.CreateDatabase();
                }
                Status = CheckDatabaseSchema();
                if (Status == EDataSchemaStatus.NotCurrent && !SqlDataProvider.NucleusOptions.ReadOnlySchema)
                    Status = Update();
                return Status;
            //}
            //catch (Exception ex)
            //{
            //    _logger?.LogError(ex, "Error initializing SQL Data Schema. {error}", ex.Message);
            //    throw;
            //}
        }
        public async Task<EDataSchemaStatus> InitializeAsync(CancellationToken? cancellationToken = null)
        {
            //try
            //{
                if (Status != EDataSchemaStatus.NotInitialized)
                    return Status;
                BuildSchema();
                if (!await SqlDataProvider.SqlConfig.DatabaseExistsAsync(cancellationToken))
                {
                    if (SqlDataProvider.NucleusOptions.ReadOnlySchema)
                    { 
                        _logger?.LogError("Database does not exist and the schema is read-only. Cannot initialize.");
                        Status = EDataSchemaStatus.DatasourceNotFound;
                        return Status;
                    }
                    _logger?.LogInformation("Database does not exist. Creating database...");
                    await SqlDataProvider.SqlConfig.CreateDatabaseAsync(cancellationToken);
                }
                Status = await CheckDatabaseSchemaAsync(cancellationToken);
                if (Status == EDataSchemaStatus.NotCurrent && !SqlDataProvider.NucleusOptions.ReadOnlySchema)
                    Status = await UpdateAsync(cancellationToken);
                return Status;
            //}
            //catch (Exception ex)
            //{
            //    _logger?.LogError(ex, "Error initializing SQL Data Schema");
            //    Status = EDataSchemaStatus.Error;
            //    return Status;
            //}
        }

        // System columns backing [Deletable(DeleteBehavior.Soft)] types; they have no entity
        // property — DeleteQuery writes them and SelectQuery filters on them.
        private static readonly string[] SoftDeleteColumnNames = ["IsDeleted", "DeletedAt"];

        private static bool IsSoftDeletable(SqlTableSchema table) =>
            table.ObjectSchema is { IsDeletable: true, DeleteBehavior: DeleteBehavior.Soft };

        private static bool HasSoftDeleteColumns(string tableName, SqlColumn[] dbColumns) =>
            SoftDeleteColumnNames.All(name => dbColumns.Any(c =>
                c.TableName!.Equals(tableName, StringComparison.OrdinalIgnoreCase) &&
                c.Name!.Equals(name, StringComparison.OrdinalIgnoreCase)));

        private static string SoftDeleteColumnSql(SqlTableSchema tableSchema) =>
            $"[IsDeleted] [bit] NOT NULL CONSTRAINT [DF_{tableSchema.TableName}_IsDeleted] DEFAULT(0),[DeletedAt] [datetimeoffset](7) NULL, ";

        // Every column-level index Nucleus creates carries one of these prefixes. The schema
        // compare and sync only look at, drop or create indexes with these prefixes, so indexes a
        // DBA adds by hand are never touched.
        private const string NonClusteredIndexPrefix = "NCI_";
        private const string UniqueIndexPrefix = "UQ_";

        internal static string ColumnIndexName(SqlTableSchema table, SqlColumnSchema col) =>
            $"{(col.Unique ? UniqueIndexPrefix : NonClusteredIndexPrefix)}{table.TableName}_{col.ColumnName}";

        /// <summary>
        /// The CREATE INDEX statement for a column carrying NonClusteredIndex or Unique. Unique
        /// wins when both are set and produces one filtered unique index. NULLs are excluded by
        /// the filter because every Nucleus column is created NULL and an unfiltered unique index
        /// would allow only a single NULL row per table.
        /// </summary>
        internal static string ColumnIndexSql(SqlTableSchema table, SqlColumnSchema col)
        {
            if (!col.HasColumnIndex)
                throw new ArgumentException($"Column '{col.ColumnName}' does not carry a column index.", nameof(col));
            var name = ColumnIndexName(table, col);
            if (col.Unique)
                return $"CREATE UNIQUE NONCLUSTERED INDEX [{name}] ON [dbo].[{table.TableName}]" +
                       $"([{col.ColumnName}] {col.IndexSortOrder}) WHERE [{col.ColumnName}] IS NOT NULL " +
                       "WITH(PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, " +
                       "DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON[PRIMARY]";
            return $"CREATE NONCLUSTERED INDEX [{name}] ON [dbo].[{table.TableName}]" +
                   $"([{col.ColumnName}] {col.IndexSortOrder}) " +
                   "WITH(PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, " +
                   "DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON[PRIMARY]";
        }

        /// <summary>
        /// Compares the column indexes a table needs against the Nucleus-named indexes the
        /// database has. Returns the index names to drop and the columns whose index must be
        /// created. The schema check and the schema sync both use this so they cannot disagree.
        /// </summary>
        internal static (List<string> drop, List<SqlColumnSchema> create) DiffColumnIndexes(
            SqlTableSchema table, IEnumerable<SqlColumnIndex> dbIndexes)
        {
            var existing = dbIndexes
                .Where(x => x.TableName!.Equals(table.TableName, StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.IndexName!, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var required = table.Columns.Where(c => c.HasColumnIndex).ToList();
            List<string> drop = [];
            List<SqlColumnSchema> create = [];

            foreach (var group in existing)
            {
                var col = required.FirstOrDefault(c => ColumnIndexName(table, c).Equals(group.Key, StringComparison.OrdinalIgnoreCase));
                // A Nucleus index always has exactly one key column. Anything else with our prefix
                // is stale and gets rebuilt.
                if (col == null || group.Count() != 1 || !ColumnIndexMatches(col, group.First()))
                    drop.Add(group.Key);
            }
            foreach (var col in required)
            {
                var name = ColumnIndexName(table, col);
                if (!existing.Any(g => g.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
                    drop.Contains(name, StringComparer.OrdinalIgnoreCase))
                    create.Add(col);
            }
            return (drop, create);
        }

        private static bool ColumnIndexMatches(SqlColumnSchema col, SqlColumnIndex existing) =>
            existing.ColumnName!.Equals(col.ColumnName, StringComparison.OrdinalIgnoreCase) &&
            existing.IsUnique == col.Unique &&
            existing.HasFilter == col.Unique &&
            existing.IsDescending == col.IndexSortDescending;

        // SQL Server error 1505: CREATE UNIQUE INDEX terminated because a duplicate key was found.
        private static bool IsDuplicateKeyError(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
                if (e is SqlException sql && sql.Number == 1505)
                    return true;
            return false;
        }

        /// <summary>Compares the expected schema to the database without modifying anything.</summary>
        internal EDataSchemaStatus CheckSchema() => CheckDatabaseSchema();
        internal Task<EDataSchemaStatus> CheckSchemaAsync(CancellationToken? cancellationToken = null) =>
            CheckDatabaseSchemaAsync(cancellationToken);

        public List<SqlTableSchema> BuildSchema()
        {
            // A rooted provider only builds/migrates tables for entity types under its root.
            foreach (var objSchema in ObjectSchemaProvider.GetAll()!
                .Where(x => x.IsDatabaseEnabled && DataProviderRegistry.IsUnderRoot(x.Type, SqlDataProvider.RootType)))
            {
                if (!TableCache.ContainsKey(objSchema.Type))
                    TableCache[objSchema.Type] = new SqlTableSchema(objSchema, this);
            }
            return [.. TableCache.Values];
        }

        public IEnumerable<SqlTableSchema> GetSqlTableSchemas() => Initialized ? TableCache.Values : BuildSchema();

        private async Task<EDataSchemaStatus> CheckDatabaseSchemaAsync(CancellationToken? cancellationToken = null)
        {
            try
            {
                var dbTables = await SqlDataProvider.SqlConfig.GetDatabaseTableNamesAsync(cancellationToken);
                var dbColumns = await SqlDataProvider.SqlConfig.GetDatabaseColumnsAsync(cancellationToken: cancellationToken);
                var dtIndexes = await SqlDataProvider.SqlConfig.GetFullTextIndexesAsync(cancellationToken: cancellationToken);
                var dbIndexes = await SqlDataProvider.SqlConfig.GetColumnIndexesAsync(cancellationToken: cancellationToken);
                return CheckDatabaseSchema(dbTables, dbColumns, dtIndexes, dbIndexes);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error checking SQL Data schema");
                return EDataSchemaStatus.Error;
            }
        }
        private EDataSchemaStatus CheckDatabaseSchema(string[]? dbTables = null, SqlColumn[]? dbColumns = null,
            List<(string tableName, string ftCatalogName, string UniqueIdxName, string columnName)>? dtIndexes = null,
            List<SqlColumnIndex>? dbIndexes = null)
        {
            try
            {
                dbTables ??= SqlDataProvider.SqlConfig.GetDatabaseTableNames();
                dbColumns ??= SqlDataProvider.SqlConfig.GetDatabaseColumns();
                dtIndexes ??= SqlDataProvider.SqlConfig.GetFullTextIndexes();
                dbIndexes ??= SqlDataProvider.SqlConfig.GetColumnIndexes();
                var sqlSchemas = GetSqlTableSchemas();
                var softDeleteColumnsCurrent = true;
                foreach (var table in sqlSchemas)
                {
                    if (!dbTables.Contains(table.TableName, StringComparer.OrdinalIgnoreCase))
                    {
                        table.ExistsInDatabase = false;
                        _logger?.LogWarning("Database table schema is not current. '{tablename}' is missing.", table.TableName!);
                    }
                    else
                    {
                        table.ExistsInDatabase = true;
                        if (IsSoftDeletable(table) && !HasSoftDeleteColumns(table.TableName!, dbColumns))
                        {
                            softDeleteColumnsCurrent = false;
                            _logger?.LogWarning("Soft-delete system columns are missing from table: {tablename}", table.TableName);
                        }
                        foreach (var schemaColumn in table.Columns)
                        {
                            if (!dbColumns.Any(c => c.TableName!.Equals(table.TableName, StringComparison.OrdinalIgnoreCase) &&
                                c.Name!.Equals(schemaColumn.ColumnName, StringComparison.OrdinalIgnoreCase)))
                            {
                                schemaColumn.ExistsInDatabase = false;
                                _logger?.LogWarning("Database Column schema is not current for column: {columnname} in table: {tablename}", schemaColumn.ColumnName, table.TableName);
                            }
                            else
                            {
                                schemaColumn.ExistsInDatabase = true;
                                var dbColumn = dbColumns.First(c => c.TableName!.Equals(table.TableName, StringComparison.OrdinalIgnoreCase) &&
                                    c.Name!.Equals(schemaColumn.ColumnName, StringComparison.OrdinalIgnoreCase));
                                if (!schemaColumn.CheckColumn(dbColumn))
                                {
                                    schemaColumn.SchemaMatched = false;
                                    _logger?.LogWarning("Database Column schema is not current for column: {columnname} in table: {tablename}", schemaColumn.ColumnName, table.TableName);
                                }
                                else
                                {
                                    schemaColumn.SchemaMatched = true;
                                }
                            }
                        }
                    }
                }
                if (sqlSchemas.Any(x => !x.IsCurrent) || !softDeleteColumnsCurrent)
                {
                    _logger?.LogWarning("Database schema is not current.");
                    return EDataSchemaStatus.NotCurrent;
                }
                else
                {
                    //check full-text indexes
                    bool indexesMatch = true;
                    foreach (var table in sqlSchemas.Where(x => x.HasFullTextIndex))
                    {
                        var ftColumns = table.Columns.Where(x => x.FullTextIndex).Select(x => x.ColumnName).ToList();
                        var existingFtIndexes = dtIndexes.Where(x => x.tableName == table.TableName);
                        var tableIndexesMatch = true;
                        if (existingFtIndexes.Count() != ftColumns.Count)
                        {
                            tableIndexesMatch = false;
                        }
                        else
                        {
                            var existingFtColumns = existingFtIndexes.Select(x => x.columnName).ToList();
                            //check if any columns are missing
                            foreach (var col in ftColumns)
                                if (!existingFtColumns.Contains(col))
                                    tableIndexesMatch = false;
                            //check if any extra columns exist
                            foreach (var col in existingFtColumns)
                                if (!ftColumns.Contains(col))
                                    tableIndexesMatch = false;
                        }
                        if (!tableIndexesMatch)
                        {
                            _logger?.LogWarning("Full-text index schema is not current for table: {tablename}", table.TableName);
                            indexesMatch = false;
                        }
                    }
                    //check column indexes (NonClusteredIndex / Unique)
                    foreach (var table in sqlSchemas)
                    {
                        var (drop, create) = DiffColumnIndexes(table, dbIndexes);
                        if (drop.Count > 0 || create.Count > 0)
                        {
                            _logger?.LogWarning("Column index schema is not current for table: {tablename}. Missing or changed: {missing}. Unexpected: {unexpected}",
                                table.TableName, string.Join(", ", create.Select(c => ColumnIndexName(table, c))), string.Join(", ", drop));
                            indexesMatch = false;
                        }
                    }
                    if (!indexesMatch)
                        return EDataSchemaStatus.NotCurrent;
                }

                return EDataSchemaStatus.Ok;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error checking SQL Data schema. {error}", ex.Message);
                throw;
            }
        }

        public EDataSchemaStatus Update()
        {
            // Only call after checking schema is not current
            try
            {
                using var ctx = SqlDataProvider.CreateSqlDataContext(null, true, false, true);
                try
                {
                    var sqlSchema = GetSqlTableSchemas();
                    foreach (var tableSchema in sqlSchema.Where(x => !x.IsCurrent))
                    {
                        try
                        {
                            UpdateTableSchema(ctx, tableSchema);
                        }
                        catch (Exception ex)
                        {
                            ctx.Rollback();
                            _logger?.LogError(ex, "Error updating schema for table: {tablename}", tableSchema.TableName);
                            throw new Exception($"Error updating schema for table: {tableSchema.TableName}", ex);
                        }
                    }
                    ctx.Dispose();
                    UpdateTableColumnIndexes(sqlSchema);
                    UpdateTableFullTextIndexes(sqlSchema);
                    return EDataSchemaStatus.Ok;
                }
                catch (Exception ex)
                {
                    ctx.Rollback();
                    _logger?.LogError(ex, "Error updating SQL Data schema");
                    return EDataSchemaStatus.Error;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error updating SQL Data schema");
                return EDataSchemaStatus.Error;
            }
        }
        public async Task<EDataSchemaStatus> UpdateAsync(CancellationToken? cancellationToken = null)
        {
            // Only call after checking schema is not current
            try
            {
                using var ctx = await SqlDataProvider.CreateSqlDataContextAsync(null, true, false, cancellationToken ?? CancellationToken.None, true);
                try
                {
                    var sqlSchema = GetSqlTableSchemas();
                    foreach (var tableSchema in sqlSchema.Where(x => !x.IsCurrent))
                    {
                        try
                        {
                            await UpdateTableSchemaAsync(ctx, tableSchema, cancellationToken ?? CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            ctx.Rollback();
                            _logger?.LogError(ex, "Error updating schema for table: {tablename}", tableSchema.TableName);
                            throw new Exception($"Error updating schema for table: {tableSchema.TableName}", ex);
                        }
                    }
                    await ctx.DisposeAsync();
                    await UpdateTableColumnIndexesAsync(sqlSchema, cancellationToken);
                    await UpdateTableFullTextIndexesAsync(sqlSchema, cancellationToken);
                    return EDataSchemaStatus.Ok;
                }
                catch (Exception ex)
                {
                    ctx.Rollback();
                    _logger?.LogError(ex, "Error updating SQL Data schema");
                    return EDataSchemaStatus.Error;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error updating SQL Data schema");
                return EDataSchemaStatus.Error;
            }
        }

        // ---- Statement builders. Every DDL string the schema runs is built here, so the text can
        // ---- be pinned by golden tests apart from the code that executes it.

        internal static IReadOnlyList<string> CreateTableStatements(SqlTableSchema tableSchema)
        {
            var idColumn = tableSchema.IdentityColumn.ColumnName;
            //Create table
            var qry = $"CREATE TABLE [dbo].[{tableSchema.TableName}](" +
                $"[{idColumn}][uniqueidentifier] ROWGUIDCOL NOT NULL," +
                "[ClusterID] [int] IDENTITY(1,1) NOT FOR REPLICATION NOT NULL, ";
            foreach (var col in tableSchema.Columns)
                if (col.ColumnName != idColumn)
                    qry += $"[{col.ColumnName}] {col.SqlDataConstructor}";
            if (IsSoftDeletable(tableSchema))
                qry += SoftDeleteColumnSql(tableSchema);
            //add default primary key constraint
            qry += $" CONSTRAINT [PK_{tableSchema.TableName}] PRIMARY KEY NONCLUSTERED( [{idColumn}] ASC )WITH(PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, " +
                "IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY] ) ON [PRIMARY] ";
            if (qry.Contains("nvarchar(max)") || qry.Contains("varchar(max)") || qry.Contains("varbinary(max)"))
                qry += "TEXTIMAGE_ON [PRIMARY] ";
            if (qry.Contains("FILESTREAM"))
                qry += "FILESTREAM_ON [FileStream] ";
            List<string> statements = [qry];

            //add id generation default constraint
            statements.Add($"ALTER TABLE[dbo].[{tableSchema.TableName}] ADD CONSTRAINT[DF_{tableSchema.TableName}_{idColumn}]  DEFAULT(newid()) FOR[{idColumn}]");
            //create indexes
            statements.Add($"CREATE UNIQUE CLUSTERED INDEX [CIX_{tableSchema.TableName}_ClusterID] ON [dbo].[{tableSchema.TableName}](" +
                "[ClusterID] DESC)WITH(PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, " +
                "IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON[PRIMARY]");
            foreach (var col in tableSchema.Columns.Where(c => c.HasColumnIndex))
                statements.Add(ColumnIndexSql(tableSchema, col));
            return statements;
        }

        internal static string RenameTableSql(string tableName, string tempTableName) =>
            $"EXEC sp_rename '{tableName}', '{tempTableName}'";

        /// <summary>Renames the default constraints of the renamed table; binds the temp table name to <paramref name="tempTableVariable"/>.</summary>
        internal static string RenameDefaultConstraintsSql(string tempTableVariable) => @$"DECLARE @sql NVARCHAR(MAX) = '';

SELECT @sql = @sql + 'EXEC sp_rename N''dbo.[' + name + ']'', N''' + name + '_temp'', N''OBJECT'';' + CHAR(13)
FROM sys.objects
WHERE parent_object_id = OBJECT_ID(@{tempTableVariable}) AND type = 'D';

EXEC sp_executesql @sql;";

        /// <summary>Renames the indexes of the renamed table; binds @tempTableName and @tblname.</summary>
        internal const string RenameIndexesSql = @"DECLARE @sql NVARCHAR(MAX) = '';

SELECT @sql = @sql + 'EXEC sp_rename N''dbo.' + @tempTableName + '.' + name + ''', N''' + REPLACE(name, @tblname, @tempTableName) + ''', N''INDEX'';' + CHAR(13)
FROM sys.indexes
WHERE object_id = OBJECT_ID(@tempTableName) AND type_desc != 'HEAP' AND name IS NOT NULL;

EXEC sp_executesql @sql;";

        internal static string CopyTableDataSql(string sourceTableName, string destTableName, IReadOnlyList<string> commonColumns, bool orderByClusterId)
        {
            var qry = $"INSERT INTO {destTableName}({String.Join(", ", commonColumns)}) " +
                $"SELECT {String.Join(", ", commonColumns)} FROM {sourceTableName}";
            if (orderByClusterId)
                qry += " ORDER BY ClusterID ASC";
            return qry;
        }

        internal static string DropTableSql(string tableName) => $"DROP TABLE [dbo].[{tableName}]";

        //update timestamps if necessary. *only needed for legacy tables, may be deprecated in the future
        internal static string BackfillTimestampSql(string tableName) =>
            $"UPDATE [dbo].[{tableName}] SET [Timestamp] = SYSDATETIMEOFFSET() WHERE [Timestamp] IS NULL";

        internal static string DropColumnIndexSql(string indexName, string tableName) => $"DROP INDEX [{indexName}] ON [dbo].[{tableName}]";

        internal static string DropFullTextCatalogSql(string catalogName) => $"DROP FULLTEXT CATALOG [{catalogName}]";

        internal static string DropFullTextIndexSql(string tableName) => $"DROP FULLTEXT INDEX ON [dbo].[{tableName}]";

        internal static string CreateFullTextIndexSql(string tableName) =>
            $"CREATE FULLTEXT INDEX ON [dbo].[{tableName}] KEY INDEX [PK_{tableName}] ON ([ftc_{tableName}]) WITH CHANGE_TRACKING AUTO";

        internal static string AddFullTextColumnSql(string tableName, string columnName) =>
            $"ALTER FULLTEXT INDEX ON [dbo].[{tableName}] ADD([{columnName}] LANGUAGE [English])";

        /// <summary>The columns both tables share, minus the identity, which the new table generates.</summary>
        private static List<string> CommonColumns(SqlColumn[] sourceColumns, SqlColumn[] destColumns)
        {
            List<string> commoncolumns = [];
            foreach (var s in destColumns.Select(x => x.Name))
                if (sourceColumns.Any(x => x.Name!.Equals(s, StringComparison.OrdinalIgnoreCase) && !x.Name.Equals("ClusterID")))
                    commoncolumns.Add(s!);
            return commoncolumns;
        }

        private static bool HasClusterId(SqlColumn[] columns) =>
            columns.Any(x => x.Name!.Equals("ClusterID", StringComparison.OrdinalIgnoreCase));

        private void UpdateTableSchema(SqlDataContext context, SqlTableSchema tableSchema)
        {
            var tempTableName = $"{tableSchema.TableName}_{DateTime.Now.ToString("yyyyMMddHHmmss")}";
            if (tableSchema.ExistsInDatabase)
            {
                _logger?.LogInformation("Updating the Database Table schema for the '{objectName}' object to the latest version.", tableSchema.ObjectSchema!.FullName);
                RenameTable(context, tableSchema, tempTableName);
            }
            else
                _logger?.LogInformation("Creating the Database Table schema for the '{objectFullname}' object.", tableSchema.ObjectSchema!.FullName);

            //create new table
            CreateTable(context, tableSchema);

            //Copy Data
            if (tableSchema.ExistsInDatabase)
            {
                CopyTableData(context, tempTableName, tableSchema.TableName!);
                //drop old table
                DropTable(context, tempTableName);
                context.ExecuteSQL(BackfillTimestampSql(tableSchema.TableName));
            }
        }
        private async Task UpdateTableSchemaAsync(SqlDataContext context, SqlTableSchema tableSchema, CancellationToken? cancellationToken = null)
        {
            var tempTableName = $"{tableSchema.TableName}_{DateTime.Now.ToString("yyyyMMddHHmmss")}";
            if (tableSchema.ExistsInDatabase)
            {
                _logger?.LogInformation("Updating the Database Table schema for the '{objectName}' object to the latest version.", tableSchema.ObjectSchema!.FullName);
                await RenameTableAsync(context, tableSchema, tempTableName, cancellationToken);
            }
            else
                _logger?.LogInformation("Creating the Database Table schema for the '{objectFullname}' object.", tableSchema.ObjectSchema!.FullName);

            //create new table
            await CreateTableAsync(context, tableSchema, cancellationToken);

            //Copy Data
            if (tableSchema.ExistsInDatabase)
            {
                await CopyTableDataAsync(context, tempTableName, tableSchema.TableName!, cancellationToken);
                //drop old table
                await DropTableAsync(context, tempTableName, cancellationToken);
                await context.ExecuteSQLAsync(BackfillTimestampSql(tableSchema.TableName));
            }
        }

        private void CreateTable(SqlDataContext context, SqlTableSchema tableSchema)
        {
            foreach (var statement in CreateTableStatements(tableSchema))
                context.ExecuteSQL(statement);
        }
        private async Task CreateTableAsync(SqlDataContext context, SqlTableSchema tableSchema, CancellationToken? cancellationToken = null)
        {
            foreach (var statement in CreateTableStatements(tableSchema))
                await context.ExecuteSQLAsync(statement);
        }

        private void RenameTable(SqlDataContext context, SqlTableSchema tableSchema, string tempTableName)
        {
            context.ExecuteSQL(RenameTableSql(tableSchema.TableName, tempTableName));

            //rename default constraints
            var tempTableParam = new Parameter("tempTableName", tempTableName);
            context.ExecuteSQL(RenameDefaultConstraintsSql(tempTableParam.Variable!), tempTableParam);

            context.ExecuteSQL(RenameIndexesSql, new ParameterCollection(new Parameter("tempTableName", tempTableName, false), new Parameter("tblname", tableSchema.TableName!, false)));
        }
        private async Task RenameTableAsync(SqlDataContext context, SqlTableSchema tableSchema, string tempTableName, CancellationToken? cancellationToken = null)
        {
            await context.ExecuteSQLAsync(RenameTableSql(tableSchema.TableName, tempTableName));

            //rename default constraints
            var tempTableParam = new Parameter("tempTableName", tempTableName);
            await context.ExecuteSQLAsync(RenameDefaultConstraintsSql(tempTableParam.Variable!), tempTableParam);

            await context.ExecuteSQLAsync(RenameIndexesSql, new ParameterCollection(new Parameter("tempTableName", tempTableName, false), new Parameter("tblname", tableSchema.TableName!, false)));
        }

        private void CopyTableData(SqlDataContext context, string sourceTableName, string destTableName)
        {
            var sourceColumns = SqlDataProvider.SqlConfig.GetDatabaseColumns(sourceTableName, context);
            var destColumns = SqlDataProvider.SqlConfig.GetDatabaseColumns(destTableName, context);
            context.ExecuteSQL(CopyTableDataSql(sourceTableName, destTableName, CommonColumns(sourceColumns, destColumns), HasClusterId(sourceColumns)));
        }
        private async Task CopyTableDataAsync(SqlDataContext context, string sourceTableName, string destTableName, CancellationToken? cancellationToken = null)
        {
            var sourceColumns = await SqlDataProvider.SqlConfig.GetDatabaseColumnsAsync(sourceTableName, context, cancellationToken ?? CancellationToken.None);
            var destColumns = await SqlDataProvider.SqlConfig.GetDatabaseColumnsAsync(destTableName, context, cancellationToken ?? CancellationToken.None);
            await context.ExecuteSQLAsync(CopyTableDataSql(sourceTableName, destTableName, CommonColumns(sourceColumns, destColumns), HasClusterId(sourceColumns)));
        }

        private void DropTable(SqlDataContext context, string tableName)
        {
            context.ExecuteSQL(DropTableSql(tableName));
        }
        private async Task DropTableAsync(SqlDataContext context, string tableName, CancellationToken? cancellationToken = null)
        {
            await context.ExecuteSQLAsync(DropTableSql(tableName));
        }

        private void UpdateTableFullTextIndexes(IEnumerable<SqlTableSchema> sqlSchema)
        {
            //drop any unused full-text indexes
            var dbCatalogs = SqlDataProvider.SqlConfig.GetFullTextCatalogs(null);
            foreach (var ftc in dbCatalogs.Where(x => x.StartsWith("ftc_")))
            {
                var tblname = ftc.Substring(4); //remove 'ftc_' prefix
                if (!sqlSchema.Any(x => x.TableName == tblname && x.HasFullTextIndex))
                {
                    //drop full-text catalog
                    SqlDataProvider.SqlConfig.ExecuteQuery(DropFullTextCatalogSql(ftc), null);
                }
            }
            var ftTables = sqlSchema.Where(x => x.HasFullTextIndex).ToList();
            if (ftTables.Count == 0)
                return;

            //create any missing full-text catalogs
            foreach (var tableSchema in ftTables)
                if (dbCatalogs != null && !dbCatalogs.Contains($"ftc_{tableSchema.TableName}"))
                    SqlDataProvider.SqlConfig.CreateFullTextCatalog($"ftc_{tableSchema.TableName}");

            //get all existing full-text indexes
            var ftIndexes = SqlDataProvider.SqlConfig.GetFullTextIndexes(null);

            foreach (var tableSchema in ftTables)
            {
                var ftColumns = tableSchema.Columns.Where(x => x.FullTextIndex).Select(x => x.ColumnName).ToList();
                var existingFtIndexes = ftIndexes.Where(x => x.tableName == tableSchema.TableName);
                bool needsRebuild = false;
                if (existingFtIndexes.Count() != ftColumns.Count)
                {
                    needsRebuild = true;
                }
                else
                {
                    var existingFtColumns = existingFtIndexes.Select(x => x.columnName).ToList();
                    //check if any columns are missing
                    foreach (var col in ftColumns)
                        if (!existingFtColumns.Contains(col))
                            needsRebuild = true;
                    //check if any extra columns exist
                    foreach (var col in existingFtColumns)
                        if (!ftColumns.Contains(col))
                            needsRebuild = true;
                }
                if (needsRebuild)
                {
                    //drop existing full-text index
                    if (SqlDataProvider.SqlConfig.TableHasFtIndex(tableSchema.TableName!))
                        SqlDataProvider.SqlConfig.ExecuteQuery(DropFullTextIndexSql(tableSchema.TableName), null);
                    //recreate full-text index
                    SqlDataProvider.SqlConfig.ExecuteQuery(CreateFullTextIndexSql(tableSchema.TableName), null);
                    foreach (var col in tableSchema.Columns.Where(x => x.FullTextIndex))
                        SqlDataProvider.SqlConfig.ExecuteQuery(AddFullTextColumnSql(tableSchema.TableName, col.ColumnName), null);
                }
            }
        }
        private async Task UpdateTableFullTextIndexesAsync(IEnumerable<SqlTableSchema> sqlSchema, CancellationToken? cancellationToken = null)
        {
            //drop any unused full-text indexes
            var dbCatalogs = await SqlDataProvider.SqlConfig.GetFullTextCatalogsAsync(null, cancellationToken);
            foreach (var ftc in dbCatalogs.Where(x => x.StartsWith("ftc_")))
            {
                var tblname = ftc.Substring(4); //remove 'ftc_' prefix
                if (!sqlSchema.Any(x => x.TableName == tblname && x.HasFullTextIndex))
                {
                    //drop full-text catalog
                    await SqlDataProvider.SqlConfig.ExecuteQueryAsync(DropFullTextCatalogSql(ftc), null, cancellationToken);
                }
            }
            var ftTables = sqlSchema.Where(x => x.HasFullTextIndex).ToList();
            if (ftTables.Count == 0)
                return;

            //create any missing full-text catalogs
            foreach (var tableSchema in ftTables)
                if (dbCatalogs != null && !dbCatalogs.Contains($"ftc_{tableSchema.TableName}"))
                    await SqlDataProvider.SqlConfig.CreateFullTextCatalogAsync($"ftc_{tableSchema.TableName}", cancellationToken);

            //get all existing full-text indexes
            var ftIndexes = await SqlDataProvider.SqlConfig.GetFullTextIndexesAsync(null, cancellationToken);

            foreach (var tableSchema in ftTables)
            {
                var ftColumns = tableSchema.Columns.Where(x => x.FullTextIndex).Select(x => x.ColumnName).ToList();
                var existingFtIndexes = ftIndexes.Where(x => x.tableName == tableSchema.TableName);
                bool needsRebuild = false;
                if (existingFtIndexes.Count() != ftColumns.Count)
                {
                    needsRebuild = true;
                }
                else
                {
                    var existingFtColumns = existingFtIndexes.Select(x => x.columnName).ToList();
                    //check if any columns are missing
                    foreach (var col in ftColumns)
                        if (!existingFtColumns.Contains(col))
                            needsRebuild = true;
                    //check if any extra columns exist
                    foreach (var col in existingFtColumns)
                        if (!ftColumns.Contains(col))
                            needsRebuild = true;
                }
                if (needsRebuild)
                {
                    //drop existing full-text index
                    if (await SqlDataProvider.SqlConfig.TableHasFtIndexAsync(tableSchema.TableName!))
                        await SqlDataProvider.SqlConfig.ExecuteQueryAsync(DropFullTextIndexSql(tableSchema.TableName), null, cancellationToken);
                    //recreate full-text index
                    await SqlDataProvider.SqlConfig.ExecuteQueryAsync(CreateFullTextIndexSql(tableSchema.TableName), null, cancellationToken);
                    foreach (var col in tableSchema.Columns.Where(x => x.FullTextIndex))
                        await SqlDataProvider.SqlConfig.ExecuteQueryAsync(AddFullTextColumnSql(tableSchema.TableName, col.ColumnName), null, cancellationToken);
                }
            }
        }


        /// <summary>
        /// Brings the NonClusteredIndex / Unique indexes of every table in line with the entity
        /// model: drops Nucleus-named indexes that are no longer required or whose definition
        /// changed, then creates the missing ones. Runs after the table rebuilds, so a table that
        /// was just recreated already matches and is left alone.
        /// </summary>
        private void UpdateTableColumnIndexes(IEnumerable<SqlTableSchema> sqlSchema)
        {
            var dbIndexes = SqlDataProvider.SqlConfig.GetColumnIndexes(null);
            foreach (var table in sqlSchema)
            {
                var (drop, create) = DiffColumnIndexes(table, dbIndexes);
                foreach (var indexName in drop)
                {
                    _logger?.LogInformation("Dropping column index {index} on table {tablename}.", indexName, table.TableName);
                    SqlDataProvider.SqlConfig.ExecuteQuery(DropColumnIndexSql(indexName, table.TableName), null);
                }
                foreach (var col in create)
                {
                    _logger?.LogInformation("Creating column index {index} on table {tablename}.", ColumnIndexName(table, col), table.TableName);
                    try
                    {
                        SqlDataProvider.SqlConfig.ExecuteQuery(ColumnIndexSql(table, col), null);
                    }
                    catch (Exception ex) when (IsDuplicateKeyError(ex))
                    {
                        throw DuplicateKeyFailure(table, col, ex);
                    }
                }
            }
        }
        private async Task UpdateTableColumnIndexesAsync(IEnumerable<SqlTableSchema> sqlSchema, CancellationToken? cancellationToken = null)
        {
            var dbIndexes = await SqlDataProvider.SqlConfig.GetColumnIndexesAsync(null, cancellationToken);
            foreach (var table in sqlSchema)
            {
                var (drop, create) = DiffColumnIndexes(table, dbIndexes);
                foreach (var indexName in drop)
                {
                    _logger?.LogInformation("Dropping column index {index} on table {tablename}.", indexName, table.TableName);
                    await SqlDataProvider.SqlConfig.ExecuteQueryAsync(DropColumnIndexSql(indexName, table.TableName), null, cancellationToken);
                }
                foreach (var col in create)
                {
                    _logger?.LogInformation("Creating column index {index} on table {tablename}.", ColumnIndexName(table, col), table.TableName);
                    try
                    {
                        await SqlDataProvider.SqlConfig.ExecuteQueryAsync(ColumnIndexSql(table, col), null, cancellationToken);
                    }
                    catch (Exception ex) when (IsDuplicateKeyError(ex))
                    {
                        throw DuplicateKeyFailure(table, col, ex);
                    }
                }
            }
        }

        // Existing rows violate a newly required unique index. The data is never touched; the
        // caller must resolve the duplicates by hand and initialise again.
        private InvalidOperationException DuplicateKeyFailure(SqlTableSchema table, SqlColumnSchema col, Exception inner)
        {
            var message = $"Cannot create unique index on [{table.TableName}].[{col.ColumnName}]: the column already contains duplicate values. " +
                          "Remove or change the duplicate rows and initialise the schema again. No data was modified.";
            _logger?.LogError(inner, "{message}", message);
            return new InvalidOperationException(message, inner);
        }

        #region IDataSchema
        IEnumerable<IDataTableSchema> IDataSchema.GetTables() => GetSqlTableSchemas();
        IDataTableSchema? IDataSchema.GetTableSchema<T>() => GetTableSchema<T>();
        IDataTableSchema? IDataSchema.GetTableSchema(Type objectType) => GetTableSchema(objectType);
        public IEnumerable<IDataTableSchema> GetTableSchemas() => GetSqlTableSchemas();
        public bool IsCompatible(IEnumerable<IObjectSchema> objSchemas) =>
            objSchemas.All(schema => GetSqlTableSchemas().Any(table => table.ObjectSchema.Type == schema.Type && table.IsCurrent));
        #endregion


    }

    public class SqlTableSchema : IDataTableSchema
    {
        public readonly string TableName;
        public readonly IObjectSchema ObjectSchema;
        private readonly Dictionary<string,SqlColumnSchema> SqlColumns = [];

        public SqlDataSchema ParentSchema => _parentSchema ?? throw new InvalidOperationException("Parent schema not set");
        private SqlDataSchema? _parentSchema;

        public IEnumerable<SqlColumnSchema> Columns => SqlColumns.Values;
        public SqlColumnSchema IdentityColumn =>
            identityColumn ??= SqlColumns.Values.FirstOrDefault(x => x.PrimaryKey)
            ?? throw new InvalidOperationException($"No identity column found for table {TableName}");

        private SqlColumnSchema? identityColumn;
        public bool ExistsInDatabase { get; internal set; } = false;
        public bool IsCurrent => ExistsInDatabase && Columns.All(c => c.IsCurrent);
        public bool HasFullTextIndex => Columns.Any(c => c.FullTextIndex);
        public bool Equals(SqlTableSchema? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return string.Equals(TableName, other.TableName, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object? obj) => Equals(obj as SqlTableSchema);

        public override int GetHashCode() =>
            TableName.ToUpperInvariant().GetHashCode();
        //public bool IsCompatible(IObjectSchema objSchema)
        //{
        //    if(objSchema.AllDBColumns.Count() != Columns.Count())
        //        return false;
        //    if(!objSchema.AllDBColumns.All(x => Columns.Any(c => c.ColumnName == x.Name)))
        //        return false;

        //    foreach (var propertySchema in objSchema.AllDBColumns)
        //    {
        //        if (Columns.First(c => c.ColumnName == propertySchema.Name).IsCompatible(propertySchema) == false)
        //            return false;
        //    }
        //    return true;
        //}

        public SqlTableSchema(IObjectSchema objectSchema, SqlDataSchema parentSchema)
        {
            TableName = $"tbl_{objectSchema.FullName.Replace((char)46, (char)95).Replace((char)43, (char)95)}";
            ObjectSchema = objectSchema;
            _parentSchema = parentSchema;
            foreach (var propertySchema in objectSchema.AllDBColumns)
                SqlColumns[propertySchema.Name] = (new SqlColumnSchema(propertySchema, this));

            // The entity model is built around the Guid ID column CoreObject provides: newid()
            // default, ROWGUIDCOL, Get by ID and the cache index all assume it. The PrimaryKey
            // flag is therefore reserved for ID, and a type that marks anything else, or nothing,
            // fails here at schema build rather than at the first update or delete.
            var pkColumns = SqlColumns.Values.Where(c => c.PrimaryKey).ToList();
            if (pkColumns.Count != 1)
                throw new InvalidOperationException(
                    $"Type '{objectSchema.FullName}' must have exactly one [DatabaseColumn(PrimaryKey = true)] property, found {pkColumns.Count}" +
                    (pkColumns.Count == 0 ? "." : $": {string.Join(", ", pkColumns.Select(c => c.PropertySchema.Name))}.") +
                    " The primary key is the ID column CoreObject provides.");
            if (!pkColumns[0].ColumnName.Equals("ID", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Type '{objectSchema.FullName}' marks '{pkColumns[0].PropertySchema.Name}' as PrimaryKey. " +
                    "The primary key is reserved for the ID column CoreObject provides.");
        }
        public SqlColumnSchema? FindByPropertyName(string propertyName)
        { 
            if(SqlColumns.TryGetValue(propertyName, out var columnSchema))
                return columnSchema;
            return null;
        }
        public SqlColumnSchema? FindByColumnName(string columnName)
        { 
            if(SqlColumns.Values.FirstOrDefault(x => x.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase)) is SqlColumnSchema columnSchema)
                return columnSchema;
            return null;
        }
        public bool TryGetValueByColumn(string columnName, out IDataColumnSchema? dataTableSchema) =>
            (dataTableSchema = FindByColumnName(columnName)) != null;
        public bool TryGetValueByProperty(string propertyName, out IDataColumnSchema? dataTableSchema) =>
            SqlColumns.TryGetValue(propertyName, out var columnSchema) ? (dataTableSchema = columnSchema) != null : (dataTableSchema = null) != null;

        public SqlColumnSchema this[string columnName] => SqlColumns[columnName];


        #region IDataTableSchema
        string IDataTableSchema.TableName => TableName;
        IObjectSchema IDataTableSchema.ObjectSchema => ObjectSchema;
        //bool IDataTableSchema.IsCompatible(IObjectSchema objSchema) => throw new NotImplementedException();
        IEnumerable<IDataColumnSchema> IDataTableSchema.Columns => Columns;
        IDataColumnSchema? IDataTableSchema.IdentityColumn => IdentityColumn;
        IDataColumnSchema? IDataTableSchema.FindByColumnName(string columnName) => FindByColumnName(columnName);
        IDataColumnSchema? IDataTableSchema.FindByPropertyName(string propertyName) => FindByPropertyName(propertyName);
        IDataColumnSchema IDataTableSchema.this[string columnName] => this[columnName];
        bool IDataTableSchema.TryGetValueByColumn(string columnName, out IDataColumnSchema? dataTableSchema) => 
            TryGetValueByColumn(columnName, out dataTableSchema);
        bool IDataTableSchema.TryGetValueByProperty(string propertyName, out IDataColumnSchema? dataTableSchema) => 
            TryGetValueByProperty(propertyName, out dataTableSchema);
        #endregion

        public override string ToString()
        {
            return TableName ?? "null";
        }


    }

    public class SqlColumn
    {
        public string? Name;
        public string? TableName;
        public SqlColumnSchema.ESqlDataType DataType;
        public int DataLength = 0;
        public int Precision = 0;
        public int Scale = 0;
        public bool IsNullable = true;
    }

    /// <summary>One key column of a Nucleus-named nonclustered index as read from sys.indexes.</summary>
    public class SqlColumnIndex
    {
        public string? TableName;
        public string? IndexName;
        public string? ColumnName;
        public bool IsUnique;
        public bool IsDescending;
        public bool HasFilter;
    }

    public class SqlColumnSchema : IDataColumnSchema
    {
        public SqlTableSchema ParentTable => _parentTable ?? throw new InvalidOperationException("Parent table not set");
        private SqlTableSchema? _parentTable;
        public readonly string ColumnName;
        public readonly ESqlDataType DataType;
        public readonly IPropertySchema PropertySchema;
        public Type ClrType => _clrType;
        private Type _clrType;
        public string? SqlDataConstructor => MSSQLConstructor();
        public readonly int? DataLength;
        public readonly string? SqlDataSize;
        public readonly int? Precision;
        public readonly int? Scale;
        public readonly bool IsNullable = true;
        public bool ExistsInDatabase { get; internal set; } = false;
        public bool SchemaMatched { get; internal set; } = false;
        public bool RequiresCast { get; internal set; } = false;
        public bool IsCurrent => ExistsInDatabase && SchemaMatched;
        public readonly bool Filestream = false;

        public bool PrimaryKey = false;
        public bool FullTextIndex = false;
        public bool Unique = false;
        public bool NonClusteredIndex = false;
        public readonly bool IndexSortDescending = false;
        public string? IndexSortOrder = null;
        /// <summary>True when the column gets a nonclustered index of its own (plain or unique).</summary>
        public bool HasColumnIndex => NonClusteredIndex || Unique;

        #region IDataColumnSchema
        string IDataColumnSchema.ColumnName => ColumnName;
        bool IDataColumnSchema.IsNullable => IsNullable;
        Type IDataColumnSchema.DataType => PropertySchema.Type;
        IPropertySchema IDataColumnSchema.PropertySchema => PropertySchema;
        #endregion


        public SqlColumnSchema(IPropertySchema propertySchema, SqlTableSchema parentTable)
        {
            var attr = propertySchema.DatabaseColumnAttribute ?? throw new Exception("PropertySchema does not have a DatabaseColumnAttribute defined.");
            PropertySchema = propertySchema;
            ColumnName = propertySchema.Name;
            DataType = MSSQLDataType(propertySchema);
            _parentTable = parentTable;
            DataLength = attr.DataLength ?? MSSQLDefaultLength();
            Scale = attr.DecimalPlaces ?? MSSQLDefaultScale();
            Precision = DataLength - Scale;
            SqlDataSize = MSSQLDataSize();
            Filestream = attr.Filestream;
            PrimaryKey = attr.PrimaryKey;
            Unique = attr.Unique;
            FullTextIndex = attr.FullTextIndex;
            NonClusteredIndex = attr.NonClusteredIndex;
            _clrType = GetClrType(propertySchema);
            RequiresCast = WillRequireCast(propertySchema);
            IndexSortDescending = attr.IndexSortDESC;
            if (HasColumnIndex)
            {
                // SQL Server cannot key an index on a max-length or FILESTREAM column. Fail at
                // schema build with a readable message instead of a raw SQL error inside Update.
                if (DataLength == -1 || Filestream)
                    throw new InvalidOperationException(
                        $"Property '{propertySchema.Name}' on '{parentTable.ObjectSchema.FullName}' cannot carry NonClusteredIndex or Unique: " +
                        "a max-length or FILESTREAM column cannot be an index key.");
                IndexSortOrder = IndexSortDescending ? "DESC" : "ASC";
            }

            //    propertySchema.Name,
            //    propertySchema.DatabaseColumnAttribute?.DataType.ToString() ?? "NotSpecified",
            //    propertySchema.DatabaseColumnAttribute?.DataLength ?? 0,
            //    propertySchema.DatabaseColumnAttribute?.DecimalPlaces ?? 0,
            //    propertySchema.DatabaseColumnAttribute?.Scale ?? 0

        }
        //public bool IsCompatible(IPropertySchema propertySchema) => CheckColumn(propertySchema);

        protected internal bool CheckColumn(SqlColumn sqlColumn)
        {
            if(sqlColumn.DataType != DataType)
                return false;
            if (IsSqlDataTypeWithPrecisionScale())
            {
                if (sqlColumn.Precision != Precision)
                    return false;
                if (sqlColumn.Scale != Scale)
                    return false;
            }
            else if(IsSqlDataTypeWithSize() && sqlColumn.DataLength != DataLength)
                return false;

            
            return true;



            //if (propertySchema.DatabaseColumnAttribute.DataType == Nucleus.Data.DataType.NotSpecified)
            //{
            //    //TO DO tidy up and make efficient
            //    if (propertySchema.Type == typeof(string) 
            //        && (DataType != "varchar" || (propertySchema.DatabaseColumnAttribute.DataLength != 0 && DataLength != propertySchema.DatabaseColumnAttribute.DataLength) 
            //        || (propertySchema.DatabaseColumnAttribute.DataLength == 0 && DataLength != 255)
            //        ))
            //        return false;
            //    else if (propertySchema.Type == typeof(int) && DataType != "int")
            //        return false;
            //    else if (propertySchema.Type == typeof(long) && DataType != "bigint")
            //        return false;
            //    else if (propertySchema.Type == typeof(DateTimeOffset) && DataType != "datetimeoffset")
            //        return false;
            //    else if (propertySchema.Type == typeof(Guid) && DataType != "uniqueidentifier")
            //        return false;
            //    else if (propertySchema.Type == typeof(bool) && DataType != "bit")
            //        return false;
            //    else if (propertySchema.Type == typeof(DateTime) && DataType != "datetime2")
            //        return false;
            //    else if (propertySchema.Type == typeof(decimal) && (DataType != "decimal" || Scale != propertySchema.DatabaseColumnAttribute.DecimalPlaces))
            //        return false;
            //    else if (propertySchema.Type == typeof(byte[]) 
            //        && (DataType != "varbinary" || (propertySchema.DatabaseColumnAttribute.DataLength != 0 && DataLength != propertySchema.DatabaseColumnAttribute.DataLength) 
            //        || (propertySchema.DatabaseColumnAttribute.DataLength == 0 && DataLength != -1)
            //        ))
            //        return false;
            //    else if (propertySchema.Type.IsEnum && DataType != "int")
            //        return false;

            //}
            //else
            //{
            //    if (propertySchema.DatabaseColumnAttribute.DataType == Data.DataType.Unicode 
            //        && (DataType != "nvarchar" || (propertySchema.DatabaseColumnAttribute.DataLength != 0 && propertySchema.DatabaseColumnAttribute.DataLength != DataLength))
            //        )
            //        return false;
            //    else if (propertySchema.DatabaseColumnAttribute.DataType == Data.DataType.Date && DataType != "date")
            //        return false;
            //    else if (propertySchema.DatabaseColumnAttribute.DataType == Data.DataType.Integer && DataType != "int")
            //        return false;
            //}
            //return true;
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
            if (_clrType == typeof(Guid) && value is ICoreObject icoreObj)
                return icoreObj.ID;
            if (_clrType == typeof(string) && value is IDatabaseStringValue dbStr)
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

        private static ESqlDataType MSSQLDataType(IPropertySchema propertySchema)
        {
            var dt = propertySchema.DatabaseColumnAttribute!.DataType;
            if (dt == Data.DataType.NotSpecified)
            {
                return MSSQLDataType(propertySchema.Type);
            }
            else if (dt == Data.DataType.Date)
                return ESqlDataType.Date;
            else if (dt == Data.DataType.Integer)
                return ESqlDataType.Int;
            else if (dt == Data.DataType.Unicode)
                return ESqlDataType.NVarChar;
            throw new Exception("Unknown property type '" + dt.ToString() + "' unable to select a  matching sql datatype.");
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
        private static ESqlDataType MSSQLDataType(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(string))
                return ESqlDataType.VarChar;
            else if (type == typeof(int))
                return ESqlDataType.Int;
            else if (type == typeof(double))
                return ESqlDataType.Float;
            else if (type == typeof(bool))
                return ESqlDataType.Bit;
            else if (type == typeof(DateTime))
                return ESqlDataType.DateTime2;
            else if (type == typeof(DateTimeOffset))
                return ESqlDataType.DateTimeOffset;
            else if (type == typeof(Guid))
                return ESqlDataType.UniqueIdentifier;
            else if (type == typeof(byte[]))
                return ESqlDataType.VarBinary;
            else if (type == typeof(decimal))
                return ESqlDataType.Decimal;
            else if (type == typeof(long))
                return ESqlDataType.BigInt;
            else if (type.Name.Contains("TFMJsonString"))
                return ESqlDataType.Json;
            else if (type.IsEnum)
                return ESqlDataType.Int;
            else if (typeof(ICoreObject).IsAssignableFrom(type))
                return ESqlDataType.UniqueIdentifier;
            else if (typeof(IDatabaseStringValue).IsAssignableFrom(type))
                return ESqlDataType.VarChar;
            throw new Exception("Unknown property type '" + type.Namespace + "." + type.Name + "' unable to select a  matching sql datatype.");
        }
        
        private string MSSQLDataSize()
        {
            if (DataLength == null)
                return "";
            else if (DataLength == -1)
                return "max";
            else if (DataLength > 0)
                return DataLength.ToString()!;
            else
                throw new Exception("Unable to determine sql data size.");
        }
        private int? MSSQLDefaultLength()
        {
            if (DataType == ESqlDataType.VarChar || DataType == ESqlDataType.Char)
                return 255;
            else if (DataType == ESqlDataType.NVarChar || DataType == ESqlDataType.NChar)
                return 510;
            else if (DataType == ESqlDataType.DateTimeOffset)
                return 7;
            else if (DataType == ESqlDataType.DateTime2)
                return 7;
            else if (DataType == ESqlDataType.VarBinary)
                return -1;
            else if(DataType == ESqlDataType.Decimal ||
                   DataType == ESqlDataType.Numeric)
                return 20;
            return null;
        }
        private int? MSSQLDefaultScale()
        {
            if (DataType == ESqlDataType.Decimal ||
                DataType == ESqlDataType.Numeric)
                return 5;
            return null;
        }
        private bool IsSqlDataTypeWithSize()
        {
            return DataType == ESqlDataType.VarChar ||
                   DataType == ESqlDataType.NVarChar ||
                   DataType == ESqlDataType.Decimal ||
                   DataType == ESqlDataType.NChar ||
                   DataType == ESqlDataType.Char ||
                   DataType == ESqlDataType.Numeric ||
                   DataType == ESqlDataType.VarBinary;
        }
        private bool IsSqlDataTypeWithPrecisionScale()
        {
            return DataType == ESqlDataType.Decimal ||
                   DataType == ESqlDataType.Numeric;
        }

        private string MSSQLConstructor()
        {
            var constr = $"[{DataType}]";
            if(IsSqlDataTypeWithPrecisionScale())
            {
                constr += $"({Precision},{Scale})";
            }
            else if (!string.IsNullOrEmpty(SqlDataSize))
                constr += $"({SqlDataSize})";
            if (Filestream && DataType == ESqlDataType.VarBinary)
                constr += " FILESTREAM ";
            constr += " NULL, ";
            return constr;
        }







        //private string GetDataTypeString(Type type)
        //{
        //    if (type == typeof(string))
        //        return "nvarchar";
        //    else if (type == typeof(int))
        //        return "int";
        //    else if (type == typeof(long))
        //        return "bigint";
        //    else if (type == typeof(DateTimeOffset))
        //        return "datetimeoffset";
        //    else if (type == typeof(Guid))
        //        return "uniqueidentifier";
        //    else if (type == typeof(bool))
        //        return "bit";
        //    else if (type == typeof(DateTime))
        //        return "datetime2";
        //    else if (type == typeof(decimal))
        //        return "decimal";
        //    else if (type == typeof(byte[]))
        //        return "varbinary(max)";
        //    else if (type.IsEnum)
        //        return "int"; // Enums are stored as integers
        //    throw new NotSupportedException($"Type {type.FullName} is not supported.");
        //}
        //private string GetDataTypeString(Nucleus.DataType dataType)
        //{

        //    return dataType switch
        //    {
        //        Nucleus.DataType.Unicode => "nvarchar",
        //        Nucleus.DataType.Date => "date",
        //        Nucleus.DataType.Integer => "int",
        //        Nucleus.DataType.Binary => "varbinary",
        //        Nucleus.DataType.LargeBinary => "varbinary(max)",
        //        _ => throw new NotSupportedException($"Data type {dataType} is not supported.")
        //    };
        //}

        public override string ToString()
        {
            return ColumnName ?? "null";
        }

        public enum ESqlDataType
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

    }


}
