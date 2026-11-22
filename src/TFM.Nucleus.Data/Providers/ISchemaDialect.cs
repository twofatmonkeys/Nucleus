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
        /// Renames the existing table <paramref name="legacyTableName"/> (a logical name) to
        /// <paramref name="table"/>'s name, and gives the primary key, constraints and
        /// Nucleus-named indexes it carries the names they would have had if it had been created
        /// under the new name, so nothing is rebuilt and the old names are free again. Runs in the
        /// schema transaction. Objects the engine cannot rename there are left for the index and
        /// extras sync to replace. A dialect that does not implement this refuses to adopt a table.
        /// </summary>
        IReadOnlyList<SchemaStatement> AdoptTable(string legacyTableName, TableSchema table) =>
            throw new NotSupportedException($"{GetType().Name} cannot rename an existing table to adopt it.");

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

        /// <summary>
        /// Creates a [TableIndex] index: its keys in order, ascending, carrying its included
        /// columns. A dialect that does not implement this refuses entities that declare one.
        /// </summary>
        SchemaStatement CreateTableIndex(TableSchema table, TableIndexSchema index) =>
            throw new NotSupportedException($"{GetType().Name} does not support [TableIndex] indexes.");

        /// <summary>
        /// False for an engine whose indexes cannot carry non-key columns. Its [TableIndex] indexes
        /// are created with their keys only, and the schema check compares their keys only, so the
        /// included columns neither appear nor make an index look stale.
        /// </summary>
        bool SupportsIncludedColumns => true;

        /// <summary>
        /// True for an engine whose dialect writes foreign keys. A dialect that does not implement
        /// the members below leaves this false, and its provider creates no keys.
        /// </summary>
        bool SupportsForeignKeys => false;

        /// <summary>
        /// Adds <paramref name="key"/> to its table. With <paramref name="checkExisting"/> false the
        /// rows already there are not checked against it, because some point at rows that do not
        /// exist; the key still holds for every row written after it.
        /// </summary>
        SchemaStatement AddForeignKey(ForeignKeySchema key, bool checkExisting) =>
            throw new NotSupportedException($"{GetType().Name} does not support foreign keys.");

        /// <summary>Drops the key named <paramref name="physicalKeyName"/> from the table named <paramref name="physicalTableName"/>.</summary>
        SchemaStatement DropForeignKey(string physicalTableName, string physicalKeyName) =>
            throw new NotSupportedException($"{GetType().Name} does not support foreign keys.");

        /// <summary>
        /// A statement whose single value is the number of rows of the key's table whose column
        /// holds an ID its referenced table does not have.
        /// </summary>
        SchemaStatement CountOrphans(ForeignKeySchema key) =>
            throw new NotSupportedException($"{GetType().Name} does not support foreign keys.");

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

        /// <summary>
        /// Every Nucleus-named foreign key. Empty from a reader that predates foreign keys, whose
        /// dialect creates none.
        /// </summary>
        ValueTask<IReadOnlyList<CatalogForeignKey>> ReadForeignKeysAsync(bool async, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<CatalogForeignKey>>([]);

        /// <summary>Runs a statement that returns one number, on a connection of its own.</summary>
        ValueTask<long> ReadCountAsync(SchemaStatement statement, bool async, CancellationToken cancellationToken) =>
            throw new NotSupportedException($"{GetType().Name} does not read counts.");
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

        /// <summary>How the provider names the tables of entities that do not name their own.</summary>
        TableNamingPolicy TableNaming => TableNamingPolicy.FullName;

        /// <summary>A missing table is renamed from its full-name form when a table under that name exists.</summary>
        bool RenameFullNameTables => false;

        /// <summary>
        /// The provider keeps the foreign keys the entity model implies (where its dialect
        /// supports them). False for a store that predates foreign keys.
        /// </summary>
        bool ForeignKeys => false;

        /// <summary>A transaction for table rebuilds, committed by <see cref="ISchemaTransaction.CommitAsync"/>.</summary>
        ValueTask<ISchemaTransaction> BeginRebuildAsync(bool async, CancellationToken cancellationToken);
    }

    /// <summary>The transaction a table rebuild runs in.</summary>
    public interface ISchemaTransaction : IDisposable, IAsyncDisposable
    {
        ValueTask ExecuteAsync(SchemaStatement statement, bool async, CancellationToken cancellationToken);

        /// <summary>The columns of one table as this transaction sees them.</summary>
        ValueTask<IReadOnlyList<CatalogColumn>> ReadColumnsAsync(string tableName, bool async, CancellationToken cancellationToken);

        /// <summary>Commits the rebuilds. Disposing without a commit commits only while the provider does not require one.</summary>
        /// <remarks>An implementation that predates this member does not support it.</remarks>
        ValueTask CommitAsync(bool async, CancellationToken cancellationToken) =>
            throw new NotSupportedException($"{GetType().Name} does not support an explicit commit.");

        void Rollback();
    }
}
