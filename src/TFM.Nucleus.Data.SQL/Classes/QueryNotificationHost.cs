using Microsoft.Data.SqlClient;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Data.SQL
{
    /// <summary>
    /// Ref-counts SqlDependency.Start/Stop per connection string: the first subscription for a
    /// string starts the process-wide notification listener, the last disposal stops it.
    /// Server prerequisites, per subscribed database: Service Broker enabled
    /// (ALTER DATABASE [db] SET ENABLE_BROKER) and the login granted the
    /// SUBSCRIBE QUERY NOTIFICATIONS permission.
    /// </summary>
    internal static class QueryNotificationHost
    {
        private static readonly ListenerRefCounter _listeners = new(
            cs => SqlDependency.Start(cs),
            cs => SqlDependency.Stop(cs),
            "Unable to start the query-notification listener. The target database needs Service Broker " +
            "enabled (ALTER DATABASE [db] SET ENABLE_BROKER) and the login needs the SUBSCRIBE QUERY " +
            "NOTIFICATIONS permission.");

        // Swappable in tests (InternalsVisibleTo) to assert ref-counting without a SQL Server.
        internal static Action<string> StartListener { get => _listeners.Start; set => _listeners.Start = value; }
        internal static Action<string> StopListener { get => _listeners.Stop; set => _listeners.Stop = value; }

        /// <summary>Registers a subscription for the connection string, starting the listener on first use.</summary>
        public static void Acquire(string connectionString) => _listeners.Acquire(connectionString);

        /// <summary>Releases a subscription for the connection string, stopping the listener when none remain.</summary>
        public static void Release(string connectionString) => _listeners.Release(connectionString);
    }
}
