using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.Queries;
using TFM.Nucleus.Data.Schema;

namespace TFM.Nucleus.Data.SQLite
{
    /// <summary>
    /// A SQLite data context: the shared relational context over a SqliteConnection, with values
    /// bound in the forms <see cref="SqliteValues"/> stores and read back as the types their
    /// properties expect. SQLite lets one connection write at a time, so an oversized write runs as
    /// commands one after another on this context's own transaction; there is no bulk engine, and
    /// none is needed in-process. A transaction begins IMMEDIATE (the driver's default), taking
    /// the write lock at once, so two writers wait for each other rather than deadlock.
    /// ContextOptions.WatchForChanges has no meaning here: SQLite has no change notifications.
    /// </summary>
    internal sealed class SqliteDataContext : DbDataContext<SqliteDataContext>
    {
        private readonly SqliteDataProvider _sqliteProvider;

        private SqliteDataContext(SqliteDataProvider provider) : base(provider)
        {
            _sqliteProvider = provider;
        }

        private SqliteConfiguration Config => _sqliteProvider.Config;

        internal static SqliteDataContext Create(SqliteDataProvider provider, SqliteDataContext? parent, bool useTransaction, bool useDependency)
        {
            var context = new SqliteDataContext(provider);
            context.Open(parent, useTransaction, useDependency);
            return context;
        }

        internal static async Task<SqliteDataContext> CreateAsync(SqliteDataProvider provider, SqliteDataContext? parent, bool useTransaction, bool useDependency, CancellationToken token)
        {
            var context = new SqliteDataContext(provider);
            await context.OpenAsync(parent, useTransaction, useDependency, token);
            return context;
        }

        protected override DbConnection BuildConnection(bool useTransaction) => Config.BuildConnection();

        protected override DbParameter CreateParameter(DbCommand command, string name, object? value, IQueryParameter source)
        {
            var column = (source as Parameter)?.ColumnSchema as ColumnSchema;
            return new SqliteParameter($"{Provider.ParameterPrefix}{name}", SqliteValues.ToDb(value, column));
        }

        protected override void ReadRow(DbDataReader reader, object?[] row, IDataTableSchema? schema)
        {
            reader.GetValues(row!);
            if (schema == null)
                return;
            var i = 0;
            foreach (var column in schema.Columns)
            {
                if (i >= row.Length)
                    break;
                row[i] = SqliteValues.FromDb(row[i], column);
                i++;
            }
        }

        protected override void ReadResultRow(DbDataReader reader, object?[] row, IReadOnlyList<ResultColumn> columns)
        {
            reader.GetValues(row!);
            for (var i = 0; i < row.Length && i < columns.Count; i++)
                if (columns[i].Column is { } column)
                    row[i] = SqliteValues.FromDb(row[i], column);
        }

        protected override object? ReadOutputValue(object? value, IDataColumnSchema? column) => SqliteValues.FromDb(value, column);

        protected override string DescribeConnection() =>
            Connection is SqliteConnection { State: System.Data.ConnectionState.Open } connection ? $"file {connection.DataSource}" : "closed connection";
    }
}
