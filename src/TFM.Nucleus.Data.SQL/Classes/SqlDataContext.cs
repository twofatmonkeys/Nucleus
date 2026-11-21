global using TFM.Nucleus.Data;
using Microsoft.Data.SqlClient;
using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.Queries;

namespace TFM.Nucleus.Data.SQL
{
    /// <summary>
    /// A SQL Server data context: the shared relational context over a SqlConnection, with
    /// SqlDependency registration when the context was asked for dependencies and the
    /// SqlBulkCopy engine for oversized writes.
    /// </summary>
    internal partial class SqlDataContext : DbDataContext<SqlDataContext>
    {
        private readonly SQLDataProvider _sqlProvider;

        private SqlDataContext(SQLDataProvider provider) : base(provider)
        {
            _sqlProvider = provider;
        }

        private SQLConfiguration SqlConfig => _sqlProvider.SqlConfig;

        private SqlConnection? SqlConnection => (SqlConnection?)Connection;
        private SqlTransaction? SqlTransaction => (SqlTransaction?)Transaction;
        private SqlCommand SqlCommand => (SqlCommand?)Command ?? throw new DataContextException("No active Command", DataContextErrorKind.CurrentCommandIsNull);

        internal static SqlDataContext Create(SQLDataProvider provider, SqlDataContext? parent, bool useTransaction, bool useDependency)
        {
            var context = new SqlDataContext(provider);
            context.Open(parent, useTransaction, useDependency);
            return context;
        }

        internal static async Task<SqlDataContext> CreateAsync(SQLDataProvider provider, SqlDataContext? parent, bool useTransaction, bool useDependency, CancellationToken token)
        {
            var context = new SqlDataContext(provider);
            await context.OpenAsync(parent, useTransaction, useDependency, token);
            return context;
        }

        protected override DbConnection BuildConnection(bool useTransaction) => SqlConfig.BuildNewSQLConnection(useTransaction);

        protected override Task<SqlDataContext> CreateChildContextAsync(CancellationToken token) =>
            CreateAsync(_sqlProvider, this, HasTransaction, WatchesForChanges, token);

        // Start the dependency. The login must have SUBSCRIBE QUERY NOTIFICATIONS permission and
        // the database must have Service Broker enabled (ALTER DATABASE [db] SET ENABLE_BROKER).
        protected override void OnOpened() => SqlDependency.Start(SqlConnection!.ConnectionString);

        protected override void OnDisposing() => SqlDependency.Stop(SqlConnection!.ConnectionString);

        protected override void PrepareCommand(DbCommand command)
        {
            if (!WatchesForChanges)
                return;
            var sqlCommand = (SqlCommand)command;
            sqlCommand.Notification = null;
            var dependency = new SqlDependency(sqlCommand);
            dependency.OnChange += OnChange;
        }

        // Notices are one-shot, so a fired dependency detaches itself.
        private void OnChange(object sender, SqlNotificationEventArgs e)
        {
            if (sender is SqlDependency dependency)
                dependency.OnChange -= OnChange;
        }

        protected override bool CanUseBulkPath(Query query) => BulkWritePlanner.CanUseBulkPath(query);

        protected override string DescribeConnection() =>
            SqlConnection?.ClientConnectionId.ToString() ?? "error: null connection";
    }
}
