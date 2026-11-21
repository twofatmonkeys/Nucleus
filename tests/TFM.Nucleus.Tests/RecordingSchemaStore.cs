using Microsoft.Extensions.Logging;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// A SQL Server schema store over a fixed catalog that records every statement it is asked to
    /// run, inside the schema transaction and outside it, and every message the schema manager
    /// logs. It stores the entity types it is given, named by its <see cref="TableNaming"/>.
    /// </summary>
    internal sealed class RecordingSchemaStore : ISchemaStore, ICatalogReader, IObjectSchemaProvider, ILogger
    {
        private readonly Type[] _types;

        /// <param name="catalog">What the database holds.</param>
        /// <param name="types">The entity types the store stores; <see cref="TestGrowingWidget"/> when none are given.</param>
        public RecordingSchemaStore(CatalogSnapshot catalog, params Type[] types)
        {
            Snapshot = catalog;
            _types = types.Length == 0 ? [typeof(TestGrowingWidget)] : types;
        }

        public List<string> InTransaction { get; } = [];
        public List<string> OutsideTransaction { get; } = [];
        public List<(LogLevel Level, string Message)> Logged { get; } = [];
        public CatalogSnapshot Snapshot { get; }
        public bool RolledBack { get; private set; }
        public bool Committed { get; private set; }

        /// <summary>
        /// The columns the schema transaction reads back for a table. By default a table the store
        /// stores has every column its schema needs (a table just created), and any other table,
        /// such as a rebuild's renamed one, has the catalog's columns.
        /// </summary>
        public Func<string, IReadOnlyList<CatalogColumn>>? ColumnsInTransaction { get; init; }

        public SchemaManager Manager() => new(this, this, this);

        public Type? RootType => null;
        public bool ReadOnlySchema => false;
        public TableNamingPolicy TableNaming { get; init; } = TableNamingPolicy.FullName;
        public bool RenameFullNameTables { get; init; }
        public ISchemaDialect Dialect { get; } = new SqlServerSchemaDialect(null);
        public ICatalogReader Catalog => this;

        public ValueTask<ISchemaTransaction> BeginRebuildAsync(bool async, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ISchemaTransaction>(new Transaction(this));

        public ValueTask<bool> DatabaseExistsAsync(bool async, CancellationToken cancellationToken) => ValueTask.FromResult(true);
        public ValueTask CreateDatabaseAsync(bool async, CancellationToken cancellationToken) => throw new InvalidOperationException("The database exists.");
        public ValueTask<CatalogSnapshot> ReadAsync(bool async, CancellationToken cancellationToken) => ValueTask.FromResult(Snapshot);
        public ValueTask<IReadOnlyList<CatalogIndex>> ReadIndexesAsync(bool async, CancellationToken cancellationToken) => ValueTask.FromResult(Snapshot.Indexes);

        public ValueTask ExecuteAsync(SchemaStatement statement, bool async, CancellationToken cancellationToken)
        {
            OutsideTransaction.Add(statement.Text);
            return ValueTask.CompletedTask;
        }

        public IObjectSchema? Get<T>() => Get(typeof(T));
        public IObjectSchema? Get(Type type) => _types.Contains(type) ? ObjectSchema.Get(type) : null;
        public bool TryGet<T>(out IObjectSchema? schema) => TryGet(typeof(T), out schema);
        public bool TryGet(Type type, out IObjectSchema? schema) => (schema = Get(type)) != null;
        public IEnumerable<IObjectSchema>? GetAll() => [.. _types.Select(t => ObjectSchema.Get(t))];

        private IReadOnlyList<CatalogColumn> DefaultColumns(string tableName)
        {
            foreach (var type in _types)
            {
                var table = new TableSchema(ObjectSchema.Get(type), TableNaming);
                if (Dialect.IdentifierComparer.Equals(tableName, table.TableName))
                    return SchemaCatalog.Current("sqlserver", table).Columns;
            }
            return Snapshot.Columns;
        }

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        bool ILogger.IsEnabled(LogLevel logLevel) => true;
        void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Logged.Add((logLevel, exception == null ? formatter(state, null) : $"{formatter(state, exception)} {exception.Message}"));

        private sealed class Transaction(RecordingSchemaStore store) : ISchemaTransaction
        {
            public ValueTask ExecuteAsync(SchemaStatement statement, bool async, CancellationToken cancellationToken)
            {
                store.InTransaction.Add(statement.Text);
                return ValueTask.CompletedTask;
            }

            public ValueTask<IReadOnlyList<CatalogColumn>> ReadColumnsAsync(string tableName, bool async, CancellationToken cancellationToken) =>
                ValueTask.FromResult((store.ColumnsInTransaction ?? store.DefaultColumns)(tableName));

            public ValueTask CommitAsync(bool async, CancellationToken cancellationToken)
            {
                store.Committed = true;
                return ValueTask.CompletedTask;
            }

            public void Rollback() => store.RolledBack = true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
