using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static TFM.Nucleus.Data.SQL.SqlColumnSchema;

using TFM.Nucleus.Data.Queries;

namespace TFM.Nucleus.Data.SQL
{
    public partial class SqlDataContext
    {
        /// <summary>
        /// Default batch size for bulk INSERT/UPDATE operations.
        /// Prevents transaction log pressure and lock escalation at high row counts.
        /// Configurable via SqlConfig.BulkBatchSize if available, otherwise defaults to 50000.
        /// </summary>
        private const int DefaultBulkBatchSize = 50000;

        /// <summary>
        /// Column metadata for bulk temp table construction.
        /// SqlTypeDef is the exact SQL type from schema (e.g. "VarChar(255) NULL").
        /// ClrType is the matching CLR type for the DataTable column.
        /// </summary>
        private readonly record struct BulkColumn(string Name, string SqlTypeDef, Type ClrType);

        /// <summary>
        /// Must be called BEFORE NeedsSplitting (which mutates LinkedQueries).
        /// Only checks that there are linked queries and the root has schema.
        /// Per-table type validation happens inside ExecuteBulkAsync.
        /// </summary>
        private static bool CanUseBulkPath(Query query)
        {
            return query.CountLinkedQueries() > 0
                && (query.Type == QueryType.INSERT || query.Type == QueryType.UPDATE)
                && query.TableSchema is SqlTableSchema;
        }

        /// <summary>
        /// Returns true if the parameter is a server function (e.g. SYSDATETIMEOFFSET()).
        /// In SET context, UsesVariable==false with non-null Value is always a server function.
        /// </summary>
        private static bool IsServerFunction(IQueryParameter p) => !p.UsesVariable && p.Value != null;

        /// <summary>
        /// Routes to the appropriate bulk strategy:
        /// - INSERT: grouping by column signature (preserves DB defaults for unset columns)
        /// - UPDATE: VARBINARY bitmask (single statement, no grouping, handles varying ChangedProperties)
        /// 
        /// LinkedQueries can reference different tables/types, so queries are first
        /// grouped by TableName before dispatching to single-table bulk operations.
        /// Both use #temp table + SqlBulkCopy for maximum throughput at high row counts.
        /// </summary>
        private async Task ExecuteBulkAsync(Query rootQuery, CancellationToken token)
        {
            var timer = Stopwatch.StartNew();
            var executionId = Guid.NewGuid();

            var allQueries = new List<Query>(rootQuery.LinkedQueries.Count + 1) { rootQuery };
            allQueries.AddRange(rootQuery.LinkedQueries);

            // Safety: all queries must have schema for the bulk path
            var queriesWithoutSchema = allQueries.Count(q => q.TableSchema is not SqlTableSchema || q.TableName == null);
            if (queriesWithoutSchema > 0)
                throw new InvalidOperationException(
                    $"{queriesWithoutSchema} of {allQueries.Count} queries are missing TableSchema or TableName. " +
                    $"All queries in a bulk batch must have SqlTableSchema set.");

            var tableGroups = allQueries
                .GroupBy(q => q.TableName!, StringComparer.OrdinalIgnoreCase);

            Debug.WriteLine($"{DateTime.Now} Starting bulk execution {executionId} on context:{_ID} " +
                $"({allQueries.Count} total rows)");

            int tableGroupIndex = 0;
            foreach (var tableGroup in tableGroups)
            {
                var queries = tableGroup.ToList();
                var schema = (SqlTableSchema)queries[0].TableSchema!;
                var tableName = tableGroup.Key;
                var isUpdate = queries[0].Type == QueryType.UPDATE;
                var groupId = Guid.NewGuid();

                // Validate all queries in the group are the same operation type
                if (queries.Any(q => q.Type != queries[0].Type))
                    throw new InvalidOperationException(
                        $"Mixed INSERT/UPDATE operations for table '{tableName}' in a single bulk batch. " +
                        $"Split inserts and updates into separate queries.");

                Debug.WriteLine($"  Table group {tableGroupIndex}: {tableName} - {queries.Count} {(isUpdate ? "UPDATE" : "INSERT")} rows");

                if (isUpdate)
                    await ExecuteBulkUpdateAsync(queries, schema, tableName, groupId, token);
                else
                    await ExecuteBulkInsertAsync(queries, schema, tableName, groupId, token);

                tableGroupIndex++;
            }

            Debug.WriteLine($"{DateTime.Now} Finished bulk execution {executionId} " +
                $"in {timer.ElapsedMilliseconds} ms ({allQueries.Count} total rows)");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  INSERT: grouped by column signature
        //  Each group gets its own temp table + INSERT...SELECT so that
        //  omitted columns receive DB defaults (not NULL).
        // ─────────────────────────────────────────────────────────────────────

        #region Bulk INSERT

        private async Task ExecuteBulkInsertAsync(
            List<Query> allQueries, SqlTableSchema schema,
            string tableName, Guid executionId, CancellationToken token)
        {
            var groups = GroupByColumnSignature(allQueries);

            Debug.WriteLine($"  Bulk INSERT: {groups.Count} column group{(groups.Count > 1 ? "s" : "")}");

            var baseTempName = $"#BulkI_{executionId.ToString("N")[..8]}";

            for (int gi = 0; gi < groups.Count; gi++)
            {
                var group = groups[gi];
                var tempTable = groups.Count == 1 ? baseTempName : $"{baseTempName}_{gi}";

                var dataColumns = new List<BulkColumn>();
                var serverFunctionColumns = new Dictionary<string, string>();

                ScanSetColumns(group, schema, dataColumns, serverFunctionColumns);

                if (dataColumns.Count == 0 && serverFunctionColumns.Count == 0)
                    continue;

                var dt = BuildInsertDataTable(group, dataColumns, serverFunctionColumns);

                try
                {
                    await CreateTempTableAsync(tempTable, dataColumns, null, 0, token);
                    await BulkCopyToTempTableAsync(tempTable, dt, token);

                    var baseSql = BuildBulkInsertSql(tableName, tempTable, dataColumns, serverFunctionColumns);
                    int totalRows = dt.Rows.Count;
                    int batchSize = DefaultBulkBatchSize;

                    await ExecuteInBatchesAsync(baseSql, tempTable, totalRows, batchSize, "INSERT", token);
                }
                finally
                {
                    await TryDropTempTableAsync(tempTable);
                }
            }
        }

        /// <summary>
        /// Groups queries by their SET column signature (sorted set of data column names,
        /// excluding server functions). Queries in the same group have identical columns.
        /// </summary>
        private static List<List<Query>> GroupByColumnSignature(List<Query> queries)
        {
            var groups = new Dictionary<string, List<Query>>(StringComparer.Ordinal);

            foreach (var q in queries)
            {
                var names = q.SetParameters
                    .Where(p => p != null && p.Name != null && !IsServerFunction(p))
                    .Select(p => p!.Name!)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

                var signature = string.Join("|", names);

                if (!groups.TryGetValue(signature, out var group))
                {
                    group = [];
                    groups[signature] = group;
                }
                group.Add(q);
            }

            return [.. groups.Values];
        }

        private static DataTable BuildInsertDataTable(
            List<Query> queryGroup,
            List<BulkColumn> dataColumns,
            Dictionary<string, string> serverFunctionColumns)
        {
            var dt = new DataTable();
            foreach (var col in dataColumns)
                dt.Columns.Add(col.Name, col.ClrType).AllowDBNull = true;

            foreach (var q in queryGroup)
            {
                var row = dt.NewRow();
                foreach (DataColumn dc in dt.Columns)
                    row[dc] = DBNull.Value;

                foreach (var p in q.SetParameters)
                {
                    if (p?.Name == null) continue;
                    if (serverFunctionColumns.ContainsKey(p.Name)) continue;
                    if (!dt.Columns.Contains(p.Name)) continue;
                    row[p.Name] = CoerceValueForDataTable(p.Value);
                }

                dt.Rows.Add(row);
            }

            return dt;
        }

        /// <summary>
        /// INSERT INTO Target ([Col1],[Col2],[Timestamp])
        /// SELECT s.[Col1], s.[Col2], SYSDATETIMEOFFSET()
        /// FROM [#BulkI_xxx] s
        /// </summary>
        private static string BuildBulkInsertSql(
            string tableName, string tempTable,
            List<BulkColumn> dataColumns,
            Dictionary<string, string> serverFunctionColumns)
        {
            var insertCols = new List<string>(dataColumns.Count + serverFunctionColumns.Count);
            var selectExprs = new List<string>(dataColumns.Count + serverFunctionColumns.Count);

            foreach (var col in dataColumns)
            {
                insertCols.Add($"[{col.Name}]");
                selectExprs.Add($"s.[{col.Name}]");
            }
            foreach (var kvp in serverFunctionColumns)
            {
                insertCols.Add($"[{kvp.Key}]");
                selectExprs.Add(kvp.Value);
            }

            return $"INSERT INTO {tableName} ({string.Join(",", insertCols)}) " +
                   $"SELECT {string.Join(",", selectExprs)} FROM [{tempTable}] s";
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        //  UPDATE: single temp table with VARBINARY bitmask
        //  One SqlBulkCopy, one UPDATE statement regardless of how many
        //  different ChangedProperties combinations exist.
        //  Supports unlimited columns via VARBINARY (not limited to 64/BIGINT).
        //  SQL Server skips log writes when CASE evaluates to existing value.
        // ─────────────────────────────────────────────────────────────────────

        #region Bulk UPDATE

        private async Task ExecuteBulkUpdateAsync(
            List<Query> allQueries, SqlTableSchema schema,
            string tableName, Guid executionId, CancellationToken token)
        {
            var dataColumns = new List<BulkColumn>();
            var serverFunctionColumns = new Dictionary<string, string>();
            var whereColumns = new List<BulkColumn>();

            // Scan ALL queries to build the union of changed columns.
            // Each column gets a stable index for the bitmask.
            ScanSetColumns(allQueries, schema, dataColumns, serverFunctionColumns);
            ScanWhereColumns(allQueries, schema, whereColumns);

            if (dataColumns.Count == 0 && serverFunctionColumns.Count == 0)
                throw new InvalidOperationException("Bulk UPDATE resolved zero columns to process.");
            if (whereColumns.Count == 0)
                throw new InvalidOperationException("Bulk UPDATE resolved zero JOIN/WHERE columns.");

            // Build column name → bit index lookup
            var columnIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < dataColumns.Count; i++)
                columnIndex[dataColumns[i].Name] = i;

            int bitmaskByteCount = (dataColumns.Count + 7) / 8;

            Debug.WriteLine($"  Bulk UPDATE: {dataColumns.Count} data columns, " +
                $"{bitmaskByteCount} bitmask byte{(bitmaskByteCount > 1 ? "s" : "")}, " +
                $"{serverFunctionColumns.Count} server function column{(serverFunctionColumns.Count != 1 ? "s" : "")}");

            var dt = BuildUpdateDataTable(allQueries, dataColumns, whereColumns,
                serverFunctionColumns, columnIndex, bitmaskByteCount);

            var tempTable = $"#BulkU_{executionId.ToString("N")[..8]}";
            try
            {
                await CreateTempTableAsync(tempTable, dataColumns, whereColumns, bitmaskByteCount, token);
                await BulkCopyToTempTableAsync(tempTable, dt, token);

                var baseSql = BuildBitmaskUpdateSql(
                    tableName, tempTable, dataColumns, whereColumns, serverFunctionColumns);
                int totalRows = dt.Rows.Count;
                int batchSize = DefaultBulkBatchSize;

                await ExecuteInBatchesAsync(baseSql, tempTable, totalRows, batchSize, "UPDATE", token);
            }
            finally
            {
                await TryDropTempTableAsync(tempTable);
            }
        }

        private static DataTable BuildUpdateDataTable(
            List<Query> allQueries,
            List<BulkColumn> dataColumns,
            List<BulkColumn> whereColumns,
            Dictionary<string, string> serverFunctionColumns,
            Dictionary<string, int> columnIndex,
            int bitmaskByteCount)
        {
            var dt = new DataTable();

            foreach (var col in dataColumns)
                dt.Columns.Add(col.Name, col.ClrType).AllowDBNull = true;
            foreach (var col in whereColumns)
                dt.Columns.Add(WhereColumnAlias(col.Name), col.ClrType).AllowDBNull = true;
            dt.Columns.Add("_changed", typeof(byte[]));

            foreach (var q in allQueries)
            {
                var row = dt.NewRow();
                foreach (DataColumn dc in dt.Columns)
                    row[dc] = DBNull.Value;

                var bitmask = new byte[bitmaskByteCount];

                foreach (var p in q.SetParameters)
                {
                    if (p?.Name == null) continue;
                    if (serverFunctionColumns.ContainsKey(p.Name)) continue;
                    if (!columnIndex.TryGetValue(p.Name, out int bitIdx)) continue;

                    // Set the bit for this column
                    bitmask[bitIdx / 8] |= (byte)(1 << (bitIdx % 8));
                    // Set the value (works for both normal values and explicit NULLs)
                    row[p.Name] = CoerceValueForDataTable(p.Value);
                }

                // WHERE columns
                var whereParams = q.Where.AllParameters();
                if (whereParams != null)
                {
                    foreach (var p in whereParams)
                    {
                        if (p?.Name == null) continue;
                        var alias = WhereColumnAlias(p.Name);
                        if (!dt.Columns.Contains(alias)) continue;
                        row[alias] = CoerceValueForDataTable(p.Value);
                    }
                }

                row["_changed"] = bitmask;
                dt.Rows.Add(row);
            }

            return dt;
        }

        /// <summary>
        /// UPDATE t WITH (ROWLOCK) SET
        ///     t.[Name]  = CASE WHEN CAST(SUBSTRING(s.[_changed],1,1) AS INT) &amp; 1 > 0 THEN s.[Name]  ELSE t.[Name]  END,
        ///     t.[Email] = CASE WHEN CAST(SUBSTRING(s.[_changed],1,1) AS INT) &amp; 2 > 0 THEN s.[Email] ELSE t.[Email] END,
        ///     t.[Timestamp] = SYSDATETIMEOFFSET()
        /// FROM Target t
        /// INNER JOIN [#BulkU_xxx] s ON t.[ID] = s.[_w_ID]
        /// 
        /// SQL Server 2008 R2 compatible. SUBSTRING on VARBINARY returns a single byte,
        /// CAST to INT for bitwise AND.
        /// Storage engine skips log writes when ELSE evaluates to the existing value.
        /// </summary>
        private static string BuildBitmaskUpdateSql(
            string tableName, string tempTable,
            List<BulkColumn> dataColumns,
            List<BulkColumn> whereColumns,
            Dictionary<string, string> serverFunctionColumns)
        {
            var setClauses = new List<string>(dataColumns.Count + serverFunctionColumns.Count);

            for (int i = 0; i < dataColumns.Count; i++)
            {
                var col = dataColumns[i];
                var bytePos = (i / 8) + 1; // 1-indexed for SUBSTRING
                var bitValue = 1 << (i % 8);

                setClauses.Add(
                    $"t.[{col.Name}]=CASE WHEN CAST(SUBSTRING(s.[_changed],{bytePos},1) AS INT)&{bitValue}>0 " +
                    $"THEN s.[{col.Name}] ELSE t.[{col.Name}] END");
            }

            // Server functions apply unconditionally to every row
            foreach (var kvp in serverFunctionColumns)
                setClauses.Add($"t.[{kvp.Key}]={kvp.Value}");

            var joinClauses = new List<string>(whereColumns.Count);
            foreach (var col in whereColumns)
                joinClauses.Add($"t.[{col.Name}]=s.[{WhereColumnAlias(col.Name)}]");

            return $"UPDATE t WITH (ROWLOCK) SET {string.Join(",", setClauses)} " +
                   $"FROM {tableName} t INNER JOIN [{tempTable}] s ON {string.Join(" AND ", joinClauses)}";
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        //  Shared infrastructure
        // ─────────────────────────────────────────────────────────────────────

        #region Bulk - Column Scanning

        /// <summary>
        /// Scans queries for the union of SET columns, split into data columns
        /// and server function columns. Uses SqlColumnSchema for exact SQL type mapping.
        /// </summary>
        private static void ScanSetColumns(
            List<Query> queries, SqlTableSchema schema,
            List<BulkColumn> dataColumns,
            Dictionary<string, string> serverFunctionColumns)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var q in queries)
            {
                foreach (var p in q.SetParameters)
                {
                    if (p?.Name == null) continue;

                    if (IsServerFunction(p))
                    {
                        serverFunctionColumns.TryAdd(p.Name, SqlServerQueryRenderer.SetValue(p));
                        continue;
                    }

                    if (!seen.Add(p.Name)) continue;

                    var schemaCol = schema.FindByColumnName(p.Name)
                        ?? throw new InvalidOperationException(
                            $"Column '{p.Name}' not found in schema for table '{schema.TableName}'.");

                    dataColumns.Add(new BulkColumn(
                        p.Name,
                        BuildTempTableTypeDef(schemaCol),
                        GetClrTypeForBulkColumn(schemaCol)));
                }
            }
        }

        /// <summary>
        /// Scans queries for WHERE columns (typically the identity column for UPDATE).
        /// </summary>
        private static void ScanWhereColumns(
            List<Query> queries, SqlTableSchema schema,
            List<BulkColumn> whereColumns)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var q in queries)
            {
                var whereParams = q.Where.AllParameters();
                if (whereParams == null) continue;
                foreach (var p in whereParams)
                {
                    if (p?.Name == null) continue;
                    if (!seen.Add(p.Name)) continue;

                    var schemaCol = schema.FindByColumnName(p.Name)
                        ?? throw new InvalidOperationException(
                            $"WHERE column '{p.Name}' not found in schema for table '{schema.TableName}'.");

                    whereColumns.Add(new BulkColumn(
                        p.Name,
                        BuildTempTableTypeDef(schemaCol),
                        GetClrTypeForBulkColumn(schemaCol)));
                }
            }
        }

        #endregion

        #region Bulk - SQL Execution Helpers

        /// <summary>
        /// Creates a temp table with a [_rowid] IDENTITY column for batch processing.
        /// When bitmaskByteCount > 0, appends a [_changed] VARBINARY column.
        /// </summary>
        private async Task CreateTempTableAsync(
            string tempTable,
            List<BulkColumn> dataColumns,
            List<BulkColumn>? whereColumns,
            int bitmaskByteCount,
            CancellationToken token)
        {
            var sb = new StringBuilder($"CREATE TABLE [{tempTable}] ([_rowid] INT IDENTITY(1,1) NOT NULL");

            foreach (var col in dataColumns)
                sb.Append($",[{col.Name}] {col.SqlTypeDef}");

            if (whereColumns != null)
            {
                foreach (var col in whereColumns)
                    sb.Append($",[{WhereColumnAlias(col.Name)}] {col.SqlTypeDef}");
            }

            if (bitmaskByteCount > 0)
                sb.Append($",[_changed] VARBINARY({bitmaskByteCount}) NOT NULL");

            sb.Append(')');

            _CurrentCommand!.Parameters.Clear();
            _CurrentCommand.CommandTimeout = SqlConfig.BulkCopyTimeoutSeconds;
            _CurrentCommand.CommandText = sb.ToString();
            await _CurrentCommand.ExecuteNonQueryAsync(token);

            Debug.WriteLine($"  Created Bulk {(bitmaskByteCount > 0 ? "UPDATE" : "INSERT")} temp table: {tempTable}");
        }

        private async Task BulkCopyToTempTableAsync(string tempTable, DataTable dt, CancellationToken token)
        {
            using var bulkCopy = new SqlBulkCopy(_CurrentConnection!, SqlBulkCopyOptions.Default, _CurrentTransaction);
            bulkCopy.DestinationTableName = tempTable;
            bulkCopy.BulkCopyTimeout = SqlConfig.BulkCopyTimeoutSeconds;
            // Map only data columns - _rowid is IDENTITY and auto-populated
            foreach (DataColumn col in dt.Columns)
                bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            await bulkCopy.WriteToServerAsync(dt, token);

            Debug.WriteLine($"  Bulk copied {dt.Rows.Count} rows to temp table: {tempTable}");
        }

        /// <summary>
        /// Executes the bulk SQL in batches using the [_rowid] IDENTITY column.
        /// Appends WHERE s.[_rowid] BETWEEN @start AND @end to each execution.
        /// 
        /// Batching prevents:
        /// - Transaction log pressure (each batch generates its own log records)
        /// - Lock escalation to table locks (stays at row/page level)
        /// - Command timeouts on large datasets
        /// 
        /// All batches execute on the same connection/transaction, so atomicity
        /// is preserved - a failure on any batch rolls back the entire transaction.
        /// </summary>
        private async Task ExecuteInBatchesAsync(
            string baseSql, string tempTable,
            int totalRows, int batchSize,
            string operationName, CancellationToken token)
        {
            if (totalRows <= batchSize)
            {
                // Small enough for a single pass - append WHERE to constrain to all rows
                _CurrentCommand!.Parameters.Clear();
                _CurrentCommand.CommandTimeout = SqlConfig.BulkCopyTimeoutSeconds;
                _CurrentCommand.CommandText = $"{baseSql} WHERE s.[_rowid] BETWEEN 1 AND {totalRows}";
                await _CurrentCommand.ExecuteNonQueryAsync(token);

                Debug.WriteLine($"  Bulk {operationName}: {totalRows} rows in single batch");
                return;
            }

            int batchStart = 1;
            int batchNumber = 0;
            var batchTimer = Stopwatch.StartNew();

            while (batchStart <= totalRows)
            {
                int batchEnd = Math.Min(batchStart + batchSize - 1, totalRows);
                batchNumber++;

                _CurrentCommand!.Parameters.Clear();
                _CurrentCommand.CommandTimeout = SqlConfig.BulkCopyTimeoutSeconds;
                _CurrentCommand.CommandText = $"{baseSql} WHERE s.[_rowid] BETWEEN {batchStart} AND {batchEnd}";
                await _CurrentCommand.ExecuteNonQueryAsync(token);

                Debug.WriteLine($"  Bulk {operationName} batch {batchNumber}: " +
                    $"rows {batchStart}-{batchEnd} ({batchEnd - batchStart + 1} rows) " +
                    $"in {batchTimer.ElapsedMilliseconds} ms");

                batchTimer.Restart();
                batchStart = batchEnd + 1;
            }

            Debug.WriteLine($"  Bulk {operationName}: {totalRows} rows in {batchNumber} batches");
        }

        private async Task TryDropTempTableAsync(string tempTable)
        {
            try
            {
                Debug.WriteLine($"  Dropping temp table: {tempTable}");
                _CurrentCommand!.Parameters.Clear();
                _CurrentCommand.CommandTimeout = SqlConfig.BulkCopyTimeoutSeconds;
                _CurrentCommand.CommandText = $"IF OBJECT_ID('tempdb..[{tempTable}]') IS NOT NULL DROP TABLE [{tempTable}]";
                await _CurrentCommand.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch
            {
                // Temp tables auto-drop on session end
            }
        }

        #endregion

        #region Bulk - Shared Utilities

        private static object CoerceValueForDataTable(object? value)
        {
            if (value == null)
                return DBNull.Value;
            if (value.GetType().IsEnum)
                return Convert.ToInt32(value);
            return value;
        }

        private static string WhereColumnAlias(string columnName) => $"_w_{columnName}";

        /// <summary>
        /// SQL type definition from schema. Mirrors SqlColumnSchema.MSSQLConstructor()
        /// but omits FILESTREAM (invalid on temp tables) and trailing comma.
        /// </summary>
        private static string BuildTempTableTypeDef(SqlColumnSchema col)
        {
            var sb = new StringBuilder();
            sb.Append(col.DataType);

            if (col.DataType is ESqlDataType.Decimal or ESqlDataType.Numeric)
                sb.Append($"({col.Precision},{col.Scale})");
            else if (!string.IsNullOrEmpty(col.SqlDataSize))
                sb.Append($"({col.SqlDataSize})");

            sb.Append(" NULL");
            return sb.ToString();
        }

        /// <summary>
        /// CLR type for DataTable columns. Uses PropertySchema.Type directly,
        /// with overrides for types that SQL maps differently to their CLR representation
        /// (enums → int, ICoreObject → Guid, IDatabaseStringValue → string).
        /// </summary>
        private static Type GetClrTypeForBulkColumn(SqlColumnSchema col)
        {
            var type = col.PropertySchema.Type;

            if (type.IsEnum) return typeof(int);
            if (typeof(ICoreObject).IsAssignableFrom(type)) return typeof(Guid);
            if (typeof(IDatabaseStringValue).IsAssignableFrom(type)) return typeof(string);

            return Nullable.GetUnderlyingType(type) ?? type;
        }

        #endregion
    }
}