using Microsoft.Data.SqlClient;
using System.Diagnostics;

using TFM.Nucleus.Data.Queries;

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
        private static readonly Dictionary<string, int> _refCounts = [];
        private static readonly object _lock = new();

        // Swappable in tests (InternalsVisibleTo) to assert ref-counting without a SQL Server.
        internal static Action<string> StartListener = cs => SqlDependency.Start(cs);
        internal static Action<string> StopListener = cs => SqlDependency.Stop(cs);

        /// <summary>Registers a subscription for the connection string, starting the listener on first use.</summary>
        public static void Acquire(string connectionString)
        {
            lock (_lock)
            {
                _refCounts.TryGetValue(connectionString, out var count);
                if (count == 0)
                {
                    try
                    {
                        StartListener(connectionString);
                    }
                    catch (Exception ex)
                    {
                        throw new Exception(
                            "Unable to start the query-notification listener. The target database needs Service Broker " +
                            "enabled (ALTER DATABASE [db] SET ENABLE_BROKER) and the login needs the SUBSCRIBE QUERY " +
                            $"NOTIFICATIONS permission. {ex.Message}", ex);
                    }
                }
                _refCounts[connectionString] = count + 1;
            }
        }

        /// <summary>Releases a subscription for the connection string, stopping the listener when none remain.</summary>
        public static void Release(string connectionString)
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
                        StopListener(connectionString);
                    }
                    catch (Exception ex)
                    {
                        // Teardown must not throw; a dead server just means there is nothing left to stop.
                        Trace.TraceWarning($"SqlDependency.Stop failed during subscription teardown: {ex.Message}");
                    }
                }
                else
                {
                    _refCounts[connectionString] = count - 1;
                }
            }
        }
    }
}
