using System;
using System.Data.Common;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data.Providers
{
    /// <summary>
    /// Opens a connection, retrying a failure the provider calls transient with an exponential
    /// back-off (200 ms, 400 ms, 800 ms...). A failure that is not transient, or the last
    /// attempt's, propagates unchanged.
    /// </summary>
    public static class ConnectionOpener
    {
        public static async ValueTask OpenAsync(DbConnection connection, bool async, int attempts, Func<Exception, bool> isTransient, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(connection);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (async)
                        await connection.OpenAsync(token);
                    else
                        connection.Open();
                    return;
                }
                catch (Exception ex) when (attempt < attempts && isTransient(ex) && !token.IsCancellationRequested)
                {
                    var delay = TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1));
                    Debug.WriteLine($"Opening a connection failed transiently (attempt {attempt}); retrying in {delay.TotalMilliseconds} ms. {ex.Message}", "Database");
                    if (async)
                        await Task.Delay(delay, token);
                    else
                        Thread.Sleep(delay);
                }
            }
        }
    }
}
