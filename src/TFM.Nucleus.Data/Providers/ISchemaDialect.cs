using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Data.Schema;

namespace TFM.Nucleus.Data.Providers
{
    // Methods that do I/O take a flag saying whether to run asynchronously and return a
    // ValueTask. With async false an implementation calls only synchronous APIs, so the
    // ValueTask is already complete and the sync callers read it without blocking a thread.

    /// <summary>
    /// How one engine names, types and builds tables. <see cref="SchemaManager"/> decides what
    /// to change; the dialect writes the statements.
    /// </summary>
    public interface ISchemaDialect
    {
        /// <summary>How the engine compares identifiers it reports back from its catalog.</summary>
        StringComparer IdentifierComparer { get; }

        /// <summary>
        /// The name the engine stores for a logical table or index name. Identity unless the
        /// engine limits identifier length, in which case it must be deterministic.
        /// </summary>
        string PhysicalName(string logicalName);

        /// <summary>True when a column found in the catalog has the type and size the schema needs.</summary>
        bool ColumnMatches(ColumnSchema column, CatalogColumn existing);

        /// <summary>Creates the table with its system columns, primary key and column indexes.</summary>
        IReadOnlyList<SchemaStatement> CreateTable(TableSchema table);

        /// <summary>
        /// Renames the current table to <paramref name="tempTableName"/>, with whatever constraints
        /// and indexes must be renamed so the new table can take the old names.
        /// </summary>
        IReadOnlyList<SchemaStatement> RenameForRebuild(TableSchema table, string tempTableName);

        /// <summary>
        /// Adds columns to an existing table without touching its rows: the entity
        /// <paramref name="columns"/>, created NULL as <see cref="CreateTable"/> creates them, and the
        /// soft-delete system columns named in <paramref name="softDeleteColumns"/>. Returns null
        /// when the engine cannot add one of them in place, and the table is rebuilt instead, which
        /// is also what a dialect that does not implement this gets.
        /// </summary>
        IReadOnlyList<SchemaStatement>? AddColumns(TableSchema table, IReadOnlyList<ColumnSchema> columns, IReadOnlyList<string> softDeleteColumns) => null;

        /// <summary>Copies the named columns of every row from one table to another.</summary>
        SchemaStatement CopyRows(string sourceTableName, string destTableName, IReadOnlyList<string> columns, bool orderByClusterId);

        SchemaStatement DropTable(string tableName);

        /// <summary>Fills a missing <see cref="TableSchema.TimestampColumn"/> in rows copied from a legacy table.</summary>
        SchemaStatement BackfillTimestamp(TableSchema table);

        SchemaStatement CreateIndex(TableSchema table, ColumnSchema column);

        SchemaStatement DropIndex(TableSchema table, string physicalIndexName);

        /// <summary>True when building a unique index failed because existing rows hold duplicate keys.</summary>
        bool IsDuplicateKeyOnIndexBuild(Exception exception);

        /// <summary>Engine-specific objects beyond tables and column indexes, or null when there are none.</summary>
        ISchemaExtras? Extras { get; }
    }

    /// <summary>
    /// Engine-specific schema objects a provider keeps in step with the entity model, such as
    /// full-text indexes or change notification triggers.
    /// </summary>
    public interface ISchemaExtras
    {
        ValueTask<bool> AreCurrentAsync(IReadOnlyCollection<TableSchema> tables, bool async, CancellationToken cancellationToken);

        ValueTask SyncAsync(IReadOnlyCollection<TableSchema> tables, bool async, CancellationToken cancellationToken);
    }

    /// <summary>Reads a database's catalog, and runs schema statements outside any transaction.</summary>
    public interface ICatalogReader
    {
        ValueTask<bool> DatabaseExistsAsync(bool async, CancellationToken cancellationToken);

        ValueTask CreateDatabaseAsync(bool async, CancellationToken cancellationToken);

        /// <summary>Every table, every column and every Nucleus-named index.</summary>
        ValueTask<CatalogSnapshot> ReadAsync(bool async, CancellationToken cancellationToken);

        /// <summary>Every Nucleus-named index.</summary>
        ValueTask<IReadOnlyList<CatalogIndex>> ReadIndexesAsync(bool async, CancellationToken cancellationToken);

        /// <summary>Runs a statement on a connection of its own.</summary>
        ValueTask ExecuteAsync(SchemaStatement statement, bool async, CancellationToken cancellationToken);
    }

    /// <summary>What a provider gives its <see cref="SchemaManager"/>.</summary>
    public interface ISchemaStore
    {
        /// <summary>The root type whose entities this provider stores; null for every entity.</summary>
        Type? RootType { get; }

        /// <summary>The schema is checked but never changed.</summary>
        bool ReadOnlySchema { get; }

        ISchemaDialect Dialect { get; }

        ICatalogReader Catalog { get; }

        /// <summary>A transaction for table rebuilds, which commits on dispose unless rolled back.</summary>
        ValueTask<ISchemaTransaction> BeginRebuildAsync(bool async, CancellationToken cancellationToken);
    }

    /// <summary>The transaction a table rebuild runs in.</summary>
    public interface ISchemaTransaction : IDisposable, IAsyncDisposable
    {
        ValueTask ExecuteAsync(SchemaStatement statement, bool async, CancellationToken cancellationToken);

        /// <summary>The columns of one table as this transaction sees them.</summary>
        ValueTask<IReadOnlyList<CatalogColumn>> ReadColumnsAsync(string tableName, bool async, CancellationToken cancellationToken);

        void Rollback();
    }
}
