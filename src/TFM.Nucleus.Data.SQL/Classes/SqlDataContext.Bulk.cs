using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.Queries;

namespace TFM.Nucleus.Data.SQL
{
    /// <summary>
    /// SQL Server's bulk engine. <see cref="BulkWritePlanner"/> decides the tables, columns and
    /// staged rows; this writes them with a #temp table, SqlBulkCopy and one set-based statement
    /// per batch of rows.
    /// </summary>
    internal partial class SqlDataContext
    {
        /// <summary>
        /// Default batch size for bulk INSERT/UPDATE operations.
        /// Prevents transaction log pressure and lock escalation at high row counts.
        /// </summary>
        private const int DefaultBulkBatchSize = 50000;

        /// <summary>
        /// Routes each table of the batch to the insert or the update strategy:
        /// - INSERT: grouped by column signature (preserves DB defaults for unset columns)
        /// - UPDATE: VARBINARY bitmask (single statement, no grouping, handles varying ChangedProperties)
        /// Both use #temp table + SqlBulkCopy for maximum throughput at high row counts.
        /// </summary>
        protected override async Task ExecuteBulkAsync(Query rootQuery, CancellationToken token)
        {
            var timer = Stopwatch.StartNew();
            var executionId = Guid.NewGuid();
            var writes = BulkWritePlanner.GroupByTable(rootQuery);
            var totalRows = writes.Sum(w => w.Queries.Count);

            Debug.WriteLine($"{DateTime.Now} Starting bulk execution {executionId} on context:{ContextId} " +
                $"({totalRows} total rows)");

            int tableGroupIndex = 0;
            foreach (var write in writes)
            {
                Debug.WriteLine($"  Table group {tableGroupIndex}: {write.TableName} - {write.Queries.Count} {(write.IsUpdate ? "UPDATE" : "INSERT")} rows");

                if (write.IsUpdate)
                    await ExecuteBulkUpdateAsync(write, Guid.NewGuid(), token);
                else
                    await ExecuteBulkInsertAsync(write, Guid.NewGuid(), token);

                tableGroupIndex++;
            }

            Debug.WriteLine($"{DateTime.Now} Finished bulk execution {executionId} " +
                $"in {timer.ElapsedMilliseconds} ms ({totalRows} total rows)");
        }

        #region Bulk INSERT

        private async Task ExecuteBulkInsertAsync(BulkTableWrite write, Guid executionId, CancellationToken token)
        {
            var groups = BulkWritePlanner.GroupByColumnSignature(write.Queries);

            Debug.WriteLine($"  Bulk INSERT: {groups.Count} column group{(groups.Count > 1 ? "s" : "")}");

            var baseTempName = $"#BulkI_{executionId.ToString("N")[..8]}";

            for (int gi = 0; gi < groups.Count; gi++)
            {
                var group = groups[gi];
                var tempTable = groups.Count == 1 ? baseTempName : $"{baseTempName}_{gi}";

                var (dataColumns, serverFunctionColumns) = BulkWritePlanner.ScanSetColumns(group, write.Schema);

                if (dataColumns.Count == 0 && serverFunctionColumns.Count == 0)
                    continue;

                var dt = ToDataTable(dataColumns.Select(c => (c.Name, c.ClrType)), BulkWritePlanner.InsertRows(group, dataColumns));

                try
                {
                    await CreateTempTableAsync(tempTable, dataColumns, null, 0, token);
                    await BulkCopyToTempTableAsync(tempTable, dt, token);

                    var baseSql = BuildBulkInsertSql(write.TableName, tempTable, dataColumns, serverFunctionColumns);
                    await ExecuteInBatchesAsync(baseSql, dt.Rows.Count, DefaultBulkBatchSize, "INSERT", token);
                }
                finally
                {
                    await TryDropTempTableAsync(tempTable);
                }
            }
        }

        /// <summary>
        /// INSERT INTO Target ([Col1],[Col2],[Timestamp])
        /// SELECT s.[Col1], s.[Col2], SYSDATETIMEOFFSET()
        /// FROM [#BulkI_xxx] s
        /// </summary>
        private static string BuildBulkInsertSql(
            string tableName, string tempTable,
            List<BulkColumn> dataColumns,
            Dictionary<string, Parameter> serverFunctionColumns)
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
                selectExprs.Add(SqlServerQueryRenderer.SetValue(kvp.Value));
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

        private async Task ExecuteBulkUpdateAsync(BulkTableWrite write, Guid executionId, CancellationToken token)
        {
            // The union of changed columns across every query; each gets a stable bit index.
            var (dataColumns, serverFunctionColumns) = BulkWritePlanner.ScanSetColumns(write.Queries, write.Schema);
            var whereColumns = BulkWritePlanner.ScanWhereColumns(write.Queries, write.Schema);

            if (dataColumns.Count == 0 && serverFunctionColumns.Count == 0)
                throw new InvalidOperationException("Bulk UPDATE resolved zero columns to process.");
            if (whereColumns.Count == 0)
                throw new InvalidOperationException("Bulk UPDATE resolved zero JOIN/WHERE columns.");

            int bitmaskByteCount = BulkWritePlanner.BitmaskBytes(dataColumns.Count);

            Debug.WriteLine($"  Bulk UPDATE: {dataColumns.Count} data columns, " +
                $"{bitmaskByteCount} bitmask byte{(bitmaskByteCount > 1 ? "s" : "")}, " +
                $"{serverFunctionColumns.Count} server function column{(serverFunctionColumns.Count != 1 ? "s" : "")}");

            var columns = dataColumns.Select(c => (c.Name, c.ClrType))
                .Concat(whereColumns.Select(c => (BulkWritePlanner.WhereColumnAlias(c.Name), c.ClrType)))
                .Append(("_changed", typeof(byte[])));
            var dt = ToDataTable(columns, BulkWritePlanner.UpdateRows(write.Queries, dataColumns, whereColumns));

            var tempTable = $"#BulkU_{executionId.ToString("N")[..8]}";
            try
            {
                await CreateTempTableAsync(tempTable, dataColumns, whereColumns, bitmaskByteCount, token);
                await BulkCopyToTempTableAsync(tempTable, dt, token);

                var baseSql = BuildBitmaskUpdateSql(
                    write.TableName, tempTable, dataColumns, whereColumns, serverFunctionColumns);
                await ExecuteInBatchesAsync(baseSql, dt.Rows.Count, DefaultBulkBatchSize, "UPDATE", token);
            }
            finally
            {
                await TryDropTempTableAsync(tempTable);
            }
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
            Dictionary<string, Parameter> serverFunctionColumns)
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
                setClauses.Add($"t.[{kvp.Key}]={SqlServerQueryRenderer.SetValue(kvp.Value)}");

            var joinClauses = new List<string>(whereColumns.Count);
            foreach (var col in whereColumns)
                joinClauses.Add($"t.[{col.Name}]=s.[{BulkWritePlanner.WhereColumnAlias(col.Name)}]");

            return $"UPDATE t WITH (ROWLOCK) SET {string.Join(",", setClauses)} " +
                   $"FROM {tableName} t INNER JOIN [{tempTable}] s ON {string.Join(" AND ", joinClauses)}";
        }

        #endregion

        #region Bulk - SQL Execution Helpers

        private static DataTable ToDataTable(IEnumerable<(string Name, Type ClrType)> columns, List<object?[]> rows)
        {
            var dt = new DataTable();
            foreach (var (name, clrType) in columns)
                dt.Columns.Add(name, clrType).AllowDBNull = true;
            foreach (var row in rows)
                dt.Rows.Add(row);
            return dt;
        }

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
                sb.Append($",[{col.Name}] {SqlServerTypeMapper.TempTableTypeDef(col.Column)}");

            if (whereColumns != null)
            {
                foreach (var col in whereColumns)
                    sb.Append($",[{BulkWritePlanner.WhereColumnAlias(col.Name)}] {SqlServerTypeMapper.TempTableTypeDef(col.Column)}");
            }

            if (bitmaskByteCount > 0)
                sb.Append($",[_changed] VARBINARY({bitmaskByteCount}) NOT NULL");

            sb.Append(')');

            await ExecuteBulkStatementAsync(sb.ToString(), token);

            Debug.WriteLine($"  Created Bulk {(bitmaskByteCount > 0 ? "UPDATE" : "INSERT")} temp table: {tempTable}");
        }

        private async Task BulkCopyToTempTableAsync(string tempTable, DataTable dt, CancellationToken token)
        {
            using var bulkCopy = new SqlBulkCopy(SqlConnection!, SqlBulkCopyOptions.Default, SqlTransaction);
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
        /// Appends WHERE s.[_rowid] BETWEEN start AND end to each execution.
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
            string baseSql,
            int totalRows, int batchSize,
            string operationName, CancellationToken token)
        {
            if (totalRows <= batchSize)
            {
                // Small enough for a single pass - append WHERE to constrain to all rows
                await ExecuteBulkStatementAsync($"{baseSql} WHERE s.[_rowid] BETWEEN 1 AND {totalRows}", token);

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

                await ExecuteBulkStatementAsync($"{baseSql} WHERE s.[_rowid] BETWEEN {batchStart} AND {batchEnd}", token);

                Debug.WriteLine($"  Bulk {operationName} batch {batchNumber}: " +
                    $"rows {batchStart}-{batchEnd} ({batchEnd - batchStart + 1} rows) " +
                    $"in {batchTimer.ElapsedMilliseconds} ms");

                batchTimer.Restart();
                batchStart = batchEnd + 1;
            }

            Debug.WriteLine($"  Bulk {operationName}: {totalRows} rows in {batchNumber} batches");
        }

        private async Task ExecuteBulkStatementAsync(string commandText, CancellationToken token)
        {
            var command = SqlCommand;
            command.Parameters.Clear();
            command.CommandTimeout = SqlConfig.BulkCopyTimeoutSeconds;
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync(token);
        }

        private async Task TryDropTempTableAsync(string tempTable)
        {
            try
            {
                Debug.WriteLine($"  Dropping temp table: {tempTable}");
                await ExecuteBulkStatementAsync($"IF OBJECT_ID('tempdb..[{tempTable}]') IS NOT NULL DROP TABLE [{tempTable}]", CancellationToken.None);
            }
            catch
            {
                // Temp tables auto-drop on session end
            }
        }

        #endregion
    }
}
