using Microsoft.Extensions.Logging;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// The audit entry a commit writes. <c>[SuppressLogging]</c> on a type omits it even when a
    /// logger is passed, which every manager save does; it used to be audited anyway.
    /// </summary>
    public class AuditLoggingTests
    {
        private sealed class RecordingLogger(LogLevel minimum = LogLevel.Trace) : ILogger
        {
            public List<string> Entries { get; } = [];
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                    Entries.Add(formatter(state, exception));
            }
        }

        [Fact]
        public void SuppressedType_WritesNoEntry_EvenWithALogger()
        {
            var logger = new RecordingLogger();
            var secret = new AuditSuppressedWidget { Name = "hidden" };

            ((ICoreObject)secret).CommitChanges(logger);
            secret.Name = "still hidden";
            ((ICoreObject)secret).CommitChanges(logger);

            Assert.Empty(logger.Entries);
        }

        [Fact]
        public void AuditedType_WritesItsEntry()
        {
            var logger = new RecordingLogger();
            var widget = new TestWidget { Name = "a" };

            ((ICoreObject)widget).CommitChanges(logger);
            widget.Name = "b";
            ((ICoreObject)widget).CommitChanges(logger);

            Assert.Equal(2, logger.Entries.Count);
            Assert.StartsWith("Created", logger.Entries[0]);
            Assert.StartsWith("Updated", logger.Entries[1]);
        }

        [Fact]
        public void LoggerThatDropsInformation_IsNotWrittenTo()
        {
            var logger = new RecordingLogger(LogLevel.Warning);

            ((ICoreObject)new TestWidget { Name = "a" }).CommitChanges(logger);

            Assert.Empty(logger.Entries);
        }
    }

    [SuppressLogging]
    public class AuditSuppressedWidget : CoreObject<AuditSuppressedWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }
}
