using Npgsql;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Data.Providers;
using static TFM.Nucleus.Data.PostgreSQL.PostgresIdentifiers;

namespace TFM.Nucleus.Data.PostgreSQL
{
    /// <summary>
    /// A live PostgreSQL change subscription over one entity type. The table's trigger notifies
    /// its channel once per statement that writes it, from any session; the subscription owns a
    /// connection that LISTENs on that channel for its whole lifetime, and Changed fires per
    /// notification. Rows are never surfaced. When the connection drops it reconnects, retrying
    /// transient failures, and reports one change, since a notification sent while it was down is
    /// lost; when it cannot reconnect it faults.
    /// </summary>
    internal sealed class PostgresChangeSubscription<T> : ChangeSubscriptionBase<T> where T : ICoreObject<T>, new()
    {
        private readonly PostgresConfiguration _config;
        private readonly string _channel;
        private readonly CancellationTokenSource _stop = new();
        private NpgsqlConnection? _connection;
        private Task _listening = Task.CompletedTask;

        private PostgresChangeSubscription(PostgresConfiguration config, string channel)
        {
            _config = config;
            _channel = channel;
        }

        internal static async Task<IChangeSubscription> CreateAsync(PostgresDataProvider provider, CancellationToken token = default)
        {
            var table = provider.GetTableSchema<T>();
            // The LISTEN connection is one of the provider's MaxConnectionPoolSize, held for the
            // subscription's life and across reconnects: taken first, so a full budget fails as
            // PoolExhausted instead of waiting on the driver's pool.
            var slot = await provider.ConnectionSlots.AcquireAsync(token);
            var subscription = new PostgresChangeSubscription<T>(provider.Config, PostgresSchemaDialect.NotifyChannel(table.TableName)) { Slot = slot };
            try
            {
                await subscription.ConnectAsync(token);
            }
            catch (Exception ex)
            {
                // Whatever failed, the connection and the slot are released before it propagates.
                await subscription.DisposeConnectionAsync();
                slot.Dispose();
                if (DatabaseFailures.IsDatabaseFailure(ex))
                    throw DatabaseFailures.Translate($"Unable to register a change subscription for {typeof(T).FullName}. {ex.Message}", ex,
                        PostgresErrors.Classify(ex), DatabaseOperation.Subscribe, token: token);
                throw;
            }
            subscription._listening = Task.Run(subscription.ListenAsync);
            return subscription;
        }

        private async Task ConnectAsync(CancellationToken token)
        {
            var connection = _config.BuildConnection();
            try
            {
                await ConnectionOpener.OpenAsync(connection, async: true, Math.Max(1, _config.ConnectAttempts), PostgresErrors.IsTransient, ConnectionOpener.TimeoutOf(connection), token);
                connection.Notification += OnNotification;
                await using var command = connection.CreateCommand();
                command.CommandText = $"LISTEN {Quote(_channel)}";
                await command.ExecuteNonQueryAsync(token);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
            _connection = connection;
        }

        private void OnNotification(object sender, NpgsqlNotificationEventArgs e)
        {
            if (string.Equals(e.Channel, _channel, StringComparison.Ordinal))
                _ = HandleNotificationAsync(() => Task.FromResult(NotificationOutcome.Change));
        }

        /// <summary>Waits for notifications until disposed; the driver raises them during the wait.</summary>
        private async Task ListenAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await _connection!.WaitAsync(_stop.Token);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException or TimeoutException)
                {
                    if (await TryReconnectAsync())
                    {
                        await HandleNotificationAsync(() => Task.FromResult(NotificationOutcome.Change));
                        continue;
                    }
                    await HandleNotificationAsync(() => Task.FromResult(NotificationOutcome.Faulted(new InvalidOperationException(
                        $"The change subscription for {typeof(T).FullName} lost its connection and could not reconnect. {ex.Message}", ex))));
                    return;
                }
            }
        }

        private async Task<bool> TryReconnectAsync()
        {
            await DisposeConnectionAsync();
            try
            {
                await ConnectAsync(_stop.Token);
                return true;
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException or TimeoutException or OperationCanceledException)
            {
                Trace.TraceWarning($"Reconnecting the change subscription for {typeof(T).FullName} failed: {ex.Message}");
                return false;
            }
        }

        private async Task DisposeConnectionAsync()
        {
            var connection = Interlocked.Exchange(ref _connection, null);
            if (connection == null)
                return;
            connection.Notification -= OnNotification;
            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException)
            {
                // Teardown must not throw; a dead connection has nothing left to close.
                Trace.TraceWarning($"Closing the change subscription connection for {typeof(T).FullName} failed: {ex.Message}");
            }
        }

        /// <summary>Stops listening and closes the connection; the server forgets the LISTEN with it.</summary>
        protected override async ValueTask DisposeCoreAsync()
        {
            _stop.Cancel();
            try
            {
                await _listening;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"The change subscription for {typeof(T).FullName} stopped with an error: {ex.Message}");
            }
            await DisposeConnectionAsync();
            _stop.Dispose();
        }
    }
}
