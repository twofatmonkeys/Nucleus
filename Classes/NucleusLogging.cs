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
    }

    /// <summary>
    /// An ILoggerProvider that persists host application log entries into the Nucleus-managed
    /// NucleusLogEntry table. Entries buffer in memory and flush in batches on a timer so logging
    /// never blocks the caller. Guards against re-entrancy: TFM.Nucleus.* categories are never
    /// persisted, and entries logged while the runtime is uninitialized are dropped.
    /// </summary>
    public sealed class NucleusLoggerProvider : ILoggerProvider
    {
        private const int FlushIntervalMilliseconds = 5000;
        private const int MaxBufferedEntries = 10_000;

        private readonly NucleusOptions _options;
        private readonly ConcurrentQueue<NucleusLogEntry> _buffer = new();
        private readonly Timer _flushTimer;
        private int _flushing;

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
            if (_buffer.Count >= MaxBufferedEntries)
                return; // shed load rather than grow unbounded
            _buffer.Enqueue(entry);
        }

        /// <summary>Persists buffered entries; failures are swallowed (logging must never throw).</summary>
        public void Flush()
        {
            if (_buffer.IsEmpty || !NucleusRuntime.IsInitialized)
                return;
            if (Interlocked.CompareExchange(ref _flushing, 1, 0) == 1)
                return;
            try
            {
                var batch = new List<ICoreObject?>();
                while (_buffer.TryDequeue(out var entry))
                    batch.Add(entry);
                if (batch.Count > 0)
                    NucleusRuntime.Current.BulkSave(batch);
            }
            catch (Exception)
            {
                // Never propagate logging failures into the application.
            }
            finally
            {
                Interlocked.Exchange(ref _flushing, 0);
            }
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
                });
            }
        }
    }
}
