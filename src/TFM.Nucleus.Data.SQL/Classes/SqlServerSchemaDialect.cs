using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.Queries;
using TFM.Nucleus.Data.Schema;

namespace TFM.Nucleus.Data.SQL
{
    /// <summary>
    /// Every DDL string the SQL Server provider runs. The statements are pinned byte for byte by
    /// the raw SQL Server goldens, so existing databases see exactly the schema they always had.
    /// </summary>
    internal sealed class SqlServerSchemaDialect(ISchemaExtras? extras) : ISchemaDialect
    {
        public StringComparer IdentifierComparer => StringComparer.OrdinalIgnoreCase;

        public string PhysicalName(string logicalName) => logicalName;

        public bool ColumnMatches(ColumnSchema column, CatalogColumn existing) => SqlServerTypeMapper.Matches(column, existing);

        public ISchemaExtras? Extras { get; } = extras;

        public IReadOnlyList<SchemaStatement> CreateTable(TableSchema table) =>
            [.. CreateTableStatements(table).Select(s => (SchemaStatement)s)];

        public IReadOnlyList<SchemaStatement> RenameForRebuild(TableSchema table, string tempTableName)
        {
            var tempTableParam = new Parameter("tempTableName", tempTableName);
            return
            [
                RenameTableSql(table.TableName, tempTableName),
                //rename default constraints
                new SchemaStatement(RenameDefaultConstraintsSql(tempTableParam.Variable!), [tempTableParam]),
                new SchemaStatement(RenameIndexesSql, new ParameterCollection(
                    new Parameter("tempTableName", tempTableName, false), new Parameter("tblname", table.TableName, false))),
            ];
        }

        // A FILESTREAM column needs the table to have a FILESTREAM filegroup, which only a table
        // created with one has, so a table gaining one is rebuilt.
        public IReadOnlyList<SchemaStatement>? AddColumns(TableSchema table, IReadOnlyList<ColumnSchema> columns, IReadOnlyList<string> softDeleteColumns) =>
            columns.Any(SqlServerTypeMapper.IsFileStream) ? null : [AddColumnsSql(table, columns, softDeleteColumns)];

        public SchemaStatement CopyRows(string sourceTableName, string destTableName, IReadOnlyList<string> columns, bool orderByClusterId) =>
            CopyTableDataSql(sourceTableName, destTableName, columns, orderByClusterId);

        public SchemaStatement DropTable(string tableName) => DropTableSql(tableName);

        public SchemaStatement BackfillTimestamp(TableSchema table) => BackfillTimestampSql(table.TableName);

        public SchemaStatement CreateIndex(TableSchema table, ColumnSchema column) => ColumnIndexSql(table, column);

        public SchemaStatement DropIndex(TableSchema table, string physicalIndexName) => DropColumnIndexSql(physicalIndexName, table.TableName);

        public SchemaStatement CreateTableIndex(TableSchema table, TableIndexSchema index) => TableIndexSql(table, index);

        // SQL Server error 1505: CREATE UNIQUE INDEX terminated because a duplicate key was found.
        public bool IsDuplicateKeyOnIndexBuild(Exception exception)
        {
            for (var e = exception; e != null; e = e.InnerException)
                if (e is SqlException sql && sql.Number == 1505)
                    return true;
            return false;
        }

        // ---- Statement builders.

        private static string SoftDeleteColumnSql(TableSchema tableSchema) =>
            $"{SoftDeleteColumnDefinition(tableSchema, SoftDelete.IsDeletedColumn)},{SoftDeleteColumnDefinition(tableSchema, SoftDelete.DeletedAtColumn)}, ";

        /// <summary>One soft-delete system column, as CREATE TABLE and ALTER TABLE ADD both write it.</summary>
        private static string SoftDeleteColumnDefinition(TableSchema tableSchema, string column) => column switch
        {
            SoftDelete.IsDeletedColumn => $"[{SoftDelete.IsDeletedColumn}] [bit] NOT NULL CONSTRAINT [DF_{tableSchema.TableName}_{SoftDelete.IsDeletedColumn}] DEFAULT(0)",
            SoftDelete.DeletedAtColumn => $"[{SoftDelete.DeletedAtColumn}] [datetimeoffset](7) NULL",
            _ => throw new ArgumentException($"'{column}' is not a soft-delete system column.", nameof(column)),
        };

        /// <summary>
        /// Adds columns to an existing table in one statement. A NULL column is a metadata-only
        /// change at any table size. IsDeleted is NOT NULL with a constant default, which is
        /// metadata-only on Enterprise, Developer and Azure SQL and fills every row on the other
        /// editions. Every name is bracketed, as CREATE TABLE writes it.
        /// </summary>
        internal static string AddColumnsSql(TableSchema tableSchema, IReadOnlyList<ColumnSchema> columns, IReadOnlyList<string> softDeleteColumns)
        {
            if (columns.Count == 0 && softDeleteColumns.Count == 0)
                throw new ArgumentException("There are no columns to add.", nameof(columns));
            var definitions = columns.Select(c => $"[{c.ColumnName}] {SqlServerTypeMapper.TypeDefinition(c)}")
                .Concat(softDeleteColumns.Select(c => SoftDeleteColumnDefinition(tableSchema, c)));
            return $"ALTER TABLE [dbo].[{tableSchema.TableName}] ADD {string.Join(", ", definitions)}";
        }

        internal static IReadOnlyList<string> CreateTableStatements(TableSchema tableSchema)
        {
            var idColumn = tableSchema.IdentityColumn.ColumnName;
            //Create table
            var qry = $"CREATE TABLE [dbo].[{tableSchema.TableName}](" +
                $"[{idColumn}][uniqueidentifier] ROWGUIDCOL NOT NULL," +
                "[ClusterID] [int] IDENTITY(1,1) NOT FOR REPLICATION NOT NULL, ";
            foreach (var col in tableSchema.Columns)
                if (col.ColumnName != idColumn)
                    qry += $"[{col.ColumnName}] {SqlServerTypeMapper.Constructor(col)}";
            if (tableSchema.IsSoftDeletable)
                qry += SoftDeleteColumnSql(tableSchema);
            //add default primary key constraint
            qry += $" CONSTRAINT [PK_{tableSchema.TableName}] PRIMARY KEY NONCLUSTERED( [{idColumn}] ASC )WITH(PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, " +
                "IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY] ) ON [PRIMARY] ";
            // Large values stay in the table's own filegroup, PRIMARY, which is SQL Server's
            // default, so no TEXTIMAGE_ON clause is needed.
            if (qry.Contains("FILESTREAM"))
                qry += "FILESTREAM_ON [FileStream] ";
            List<string> statements = [qry];

            //add id generation default constraint
            statements.Add($"ALTER TABLE[dbo].[{tableSchema.TableName}] ADD CONSTRAINT[DF_{tableSchema.TableName}_{idColumn}]  DEFAULT(newid()) FOR[{idColumn}]");
            //create indexes
            statements.Add($"CREATE UNIQUE CLUSTERED INDEX [CIX_{tableSchema.TableName}_ClusterID] ON [dbo].[{tableSchema.TableName}](" +
                "[ClusterID] DESC)WITH(PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, " +
                "IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON[PRIMARY]");
            foreach (var col in tableSchema.IndexedColumns)
                statements.Add(ColumnIndexSql(tableSchema, col));
            return statements;
        }

        /// <summary>
        /// The CREATE INDEX statement for a column carrying Indexed or Unique. Unique
        /// wins when both are set and produces one filtered unique index. NULLs are excluded by
        /// the filter because every Nucleus column is created NULL and an unfiltered unique index
        /// would allow only a single NULL row per table.
        /// </summary>
        internal static string ColumnIndexSql(TableSchema table, ColumnSchema col)
        {
            if (!col.HasColumnIndex)
                throw new ArgumentException($"Column '{col.ColumnName}' does not carry a column index.", nameof(col));
            var name = IndexNaming.ColumnIndexName(table, col);
            var sortOrder = col.IndexSortDescending ? "DESC" : "ASC";
            if (col.Unique)
                return $"CREATE UNIQUE NONCLUSTERED INDEX [{name}] ON [dbo].[{table.TableName}]" +
                       $"([{col.ColumnName}] {sortOrder}) WHERE [{col.ColumnName}] IS NOT NULL " +
                       "WITH(PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, " +
                       "DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON[PRIMARY]";
            return $"CREATE NONCLUSTERED INDEX [{name}] ON [dbo].[{table.TableName}]" +
                   $"([{col.ColumnName}] {sortOrder}) " +
                   "WITH(PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, " +
                   "DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON[PRIMARY]";
        }

        /// <summary>The CREATE INDEX statement for a [TableIndex] index: its keys ascending, then its included columns.</summary>
        internal static string TableIndexSql(TableSchema table, TableIndexSchema index)
        {
            var name = IndexNaming.TableIndexName(table, index);
            var keys = string.Join(", ", index.Keys.Select(k => $"[{k.ColumnName}] ASC"));
            var include = index.Include.Count == 0 ? "" : $" INCLUDE ({string.Join(", ", index.Include.Select(c => $"[{c.ColumnName}]"))})";
            return $"CREATE NONCLUSTERED INDEX [{name}] ON [dbo].[{table.TableName}]({keys}){include} " +
                   "WITH(PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, " +
                   "DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON[PRIMARY]";
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

        /// <summary>
        /// Copies the rows of a table being rebuilt into its new table. Every name is bracketed, as
        /// CREATE TABLE writes them: a column named with a keyword (By, Order, Key) is created
        /// without trouble, so the copy must not be the step that fails on it.
        /// </summary>
        internal static string CopyTableDataSql(string sourceTableName, string destTableName, IReadOnlyList<string> commonColumns, bool orderByClusterId)
        {
            var columns = String.Join(", ", commonColumns.Select(c => $"[{c}]"));
            var qry = $"INSERT INTO [dbo].[{destTableName}]({columns}) " +
                $"SELECT {columns} FROM [dbo].[{sourceTableName}]";
            if (orderByClusterId)
                qry += $" ORDER BY [{TableSchema.ClusterIdColumn}] ASC";
            return qry;
        }

        internal static string DropTableSql(string tableName) => $"DROP TABLE [dbo].[{tableName}]";

        //update timestamps if necessary. *only needed for legacy tables, may be deprecated in the future
        internal static string BackfillTimestampSql(string tableName) =>
            $"UPDATE [dbo].[{tableName}] SET [Timestamp] = SYSDATETIMEOFFSET() WHERE [Timestamp] IS NULL";

        internal static string DropColumnIndexSql(string indexName, string tableName) => $"DROP INDEX [{indexName}] ON [dbo].[{tableName}]";

        internal static string CreateFullTextCatalogSql(string catalogName) => $"CREATE FULLTEXT CATALOG [{catalogName}]";

        internal static string DropFullTextCatalogSql(string catalogName) => $"DROP FULLTEXT CATALOG [{catalogName}]";

        internal static string DropFullTextIndexSql(string tableName) => $"DROP FULLTEXT INDEX ON [dbo].[{tableName}]";

        internal static string CreateFullTextIndexSql(string tableName) =>
            $"CREATE FULLTEXT INDEX ON [dbo].[{tableName}] KEY INDEX [PK_{tableName}] ON ([ftc_{tableName}]) WITH CHANGE_TRACKING AUTO";

        internal static string AddFullTextColumnSql(string tableName, string columnName) =>
            $"ALTER FULLTEXT INDEX ON [dbo].[{tableName}] ADD([{columnName}] LANGUAGE [English])";
    }

    /// <summary>
    /// SQL Server full-text search: a catalog per table (ftc_{table}) and a full-text index over
    /// the table's FullTextSearch columns, keyed by the primary key. Catalogs of tables that no
    /// longer need one are dropped.
    /// </summary>
    internal sealed class SqlServerFullTextExtras(SqlServerCatalogReader catalog, ILogger? logger) : ISchemaExtras
    {
        public async ValueTask<bool> AreCurrentAsync(IReadOnlyCollection<TableSchema> tables, bool async, CancellationToken cancellationToken)
        {
            var dtIndexes = await catalog.ReadFullTextIndexesAsync(async, cancellationToken);
            bool indexesMatch = true;
            foreach (var table in tables.Where(x => x.HasFullTextSearch))
            {
                if (!FullTextColumnsMatch(table, dtIndexes))
                {
                    logger?.LogWarning("Full-text index schema is not current for table: {tablename}", table.TableName);
                    indexesMatch = false;
                }
            }
            return indexesMatch;
        }

        private static bool FullTextColumnsMatch(TableSchema table, IReadOnlyList<SqlServerCatalogReader.FullTextIndexColumn> dtIndexes)
        {
            var ftColumns = table.Columns.Where(x => x.FullTextSearch).Select(x => x.ColumnName).ToList();
            var existingFtColumns = dtIndexes.Where(x => x.TableName == table.TableName).Select(x => x.ColumnName).ToList();
            if (existingFtColumns.Count != ftColumns.Count)
                return false;
            //check if any columns are missing, then if any extra columns exist
            return ftColumns.All(existingFtColumns.Contains) && existingFtColumns.All(ftColumns.Contains);
        }

        public async ValueTask SyncAsync(IReadOnlyCollection<TableSchema> tables, bool async, CancellationToken cancellationToken)
        {
            //drop any unused full-text indexes
            var dbCatalogs = await catalog.ReadFullTextCatalogsAsync(async, cancellationToken);
            foreach (var ftc in dbCatalogs.Where(x => x.StartsWith("ftc_")))
            {
                var tblname = ftc.Substring(4); //remove 'ftc_' prefix
                if (!tables.Any(x => x.TableName == tblname && x.HasFullTextSearch))
                {
                    //drop full-text catalog
                    await catalog.ExecuteAsync(SqlServerSchemaDialect.DropFullTextCatalogSql(ftc), async, cancellationToken);
                }
            }
            var ftTables = tables.Where(x => x.HasFullTextSearch).ToList();
            if (ftTables.Count == 0)
                return;

            //create any missing full-text catalogs
            foreach (var tableSchema in ftTables)
                if (!dbCatalogs.Contains($"ftc_{tableSchema.TableName}"))
                    await catalog.CreateFullTextCatalogAsync($"ftc_{tableSchema.TableName}", async, cancellationToken);

            //get all existing full-text indexes
            var ftIndexes = await catalog.ReadFullTextIndexesAsync(async, cancellationToken);

            foreach (var tableSchema in ftTables)
            {
                if (FullTextColumnsMatch(tableSchema, ftIndexes))
                    continue;
                //drop existing full-text index
                if (await catalog.TableHasFullTextIndexAsync(tableSchema.TableName, async, cancellationToken))
                    await catalog.ExecuteAsync(SqlServerSchemaDialect.DropFullTextIndexSql(tableSchema.TableName), async, cancellationToken);
                //recreate full-text index
                await catalog.ExecuteAsync(SqlServerSchemaDialect.CreateFullTextIndexSql(tableSchema.TableName), async, cancellationToken);
                foreach (var col in tableSchema.Columns.Where(x => x.FullTextSearch))
                    await catalog.ExecuteAsync(SqlServerSchemaDialect.AddFullTextColumnSql(tableSchema.TableName, col.ColumnName), async, cancellationToken);
            }
        }
    }
}
