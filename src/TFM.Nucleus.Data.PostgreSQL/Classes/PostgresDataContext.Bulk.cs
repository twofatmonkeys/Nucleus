using Npgsql;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.Queries;
using static TFM.Nucleus.Data.PostgreSQL.PostgresIdentifiers;

namespace TFM.Nucleus.Data.PostgreSQL
{
    /// <summary>
    /// PostgreSQL's bulk engine. <see cref="BulkWritePlanner"/> decides the tables, columns and
    /// staged rows; this copies them into a temp table with COPY BINARY and writes them with one
    /// set-based statement. An update applies each row's changed columns only, by the bitmask the
    /// planner staged with it.
    /// </summary>
    internal sealed partial class PostgresDataContext
    {
        private const string ChangedColumn = "_changed";

        protected override async Task ExecuteBulkAsync(Query rootQuery, CancellationToken token)
        {
            var timer = Stopwatch.StartNew();
            var writes = BulkWritePlanner.GroupByTable(rootQuery);
            foreach (var write in writes)
            {
                if (write.IsUpdate)
                    await BulkUpdateAsync(write, token);
                else
                    await BulkInsertAsync(write, token);
            }
            Debug.WriteLine($"{DateTime.Now} Finished bulk execution on context:{ContextId} in {timer.ElapsedMilliseconds} ms ({writes.Sum(w => w.Queries.Count)} total rows)");
        }

        private async Task BulkInsertAsync(BulkTableWrite write, CancellationToken token)
        {
            // Grouped by the columns each row sets, so a column a row leaves unset keeps its default.
            foreach (var group in BulkWritePlanner.GroupByColumnSignature(write.Queries))
            {
                var (dataColumns, serverFunctionColumns) = BulkWritePlanner.ScanSetColumns(group, write.Schema);
                if (dataColumns.Count == 0 && serverFunctionColumns.Count == 0)
                    continue;

                var staging = NewStagingTableName();
                try
                {
                    await CreateStagingTableAsync(staging, dataColumns, [], bitmask: false, token);
                    await CopyAsync(staging, dataColumns, [], bitmask: false, BulkWritePlanner.InsertRows(group, dataColumns), token);

                    var targets = dataColumns.Select(c => Quote(c.Name)).Concat(serverFunctionColumns.Keys.Select(Quote));
                    var values = dataColumns.Select(c => $"s.{Quote(c.Name)}")
                        .Concat(serverFunctionColumns.Values.Select(PostgresQueryRenderer.SetValue));
                    var outputs = BulkWritePlanner.ScanOutputs(group, write.Schema);
                    await ExecuteBulkStatementAsync(
                        $"INSERT INTO {Table(_pgProvider.SchemaName, write.TableName)} ({string.Join(", ", targets)}) " +
                        $"SELECT {string.Join(", ", values)} FROM {Quote(staging)} AS s{ReturningClause(outputs, "")}", outputs, token);
                }
                finally
                {
                    await TryDropStagingTableAsync(staging);
                }
            }
        }

        private async Task BulkUpdateAsync(BulkTableWrite write, CancellationToken token)
        {
            var (dataColumns, serverFunctionColumns) = BulkWritePlanner.ScanSetColumns(write.Queries, write.Schema);
            var whereColumns = BulkWritePlanner.ScanWhereColumns(write.Queries, write.Schema);
            if (dataColumns.Count == 0 && serverFunctionColumns.Count == 0)
                throw new InvalidOperationException("Bulk update resolved zero columns to process.");
            if (whereColumns.Count == 0)
                throw new InvalidOperationException("Bulk update resolved zero join columns.");

            var staging = NewStagingTableName();
            try
            {
                await CreateStagingTableAsync(staging, dataColumns, whereColumns, bitmask: true, token);
                await CopyAsync(staging, dataColumns, whereColumns, bitmask: true, BulkWritePlanner.UpdateRows(write.Queries, dataColumns, whereColumns), token);

                // Column i is changed when bit i % 8 of byte i / 8 of the row's bitmask is set.
                var sets = dataColumns.Select((c, i) =>
                        $"{Quote(c.Name)} = CASE WHEN (get_byte(s.{Quote(ChangedColumn)}, {i / 8}) & {1 << (i % 8)}) > 0 THEN s.{Quote(c.Name)} ELSE t.{Quote(c.Name)} END")
                    .Concat(serverFunctionColumns.Select(kvp => $"{Quote(kvp.Key)} = {PostgresQueryRenderer.SetValue(kvp.Value)}"));
                var joins = whereColumns.Select(c => $"t.{Quote(c.Name)} = s.{Quote(BulkWritePlanner.WhereColumnAlias(c.Name))}");
                var outputs = BulkWritePlanner.ScanOutputs(write.Queries, write.Schema);
                await ExecuteBulkStatementAsync(
                    $"UPDATE {Table(_pgProvider.SchemaName, write.TableName)} AS t SET {string.Join(", ", sets)} " +
                    $"FROM {Quote(staging)} AS s WHERE {string.Join(" AND ", joins)}{ReturningClause(outputs, "t.")}", outputs, token);
            }
            finally
            {
                await TryDropStagingTableAsync(staging);
            }
        }

        private static string NewStagingTableName() => $"nucleus_bulk_{Guid.NewGuid():N}"[..29];

        private async Task CreateStagingTableAsync(string staging, List<BulkColumn> dataColumns, List<BulkColumn> whereColumns, bool bitmask, CancellationToken token)
        {
            var columns = dataColumns.Select(c => $"{Quote(c.Name)} {PostgresTypes.TypeName(c.Column)}")
                .Concat(whereColumns.Select(c => $"{Quote(BulkWritePlanner.WhereColumnAlias(c.Name))} {PostgresTypes.TypeName(c.Column)}"));
            if (bitmask)
                columns = columns.Append($"{Quote(ChangedColumn)} bytea NOT NULL");
            await ExecuteBulkStatementAsync($"CREATE TEMP TABLE {Quote(staging)} ({string.Join(", ", columns)})", token);
        }

        /// <summary>Streams the staged rows into the staging table with COPY BINARY, each value as its column's type.</summary>
        private async Task CopyAsync(string staging, List<BulkColumn> dataColumns, List<BulkColumn> whereColumns, bool bitmask, List<object?[]> rows, CancellationToken token)
        {
            var columns = dataColumns.Concat(whereColumns).ToList();
            var names = dataColumns.Select(c => Quote(c.Name))
                .Concat(whereColumns.Select(c => Quote(BulkWritePlanner.WhereColumnAlias(c.Name))));
            if (bitmask)
                names = names.Append(Quote(ChangedColumn));

            await using var importer = await NpgsqlConnection.BeginBinaryImportAsync(
                $"COPY {Quote(staging)} ({string.Join(", ", names)}) FROM STDIN (FORMAT BINARY)", token);
            importer.Timeout = TimeSpan.FromSeconds(Config.BulkTimeoutSeconds);
            foreach (var row in rows)
            {
                await importer.StartRowAsync(token);
                for (var i = 0; i < columns.Count; i++)
                {
                    var value = PostgresTypes.ToDb(row[i], columns[i].Column, out var type);
                    if (value is null or DBNull)
                        await importer.WriteNullAsync(token);
                    else
                        await importer.WriteAsync(value, type ?? PostgresTypes.DbType(columns[i].Column), token);
                }
                if (bitmask)
                    await importer.WriteAsync((byte[])row[columns.Count]!, NpgsqlTypes.NpgsqlDbType.Bytea, token);
            }
            await importer.CompleteAsync(token);
        }

        private Task ExecuteBulkStatementAsync(string commandText, CancellationToken token) =>
            ExecuteBulkStatementAsync(commandText, BulkOutputs.None, token);

        /// <summary>Runs one bulk statement, reading back what it returns when the write has outputs.</summary>
        private async Task ExecuteBulkStatementAsync(string commandText, BulkOutputs outputs, CancellationToken token)
        {
            var command = Command ?? throw new DataContextException("No active Command", DataContextErrorKind.CurrentCommandIsNull);
            command.Parameters.Clear();
            command.CommandTimeout = Config.BulkTimeoutSeconds;
            command.CommandText = commandText;
            if (outputs.IsEmpty)
            {
                await command.ExecuteNonQueryAsync(token);
                return;
            }
            await using var reader = await command.ExecuteReaderAsync(token);
            await ReceiveBulkOutputsAsync(reader, outputs, token);
        }

        /// <summary>Returns the identity and each output column of every row the statement writes.</summary>
        private static string ReturningClause(BulkOutputs outputs, string tableAlias) =>
            outputs.IsEmpty
                ? ""
                : $" RETURNING {string.Join(", ", outputs.Columns.Prepend(outputs.IdentityColumn).Select(c => $"{tableAlias}{Quote(c)}"))}";

        private async Task TryDropStagingTableAsync(string staging)
        {
            try
            {
                await ExecuteBulkStatementAsync($"DROP TABLE IF EXISTS {Quote(staging)}", CancellationToken.None);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                // A failed transaction refuses further statements; its rollback drops the table anyway.
                Debug.WriteLine($"Dropping bulk staging table {staging} failed: {ex.Message}", "Database");
            }
        }
    }
}
