using Npgsql;
using System;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.Queries;
using TFM.Nucleus.Data.Schema;

namespace TFM.Nucleus.Data.PostgreSQL
{
    /// <summary>
    /// A PostgreSQL data context: the shared relational context over an NpgsqlConnection, with
    /// values bound as the types their columns store, values read back as the types their
    /// properties expect, transient opening failures retried, and the COPY bulk engine for
    /// oversized writes. ContextOptions.UseDependency has no meaning here: change notifications
    /// are subscriptions (IDataProvider.SubscribeToChangesAsync), not a per-command option.
    /// </summary>
    internal sealed partial class PostgresDataContext : DbDataContext<PostgresDataContext>
    {
        private readonly PostgresDataProvider _pgProvider;

        private PostgresDataContext(PostgresDataProvider provider) : base(provider)
        {
            _pgProvider = provider;
        }

        private PostgresConfiguration Config => _pgProvider.Config;

        private NpgsqlConnection NpgsqlConnection => (NpgsqlConnection?)Connection
            ?? throw new DataContextException("No active Connection", DataContextErrorKind.CurrentConnectionIsNull);

        internal static PostgresDataContext Create(PostgresDataProvider provider, PostgresDataContext? parent, bool useTransaction, bool useDependency)
        {
            var context = new PostgresDataContext(provider);
            context.Open(parent, useTransaction, useDependency);
            return context;
        }

        internal static async Task<PostgresDataContext> CreateAsync(PostgresDataProvider provider, PostgresDataContext? parent, bool useTransaction, bool useDependency, CancellationToken token)
        {
            var context = new PostgresDataContext(provider);
            await context.OpenAsync(parent, useTransaction, useDependency, token);
            return context;
        }

        protected override DbConnection BuildConnection(bool useTransaction) => Config.BuildConnection(useTransaction);

        protected override Task<PostgresDataContext> CreateChildContextAsync(CancellationToken token) =>
            CreateAsync(_pgProvider, this, HasTransaction, HasDependency, token);

        protected override bool IsTransientOpenFailure(Exception exception) => PostgresErrors.IsTransient(exception);

        protected override int OpenAttempts => Math.Max(1, Config.ConnectRetryCount);

        protected override DbParameter CreateParameter(DbCommand command, string name, object? value, IQueryParameter source)
        {
            var column = (source as Parameter)?.ColumnSchema as ColumnSchema;
            var converted = PostgresTypes.ToDb(value, column, out var type);
            var parameter = new NpgsqlParameter(name, converted ?? DBNull.Value);
            if (type is { } dbType && converted is not null and not DBNull)
                parameter.NpgsqlDbType = dbType;
            return parameter;
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
                row[i] = PostgresTypes.FromDb(row[i], column.DataType);
                i++;
            }
        }

        protected override bool CanUseBulkPath(Query query) => BulkWritePlanner.CanUseBulkPath(query);

        protected override string DescribeConnection() =>
            Connection is NpgsqlConnection { State: System.Data.ConnectionState.Open } connection ? $"backend {connection.ProcessID}" : "closed connection";
    }
}
