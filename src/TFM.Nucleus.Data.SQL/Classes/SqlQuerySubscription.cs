using Microsoft.Data.SqlClient;
using System.Diagnostics;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Data.SQL
{
    /// <summary>
    /// A live SQL Server query-notification subscription over one entity type:
    /// Changed fires whenever rows in the entity's table are inserted, updated, or deleted by
    /// any process. The subscription owns a dedicated watcher connection for its whole lifetime
    /// (notifications are one-shot, so each fire re-executes the query and re-registers). Rows
    /// are never surfaced; Nucleus uses this for cache invalidation, so the event carries only
    /// the entity type.
    /// </summary>
    internal sealed class SqlQuerySubscription<T> : ChangeSubscriptionBase<T> where T : ICoreObject<T>, new()
    {
        private readonly string _selectText;
        private readonly string _connectionString;
        private readonly SqlConnection _connection;

        private SqlQuerySubscription(string selectText, SqlConnection connection)
        {
            _selectText = selectText;
            _connection = connection;
            _connectionString = connection.ConnectionString;
        }

        internal static async Task<IChangeSubscription> CreateAsync(SQLDataProvider provider, CancellationToken token = default)
        {
            var tableSchema = provider.GetTableSchema<T>();
            // Notification-legal SELECT: explicit column list (no *), two-part [dbo].[table]
            // name, no TOP, no ORDER BY, single statement over a single table.
            var columns = string.Join(", ", tableSchema.Columns.Select(c => $"[{c.ColumnName}]"));
            var selectText = $"SELECT {columns} FROM [dbo].[{tableSchema.TableName}]";

            // The watcher connection is one of the provider's MaxConnectionPoolSize: taken first,
            // so a full budget fails as PoolExhausted instead of waiting on the driver's pool.
            var slot = await provider.ConnectionSlots.AcquireAsync(token);
            SqlConnection connection;
            try
            {
                connection = provider.SqlConfig.BuildNewSQLConnection();
                QueryNotificationHost.Acquire(connection.ConnectionString);
            }
            catch
            {
                slot.Dispose();
                throw;
            }
            var subscription = new SqlQuerySubscription<T>(selectText, connection) { Slot = slot };
            try
            {
                await ConnectionOpener.OpenAsync(connection, async: true, Math.Max(1, provider.SqlConfig.ConnectAttempts), SqlServerErrors.IsTransient, ConnectionOpener.TimeoutOf(connection), token);

                // Query notifications require ARITHABORT ON; it is the one required SET option
                // that defaults OFF over TDS.
                using (var set = connection.CreateCommand())
                {
                    set.CommandText = "SET ARITHABORT ON";
                    await set.ExecuteNonQueryAsync(token);
                }

                await subscription.ExecuteAndRegisterAsync(token);
                return subscription;
            }
            catch (Exception ex)
            {
                // No half-open state: tear down everything acquired above before rethrowing.
                await connection.DisposeAsync();
                QueryNotificationHost.Release(subscription._connectionString);
                slot.Dispose();
                if (DatabaseFailures.IsDatabaseFailure(ex))
                    throw DatabaseFailures.Translate($"Unable to register a query-notification subscription for {typeof(T).FullName}. {ex.Message}", ex,
                        SqlServerErrors.Classify(ex), DatabaseOperation.Subscribe, token: token);
                throw;
            }
        }

        /// <summary>
        /// Executes the subscribed select with a fresh SqlDependency attached: one execution is
        /// both the (re-)registration and the read, so there is no gap between the data read and
        /// the changes watched. The reader is drained but rows are not surfaced.
        /// </summary>
        private async Task ExecuteAndRegisterAsync(CancellationToken token = default)
        {
            try
            {
                using var command = _connection.CreateCommand();
                command.CommandText = _selectText;
                var dependency = new SqlDependency(command);
                dependency.OnChange += OnDependencyChange;

                using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    // Drain only: the registration needs the statement fully executed.
                }
            }
            catch (Exception ex) when (DatabaseFailures.IsDatabaseFailure(ex))
            {
                throw DatabaseFailures.Translate($"Unable to execute the query-notification select for {typeof(T).FullName}. {ex.Message}", ex,
                    SqlServerErrors.Classify(ex), DatabaseOperation.Subscribe, token: token);
            }
        }

        private void OnDependencyChange(object? sender, SqlNotificationEventArgs e)
        {
            // Notifications are one-shot: detach first so a stale dependency can never call back twice.
            if (sender is SqlDependency dependency)
                dependency.OnChange -= OnDependencyChange;
            _ = HandleNotificationAsync(async () =>
            {
                if (e.Type == SqlNotificationType.Subscribe || e.Source == SqlNotificationSource.Statement)
                {
                    // The server refused or invalidated the registration itself; re-arming
                    // would spin on the same failure.
                    return NotificationOutcome.Faulted(new InvalidOperationException(
                        $"The query-notification subscription for {typeof(T).FullName} is invalid " +
                        $"(Type={e.Type}, Source={e.Source}, Info={e.Info}); the statement violates " +
                        "the query-notification rules or the subscription could not be created."));
                }
                await ExecuteAndRegisterAsync();
                // A registration that reached its timeout fired without a data change:
                // re-arm silently, no Changed.
                return e.Source != SqlNotificationSource.Timeout ? NotificationOutcome.Change : NotificationOutcome.Quiet;
            });
        }

        /// <summary>
        /// Closes the watcher connection and releases the listener (the last subscription for a
        /// connection string stops it). The current server-side registration is not individually
        /// cancellable; it expires on the server after the listener stops.
        /// </summary>
        protected override async ValueTask DisposeCoreAsync()
        {
            try
            {
                await _connection.DisposeAsync();
            }
            catch (Exception ex)
            {
                // Teardown must not throw; a dead connection has nothing left to close.
                Trace.TraceWarning($"Closing the query-notification watcher connection for {typeof(T).FullName} failed: {ex.Message}");
            }
            QueryNotificationHost.Release(_connectionString);
        }
    }
}
