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
    /// attempt's, propagates unchanged. Given a total timeout, every attempt and back-off shares
    /// it: no attempt starts that the back-off would push past it, and on the asynchronous path an
    /// attempt still running when it runs out is cancelled and reported as a
    /// <see cref="TimeoutException"/>. The synchronous path cannot cancel a running attempt, so the
    /// driver's own connect timeout bounds that one.
    /// </summary>
    public static class ConnectionOpener
    {
        /// <summary>Opens with no total timeout: each attempt is bounded only by the driver.</summary>
        public static ValueTask OpenAsync(DbConnection connection, bool async, int attempts, Func<Exception, bool> isTransient, CancellationToken token) =>
            OpenAsync(connection, async, attempts, isTransient, Timeout.InfiniteTimeSpan, token);

        /// <summary>Opens within <paramref name="totalTimeout"/>, retries included; zero or infinite means no limit.</summary>
        public static async ValueTask OpenAsync(DbConnection connection, bool async, int attempts, Func<Exception, bool> isTransient, TimeSpan totalTimeout, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(isTransient);
            var limited = totalTimeout > TimeSpan.Zero && totalTimeout != Timeout.InfiniteTimeSpan;
            var clock = limited ? Stopwatch.StartNew() : null;
            using var budget = limited && async ? CancellationTokenSource.CreateLinkedTokenSource(token) : null;
            budget?.CancelAfter(totalTimeout);
            var openToken = budget?.Token ?? token;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (async)
                        await connection.OpenAsync(openToken);
                    else
                        connection.Open();
                    return;
                }
                catch (Exception ex) when (budget is { IsCancellationRequested: true } && !token.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Opening a connection did not finish within its connect timeout of {totalTimeout.TotalSeconds:0.###} s, retries included.", ex);
                }
                catch (Exception ex) when (attempt < attempts && isTransient(ex) && !token.IsCancellationRequested
                    && (clock == null || clock.Elapsed + Backoff(attempt) < totalTimeout))
                {
                    var delay = Backoff(attempt);
                    Debug.WriteLine($"Opening a connection failed transiently (attempt {attempt}); retrying in {delay.TotalMilliseconds} ms. {ex.Message}", "Database");
                    if (async)
                        await Task.Delay(delay, token);
                    else
                        Thread.Sleep(delay);
                }
            }
        }

        /// <summary>
        /// The connection's own connect timeout as a total timeout for <see cref="OpenAsync(DbConnection, bool, int, Func{Exception, bool}, TimeSpan, CancellationToken)"/>:
        /// what its connection string says, raw or built, with 0 meaning no limit.
        /// </summary>
        public static TimeSpan TimeoutOf(DbConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);
            return connection.ConnectionTimeout > 0 ? TimeSpan.FromSeconds(connection.ConnectionTimeout) : Timeout.InfiniteTimeSpan;
        }

        private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1));
    }
}
