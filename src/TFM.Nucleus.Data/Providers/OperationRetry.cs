using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TFM.Nucleus.Data.Providers
{
    /// <summary>
    /// The retry policy of the operations Nucleus runs on a context of its own: a manager save,
    /// delete or load, a row read, and the read of rows a load left for later. An attempt that
    /// fails with a <see cref="DatabaseException"/> whose <see cref="DatabaseException.IsTransient"/>
    /// is true (worth another attempt, and nothing applied) is made again from the start, on a new
    /// context, up to the provider's <see cref="IDataProvider.RetryAttempts"/>, with the back-off
    /// opening a connection uses. Any other failure propagates at once. A transient failure on the
    /// last attempt is reported with <see cref="DatabaseException.IsTransient"/> false and the
    /// attempts made, so a caller does not retry on top of the policy.
    /// </summary>
    /// <remarks>
    /// With one attempt, the default, the operation runs as it is, with nothing allocated for the
    /// policy: the state is a struct and the bodies are static lambdas.
    /// </remarks>
    internal static class OperationRetry
    {
        /// <summary>The wait before attempt <paramref name="attempt"/> + 1: 200 ms, 400 ms, 800 ms...</summary>
        public static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1));

        /// <summary>Runs <paramref name="body"/>, attempting it again as the provider's policy says.</summary>
        public static Task<TResult> RunAsync<TState, TResult>(IDataProvider provider, TState state,
            Func<TState, CancellationToken, Task<TResult>> body, CancellationToken token)
        {
            var attempts = provider.RetryAttempts;
            return attempts <= 1 ? body(state, token) : RetryAsync(provider, attempts, state, body, token);
        }

        /// <inheritdoc cref="RunAsync"/>
        public static TResult Run<TState, TResult>(IDataProvider provider, TState state, Func<TState, TResult> body)
        {
            var attempts = provider.RetryAttempts;
            if (attempts <= 1)
                return body(state);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return body(state);
                }
                catch (DatabaseException ex) when (ex.IsTransient)
                {
                    if (attempt >= attempts)
                        throw Exhausted(ex, attempt);
                    Thread.Sleep(BeforeNext(provider, ex, attempt, attempts));
                }
            }
        }

        private static async Task<TResult> RetryAsync<TState, TResult>(IDataProvider provider, int attempts, TState state,
            Func<TState, CancellationToken, Task<TResult>> body, CancellationToken token)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await body(state, token).ConfigureAwait(false);
                }
                catch (DatabaseException ex) when (ex.IsTransient)
                {
                    if (attempt >= attempts)
                        throw Exhausted(ex, attempt);
                    await Task.Delay(BeforeNext(provider, ex, attempt, attempts), token).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Streams the rows <paramref name="open"/> starts, on a context it opens. Opening and the
        /// first row are attempted again as the policy says, each on a new context; once a row has
        /// been handed out, a failure propagates, because the rows already read cannot be taken back.
        /// The context is disposed when the stream ends or is abandoned.
        /// </summary>
        public static async IAsyncEnumerable<TRow> StreamAsync<TState, TRow>(IDataProvider provider, TState state,
            Func<TState, CancellationToken, Task<OpenedStream<TRow>>> open, [EnumeratorCancellation] CancellationToken token)
        {
            var attempts = provider.RetryAttempts;
            OpenedStream<TRow> stream = default;
            bool hasRow;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    stream = await open(state, token).ConfigureAwait(false);
                    hasRow = await stream.Rows.MoveNextAsync().ConfigureAwait(false);
                    break;
                }
                catch (Exception ex)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    stream = default;
                    if (attempts <= 1 || ex is not DatabaseException { IsTransient: true } failure)
                        throw;
                    if (attempt >= attempts)
                        throw Exhausted(failure, attempt);
                    await Task.Delay(BeforeNext(provider, failure, attempt, attempts), token).ConfigureAwait(false);
                }
            }
            try
            {
                while (hasRow)
                {
                    yield return stream.Rows.Current;
                    hasRow = await stream.Rows.MoveNextAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>Logs the failed attempt and returns the wait before the next.</summary>
        public static TimeSpan BeforeNext(IDataProvider provider, DatabaseException failure, int attempt, int attempts)
        {
            var delay = Backoff(attempt);
            (provider as RelationalDataProvider)?.Logger?.LogWarning(failure,
                "A {operation} failed transiently ({kind}) on attempt {attempt} of {attempts}; attempting it again in {delay} ms.",
                failure.Operation, failure.Kind, attempt, attempts, delay.TotalMilliseconds);
            return delay;
        }

        /// <summary>
        /// The last attempt's failure as the caller sees it: the same failure, not transient, since
        /// the policy has spent its attempts, with how many it made. Its inner exception is the
        /// driver's, as on every <see cref="DatabaseException"/>.
        /// </summary>
        public static DatabaseException Exhausted(DatabaseException failure, int attempts) =>
            new($"{failure.Message} Gave up after {attempts} attempts.", failure.Kind, transient: false, failure.InnerException)
            {
                MayHaveBeenApplied = failure.MayHaveBeenApplied,
                Operation = failure.Operation,
                Attempts = attempts,
            };
    }

    /// <summary>A stream <see cref="OperationRetry.StreamAsync"/> reads: its rows, and the context they come from.</summary>
    internal readonly record struct OpenedStream<TRow>(IDataContext Context, IAsyncEnumerator<TRow> Rows) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Rows != null)
                    await Rows.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (Context != null)
                    await Context.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
