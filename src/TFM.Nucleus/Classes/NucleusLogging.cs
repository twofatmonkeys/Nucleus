using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using TFM.Nucleus.Data;

namespace TFM.Nucleus
{
    /// <summary>
    /// A host application log entry persisted by the Nucleus logger provider. Suppressed from
    /// audit logging (persisting a log entry must never generate another log entry).
    /// </summary>
    [SuppressLogging]
    public class NucleusLogEntry : CoreObject<NucleusLogEntry>
    {
        [DatabaseColumn]
        public string? Category { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public string? Level { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public int EventId { get => GetValue<int>(); set => SetValue(value); }

        [DatabaseColumn]
        public string? Message { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public string? Exception { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public DateTimeOffset LoggedAt { get => GetValue<DateTimeOffset>(); set => SetValue(value); }

        /// <summary>The application that wrote the entry: <see cref="NucleusOptions.AppName"/>.</summary>
        [DatabaseColumn]
        public string? AppName { get => GetValue<string>(); set => SetValue(value); }

        /// <summary>The version of the application that wrote the entry: <see cref="NucleusOptions.AppVersion"/>.</summary>
        [DatabaseColumn]
        public string? AppVersion { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>
    /// An ILoggerProvider that persists host application log entries into the Nucleus-managed
    /// NucleusLogEntry table. Entries buffer in memory and flush in batches on a timer so logging
    /// never blocks the caller. Guards against re-entrancy: TFM.Nucleus.* categories are never
    /// persisted. Entries logged before the runtime is initialised stay buffered until it is, up
    /// to <see cref="MaxBufferedEntries"/>, after which the oldest are dropped.
    /// </summary>
    public sealed class NucleusLoggerProvider : ILoggerProvider
    {
        private const int FlushIntervalMilliseconds = 5000;
        private const int MaxBufferedEntries = 10_000;

        private readonly NucleusOptions _options;
        private readonly ConcurrentQueue<NucleusLogEntry> _buffer = new();
        private readonly Timer _flushTimer;
        private int _flushing;
        // ConcurrentQueue.Count walks the segments, so it is not something to call on every log
        // call. Track the depth alongside the queue instead.
        private int _bufferedCount;

        public NucleusLoggerProvider(NucleusOptions? options = null)
        {
            _options = options ?? new NucleusOptions();
            _flushTimer = new Timer(_ => Flush(), null, FlushIntervalMilliseconds, FlushIntervalMilliseconds);
        }

        public ILogger CreateLogger(string categoryName) => new NucleusLogger(this, categoryName);

        private bool ShouldPersist(string category, LogLevel level) =>
            level >= _options.LogEntityMinimumLevel &&
            !category.StartsWith("TFM.Nucleus", StringComparison.Ordinal);

        private void Enqueue(NucleusLogEntry entry)
        {
            if (Volatile.Read(ref _bufferedCount) >= MaxBufferedEntries)
                return; // shed load rather than grow unbounded
            _buffer.Enqueue(entry);
            Interlocked.Increment(ref _bufferedCount);
        }

        /// <summary>
        /// Persists buffered entries. Failures are swallowed, because logging must never throw into
        /// the application, but the batch is returned to the buffer so a transient database fault
        /// costs a retry rather than the entries themselves.
        /// </summary>
        public void Flush()
        {
            if (_buffer.IsEmpty || !NucleusRuntime.IsInitialized)
                return;
            if (Interlocked.CompareExchange(ref _flushing, 1, 0) == 1)
                return;

            var batch = new List<ICoreObject?>();
            try
            {
                while (_buffer.TryDequeue(out var entry))
                {
                    Interlocked.Decrement(ref _bufferedCount);
                    batch.Add(entry);
                }
                if (batch.Count > 0)
                    NucleusRuntime.Current.Save(batch);
            }
            catch (Exception)
            {
                // Put the batch back so the next flush retries it, rather than discarding entries
                // that were only unlucky in their timing.
                Requeue(batch);
            }
            finally
            {
                Interlocked.Exchange(ref _flushing, 0);
            }
        }

        private void Requeue(List<ICoreObject?> batch)
        {
            foreach (var entry in batch)
                if (entry is NucleusLogEntry logEntry)
                    Enqueue(logEntry);
        }

        public void Dispose()
        {
            _flushTimer.Dispose();
            Flush();
        }

        private sealed class NucleusLogger(NucleusLoggerProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => provider.ShouldPersist(category, logLevel);

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                    return;
                provider.Enqueue(new NucleusLogEntry
                {
                    Category = category,
                    Level = logLevel.ToString(),
                    EventId = eventId.Id,
                    Message = formatter(state, exception),
                    Exception = exception?.ToString(),
                    LoggedAt = DateTimeOffset.Now,
                    AppName = provider._options.AppName,
                    AppVersion = provider._options.AppVersion,
                });
            }
        }
    }
}
