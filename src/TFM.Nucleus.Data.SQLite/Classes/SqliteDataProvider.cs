using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.Schema;

namespace TFM.Nucleus.Data.SQLite
{
    /// <summary>
    /// The SQLite provider: the shared relational provider with SQLite's renderer, schema dialect
    /// and contexts, over one database file. It has no change notifications and no bulk engine.
    /// </summary>
    internal sealed class SqliteDataProvider(
        IObjectSchemaProvider objectSchemaProvider,
        IOptionsMonitor<NucleusOptions> nucleusOptions,
        IOptionsMonitor<SqliteConfiguration> config,
        ILogger<SqliteDataProvider>? logger, ILogger<SchemaManager>? schemaLogger)
        : RelationalDataProvider(objectSchemaProvider, nucleusOptions, logger, schemaLogger)
    {
        private SqliteSchemaStore? _schemaStore;

        internal SqliteConfiguration Config => config.CurrentValue;

        public override RelationalProviderOptions Options => Config;

        public override IQueryRenderer Renderer => SqliteQueryRenderer.Instance;

        protected override string EngineName => "SQLite";

        protected override ISchemaStore SchemaStore => _schemaStore ??= new SqliteSchemaStore(this);

        public override DataProviderCapabilities Capabilities => new()
        {
            ParameterPrefix = "@",
            MaxParametersPerCommand = Config.MaxParametersPerCommand,
            SupportsBulkOperations = false,
            SupportsChangeNotifications = false,
            SupportsOffsetPaging = true,
        };

        internal SqliteDataContext CreateSqliteDataContext(SqliteDataContext? parent, bool useTransaction, bool useDependency, bool bypassChecks = false, bool forSchema = false)
        {
            if (!bypassChecks)
                EnsureReady();
            return SqliteDataContext.Create(this, parent, useTransaction, useDependency, forSchema);
        }

        internal async Task<SqliteDataContext> CreateSqliteDataContextAsync(SqliteDataContext? parent, bool useTransaction, bool useDependency, CancellationToken token, bool bypassChecks = false, bool forSchema = false)
        {
            if (!bypassChecks)
                await EnsureReadyAsync();
            return await SqliteDataContext.CreateAsync(this, parent, useTransaction, useDependency, token, forSchema);
        }

        protected override IDataContext CreateDataContext(IDataContext? parent, bool useTransaction, bool useDependency) =>
            CreateSqliteDataContext(parent as SqliteDataContext, useTransaction, useDependency);

        protected override async Task<IDataContext> CreateDataContextAsync(IDataContext? parent, bool useTransaction, bool useDependency, CancellationToken token) =>
            await CreateSqliteDataContextAsync(parent as SqliteDataContext, useTransaction, useDependency, token);

        /// <summary>SQLite tells no other connection about a write, so there is nothing to subscribe to.</summary>
        public override Task<IChangeSubscription> SubscribeToChangesAsync<T>(CancellationToken token = default) =>
            throw new NotSupportedException("The SQLite provider has no change notifications: SQLite does not tell other connections about a write.");

        protected override Exception ProviderException(DataProviderErrorKind kind, string? message = null) =>
            message == null ? new SqliteDataProviderException(kind) : new SqliteDataProviderException(message, kind);

        protected override DatabaseErrorClass ClassifyFailure(Exception exception) => SqliteErrors.Classify(exception);

        /// <summary>
        /// SQLite names the columns a unique violation broke, not the index, so the index is
        /// found from them: the column's own unique index for one column, or the unique
        /// [TableIndex] with exactly those keys in that order. A primary key violation is the
        /// table's PK_ name, which Nucleus does not manage, so it resolves to no rule.
        /// </summary>
        protected override bool TryGetUniqueViolation(Exception exception, out string? physicalIndexName)
        {
            physicalIndexName = null;
            if (!SqliteErrors.TryGetUniqueViolation(exception, out var target))
                return false;
            if (target.IndexName != null)
            {
                physicalIndexName = target.IndexName;
                return true;
            }
            var comparer = SqliteIdentifiers.Comparer;
            var table = Schema.TableCache.Values.FirstOrDefault(t => comparer.Equals(t.TableName, target.TableName));
            if (table == null)
                return true;
            if (target.IsPrimaryKey)
            {
                physicalIndexName = $"PK_{table.TableName}";
                return true;
            }
            if (target.Columns.Count == 1 &&
                table.IndexedColumns.FirstOrDefault(c => c.Unique && comparer.Equals(c.ColumnName, target.Columns[0])) is { } column)
            {
                physicalIndexName = IndexNaming.ColumnIndexName(table, column);
                return true;
            }
            if (table.TableIndexes.FirstOrDefault(i => i.Unique && SqliteErrors.SameColumns(target.Columns, i.Keys.Select(k => k.ColumnName))) is { } index)
                physicalIndexName = IndexNaming.TableIndexName(table, index);
            return true;
        }
    }

    /// <summary>What the SQLite provider gives its <see cref="SchemaManager"/>.</summary>
    internal sealed class SqliteSchemaStore : ISchemaStore
    {
        private readonly SqliteDataProvider _provider;

        public SqliteSchemaStore(SqliteDataProvider provider)
        {
            _provider = provider;
            Catalog = new SqliteCatalogReader(() => provider.Config);
        }

        public Type? RootType => _provider.RootType;

        public bool ReadOnlySchema => _provider.NucleusOptions.ReadOnlySchema;

        public TableNamingPolicy TableNaming => _provider.Options.TableNaming;

        public bool RenameFullNameTables => _provider.Options.RenameFullNameTables;

        public bool ForeignKeys => _provider.Options.ForeignKeys;

        public ISchemaDialect Dialect { get; } = new SqliteSchemaDialect();

        public ICatalogReader Catalog { get; }

        /// <summary>A rebuild runs on one transactional context; SQLite's DDL is transactional, so it commits or rolls back whole.</summary>
        public async ValueTask<ISchemaTransaction> BeginRebuildAsync(bool async, CancellationToken cancellationToken) =>
            new DbSchemaTransaction<SqliteDataContext>(
                async
                    ? await _provider.CreateSqliteDataContextAsync(null, true, false, cancellationToken, bypassChecks: true, forSchema: true)
                    : _provider.CreateSqliteDataContext(null, true, false, bypassChecks: true, forSchema: true),
                table => new SchemaStatement(SqliteCatalogReader.ColumnsQuery(table)),
                SqliteCatalogReader.ReadColumn);
    }

    /// <summary>The SQLite provider could not be configured, initialised or reached.</summary>
    public class SqliteDataProviderException : DataProviderException
    {
        internal SqliteDataProviderException(DataProviderErrorKind kind) : base(kind)
        {
        }

        internal SqliteDataProviderException(string message, DataProviderErrorKind kind) : base(message, kind)
        {
        }

        internal SqliteDataProviderException(string message, DataProviderErrorKind kind, Exception innerException) : base(message, kind, innerException)
        {
        }
    }
}
