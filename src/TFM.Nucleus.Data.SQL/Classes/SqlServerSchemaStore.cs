using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.Schema;

namespace TFM.Nucleus.Data.SQL
{
    /// <summary>What the SQL Server provider gives its <see cref="SchemaManager"/>.</summary>
    internal sealed class SqlServerSchemaStore : ISchemaStore
    {
        private readonly SQLDataProvider _provider;
        private readonly SqlServerCatalogReader _catalog;

        public SqlServerSchemaStore(SQLDataProvider provider, ILogger? logger)
        {
            _provider = provider;
            _catalog = new SqlServerCatalogReader(() => provider.SqlConfig, logger);
            Dialect = new SqlServerSchemaDialect(new SqlServerExtras(
                new SqlServerFullTextExtras(_catalog, logger), new SqlServerListTypeExtras(_catalog, logger)));
        }

        public Type? RootType => _provider.RootType;

        public bool ReadOnlySchema => _provider.NucleusOptions.ReadOnlySchema;

        public TableNamingPolicy TableNaming => _provider.Options.TableNaming;

        public bool RenameFullNameTables => _provider.Options.RenameFullNameTables;

        public bool ForeignKeys => _provider.Options.ForeignKeys;

        public ISchemaDialect Dialect { get; }

        public ICatalogReader Catalog => _catalog;

        /// <summary>A rebuild runs on one transactional context, which commits when disposed.</summary>
        public async ValueTask<ISchemaTransaction> BeginRebuildAsync(bool async, CancellationToken cancellationToken) =>
            new DbSchemaTransaction<SqlDataContext>(
                async
                    ? await _provider.CreateSqlDataContextAsync(null, true, false, cancellationToken, true)
                    : _provider.CreateSqlDataContext(null, true, false, true),
                SqlServerCatalogReader.ColumnsQuery,
                SqlServerCatalogReader.ReadColumn);
    }
}
