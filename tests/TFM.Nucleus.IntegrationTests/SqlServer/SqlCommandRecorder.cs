using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace TFM.Nucleus.IntegrationTests.SqlServer
{
    /// <summary>
    /// Records the text of every command SqlClient executes while it is alive, from SqlClient's
    /// own diagnostic events, so a test can see the statements a provider actually sent.
    /// Process wide: use it only in the SQL Server collection, whose tests run one at a time.
    /// </summary>
    public sealed class SqlCommandRecorder : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private const string ListenerName = "SqlClientDiagnosticListener";
        private const string CommandBefore = "Microsoft.Data.SqlClient.WriteCommandBefore";

        private readonly ConcurrentQueue<string> _commands = new();
        private readonly List<IDisposable> _subscriptions = [];
        private readonly IDisposable _allListeners;

        public SqlCommandRecorder()
        {
            _allListeners = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public IReadOnlyCollection<string> Commands => _commands;

        void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
        {
            if (listener.Name == ListenerName)
                lock (_subscriptions)
                    _subscriptions.Add(listener.Subscribe(this));
        }

        void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key == CommandBefore && value.Value?.GetType().GetProperty("Command")?.GetValue(value.Value) is SqlCommand command)
                _commands.Enqueue(command.CommandText);
        }

        void IObserver<DiagnosticListener>.OnCompleted() { }
        void IObserver<DiagnosticListener>.OnError(Exception error) { }
        void IObserver<KeyValuePair<string, object?>>.OnCompleted() { }
        void IObserver<KeyValuePair<string, object?>>.OnError(Exception error) { }

        public void Dispose()
        {
            _allListeners.Dispose();
            lock (_subscriptions)
                _subscriptions.ForEach(s => s.Dispose());
        }
    }
}
