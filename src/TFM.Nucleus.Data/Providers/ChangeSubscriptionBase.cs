using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data.Providers
{
    /// <summary>
    /// A change subscription over one entity type, whatever the engine delivers notifications
    /// with. It owns the events, the fault state and the gate that serialises a notification
    /// against dispose. Events are raised on threadpool threads, outside the gate, so a handler
    /// may dispose the subscription without deadlocking; UI consumers marshal themselves.
    /// </summary>
    public abstract class ChangeSubscriptionBase<T> : IChangeSubscription where T : ICoreObject
    {
        // Serializes notification handling against dispose. Never disposed: a late notification
        // may still race a WaitAsync, and an idle SemaphoreSlim holds no unmanaged resources.
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _disposed;

        public Type EntityType => typeof(T);

        /// <summary>
        /// The provider connection slot the subscription's connection holds, taken before the
        /// connection opens. Given back when the subscription is disposed, after the engine's
        /// resources are released.
        /// </summary>
        protected ConnectionSlot? Slot { get; init; }

        /// <summary>The error that faulted the subscription, or null while it is live.</summary>
        public Exception? Fault { get; private set; }
        public bool IsFaulted => Fault != null;

        /// <summary>Data for <typeparamref name="T"/> changed outside this process.</summary>
        public event EventHandler<ChangeSubscriptionChangedEventArgs>? Changed;

        /// <summary>The subscription faulted and will not re-arm; dispose and subscribe again.</summary>
        public event EventHandler<ChangeSubscriptionFaultedEventArgs>? Faulted;

        /// <summary>What handling one notification found.</summary>
        protected readonly record struct NotificationOutcome(bool Changed, Exception? Fault)
        {
            public static NotificationOutcome Change => new(true, null);
            public static NotificationOutcome Quiet => new(false, null);
            public static NotificationOutcome Faulted(Exception fault) => new(false, fault);
        }

        /// <summary>
        /// Handles one notification under the gate, unless the subscription is disposed, then
        /// raises Changed or Faulted outside it. Nothing escapes: this runs fire and forget.
        /// </summary>
        protected async Task HandleNotificationAsync(Func<Task<NotificationOutcome>> handle)
        {
            try
            {
                NotificationOutcome outcome;
                await _gate.WaitAsync();
                try
                {
                    if (_disposed)
                        return;
                    try
                    {
                        outcome = await handle();
                    }
                    catch (Exception ex)
                    {
                        outcome = NotificationOutcome.Faulted(ex);
                    }
                    if (outcome.Fault != null)
                        Fault = outcome.Fault;
                }
                finally
                {
                    _gate.Release();
                }

                if (outcome.Changed)
                    Changed?.Invoke(this, new ChangeSubscriptionChangedEventArgs(typeof(T)));
                if (outcome.Fault != null)
                    Faulted?.Invoke(this, new ChangeSubscriptionFaultedEventArgs(outcome.Fault));
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Change notification handling for {typeof(T).FullName} failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Detaches the subscription: marks it disposed under the gate, so a notification arriving
        /// mid-dispose is swallowed, then releases what the engine holds.
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
                await DisposeCoreAsync();
            }
            finally
            {
                Slot?.Dispose();
            }
            GC.SuppressFinalize(this);
        }

        /// <summary>Releases the engine's resources. Must not throw.</summary>
        protected abstract ValueTask DisposeCoreAsync();
    }

    /// <summary>
    /// Ref-counts a process-wide listener per connection string: the first subscription for a
    /// string starts it, the last release stops it. A start the database fails is a
    /// <see cref="DatabaseException"/>, and any other a <see cref="DataProviderException"/>, both
    /// with <see cref="StartFailureMessage"/>; stop failures are traced, since teardown must not throw.
    /// </summary>
    public sealed class ListenerRefCounter(Action<string> start, Action<string> stop, string startFailureMessage)
    {
        private readonly Dictionary<string, int> _refCounts = [];
        private readonly object _lock = new();

        /// <summary>Starts a listener; swappable so tests can count without an engine.</summary>
        public Action<string> Start { get; set; } = start;

        /// <summary>Stops a listener; swappable so tests can count without an engine.</summary>
        public Action<string> Stop { get; set; } = stop;

        public string StartFailureMessage { get; } = startFailureMessage;

        /// <summary>Registers a subscription for the connection string, starting the listener on first use.</summary>
        public void Acquire(string connectionString)
        {
            lock (_lock)
            {
                _refCounts.TryGetValue(connectionString, out var count);
                if (count == 0)
                {
                    try
                    {
                        Start(connectionString);
                    }
                    catch (Exception ex) when (DatabaseFailures.IsDatabaseFailure(ex))
                    {
                        throw DatabaseFailures.Create($"{StartFailureMessage} {ex.Message}", ex,
                            DatabaseFailures.ClassifyDefault(ex), DatabaseOperation.Subscribe, mayHaveBeenApplied: false);
                    }
                    catch (Exception ex) when (ex is not (NucleusException or OperationCanceledException))
                    {
                        // The engine refused to listen, usually for a missing prerequisite the
                        // message names: a setup failure of the provider, not of the database.
                        throw new DataProviderException($"{StartFailureMessage} {ex.Message}", DataProviderErrorKind.Error, ex);
                    }
                }
                _refCounts[connectionString] = count + 1;
            }
        }

        /// <summary>Releases a subscription for the connection string, stopping the listener when none remain.</summary>
        public void Release(string connectionString)
        {
            lock (_lock)
            {
                if (!_refCounts.TryGetValue(connectionString, out var count))
                    return;
                if (count <= 1)
                {
                    _refCounts.Remove(connectionString);
                    try
                    {
                        Stop(connectionString);
                    }
                    catch (Exception ex)
                    {
                        // Teardown must not throw; a dead server just means there is nothing left to stop.
                        Trace.TraceWarning($"Stopping the change listener failed during subscription teardown: {ex.Message}");
                    }
                }
                else
                {
                    _refCounts[connectionString] = count - 1;
                }
            }
        }

        /// <summary>The live subscriptions for a connection string.</summary>
        public int CountFor(string connectionString)
        {
            lock (_lock)
                return _refCounts.TryGetValue(connectionString, out var count) ? count : 0;
        }
    }
}
