using Microsoft.Data.SqlClient;
using System.Diagnostics;

using TFM.Nucleus.Data.Queries;

namespace TFM.Nucleus.Data.SQL
{
    /// <summary>
    /// A live SQL Server query-notification subscription over one entity type:
    /// <see cref="Changed"/> fires whenever rows in the entity's table are inserted, updated,
    /// or deleted by any process. The subscription owns a dedicated watcher connection for its
    /// whole lifetime (notifications are one-shot, so each fire re-executes the query and
    /// re-registers). Rows are never surfaced — Nucleus uses this for cache invalidation, so
    /// the event carries only the entity type. Both events fire on threadpool threads; UI
    /// consumers marshal themselves.
    /// </summary>
    public sealed class SqlQuerySubscription<T> : IChangeSubscription where T : ICoreObject<T>, new()
    {
        private readonly string _selectText;
        private readonly string _connectionString;
        private readonly SqlConnection _connection;
        // Serializes re-arm against dispose. Never disposed: a late notification may still race
        // a WaitAsync, and an idle SemaphoreSlim holds no unmanaged resources.
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _disposed;

        public Type EntityType => typeof(T);

        /// <summary>The error that faulted the subscription, or null while it is live.</summary>
        public Exception? Fault { get; private set; }
        public bool IsFaulted => Fault != null;

        /// <summary>Data for <typeparamref name="T"/> changed outside this process.</summary>
        public event EventHandler<ChangeSubscriptionChangedEventArgs>? Changed;

        /// <summary>The subscription faulted and will not re-arm; dispose and subscribe again.</summary>
        public event EventHandler<ChangeSubscriptionFaultedEventArgs>? Faulted;

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

            var connection = provider.SqlConfig.BuildNewSQLConnection(false);
            QueryNotificationHost.Acquire(connection.ConnectionString);
            var subscription = new SqlQuerySubscription<T>(selectText, connection);
            try
            {
                await connection.OpenAsync(token);

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
                throw new Exception($"Unable to register a query-notification subscription for {typeof(T).FullName}. {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Executes the subscribed select with a fresh SqlDependency attached — one execution is
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
            catch (Exception ex)
            {
                throw new Exception($"Unable to execute the query-notification select for {typeof(T).FullName}. {ex.Message}", ex);
            }
        }

        private void OnDependencyChange(object? sender, SqlNotificationEventArgs e)
        {
            // Notifications are one-shot: detach first so a stale dependency can never call back twice.
            if (sender is SqlDependency dependency)
                dependency.OnChange -= OnDependencyChange;
            _ = HandleChangeAsync(e);
        }

        private async Task HandleChangeAsync(SqlNotificationEventArgs e)
        {
            ChangeSubscriptionChangedEventArgs? changed = null;
            ChangeSubscriptionFaultedEventArgs? faulted = null;
            try
            {
                await _gate.WaitAsync();
                try
                {
                    if (_disposed)
                        return;

                    if (e.Type == SqlNotificationType.Subscribe || e.Source == SqlNotificationSource.Statement)
                    {
                        // The server refused or invalidated the registration itself; re-arming
                        // would spin on the same failure.
                        Fault = new InvalidOperationException(
                            $"The query-notification subscription for {typeof(T).FullName} is invalid " +
                            $"(Type={e.Type}, Source={e.Source}, Info={e.Info}); the statement violates " +
                            "the query-notification rules or the subscription could not be created.");
                        faulted = new ChangeSubscriptionFaultedEventArgs(Fault);
                    }
                    else
                    {
                        try
                        {
                            await ExecuteAndRegisterAsync();
                            // A registration that reached its timeout fired without a data change:
                            // re-arm silently, no Changed.
                            if (e.Source != SqlNotificationSource.Timeout)
                                changed = new ChangeSubscriptionChangedEventArgs(typeof(T));
                        }
                        catch (Exception ex)
                        {
                            Fault = ex;
                            faulted = new ChangeSubscriptionFaultedEventArgs(ex);
                        }
                    }
                }
                finally
                {
                    _gate.Release();
                }

                // Raised outside the gate so a handler may dispose the subscription without deadlocking.
                if (changed != null)
                    Changed?.Invoke(this, changed);
                if (faulted != null)
                    Faulted?.Invoke(this, faulted);
            }
            catch (Exception ex)
            {
                // Fire-and-forget from OnDependencyChange: nothing may escape onto the threadpool.
                Trace.TraceError($"Query-notification change handling for {typeof(T).FullName} failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Detaches the subscription: closes the watcher connection and releases the listener
        /// (the last subscription for a connection string stops it). The current server-side
        /// registration is not individually cancellable; it expires on the server after the
        /// listener stops. A notification arriving mid-dispose is swallowed.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync();
            try
            {
                if (_disposed)
                    return;
                _disposed = true;
            }
            finally
            {
                _gate.Release();
            }

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
