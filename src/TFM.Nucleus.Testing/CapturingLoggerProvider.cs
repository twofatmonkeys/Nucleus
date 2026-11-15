using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace TFM.Nucleus.Testing;

/// <summary>
/// Keeps the warnings and errors a stack logs, so a stack whose initialisation fails can say why
/// instead of reporting only the status, and a test can assert on what was logged.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    /// <summary>Every warning and error so far, as <c>[Level] Category: message</c>, oldest first.</summary>
    public IEnumerable<string> Entries => _entries;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var detail = exception == null ? "" : $" {exception.GetType().Name}: {exception.Message}{(exception.InnerException is { } inner ? $" -> {inner.GetType().Name}: {inner.Message}" : "")}";
            owner._entries.Enqueue($"[{logLevel}] {category}: {formatter(state, exception)}{detail}");
        }
    }
}
