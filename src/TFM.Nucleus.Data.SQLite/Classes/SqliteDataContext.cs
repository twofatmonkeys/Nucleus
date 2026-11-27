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
        private readonly bool _forSchema;

        private SqliteDataContext(SqliteDataProvider provider, bool forSchema) : base(provider)
        {
            _sqliteProvider = provider;
            _forSchema = forSchema;
        }

        private SqliteConfiguration Config => _sqliteProvider.Config;

        internal static SqliteDataContext Create(SqliteDataProvider provider, SqliteDataContext? parent, bool useTransaction, bool useDependency, bool forSchema = false)
        {
            var context = new SqliteDataContext(provider, forSchema);
            context.Open(parent, useTransaction, useDependency);
            return context;
        }

        internal static async Task<SqliteDataContext> CreateAsync(SqliteDataProvider provider, SqliteDataContext? parent, bool useTransaction, bool useDependency, CancellationToken token, bool forSchema = false)
        {
            var context = new SqliteDataContext(provider, forSchema);
            await context.OpenAsync(parent, useTransaction, useDependency, token);
            return context;
        }

        protected override DbConnection BuildConnection(bool useTransaction) => Config.BuildConnection();

        /// <summary>
        /// Sets, on every connection before its transaction begins, the two settings SQLite keeps
        /// per connection and refuses to change inside a transaction. A context enforces foreign
        /// keys when the provider keeps them. The schema update's context does not, so a rebuild
        /// can copy and drop a table other tables point at, and its renames leave the references in
        /// other tables' definitions as they were (legacy_alter_table), so they point at the new
        /// table once it takes the name. Both are set every time, because the connection comes from
        /// a pool shared with the other kind.
        /// </summary>
        protected override async ValueTask PrepareConnectionAsync(DbConnection connection, bool async, CancellationToken token)
        {
            using var command = connection.CreateCommand();
            command.CommandText = _forSchema
                ? "PRAGMA foreign_keys = OFF; PRAGMA legacy_alter_table = ON"
                : Options.ForeignKeys ? "PRAGMA foreign_keys = ON; PRAGMA legacy_alter_table = OFF" : "PRAGMA foreign_keys = OFF; PRAGMA legacy_alter_table = OFF";
            if (async)
                await command.ExecuteNonQueryAsync(token);
            else
                command.ExecuteNonQuery();
        }

        // The driver's command timeout is how long a command waits for another connection's lock
        // (BusyTimeoutSeconds, which the connection gives every command), not a limit on a running
        // statement, so the statement timeout would only lengthen that wait.
        protected override bool AppliesStatementTimeout => false;

        protected override DbParameter CreateParameter(DbCommand command, string name, object? value, IQueryParameter source)
        {
            var column = (source as Parameter)?.ColumnSchema as ColumnSchema;
            return new SqliteParameter($"{Provider.ParameterPrefix}{name}", SqliteValues.ToDb(value, column));
        }

        protected override DbParameter CreateListParameter(DbCommand command, string name, ValueList values, IQueryParameter source) =>
            new SqliteParameter($"{Provider.ParameterPrefix}{name}", SqliteValues.ToJsonArray(values, (source as Parameter)?.ColumnSchema as ColumnSchema));

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
