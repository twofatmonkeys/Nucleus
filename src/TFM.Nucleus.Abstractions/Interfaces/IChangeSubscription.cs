using System;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// Raised when data for the subscribed entity type changes outside the current process.
    /// Providers coalesce notifications, so the event means "something changed — reload or
    /// invalidate", never "exactly one row changed"; it carries only the entity type.
    /// </summary>
    public sealed class ChangeSubscriptionChangedEventArgs : EventArgs
    {
        public Type EntityType { get; }

        public ChangeSubscriptionChangedEventArgs(Type entityType)
        {
            EntityType = entityType;
        }
    }

    /// <summary>
    /// Raised when a subscription faults — the provider rejected or invalidated the registration,
    /// or re-arming after a change failed. A faulted subscription never recovers; dispose it and
    /// subscribe again.
    /// </summary>
    public sealed class ChangeSubscriptionFaultedEventArgs : EventArgs
    {
        public Exception Fault { get; }

        public ChangeSubscriptionFaultedEventArgs(Exception fault)
        {
            Fault = fault;
        }
    }

    /// <summary>
    /// A provider-neutral, live subscription to external changes for one entity type. Events fire
    /// on threadpool threads; UI consumers marshal themselves. Obtain one via
    /// <see cref="IDataProvider.SubscribeToChangesAsync{T}"/> on a provider whose
    /// <see cref="DataProviderCapabilities.SupportsChangeNotifications"/> is true.
    /// </summary>
    public interface IChangeSubscription : IAsyncDisposable
    {
        Type EntityType { get; }

        /// <summary>True once the subscription is dead; <see cref="Fault"/> carries the reason.</summary>
        bool IsFaulted { get; }

        /// <summary>The error that faulted the subscription, or null while it is live.</summary>
        Exception? Fault { get; }

        /// <summary>Fires at least once after an external change to the entity type's data.</summary>
        event EventHandler<ChangeSubscriptionChangedEventArgs>? Changed;

        /// <summary>The subscription is dead and will not re-arm; the consumer must resubscribe.</summary>
        event EventHandler<ChangeSubscriptionFaultedEventArgs>? Faulted;
    }
}
