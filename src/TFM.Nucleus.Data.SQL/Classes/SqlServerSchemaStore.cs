using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
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
            _catalog = new SqlServerCatalogReader(() => provider.SqlConfig);
            Dialect = new SqlServerSchemaDialect(new SqlServerFullTextExtras(_catalog, logger));
        }

        public Type? RootType => _provider.RootType;

        public bool ReadOnlySchema => _provider.NucleusOptions.ReadOnlySchema;

        public ISchemaDialect Dialect { get; }

        public ICatalogReader Catalog => _catalog;

        public async ValueTask<ISchemaTransaction> BeginRebuildAsync(bool async, CancellationToken cancellationToken) =>
            new Transaction(async
                ? await _provider.CreateSqlDataContextAsync(null, true, false, cancellationToken, true)
                : _provider.CreateSqlDataContext(null, true, false, true));

        /// <summary>A rebuild runs on one transactional context, which commits when disposed.</summary>
        private sealed class Transaction(SqlDataContext context) : ISchemaTransaction
        {
            public async ValueTask ExecuteAsync(SchemaStatement statement, bool async, CancellationToken cancellationToken)
            {
                if (async)
                    await context.ExecuteSQLAsync(statement.Text, statement.Parameters, cancellationToken);
                else
                    context.ExecuteSQL(statement.Text, statement.Parameters);
            }

            public async ValueTask<IReadOnlyList<CatalogColumn>> ReadColumnsAsync(string tableName, bool async, CancellationToken cancellationToken)
            {
                try
                {
                    var query = SqlServerCatalogReader.ColumnsQuery(tableName);
                    // The context has one command; the sync path waits on it, as it always has.
                    using var reader = (async
                        ? await context.GetObjectDataAsync(query, cancellationToken)
                        : context.GetObjectDataAsync(query).GetAwaiter().GetResult())
                        ?? throw new Exception("Sql Datareader was null");
                    List<CatalogColumn> columns = [];
                    while (async ? await reader.ReadAsync(cancellationToken) : reader.Read())
                        columns.Add(SqlServerCatalogReader.ReadColumn(reader));
                    return columns;
                }
                catch (Exception ex)
                {
                    throw new Exception($"Error retrieving columns from database. Error: {ex.Message}", ex);
                }
            }

            public void Rollback() => context.Rollback();

            public void Dispose() => context.Dispose();

            public ValueTask DisposeAsync() => context.DisposeAsync();
        }
    }
}
