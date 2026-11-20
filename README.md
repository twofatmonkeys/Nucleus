# Nucleus

Nucleus is a custom entity framework (ORM) for .NET by Two Fat Monkeys. Domain objects derive from a common `CoreObject` base; the framework discovers their schema from reflection and attributes, tracks property changes, translates LINQ expressions into parameterised queries, materialises objects from result sets, and creates and migrates the physical database schema automatically.

This repository holds the whole family. Seven packages ship from it, all on one version.

## Packages

| Package | Role |
|---|---|
| `TFM.Nucleus.Abstractions` | Contracts: `ICoreObject`, `IDataProvider` and `IDataContext`, the neutral query model (`IQuery`, `IQueryParameter`, `Operator`), schema interfaces, the `DataSet` row buffer, `NucleusCollection` and `NucleusChildCollection`, `PagedResult<T>`, `IChangeSubscription`, `INucleusRuntime`, `NucleusOptions`. Zero SQL. |
| `TFM.Nucleus` | The core: `CoreObject` entity base classes, attribute-driven schema reflection (`ObjectSchema` and `PropertySchema` with IL-emitted materialisers), change tracking, the static convenience API, `NucleusLoggerProvider`, and DI registration. |
| `TFM.Nucleus.Data` | Provider-neutral data layer: `ObjectDataManager` (save, load and delete orchestration, cascade, child-graph hydration), `ObjectDataAdaptor<T>` (a LINQ provider with async operators), `ExpressionParser`, `DataProviderRegistry` for multi-provider routing, `NucleusCache`. It also holds the query model and the schema model every provider shares, and the provider SDK a provider package builds on (see [Provider seam](#provider-seam)). Contains no SQL, proven by a tripwire test. |
| `TFM.Nucleus.Data.SQL` | SQL Server provider: T-SQL rendering, the SQL Server schema dialect and catalog, the SqlBulkCopy engine and SqlDependency change notifications. Its public surface is its registration (`AddNucleusSQLDataProvider`) and `SQLConfiguration`. |
| `TFM.Nucleus.Data.PostgreSQL` | PostgreSQL provider (PostgreSQL 16 or later, over Npgsql): query rendering, the PostgreSQL schema dialect and catalog, the COPY bulk engine and LISTEN/NOTIFY change notifications. Its public surface is its registration (`AddNucleusPostgresDataProvider`) and `PostgresConfiguration`. See [PostgreSQL](#postgresql). |
| `TFM.Nucleus.Data.SQLite` | SQLite provider for file databases in embedded and local applications (SQLite 3.35 or later, bundled, over Microsoft.Data.Sqlite): query rendering, the SQLite schema dialect and catalog, and write-ahead logging. No change notifications or full-text indexes. Its public surface is its registration (`AddNucleusSqliteDataProvider`) and `SqliteConfiguration`. See [SQLite](#sqlite). |
| `TFM.Nucleus.Testing` | Live-database test support for anything built on Nucleus, framework neutral: the SQL Server, PostgreSQL and SQLite test database settings, a booted stack per provider, cross-process locks and the raw statements a test sets a table up with. See [Testing against a live database](#testing-against-a-live-database). |

Dependency graph: `Abstractions` <- (`Nucleus`, `Data`) <- (`Data.SQL`, `Data.PostgreSQL`, `Data.SQLite`). The core and neutral layers never reference a concrete provider, and no provider references another, so an application picks its database by picking a provider package, and a store written against the neutral layers works over any of them. `Testing` sits above the providers, because a test picks its database too. See [Stores for other libraries](#stores-for-other-libraries).

## Install

The packages are published to GitHub Packages. Add the feed once, then reference the provider package for your database: it pulls the other three transitively.

```bash
dotnet add package TFM.Nucleus.Data.SQL          # SQL Server
dotnet add package TFM.Nucleus.Data.PostgreSQL   # PostgreSQL 16 or later
dotnet add package TFM.Nucleus.Data.SQLite       # a SQLite database file
```

The feed is `https://nuget.pkg.github.com/twofatmonkeys/index.json` and requires authentication even for reads. Set `GITHUB_USER` and `GITHUB_TOKEN` (a classic PAT with `read:packages`) in your environment. See [docs/publishing.md](docs/publishing.md).

## Target frameworks

`net8.0` and `net10.0`.

## Quick start

```csharp
var services = new ServiceCollection();
services.AddLogging();
services.AddNucleus();                       // options and the schema provider
services.AddNucleusData();                   // provider registry and object data manager
services.AddNucleusSQLDataProvider(options =>
{
    options.Server = "localhost";            // host, host:port or host\instance
    options.Database = "MyApp";
    options.Username = "app";                // or UseIntegratedSecurity, or ConnectionString
    options.Password = secret;
});

var provider = services.BuildServiceProvider();
await provider.GetRequiredService<IObjectDataManager>().InitializeDataProviderAsync();
```

On PostgreSQL the only change is the registration:

```csharp
services.AddNucleusPostgresDataProvider(options =>
{
    options.Server = "localhost";            // host or host:port
    options.Database = "myapp";
    options.Username = "app";                // or ConnectionString
    options.Password = secret;
});
```

On SQLite the database is a file:

```csharp
services.AddNucleusSqliteDataProvider(options =>
{
    options.Database = @"C:\ProgramData\MyApp\myapp.db";   // created with its folder when missing
});
```

`InitializeDataProviderAsync` connects, then creates or migrates the database, tables, indexes and full-text indexes (and, on PostgreSQL, the change notification triggers) to match your entity classes. Set `NucleusOptions.ReadOnlySchema` to `true` to make it verify without writing.

How a table is migrated depends on what changed. A table that only lacks columns (a new property, or the soft-delete columns of a type newly made soft-deletable) has them added in place with `ALTER TABLE`. Every column is created NULL, so the change is metadata only and takes no time at any table size, and existing rows read the new column as null. A column whose type or size changed means a rebuild: the table is renamed, created again and its rows copied across in one transaction, which takes time in proportion to the table. A column the entity no longer has is left in the table either way. Each schema statement runs under `SchemaCommandTimeoutSeconds` (15 minutes by default).

Define entities. Properties are backed by the framework's change-tracking dictionaries through `GetValue`/`SetValue` rather than by fields:

```csharp
[Cacheable(refreshInterval: 120, cacheLifetime: 900)]
public class Customer : CoreObject<Customer>
{
    [DatabaseColumn(Indexed = true)]
    public string? Name { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn]
    public decimal CreditLimit { get => GetValue<decimal>(); set => SetValue(value); }

    public NucleusChildCollection<Order, Customer> Orders => GetChildCollection<Order, Customer>();

    [RetrieveOnDemand]   // lazy: left out of loads, read on first use
    public NucleusChildCollection<CustomerNote, Customer> Notes => GetChildCollection<CustomerNote, Customer>();
}

[Deletable(DeleteBehavior.Soft)]
public class Order : CoreChildObject<Order, Customer>
{
    [DatabaseColumn]
    public decimal Total { get => GetValue<decimal>(); set => SetValue(value); }
}

public class CustomerNote : CoreChildObject<CustomerNote, Customer>
{
    [DatabaseColumn]
    public string? Text { get => GetValue<string>(); set => SetValue(value); }
}
```

Two usage surfaces are available and both run through the same `ObjectDataManager` code path:

```csharp
// Static convenience API, routed through the ambient runtime that AddNucleusData publishes
var customer = new Customer { Name = "Acme" };
customer.Save();
var found   = CoreObject.LoadByID<Customer>(customer.ID);   // O(1) from cache for [Cacheable] types
var matches = CoreObject.LoadWhere<Customer>(c => c.Name!.StartsWith("Ac"));

// Instance and LINQ API
var manager = provider.GetRequiredService<IObjectDataManager>();
var adaptor = manager.Get<Customer>();
var list    = adaptor.Where(c => c.Name == "Acme").ToList();
var page    = await adaptor.ToPagedResultAsync(skip: 20, take: 10);   // rows plus total, one round trip
await order.DeleteAsync();                                            // [Deletable] types only

// Leave a child collection unloaded, and load it later only if it is needed
var light = await adaptor.Where(c => c.Name == "Acme").Exclude(c => c.Orders).ToListAsync();
await light[0].Orders.LoadAsync();

// A lazy ([RetrieveOnDemand]) collection is left out unless included; Include loads it for every row at once
var listing = await adaptor.Include(c => c.Notes).Where(c => c.CreditLimit > 0).ToListAsync();

// Delete by predicate: no row is loaded, child rows go too, the count comes back
var removed = await CoreObject.DeleteWhereAsync<Customer>(c => c.Name == "Old");
```

### The manager interface

DI registers `IObjectDataManager` and nothing else, and it is what an application service or a
store takes. Every call routes the entity type to the provider of its nearest registered root,
runs in a transaction of its own, evicts the `[Cacheable]` cache when it commits and marks the
objects saved. Do not cast it to `ObjectDataManager`.

| Member | Does |
|---|---|
| `Get<T>()` | The LINQ adaptor: `Where`, `Exclude`, `Include`, `FindAsync(id, token)`, `ToListAsync`, `ToPagedResultAsync(skip, take, token)` and the other async operators. |
| `Rows<T>(predicate)` | Rows of values rather than entities. See [Row reads](#row-reads). |
| `SaveAsync(obj, token)` | Saves the object with its child collections and forceSave links. |
| `SaveAsync(objs, token)` | Saves a batch of one type in one transaction. |
| `DeleteAsync(obj, token)`, `DeleteAsync(objs, token)` | Deletes, cascading to child rows. The type must be `[Deletable]`. |
| `DeleteWhereAsync<T>(predicate, token)` | Deletes by predicate without loading. |
| `LoadChildCollectionAsync(collection, token)` | Loads an excluded or lazy collection. |
| `SubscribeToChangesAsync<T>(token)` | Change notifications from the provider that owns `T`. |
| `CreateContextAsync<T>(options, token)` | A caller-owned context on the provider that owns `T`, for one transaction across several types. See the known limits: it commits on dispose unless rolled back, and the manager's eviction does not apply. |
| `InitializeDataProviderAsync()` | Creates or migrates every registered provider's schema. |

```csharp
public sealed class RosterStore(IObjectDataManager data)
{
    public Task SaveAllAsync(IReadOnlyList<Roster> rosters, CancellationToken ct) =>
        data.SaveAsync(rosters, ct);                                     // one transaction

    public Task<Roster?> FindAsync(Guid id, CancellationToken ct) =>
        data.Get<Roster>().FindAsync(id, ct);

    // Two types in one transaction: roll back on failure, or disposing commits what ran.
    public async Task MoveAsync(Roster from, Shift shift, CancellationToken ct)
    {
        await using var context = await data.CreateContextAsync<Roster>(ContextOptions.Transactional, ct);
        try
        {
            await from.SaveAsync(context, ct);                           // validates, then writes
            await shift.SaveAsync(context, ct);
        }
        catch
        {
            await context.TryRollbackAsync(ct);
            throw;
        }
    }
}
```

`DataProvider` is obsolete: it is the unrooted default provider, so a context opened or a
subscription made through it goes to the wrong database for a type under a rooted provider. The
synchronous writes, `BulkSave` and the `Load` overloads that take an `Expression` stay on the
concrete class for the static `CoreObject` API.

## PostgreSQL

`TFM.Nucleus.Data.PostgreSQL` runs the same entities and queries on PostgreSQL 16
or later. Everything in this document applies on both providers, except where a section says
otherwise. These are the differences an application can see:

- **Equality is case-sensitive.** `x.Code == "abc"` and unique indexes compare exactly, as
  PostgreSQL always does; on SQL Server's default collation they ignore case. The pattern
  predicates `StartsWith`, `EndsWith`, `Contains` and `Like` use `ILIKE`, so they ignore case on
  both. `string.Equals(x, y, StringComparison.OrdinalIgnoreCase)` ignores case on both.
- **`DateTimeOffset` is stored as UTC, to the microsecond.** A `timestamptz` keeps the instant but
  not the offset, and no ticks below a microsecond, so a value saved at +10:00 reads back as the
  same instant at +00:00. `DateTimeOffset` equality compares instants, so comparisons hold.
- **Full-text search uses each engine's own rules.** `string.Contains` on a `FullTextSearch`
  column is a full-text search on both providers: `CONTAINS()` on SQL Server, and
  `to_tsvector('english', column) @@ plainto_tsquery(...)` over a GIN index on PostgreSQL, so which
  words match can differ. On any other column it is a substring match on both.
- **Long names are shortened.** PostgreSQL keeps 63 bytes of an identifier. A table, index or
  trigger name longer than that keeps its first 30 and last 23 bytes around eight hex characters
  of its SHA-256, the same way every time, so the schema check finds what it created.
- **Change notifications need no server setup.** Initialisation creates a statement-level trigger
  per table that notifies the table's channel, and `SubscribeToChangesAsync<T>()` listens on it
  with a connection of its own, reconnecting after a dropped connection (and reporting one change,
  since a notification sent while it was down is lost). `ContextOptions.UseDependency` is a SQL
  Server option and has no effect here.
- **Bulk writes use COPY.** An oversized insert or update batch is copied into a temp table with
  COPY BINARY and written with one statement; an update applies each row's own changed columns.
- **Values the server sets use `clock_timestamp()`**, so each statement stamps its own time rather
  than the transaction's start.
- **Paging is `LIMIT`/`OFFSET`**, ordered by ID when the query asks for no order.

The schema is `public` unless `PostgresConfiguration.Schema` says otherwise, and is created when
missing. Each table carries the Guid `ID` primary key, an identity `ClusterID` that keeps a
rebuild's copy in insertion order, and a column per `[DatabaseColumn]` property. A table that only
lacks columns has them added in place, and one whose column types change is rebuilt, as on SQL
Server, in one transaction: PostgreSQL's DDL is transactional, so a failed update leaves nothing
behind. Adding a column, `IsDeleted` with its constant default included, is a catalog-only change.

## SQLite

`TFM.Nucleus.Data.SQLite` runs the same entities and queries on a SQLite database file, for
desktop, edge and other single-machine applications. It uses the SQLite bundled with
Microsoft.Data.Sqlite and refuses to start on one older than 3.35. Everything in this document
applies to it, except where a section says otherwise. These are the differences an application can
see:

- **The database is one file on a local disk.** `Database` is its path, created with its folder on
  initialise when it is missing and the schema is writable. An in-memory database is refused: it
  ends with its connection, and Nucleus closes a connection after each read. Keep the file off
  network shares, where SQLite's locking, and write-ahead logging, are not safe.
- **One connection writes at a time.** Initialise puts the file in write-ahead log mode
  (`UseWriteAheadLog`), so reads run while another connection writes. A save begins its transaction
  IMMEDIATE, taking the write lock at once, and any other writer, in this process or another,
  waits up to `BusyTimeoutSeconds` (30 by default) for it. SQLite does not queue its waiters, so a
  writer in one process can wait out its timeout while another process writes back to back; give
  each process its own file, or one process the writing, when several write heavily. A write too large for one command runs
  its commands one after another on its own transaction, so it still commits or rolls back whole.
  There is no bulk engine; none is needed in-process.
- **No change notifications.** SQLite tells no other connection about a write, so
  `SubscribeToChangesAsync<T>()` throws `NotSupportedException` and `Capabilities` says so.
- **No full-text index.** `string.Contains` on a `FullTextSearch` column is a substring match
  without regard to case, not a word search, and no full-text index is created.
- **Case folding matches the other engines.** SQLite's own `LIKE` and `lower()` fold ASCII only, so
  Nucleus registers small functions on every connection it opens and its pattern terms
  (`StartsWith`, `EndsWith`, `Contains`, `Like`) and case-insensitive equality fold case across
  Unicode, as they do on SQL Server and PostgreSQL. Equality is exact, as on PostgreSQL. The SQL
  Nucleus writes therefore runs only on its own connections; the file itself is plain SQLite, which
  any tool reads and writes.
- **Values are stored in fixed text forms.** SQLite keeps only integers, reals, text and blobs. A
  `Guid` is 36 lower-case characters; a `DateTime` is `yyyy-MM-dd HH:mm:ss.fffffff`; a
  `DateTimeOffset` is UTC in the same form with `+00:00` (the instant is kept, the offset is not, as
  on PostgreSQL); a date is `yyyy-MM-dd`; a `bool` is 0 or 1. The forms are fixed width, so ordering
  and comparing them orders and compares the values. Values the server sets (`Timestamp`,
  `DeletedAt`) are written in the same forms, to the tick.
- **A `decimal` is stored as a real.** SQLite has no exact decimal type, so a value keeps about 15
  significant digits and is read back rounded to the column's scale. A value that needs more digits,
  or exact sums, belongs on another provider or in a `long` of minor units.
- **A string's length is not enforced.** `varchar(255)` is kept as the declared type, so the schema
  check sees a changed length, but SQLite stores a longer string as it is.
- **A `[TableIndex]` has no included columns.** SQLite indexes carry keys only, so `Include` is
  ignored and the index is created over its keys. A unique `[TableIndex]` enforces exactly its keys,
  as on the other engines.
- **Unique violations name columns, not indexes.** SQLite reports the columns a write broke, and the
  provider maps them back to the column's `UQ_` index or the unique `[TableIndex]` with those keys,
  so `UniqueViolationException` reads the same as on the other engines.
- **Paging is `LIMIT`/`OFFSET`** (`LIMIT -1` when only a skip is given), ordered by ID when the query
  asks for no order.

Each table carries the Guid `ID` primary key and a column per `[DatabaseColumn]` property, with no
`ClusterID`: the table's rowid keeps its insertion order, which a rebuild's copy orders by. SQLite
cannot change a column's type, so a table that only lacks columns has them added in place, one
`ALTER TABLE ADD COLUMN` each, and any other change rebuilds it, in one transaction: SQLite's DDL is
transactional, so a failed update leaves nothing behind.

## Stores for other libraries

The stores that keep other libraries' data in Nucleus tables live with the library whose contract
they implement, so a contract change ships with its store instead of forcing a Nucleus release:

| Package | Repo | Store |
|---|---|---|
| `TFM.Bindery.Nucleus` | [TFM.Bindery](https://github.com/twofatmonkeys/TFM.Bindery) | `NucleusBinderyStore`, the CMS's page store |
| `TFM.S3Library.Nucleus` | [TFM.S3Library](https://github.com/twofatmonkeys/TFM.S3Library) | `NucleusS3AssetStore`, the media library's asset metadata |
| `TFM.Stencil.Nucleus` | [TFM.Stencil](https://github.com/twofatmonkeys/TFM.Stencil) | `NucleusTemplateStore`, Mailroom and Paperwork templates |
| `TFM.Attribution.Nucleus` | [TFM.Attribution](https://github.com/twofatmonkeys/TFM.Attribution) | `NucleusAttributionStore`, marketing touches and sales |

Until November 2026 they shipped from this repo as `TFM.Nucleus.Bindery`, `TFM.Nucleus.S3Library`,
`TFM.Nucleus.Stencil` and `TFM.Nucleus.Attribution`. Their entities now pin their tables with
`[TableNaming(TableNamingStyle.ObjectName, 1)]` and list the old full-name tables in
`[LegacyTableNames]`, so a site moving to the new packages keeps its rows. Each repo tests its
store against the same databases through `TFM.Nucleus.Testing`.

## Testing against a live database

`TFM.Nucleus.Testing` is what this repo's integration tests run on, packaged so a store that lives in another repo can be tested against the same databases the same way. It references no test framework: each framework's shim turns "not configured" into its own skip.

```csharp
using TFM.Nucleus.Testing;

var database = NucleusTestDatabase.SqlServer;          // or .Postgres, or .Sqlite
if (!database.IsConfigured)
    return;                                            // skip with database.SkipReason, your framework's way

using (await database.SchemaLockAsync())               // the net8.0 and net10.0 runs share the database
using var stack = await database.StartStackAsync();    // AddNucleus + the provider, schema initialised
var store = new MyStore(stack.Manager);
```

| Type | What it gives a test |
|---|---|
| `NucleusTestDatabaseSettings` | Where the database is: the `Nucleus:*` (SQL Server), `Nucleus:Postgres:*` and `Nucleus:Sqlite:Database` (a file path) user secrets under the id `tfm-nucleus-integrationtests`, or the `NUCLEUS_TEST_*`, `NUCLEUS_PG_TEST_*` and `NUCLEUS_SQLITE_TEST_DATABASE` environment variables. One local configuration serves every repo. `ToString()` leaves the password out. |
| `NucleusTestDatabase` | `SqlServer`, `Postgres` and `Sqlite`: `IsConfigured`, `SkipReason`, `StartSharedStackAsync` (a fixture's shared stack, booted under the schema lock), `StartStackAsync` (optionally with the provider's options changed), `StartRootedStackAsync` (a provider rooted at one entity type), `LockAsync(suite)` and `SchemaLockAsync()`, and raw SQL for a test's own setup: `ExecuteAsync`, `ScalarAsync`, `TableIdAsync`, `RowCountAsync` and the statements behind them (`RenameTableSql`, `DropTableIfExistsSql`, `CopyTableSql`, `DeleteAllSql`, `DropColumnsSql`). |
| `NucleusEntityTable` | `NucleusEntityTable.For(typeof(MyEntity))`: the table an entity's rows live in and its `[LegacyTableNames]`, computed without a database, for a test that pins them. `NucleusTestDatabase.MoveToLegacyNamesAsync(entities)` puts live tables back under their old names, so a test can prove the next start adopts them with their rows. |
| `NucleusTestStack` | A booted stack: `Provider`, `Manager` (an `IObjectDataManager`, what a store under test is given), `Log` (the warnings and errors it logged) and `Services`. Dispose it. |
| `NucleusTestLock` | A cross-process lock held as an exclusively opened temp file, so the two target framework runs of a suite never reset each other's rows. Waits up to five minutes. |

Stacks find entities the way an application does, from every assembly beside the entry assembly, so a store's test project initialises the store's tables just by referencing the store. The schema lock is not reentrant: take it once per test, around the whole change.

A PostgreSQL stack sets `MaxConnectionPoolSize` to 20 before the test's own options run, so a test can still change it. At the provider default of 100, one process could hold 200 connections, and the net8.0 and net10.0 runs of a suite share a server whose default `max_connections` is 100, so overlapping runs failed with 53300 "too many clients".

## Entity base classes

| Base class | Use for |
|---|---|
| `CoreObject<T>` | A normal root entity. `T` is the entity's own type, which gives the static API its generic constraint. |
| `CoreChildObject<T, TParent>` | A child entity that always belongs to a parent. Exposes `Parent` and participates in cascade saves and deletes. |
| `CoreChildObject<TParent>` | The non self-referencing form of the above. |

Every entity gets `ID` (a `Guid` primary key, assigned on construction) and `Timestamp` (a `DateTimeOffset` set by the server) for free. The server stamps the stored `Timestamp` on every insert and update, but the value is not read back: in memory it is the client clock at the object's first save, and a loaded object keeps the value it was loaded with. Reading it back on every write was measured at 10% to 65% slower for batched, split and bulk saves on both providers.

## Attribute reference

### Column mapping

`[DatabaseColumn]` marks a property as persisted. Without it, a property is in-memory only. The column name defaults to the property name through `[CallerMemberName]`, so `[DatabaseColumn]` on its own is the common case.

Constructors: `()`, `(DataType)`, `(int? dataLength)`, `(DataType, int? dataLength)`, each with an optional trailing column name.

| Field | Default | Meaning |
|---|---|---|
| `ColumnName` | property name | Physical column name, used by every statement, index name and predicate. Set it as `[DatabaseColumn("code")]` or `[DatabaseColumn(ColumnName = "code")]`. See renaming below. |
| `DataType` | `NotSpecified` | One of `NotSpecified`, `Unicode`, `Date`, `Integer`, `Binary`, `LargeBinary`. `NotSpecified` infers the type from the CLR property type. |
| `DataLength` | inferred | Column length. `-1` maps to `max`. |
| `DecimalPlaces` | none | Scale for decimal columns. Nullable, so attribute syntax cannot set it; use `Scale`. |
| `Scale` | provider default (5) | Sets `DecimalPlaces` from attribute syntax: `[DatabaseColumn(Scale = 6)]`. Precision is the data length (default 20) minus the scale. |
| `PrimaryKey` | `false` | Reserved for the `ID` column `CoreObject` provides. Exactly one column must carry it, and marking any other property fails at provider initialisation. |
| `Unique` | `false` | Adds a unique index on the column. NULLs are not constrained. See below. |
| `Indexed` | `false` | Adds an index on the column. Neither this nor `Unique` is allowed on a `max` length or externally stored column. Replaces `NonClusteredIndex`. |
| `IndexDescending` | `false` | Sorts that index descending. Replaces `IndexSortDESC`. |
| `FullTextSearch` | `false` | Includes the column in the table's full-text index, which `string.Contains` on the column searches. Replaces `FullTextIndex`. |
| `ExternalStorage` | `false` | Stores a `byte[]` column outside the row where the provider supports it (SQL Server FILESTREAM). Replaces `Filestream`. |
| `SetValueOverride` | `NULL` | Has the server compute the value on every insert and update, for example `eServerFunction.CurrentDateTimeOffset` for a server timestamp. A value the client set for the property is not sent. |
| `ReturnOutputToProperty` | `false` | Reads the value the server stored back into the property, in the same statement as the write (`OUTPUT` on SQL Server, `RETURNING` on PostgreSQL and SQLite), on every write path including the bulk engine. A write that reads back runs as a reader rather than a non-query, which costs batched and bulk saves measurably (see `Timestamp` above). On SQL Server a table with an enabled trigger refuses `OUTPUT` (error 334), and the write fails with a message saying so. |

**Renaming.** To rename a property and keep its data, rename the property and set `ColumnName` to the old name: the table does not change. Changing `ColumnName` on a table that already has rows is different. The schema sync sees a missing column and adds it empty, so **the property reads null on every existing row**. The old column and its values stay in the table, but nothing reads them. If the same deployment also changes a column's type, the table is rebuilt instead, copying only the columns whose names the old and new tables share, and the old column's values are lost. Rename the column in the database yourself before deploying the new name. Two properties may not map to one column (names are compared without regard to case), `ID` and `Timestamp` keep their names, and no property may take `ClusterID`, or `IsDeleted` or `DeletedAt` on a soft-deletable type. Each of these fails at provider initialisation.

Each property maps to a column type by its CLR type, or by `DataType` when one is given:

| Property | SQL Server | PostgreSQL | SQLite (declared type) |
|---|---|---|---|
| `string` | `varchar(255)`, or the given length; `-1` is `varchar(max)` | `varchar(255)`, or the given length; `-1` is `text` | `VARCHAR(255)`, or the given length (not enforced); `-1` is `TEXT` |
| `string` with `DataType.Unicode` | `nvarchar(510)`, or the given length | `varchar(510)`, or the given length | `VARCHAR(510)`, or the given length |
| `int`, any enum (stored as its integer) | `int` | `integer` | `INTEGER` |
| `long` | `bigint` | `bigint` | `BIGINT` |
| `double` | `float` | `double precision` | `REAL` |
| `bool` | `bit` | `boolean` | `BOOLEAN` (0 or 1) |
| `decimal` | `decimal(15,5)`, or as `Scale` and `DataLength` give | `numeric(15,5)`, the same way | `DECIMAL(15,5)`, stored as a real |
| `DateTime` | `datetime2(7)` | `timestamp` | `DATETIME` (text) |
| `DateTime` with `DataType.Date` | `date` | `date` | `DATE` (text) |
| `DateTimeOffset` | `datetimeoffset(7)` | `timestamptz` (UTC, microseconds) | `DATETIMEOFFSET` (UTC text, ticks) |
| `Guid`, or an entity stored by its `ID` | `uniqueidentifier` | `uuid` | `UUID` (lower-case text) |
| `byte[]` | `varbinary(max)` | `bytea` | `BLOB` |
| a type named `TFMJsonString` | `json` | `jsonb` | `JSON_TEXT` |

The SQL Server names `NonClusteredIndex`, `IndexSortDESC`, `FullTextIndex` and `Filestream`, and `eServerFunction.SYSDATETIME` and `SYSDATETIMEOFFSET`, still work and set the same flags as their neutral replacements, but are marked obsolete. The same holds below the attributes, for a provider author: `IPropertySchema.IsIndexed` and `IsFullTextSearch` replace `IsNonClusteredIndex` and `HasFullTextIndex`, and `ServerFunction.CurrentDateTime` and `CurrentDateTimeOffset` (with the matching `ServerFunctionKind` values) replace `SysDateTime` and `SysDateTimeOffset`. `DataType.Unicode` keeps its name: it means the string holds Unicode text, and a provider whose strings are always Unicode treats it like any string.

`Unique` creates a filtered unique index named `UQ_<table>_<column>` with `WHERE [column] IS NOT NULL`, so any number of rows may leave the column NULL while every non-null value must be distinct. Setting `Indexed` as well does not add a second index, and `IndexDescending` applies to whichever index is created. A write that breaks the index throws a [`UniqueViolationException`](#unique-violations). On SQL Server the index is nonclustered, and because it is filtered, SQL Server requires `SET QUOTED_IDENTIFIER ON` for every INSERT, UPDATE and DELETE against the table. SqlClient, and therefore Nucleus, sets it on by default, but SQL Agent T-SQL steps, `bcp` without `-q`, `sqlcmd` without `-I` and some legacy ODBC connections do not and will fail with error 1934 until they do. For a rule over several columns together, declare a unique [`[TableIndex]`](#class-level-behaviour) instead.

On initialise the provider compares the `NCI_*`, `UQ_*` and `NIX_*` indexes in the database with the entity model and drops or creates them to match, without rebuilding the table. Indexes with any other name are never touched. If an existing column is changed to `Unique`, or a `[TableIndex]` is made `Unique`, and existing rows already hold duplicate values, the unique index cannot be created: initialisation fails with a logged error naming the index, the table and its columns, no rows are modified, and the duplicates must be resolved by hand before initialising again. The sync drops the index being replaced before it builds the new one, so until then the table has neither.

### Class-level behaviour

| Attribute | Effect |
|---|---|
| `[Cacheable(refreshInterval, cacheLifetime)]` | Keeps the whole collection plus a Guid index in memory. Both values are in seconds and default to 120 and 900. |
| `[Deletable(DeleteBehavior.Hard)]` | Deletes physically remove rows. `[Deletable]` alone is hard. |
| `[Deletable(DeleteBehavior.Soft)]` | Deletes flag the row through `IsDeleted` and `DeletedAt` system columns, and reads filter them out unless `IncludeDeleted` is set on the query. |
| `[Deletable(false)]` | On a child type: its rows are never deleted, by any route. A declaration above it that would delete them fails the schema. |
| `[SuppressDB]` | Excludes the type from schema management entirely. |
| `[InitializePriority(n)]` | Intended to order type initialisation, descending. Parsed but not currently applied. |
| `[TableIndex(name, keys..., Include = [...], Unique = true)]` | An index over several columns, named `NIX_<table>_<name>`, optionally unique over all of its keys together. Repeat it for more than one. See below. |
| `[DatabaseTable("tbl_Name")]` | The table name, used as written in place of the one the naming policy would give. Not inherited. See [Table names](#table-names). |
| `[TableNaming(style, levels)]` | The naming policy of this type's table, in place of the provider's. Inherited, so it can sit on a root. See [Table names](#table-names). |
| `[LegacyTableNames("tbl_Old", ...)]` | Earlier names of the type's table. A missing table is renamed from the one of them that exists. Not inherited. See [Table names](#table-names). |

Deletion is opt-in. Nothing is deleted unless a `[Deletable]` declaration says so: on the type, on
a child collection property, or on an owner above a child type. See [Deleting](#deleting).

`[TableIndex]` declares an index over several columns, by property name:

```csharp
[TableIndex("KindTime", nameof(Kind), nameof(OccurredUtc), Include = [nameof(Subject), nameof(SessionId)])]
public class Visit : CoreObject<Visit> { ... }
```

The keys order the index, first to last, each ascending, so a read that filters on the first key, or on the first and a range of the second, finds its rows without scanning the table. The included columns are carried in the index without ordering it, so a read that needs only key and included columns (a [row read](#row-reads) of them, say) is answered from the index alone. Keys cannot be long text (`DataLength = -1`); included columns can. A name used twice on the type, a property that is not a stored column, a column named twice or a long text key fails when the schema is built. The index is created on SQL Server as a nonclustered index and on PostgreSQL (11 or later) with `INCLUDE`; it is compared and synced like a column index, so changing its keys or included columns drops and creates it again, and removing the attribute drops it. Creating one over a large table blocks writes to the table while it builds, and the time it took is logged.

`Unique = true` makes the keys a unique rule taken together, the several-column form of a column's `Unique`:

```csharp
[TableIndex("TemplatePath", nameof(TemplateId), nameof(FilePath), Unique = true)]
public class PdfTemplateFile : CoreObject<PdfTemplateFile> { ... }
```

No two rows may share every key value. The index is filtered to rows whose keys are all non-null (`WHERE [TemplateId] IS NOT NULL AND [FilePath] IS NOT NULL`), so a row with any key left null is not constrained, the same on SQL Server and PostgreSQL. `Include` can be used with it. A write that breaks it throws a [`UniqueViolationException`](#unique-violations) naming the entity and the index. Turning `Unique` on or off drops and creates the index again, never the table. Existing duplicates fail initialisation as described above. With `ReadOnlySchema`, a missing index, or one whose uniqueness differs, reports the schema as not current and nothing is created. On SQL Server the index is filtered, so the `QUOTED_IDENTIFIER` requirement described for `Unique` above applies to the table.

### Table names

A table is named, in order of precedence, by the type's `[DatabaseTable]`, then by its own (or an inherited) `[TableNaming]` policy, then by its provider's `TableNaming` option. The default policy, `TableNamingPolicy.FullName`, gives every table the name it always had: `tbl_` and the type's full name with each `.` and `+` replaced by `_`, so `Shop.Billing.Invoice` is `tbl_Shop_Billing_Invoice`. `TableNamingPolicy.ObjectName(levels)` keeps only the last `levels` parts of the full name, a nested type's outer type counting as a part: `ObjectName(1)` names it `tbl_Invoice` and `ObjectName(2)` `tbl_Billing_Invoice`. More levels than the name has gives the full name.

```csharp
services.AddNucleusSQLDataProvider(options =>
{
    options.TableNaming = TableNamingPolicy.ObjectName(2);
    options.RenameFullNameTables = true; // adopt each existing full-name table
});

[TableNaming(TableNamingStyle.ObjectName, 1)]          // every entity under this root
public abstract class AppRoot<T> : CoreObject<T> where T : CoreObject<T> { }

[DatabaseTable("tbl_Orders")]                          // this name, whatever the policy
[LegacyTableNames("tbl_Order", "tbl_SalesOrder")]      // and the names it had before
public class Order : AppRoot<Order> { ... }
```

A `[DatabaseTable]` name is the whole table name, prefix included: letters, digits and underscores, starting with a letter or underscore, at most 128 characters. It is not inherited, because a root and the entities under it each have a table and an inherited name would give them one. `[TableNaming]` is inherited, since a policy still gives each type a name of its own, and a type's own attribute wins over its root's. Index and constraint names follow the table name (`NCI_<table>_<column>` and so on), and on PostgreSQL a name over 63 bytes is shortened as usual. Two types that end up with one table name, a `[LegacyTableNames]` entry that is another type's table, and an old name two types both claim each fail at provider initialisation, naming the types.

**Changing a name does not move the data by itself.** On a database that already has tables, a new name makes the schema sync look for a table that is not there, and by default it creates an empty one and leaves the old table, rows and all, where it was. Two things make it rename the old table instead, inside the schema transaction, keeping its rows and its object id (nothing is copied):

- `[LegacyTableNames]` on the entity lists the names its table had. It applies under any policy.
- `RenameFullNameTables` on the provider adopts the table under the entity's full-name form, the name every table had before policies existed, whenever the configured name differs from it. Set it when turning on a shorter policy, or when adding `[DatabaseTable]` to an existing entity.

The rename covers the table's primary key, constraints and Nucleus-named indexes, which take the new table's names, so no index is rebuilt (SQLite cannot rename an index, so there the index sync drops the old-named ones and creates them again under the new names); on PostgreSQL the old change notification trigger is dropped and the new one created. The table is then brought up to date like any existing table: columns it lacks are added in place, and a changed column type rebuilds it. On SQL Server a full-text index cannot change inside a transaction, so it moves with the table and is recreated in the new table's catalog by the full-text sync.

If more than one of a missing table's old tables exists, nothing is renamed: initialisation fails, nothing is changed, and the error names the tables. Nucleus does not merge tables, because their rows can share IDs, their children point at parents by ID, and their columns can differ, so which rows to keep is a decision for a person. Move the rows you want into one of them, drop the others, and initialise again. If the entity's table exists and an old one does too, the old table is left alone and a warning is logged. Nothing is renamed while the schema is read-only; the check logs what an update would rename. Two processes starting together against a database that still needs a rename can both try it, and the second fails its initialisation and succeeds on its next start, as a racing table create does.

### Relationships

| Attribute | Applies to | Effect |
|---|---|---|
| `[LinkedObject]` | a property holding another entity | Stores the referenced entity's `ID` in this table. Overloads take a `linkedIDProperty` name and `forceSave`: with `forceSave: true`, saving this entity also inserts or updates the referenced one, in the same transaction, when it is new, changed, or holds changed or removed children; its own forceSave links follow, and a cycle ends. The referenced type must be stored by the same provider, or the save throws before anything runs. |
| `[Deletable]` | a `NucleusChildCollection` property | Removing a stored child from the collection and saving the parent deletes the child. See [Deleting](#deleting). |
| `[RetrieveOnDemand]` | a `NucleusChildCollection` property | Makes the collection lazy: loads leave its rows out, and it loads them the first time it is read. See [Lazy collections](#lazy-collections). |
| `[ParentObject(parentIDProperty)]` | the `Parent` property of a child entity | Identifies the owning entity. `CoreChildObject` already applies it. |
| `[LinkedChildCollection]` | a collection property | Obsolete, and has no effect: a child collection is a `NucleusChildCollection` property, found by its type. It was the v1 marker. |

### Logging and auditing

| Attribute | Effect |
|---|---|
| `[SuppressLogging]` | Omits the type or property from audit summaries. Note it is inherited, so applying it to a base class silences every descendant. |
| `[AbbreviatedLogging]` | Records that the property changed without recording the values, for long or sensitive data. |
| `[LinkWhenLogging]` | Includes the linked object's ID in the audit entry. |
| `[RetrieveOnDemand]` | On a child collection, makes it lazy (see [Relationships](#relationships)). On any other property it has no effect. |

## Static API reference

These live on `CoreObject` and forward to the runtime that `AddNucleusData()` publishes, so DI must be configured before they are used. Every generic argument is constrained to `where T : CoreObject<T>, new()`.

| Member | Returns | Notes |
|---|---|---|
| `LoadByID<T>(Guid id)` | `T?` | O(1) from cache for `[Cacheable]` types. |
| `TryLoadByID<T>(Guid id, out T? obj)` | `bool` | Non-throwing form of the above. |
| `LoadObjectWhere<T>(Expression<Func<T, bool>>)` | `T?` | First match, or null. |
| `LoadAll<T>()` | `NucleusCollection<T>` | Whole collection. |
| `LoadWhere<T>(Expression<Func<T, bool>>)` | `NucleusCollection<T>` | Translated to a parameterised query. |
| `LoadWhereAsync<T>(Expression<Func<T, bool>>, CancellationToken)` | `Task<NucleusCollection<T>>` | Async form. |
| `LoadByID<T>(Guid, params Expression<Func<T, INucleusChildCollection>>[] exclude)` | `T?` | Leaves the selected child collections unloaded. Bypasses the cache when anything is excluded. |
| `LoadWhere<T>(predicate, params exclude)`, `LoadWhereAsync<T>(predicate, CancellationToken, params exclude)` | as above | The same, for a predicate. |
| `DeleteWhere<T>(Expression<Func<T, bool>>)`, `DeleteWhereAsync<T>(..., CancellationToken)` | `int`, `Task<int>` | Deletes every matching row without loading any, with its child collection rows, in one transaction. Returns the rows of `T` affected. See [Deleting](#deleting). |
| `BulkSave<TObject>(IEnumerable<TObject>)` | `void` | Routes large batches through the provider's bulk engine. |

Instance members on every entity:

| Member | Notes |
|---|---|
| `Save()`, `SaveAsync()`, `SaveAsync(CancellationToken)` | Validates as every save does (`Validate()`, which also validates the new and changed children and the forceSave links, then `PreSave()`), then writes only the changed columns, the child collections (deleting removed children) and the forceSave links, in one transaction. |
| `SaveAsync(IDataContext, CancellationToken)` | Saves inside a caller-owned context and transaction, validating first; the caller commits or rolls back. |
| `Delete()`, `DeleteAsync(CancellationToken)` | Types with their own `[Deletable]` only. Removes the object's child collection rows at every depth in the same statement batch, whether or not they were loaded. Throws before anything executes if a child type is stored by another provider. |
| `IsNew`, `IsReadOnly`, `HasChanges`, `DataChanged` | Change-tracking state. |
| `ToJson()`, `DisplayName` | Serialisation and display helpers. |

Equality, `GetHashCode` and the explicit conversion to `Guid` are all based on `ID`.

## Child collections

Child collections are rows a parent owns: they are created with it, change with it and go with it
(an order's lines, a roster's shifts, a campaign's recipients). Model them as a
`NucleusChildCollection<TChild, TParent>` property on the parent, read through
`GetChildCollection`, with the child deriving from `CoreChildObject<TChild, TParent>`. A child can
own a collection of its own, so owned rows nest to any depth. A parent holds at most one collection
of each child type; two fail when the schema is built.

```csharp
public class Roster : CoreObject<Roster>
{
    [Deletable]                       // removing a line and saving deletes it
    public NucleusChildCollection<RosterLine, Roster> Lines => GetChildCollection<RosterLine, Roster>();

    [RetrieveOnDemand]                // can grow large: left out of loads, read on first use
    public NucleusChildCollection<RosterNote, Roster> Notes => GetChildCollection<RosterNote, Roster>();
}

public class RosterLine : CoreChildObject<RosterLine, Roster>
{
    [DatabaseColumn]
    public int Hours { get => GetValue<int>(); set => SetValue(value); }

    public override void Validate()
    {
        if (Hours < 0)
            throw new InvalidOperationException("Hours cannot be negative.");
        base.Validate();
    }
}
```

Nucleus then does the bookkeeping:

- **Loading.** Every load of the parent brings its children in the same round trip, bound to the
  parent and unchanged: `LoadWhere`, `LoadByID`, paged loads and the async LINQ operators. When a
  load has a `Take` or a page, the child query selects the children of the returned parents only.
  Lazy collections are the exception (below).
- **Saving.** Save the parent, never a child (a child's own `Save` throws
  `UnableToSaveFromChildObject`). New children are inserted, changed ones updated and removed ones
  deleted, in one transaction. The deletes run first, so a replacement with the same unique key
  does not collide with the row it replaces. Large batches go to the bulk engine on their own. A
  save that fails rolls back and keeps the removals, so saving again deletes them.
- **Validating.** The parent's `Validate()` runs `Validate()` on every new or changed child, at
  every depth, before anything is written, so a child's rules hold without the parent restating
  them. Unchanged loaded children are not checked again. Every save path validates: an entity's
  own `Save`, the manager's `Save`, `SaveAsync` and `BulkSave`, and a context's `Save`,
  `SaveAsync` and `BulkSave`. Do not call `Validate()` before saving; the save does.
- **Indexing.** A child's `Parent` column always has an index (`NCI_<table>_Parent`), since every
  load of a collection and every cascaded delete filters on it. Declare no `[TableIndex]` on
  `Parent` alone, which would duplicate it; one that leads with `Parent` and adds a column the
  child is read by, such as `[TableIndex("ByParent", nameof(Parent), nameof(Sequence))]`, still
  helps an ordered read.
- **Deleting.** Declared, and cascaded at every depth (below).
- **Ownership.** A child belongs to one parent: adding it to a second throws
  `ParentObjectAlreadyBound`. A child removed and added back to the same collection before the
  save is kept.
- **Change notification.** A change to a child, or to the collection, raises `PropertyChanged` on
  the parent for the collection property's name.

Do not keep owned rows as a separate entity with a hand-written parent ID column, a private list,
or attach, remove and acknowledge methods. That rebuilds what the collection already does, and gets
the transaction, the cascade or the cache eviction wrong. Use a separate entity with a plain ID
reference only for rows that outlive the parent or that more than one parent shares.

### Deleting

Nothing is deleted unless a declaration says so. `[Deletable]` can go in three places, on any or
all of them:

| Declared on | What it enables |
|---|---|
| The entity type | Its rows can be deleted directly (`Delete()`, `DeleteWhere`), and everything it owns goes with them. On a child type, removing one from its collection deletes it too. |
| A child collection property | Removing a stored child from that collection and saving the parent deletes the child, with everything it owns. |
| The parent (or any owner above) | Its own rows can be deleted, and every child type below it, at any depth, is deleted with it or when removed from its collection. |

A child type made deletable only by its collection or an owner is deleted through its owner and
never on its own: `Delete()` on it throws. `[Deletable(false)]` on a child type forbids deleting its
rows by any route, and wins: when a declaration above it would delete them, the schema fails to
build, naming both declarations, rather than orphaning rows at delete time. `[Deletable(false)]` on
a parent stops only the parent's own deletion; `[Deletable]` on its collection still makes removals
delete. `[Deletable]` on a property that is not a child collection, and `[Deletable(false)]` on a
collection, fail when the schema is built.

**Hard or soft.** A type's own declaration decides. A child type without one takes the behaviour
of the nearest declaration above it that enables deleting it (the collection's when it names one,
then the parent's, and so on up), so the lines of a soft-deleted order can be restored with it. A
child type made soft this way gets the soft-delete columns, added in place like any new column.

**Removing without a declaration.** Removing a stored child from a collection whose children are
not deletable throws `InvalidOperationException` from `Remove`, `RemoveAt`, `Clear` or the indexer,
before anything changes, and says where to declare `[Deletable]`. `Clear` checks every child first.
A child that was never saved can always be removed; it is simply dropped.

**The cascade.** Deleting a parent, by object or by predicate, deletes its child collection rows at
every depth in one batch: deepest first, each level selecting by its parent's condition, and each
type keeping its own hard or soft behaviour. It does not depend on which children were loaded.

**Deleting by predicate.** `DeleteWhere` and `DeleteWhereAsync` exist on `CoreObject` (static), the
object data manager (each in a transaction of its own, with cache eviction), and `IDataContext`
(in a caller-owned context and transaction, without cache eviction). No row is loaded. The predicate
uses the same translation as `LoadWhere`, with two guards: a predicate that matches every row, such
as `x => true`, throws `ArgumentException`, and one that matches none, such as `x => false`,
returns 0 without a round trip. Soft-deletable types are flagged, and only rows not already
deleted count.

### Leaving a collection out

`Exclude(x => x.Items)` on a query, or the `exclude` selectors on `LoadByID` and `LoadWhere`, drop
that collection's query. Its rows are never queried or transferred, and the collection comes back
with `IsLoaded` false. Reading it (enumerating, `Count`, the indexer, `Contains`, `Remove`, `Clear`)
throws `InvalidOperationException` rather than answering as if the parent had no children. Adding
still works, and added children save with the parent. `collection.LoadAsync()` fetches the stored
children by the parent's ID and merges them by ID with any added since;
`IObjectDataManager.LoadChildCollectionAsync(collection)` does the same without the ambient
runtime. An excluded load never reads from or populates the `[Cacheable]` cache. Serialising a
parent whose collection is excluded throws for the same reason.

### Lazy collections

`[RetrieveOnDemand]` on a child collection makes it lazy, for a collection that can grow large.
Every load of the parent leaves its rows out, and the collection loads them the first time it is
read (enumerating, `Count`, the indexer, `Contains`, `IndexOf`, `Remove`, `Clear`, `Insert`), once,
however many threads read it together. The read goes through the manager or data context that
loaded the parent, on a connection of its own and outside any transaction the caller holds. Adding
to an unread lazy collection does not load it, and the added children save with the parent.

Reading a lazy collection is a round trip that the call site does not show, so a loop that reads it
for each parent of a listing runs a query per parent. A listing that needs the children includes
them, and they come for every returned parent in the same round trip:

```csharp
var campaigns = await manager.Get<Campaign>().Include(c => c.Recipients).Where(c => c.Year == 2026).ToListAsync();
```

`Include` and `Exclude` apply to every read through the adaptor, `FindAsync` and
`ToPagedResultAsync` included. `Exclude` wins over lazy: an excluded lazy collection throws
when read. A lazy collection always loads all of its rows; to read part of one, query the child
type by its parent, which reads only the rows asked for and leaves the parent's collection unloaded:

```csharp
var page = await manager.Get<Recipient>()
    .Where(r => r.Parent.ID == campaign.ID && r.Status == Status.Bounced)
    .ToPagedResultAsync(0, 50);
var total = await manager.Rows<Recipient>(r => r.Parent == campaign).CountAsync();
```

Serialising a parent reads its lazy collections. A `[Cacheable]` type cannot have one, because its cached instances are shared and
loading the collection would change it for every caller; the schema fails to build.

## Lifecycle hooks

Override these on an entity to run logic at defined points:

| Hook | When it runs |
|---|---|
| `Validate()` | At the start of every save, on every path (entity, manager, context, bulk), once per object saved. Throw to reject the save: nothing is written and the exception reaches the caller as thrown. The root's also runs every new or changed child's, and each forceSave link's. Call `base.Validate()`. |
| `PreSave()` | After validation and before anything is written, on the object saved and on each new or changed child and forceSave link the save writes. A value set here is written but not validated. |
| `OnAfterLoadFromDB()` | Once per loaded object, at the end of every load: after its columns and parent are bound, its loaded children are attached and have run theirs, and an excluded collection reads as unloaded. A lazy collection's rows run theirs when it loads. |
| `OnAfterInitializeDB()` | After schema initialisation for the type. |

## Async query operators

`ObjectDataAdaptor<T>` is a LINQ provider that translates `Where`: every `Where` in a chain applies, combined with AND, and any other operator before a `Where` (`OrderBy`, `Select` and so on) throws `NotSupportedException` rather than being ignored. To choose columns, order, page or group in the database, use a [row read](#row-reads); otherwise order, page and project in memory, or use `ToPagedResultAsync`. `Where` is declared on the adaptor itself rather than coming from `Queryable`, because the adaptor is both an `IQueryable<T>` and an `IAsyncEnumerable<T>` and .NET 10 would otherwise make an unqualified call ambiguous. The async operators live in `TFM.Nucleus.Data`: `ToListAsync`, `FirstOrDefaultAsync`, `SingleOrDefaultAsync`, `AnyAsync`, `CountAsync`, `LongCountAsync`, each with an optional predicate and cancellation token, plus `ToPagedResultAsync(skip, take, cancellationToken)` which returns rows and the total count in a single round trip. An enum column compares directly in a predicate, including a nullable one and an `IN` over a collection of enum values, and is bound as the underlying integer the column stores.

An entity reference (a child's `Parent`, a `[LinkedObject]`) is stored as the referenced entity's ID, and a predicate compares it by that ID. `x => x.Parent.ID == id`, `x => x.Parent == order`, `x => ids.Contains(x.Parent.ID)` and `x => orders.Contains(x.Parent)` all translate to the reference's column, in a load, a paged load, a [row read](#row-reads), a count or a `DeleteWhere`. Only the ID translates: `x => x.Parent.Name == "a"` would need a join, and throws `NotSupportedException` naming the term, as does any other member read through a column (`x.Name.Length`) and a comparison of two columns. A comparison may be written either way round: `3 < x.Quantity` is `x.Quantity > 3`.

## Row reads

`IObjectDataManager.Rows<T>(predicate)` reads rows of values rather than entities, over the rows the predicate matches (the same single `Where` a load translates; every row when it is null). Only what is asked for is read, so a long text column that is never selected is never transferred, and nothing is materialised as an entity.

```csharp
// The newest 100 visits, two columns each, and how many match in all: one round trip.
var page = await data.Rows<Visit>(v => v.OccurredUtc >= from)
    .Select(v => v.OccurredUtc, v => v.Path)
    .OrderByDescending(v => v.OccurredUtc)
    .ToPageAsync(skip: 0, take: 100, cancellationToken);

// One row per kind and subject, with figures worked out in the database.
var groups = await data.Rows<Visit>(v => v.OccurredUtc >= from)
    .GroupBy(v => v.Kind, v => v.Subject)
    .Count("Visits")
    .CountDistinct(v => v.SessionId, "Sessions")
    .Max(v => v.OccurredUtc, "Last")
    .ToListAsync(cancellationToken);

foreach (var g in groups)
    Console.WriteLine($"{g.Get<VisitKind>("Kind")} {g.Get<string>("Subject")}: {g.Get<long>("Visits")} by {g.Get<long>("Sessions")}");
```

- **Columns:** `Select(...)` names the properties each row returns. `OrderBy`, `OrderByDescending`, `ThenBy` and `ThenByDescending` order the rows in the database.
- **Groups:** `GroupBy(...)` returns one row per distinct combination of the keys, each named by its property, with the measures asked for: `Count(alias)`, `CountDistinct(property, alias)`, `Min`, `Max` and `Sum`. Measures without `GroupBy` are one row over every match. A grouped read is not ordered or paged (order its groups in memory) and does not `Select` as well. A measure's alias is a plain identifier (letters, digits, underscores, starting with a letter or underscore) and must not repeat a key's name.
- **Running it:** `ToListAsync`, `StreamAsync` (one row at a time; each row can be kept), `ToPageAsync(skip, take)` (rows and the total count in one round trip; the read must be ordered and not grouped) and `CountAsync` (a real `COUNT` in the database, which the LINQ `CountAsync` is not: that one loads every row).
- **Values:** `ResultRow.Get<T>(name)` converts each value: a database null is the type's default, an integer becomes an enum, a number becomes the numeric type asked for. A count is a `long`; a minimum or maximum has the column's type (a date stays a `DateTimeOffset` on every engine); a sum is the number the engine returns. `row[name]` is the value as read.
- **Rules:** soft-deleted rows are left out as in any read. Child collections are never read. The rows cannot be saved: a partial entity would write nulls over the columns it never read. A predicate that matches nothing returns no rows without a round trip.

A covering [`[TableIndex]`](#class-level-behaviour) over the filter and the columns a row read needs lets the database answer it from the index alone.

## Registration reference

| Method | Package | Purpose |
|---|---|---|
| `AddNucleus()` | `TFM.Nucleus` | Registers the schema provider and derives `AppName` and `AppVersion` from the entry assembly. Note this overload sets `ReadOnlySchema` to `false`, which enables automatic schema creation and migration. |
| `AddNucleus(Action<NucleusOptions>)` | `TFM.Nucleus` | The same, with explicit options. `ReadOnlySchema` defaults to `true` here. |
| `AddNucleusLogging()` | `TFM.Nucleus` | Opt-in. Persists host log entries into the `NucleusLogEntry` table. |
| `AddNucleusData()` | `TFM.Nucleus.Data` | Registers the provider registry and the object data manager, and publishes the ambient runtime that the static API uses. |
| `AddNucleusSQLDataProvider()` | `TFM.Nucleus.Data.SQL` | Configures from the `SQL_SERVER`, `SQL_DATABASE`, `SQL_USERNAME` and `SQL_PASSWORD` environment variables. |
| `AddNucleusSQLDataProvider(Action<SQLConfiguration>)` | `TFM.Nucleus.Data.SQL` | Configures explicitly. |
| `AddNucleusSQLDataProvider<TRoot>(Action<SQLConfiguration>)` | `TFM.Nucleus.Data.SQL` | Registers a provider rooted at `TRoot`, for spanning several databases. |
| `AddNucleusSQLDataProvider(Type rootType, Action<SQLConfiguration>)` | `TFM.Nucleus.Data.SQL` | The same for open-generic roots, for example `typeof(AppRoot<>)`. |
| `AddNucleusPostgresDataProvider()` | `TFM.Nucleus.Data.PostgreSQL` | Configures from the `PG_SERVER`, `PG_DATABASE`, `PG_USERNAME` and `PG_PASSWORD` environment variables, and `PG_PORT` when set. |
| `AddNucleusPostgresDataProvider(Action<PostgresConfiguration>)` | `TFM.Nucleus.Data.PostgreSQL` | Configures explicitly. |
| `AddNucleusPostgresDataProvider<TRoot>(Action<PostgresConfiguration>)` | `TFM.Nucleus.Data.PostgreSQL` | Registers a provider rooted at `TRoot`. Roots on SQL Server and on PostgreSQL can live in one application. |
| `AddNucleusPostgresDataProvider(Type rootType, Action<PostgresConfiguration>)` | `TFM.Nucleus.Data.PostgreSQL` | The same for open-generic roots. |
| `AddNucleusSqliteDataProvider()` | `TFM.Nucleus.Data.SQLite` | Configures the database file from the `SQLITE_DATABASE` environment variable. |
| `AddNucleusSqliteDataProvider(Action<SqliteConfiguration>)` | `TFM.Nucleus.Data.SQLite` | Configures explicitly. |
| `AddNucleusSqliteDataProvider<TRoot>(Action<SqliteConfiguration>)` | `TFM.Nucleus.Data.SQLite` | Registers a provider rooted at `TRoot`, with its own database file. |
| `AddNucleusSqliteDataProvider(Type rootType, Action<SqliteConfiguration>)` | `TFM.Nucleus.Data.SQLite` | The same for open-generic roots. |

### NucleusOptions

| Option | Default | Meaning |
|---|---|---|
| `ReadOnlySchema` | `true` | When true the schema is verified but never written. The parameterless `AddNucleus()` sets it false. |
| `AppName` | entry assembly name | Reported to the database server as the application name. |
| `AppVersion` | entry assembly version | Recorded with audit entries. |
| `EnableAuditLogging` | `true` | Writes change summaries such as "Updated Name from A to B" on save and delete. |
| `AuditLogLevel` | `Information` | The level those summaries are written at. The category is the entity type's full name. |
| `LogEntityMinimumLevel` | `Warning` | Minimum level for host log entries persisted by `AddNucleusLogging()`. |

### SQLConfiguration

| Option | Default | Meaning |
|---|---|---|
| `Server` | none | `host`, `host:port` or `host\instance`. |
| `Database` | none | Initial catalog. |
| `Username`, `Password` | none | SQL authentication credentials. |
| `UseIntegratedSecurity` | `false` | Use Windows authentication and ignore the credentials above. |
| `ConnectionString` | none | A raw connection string that bypasses the builder, which is how Azure AD and managed identity are configured. `Database` is still used for schema introspection. |
| `MaxConnectionPoolSize` | `100` | The most contexts open at once, and the size of each of the driver's two connection pools (transactional and not), so the server can see up to twice this many connections from one process. See the known limits. |
| `SQLPacketSize` | `1500` | Network packet size. |
| `MaxThreadsForLargeQuery` | `12` | The most parallel contexts an oversized write without a table schema fans out to. |
| `MaxParametersPerQuery` | `2000` | The most parameters one command carries: a batch over it is split, and a larger insert or update batch goes to the bulk engine. |
| `CommandTimeoutSeconds` | `10` | How long an async context waits for a free slot under `MaxConnectionPoolSize`. Despite the name it is not a command timeout: queries and saves use the driver's default of 30 seconds. |
| `SchemaCommandTimeoutSeconds` | `900` | The command timeout of each schema statement at initialise: a rebuild's row copy, an added column, an index created or dropped. `0` means no limit; a negative value is refused. |
| `BulkCopyTimeoutSeconds` | `60` | Timeout for bulk copies and bulk statements. |
| `BulkCopyBatchSize` | `50000` | The most staged rows one bulk INSERT or UPDATE statement writes; a larger batch runs as several statements in the same transaction. Must be 1 or more. |
| `TableNaming` | `TableNamingPolicy.FullName` | How the tables of entities that name neither their table nor their policy are named: `FullName` or `ObjectName(levels)`. See [Table names](#table-names). |
| `RenameFullNameTables` | `false` | Rename a missing table's full-name form to its configured name instead of creating it empty. See [Table names](#table-names). |

Every option except `UseIntegratedSecurity`, `SQLPacketSize` and `BulkCopyBatchSize` comes from `RelationalProviderOptions`, which every relational provider's configuration shares. Connections are built with `TrustServerCertificate`, MARS enabled, a 3 second connect timeout and 4 connect retries.

### PostgresConfiguration

| Option | Default | Meaning |
|---|---|---|
| `Server` | none | `host` or `host:port`. |
| `Port` | `5432` | The port, when `Server` does not give one. |
| `Database` | none | The database. Created on initialise when missing and the schema is writable, through the `postgres` maintenance database. |
| `Username`, `Password` | none | Credentials. |
| `Schema` | `public` | The schema the tables live in. Created when missing. |
| `SslMode` | driver default | `Disable`, `Allow`, `Prefer`, `Require`, `VerifyCA` or `VerifyFull`. |
| `ConnectionString` | none | A raw Npgsql connection string that bypasses the builder. `Database` still names the database to create and read. |
| `ConnectRetryCount` | `3` | Attempts at opening a connection when the failure is transient, with an exponential back-off. |
| `MaxConnectionPoolSize` | `100` | The most contexts open at once, and the size of each of the driver's two connection pools (transactional and not), so the server can see up to twice this many connections from one process. See the known limits. |
| `MaxThreadsForLargeQuery` | `12` | As for SQL Server. |
| `MaxParametersPerQuery` | `30000` | The most parameters one command carries (PostgreSQL allows 65,535): a batch over it is split, and a larger insert or update batch goes to the bulk engine. |
| `CommandTimeoutSeconds` | `10` | How long an async context waits for a free slot under `MaxConnectionPoolSize`. Not a command timeout, as for SQL Server. |
| `SchemaCommandTimeoutSeconds` | `900` | As for SQL Server. |
| `BulkCopyTimeoutSeconds` | `60` | Timeout for each COPY and bulk statement. |
| `TableNaming` | `TableNamingPolicy.FullName` | As for SQL Server. |
| `RenameFullNameTables` | `false` | As for SQL Server. |

### SqliteConfiguration

| Option | Default | Meaning |
|---|---|---|
| `Database` | none | The path of the database file. A relative path is resolved against the current directory. Created with its folder on initialise when missing and the schema is writable. |
| `ConnectionString` | none | A raw Microsoft.Data.Sqlite connection string; its data source is the file when `Database` is not set. The provider still sets the file, the open mode and the busy timeout. |
| `Password` | none | Refused: the bundled SQLite does not encrypt, and ignoring a password would leave the file readable. |
| `Server`, `Username` | none | Ignored. |
| `UseWriteAheadLog` | `true` | Puts the file in write-ahead log mode on initialise, so reads run while one connection writes. The mode is kept in the file. |
| `BusyTimeoutSeconds` | `30` | How long a command waits for another connection's write lock before it fails. |
| `MaxConnectionPoolSize` | `100` | The most contexts open at once. |
| `MaxParametersPerQuery` | `2000` | The most parameters one command carries; a larger write is split into commands run one after another on one transaction. SQLite allows 32,766 a statement, but a command of many statements binds slower as it grows, and holds the write lock while it does. |
| `CommandTimeoutSeconds` | `10` | How long an async context waits for a free slot under `MaxConnectionPoolSize`. Not a command timeout, as for SQL Server. |
| `SchemaCommandTimeoutSeconds` | `900` | As for SQL Server. |
| `TableNaming` | `TableNamingPolicy.FullName` | As for SQL Server. |
| `RenameFullNameTables` | `false` | As for SQL Server. |

## Unique violations

A write that breaks a unique index throws `UniqueViolationException` (namespace `TFM.Nucleus.Data`, in the Abstractions package), whichever provider stores the data. `EntityType` names the entity, `IndexName` the Nucleus index name, and `PropertyNames` and `ColumnNames` the index's key properties and columns in key order. For a `[DatabaseColumn(Unique = true)]` index, `PropertyName` and `ColumnName` name its one property and column, and `Is<T>(name)` tests for it by either. For a unique `[TableIndex]`, `DeclaredIndexName` is the name it was declared with and `IsIndex<T>(name)` tests for it; `PropertyName` and `ColumnName` are then null, even for one key. For an index Nucleus does not manage, such as a primary key, the names are null and the lists empty. The message is the one an ordinary storage failure carries, and the provider's own exception is the inner exception, so code that caught `Exception` before still does. The manager's `Save` and `SaveAsync` throw it as it is, not wrapped, so the filters below work on them as they do on a context's save.

```csharp
catch (UniqueViolationException ex) when (ex.Is<Customer>(nameof(Customer.Email)))
{
    // another writer took the address first
}

catch (UniqueViolationException ex) when (ex.IsIndex<PdfTemplateFile>("TemplatePath"))
{
    // the template already has a file at that path
}
```

## Known limits

These are open findings (`docs/code-review-findings.md`) that change what a consumer must do.

- **Saving a parent rewrites every loaded child.** An update links an update of every child in each
  loaded collection, and each sets `Timestamp`, so a parent with many loaded children writes them
  all on every save. Exclude, or make lazy, a large collection the save does not need.
- **A save through a caller-owned context commits in memory before the caller commits.**
  `SaveAsync(IDataContext, ...)` marks the object, its children and its removals saved as soon as
  the writes run. A caller who then rolls back must reload the objects.
- **A lazy collection's first read is a synchronous round trip**, on a connection of its own. Use
  `Include` or `LoadAsync()` where that matters.
- **A provider can hold twice `MaxConnectionPoolSize` connections** on SQL Server and PostgreSQL.
  Transactional and other connections differ by `Enlist`, so the driver keeps a pool of that size
  for each. Size the server's connection limit for twice the setting per process, or lower the
  setting.
- **A caller-owned context commits when it is disposed** unless `Rollback()` or `TryRollback()`
  was called. A failed statement does not roll it back by itself, so wrap the work in a try/catch
  that rolls back and rethrows. Work done through it evicts no `[Cacheable]` cache. A single type's
  batch belongs in `SaveAsync(objs, token)` instead, which has neither problem.

## Behaviour changes

These came in with save validation and the parent index.

- **Every save validates.** Only an entity's own `Save` and `SaveAsync` ran `Validate()`, so a save
  through `IObjectDataManager.SaveAsync`, the manager's `Save` or `BulkSave`, or a context's `Save`,
  `SaveAsync` or `BulkSave` wrote an invalid object without a word. Every path now validates each
  object once, before anything is written, and throws as an entity save does:
  - a rule's own exception, as thrown, not wrapped in "Error saving objects";
  - `CoreObjectException` (`UnableToSaveFromChildObject`) for a child saved on its own;
  - `ReadOnlyException` for a read-only object.

  An application that called `Validate()` before a manager save, to get its rules run, can stop:
  the save runs them, and calling it first runs the object's own rules twice.
- **`PreSave()` runs.** It was documented as running before the write but ran on no path. It now
  runs on every save, after validation, on the object and on each new or changed child and
  forceSave link the save writes.
- **A child's `Parent` column is indexed** (`NCI_<table>_Parent`), on every engine. An existing
  database gains the index at its next start, in place, with no rebuild; on SQL Server the build
  blocks writes to that table while it runs, so a large child table is best started out of hours.
  A `[TableIndex]` declared on `Parent` alone is now a duplicate: remove it.
- **A failed commit leaves the objects unsaved.** The manager's `Save`, `SaveAsync` and `BulkSave`
  marked their objects saved and evicted the `[Cacheable]` cache before the transaction committed,
  so a commit that failed left them marked saved with nothing stored. They are now marked saved
  only after the commit, and a failed commit keeps their changes and removals for a retry.
  `SaveAsync` now commits on the asynchronous path.
- **The static API throws what the manager throws.** `CoreObject.Save()` and `Delete()` reach the
  manager by reflection, which wrapped any failure in a `TargetInvocationException`: a sync
  `Delete()` of a type that is not `[Deletable]`, for one. Failures now arrive as thrown.

This came in with the PostgreSQL test pool cap.

- **A PostgreSQL test stack's pool is 20, not 100.** `TFM.Nucleus.Testing` sets it before a test's
  own options, so a test that needs more sets `MaxConnectionPoolSize` itself. A full pool waits for
  a free connection rather than failing. Nothing changes outside the test package.

These came in with the manager interface fix.

- **`IObjectDataManager.DataProvider` is obsolete.** It is the unrooted default provider, so a
  store that opened a context or subscribed through it wrote to the wrong database for a type under
  a rooted provider. Use `CreateContextAsync<T>` and `SubscribeToChangesAsync<T>`. Code that reads
  it gets a compiler warning, which fails a build that treats warnings as errors.
- **`NucleusTestStack.Manager` is an `IObjectDataManager`**, the type a store receives in
  production. A store test that called a member only the concrete class has no longer compiles;
  move the store to the interface member. This is a breaking change to `TFM.Nucleus.Testing` only.
- **`IObjectDataManager` and `IObjectDataAdaptor<T>` gained members** with default bodies that throw
  `NotSupportedException`, so another implementation still compiles. `ObjectDataManager` implements
  them all.

These came in with the audit fixes.

- **`OnAfterLoadFromDB()` runs.** It ran on no load path before, so an override never ran. It now
  runs once per loaded object on every load, children before their parent.
- **`[SuppressLogging]` on a type omits its audit entries.** A save through the manager, which
  always passes a logger, used to audit a suppressed type anyway.
- **A synchronous save of a mixed batch splits within the parameter limit.** A batch whose updates
  went over the limit used to go out as one command; on SQL Server more than about 1,050 changed
  rows with new ones failed.
- **`FindAsync` reads asynchronously**, through the new `LoadByIDAsync`. It used to block the
  calling thread.
- **A NULL column reads as the default of a non-nullable value property.** An `int`, `bool`,
  `DateTimeOffset` or enum property whose column is NULL, which is what a property never set is
  saved as, used to throw `NullReferenceException` on read after a load. It now reads 0, false or
  the type's default, as `RowQuery.Get` does. Declare the property nullable (`int?`) to tell NULL
  from 0.
- **An untyped write of null to a non-nullable value property throws `ArgumentException`.**
  `IPropertySchema.SetValue(obj, null)` on an `int`, `bool`, `DateTimeOffset` or enum property
  used to throw `NullReferenceException`. It now names the property and says to declare it
  nullable. It does not write the default, unlike `PropertyInfo.SetValue`, so a caller's null is
  never saved as 0.

These came in with entity reference predicates.

- **A comparison written value first means what it says.** `3 < x.Quantity` used to render as
  `Quantity < 3`: the sides were swapped and the operator kept, so it selected the opposite range.
  It now renders as `Quantity > 3`. A caller who wrote value-first comparisons was getting the
  wrong rows, and gets the right ones now.
- **A member read through a column throws a clearer error.** `x.Name.Length`, `x.Parent.Name` and
  a comparison of two columns throw `NotSupportedException` (inside the parse's
  `InvalidOperationException`) naming the term. They used to fail too, with the "member access"
  message or an unbound parameter error.

These came in with the child model: declared deletes, removal persistence, child validation,
forceSave and lazy collections.

- **Removing a stored child and saving deletes it, when declared.** It used to leave the row behind
  without a word. Without a declaration, removing a stored child now throws at the call; removing
  a new child is unchanged.
- **Deleting a `[Deletable]` parent cascades to child types with no declaration of their own.** It
  used to throw. A child type that must survive needs `[Deletable(false)]`, and then the schema
  fails to build until one of the two declarations goes.
- **A child type with no declaration under a soft parent is deleted soft**, and its table gains the
  soft-delete columns on the next initialise.
- **The parent's `Validate()` runs the children's.** A child rule that never ran before can now
  reject a save.
- **`[LinkedObject(forceSave: true)]` writes the linked object.** It used to mark the linked
  object's changes committed without writing them, so a later save of it wrote nothing.
- **`[RetrieveOnDemand]` makes a child collection lazy.** It had no effect anywhere before.
- **`[LinkedChildCollection]` and the `LinkedChild*` schema members are obsolete.** The attribute
  only fed validation and notification, so a `NucleusChildCollection` without it was never
  validated and raised `PropertyChanged` with no property name; both now follow the collection.
  The members answer for child collections and go at the next major version.
- **A parent with two collections of one child type fails when the schema is built.** It used to
  fail with a bare `ArgumentException` from a dictionary.
- **A child entity can hold a child collection.** `GetChildCollection` required the parent to be a
  root entity.

These came in with unique `[TableIndex]`.

- **Nothing changes for existing entities.** A `[TableIndex]` without `Unique` is created and compared
  as before, so its `NIX_` index is left alone.
- **The duplicate-key initialisation error names the index.** When a unique index cannot be built
  over existing duplicates, the logged error and the exception now name the index as well as the
  table and its columns, for a column's `UQ_` index too.

These came in with table naming.

- **Nothing is renamed by default.** The default policy is the full name, `RenameFullNameTables` is
  off, and an entity without the new attributes keeps the table it had.
- **Two types with one table name fail at initialisation.** Two types whose full names differ only
  in where the dots fall (`A.B_C` and `A_B.C` both name `tbl_A_B_C`) used to share a table without
  a word. They now fail when the schema is built, naming both; give one a `[DatabaseTable]`.
- **A SQL Server full-text catalog still holding another table's index is kept.** The sync drops
  `ftc_*` catalogs no entity needs, and dropping one that still held a full-text index (an old
  table's, left behind) failed the sync. The catalog is now kept and a warning logged. An index of
  one of the provider's own tables found in another table's catalog (one an adopted table brought
  along) is dropped and recreated in its own.

These came in with row reads and `[TableIndex]`.

- **Indexes named `NIX_*` are managed.** The schema check and sync now read, create and drop
  `NIX_<table>_<name>` indexes as well as `NCI_*` and `UQ_*`, so a hand-made index whose name
  starts `NIX_` on a Nucleus table is dropped unless an entity declares it. The prefix was chosen
  to be unlike the common `IX_`, which stays untouched like every other name.
- **Nothing else changes for existing code.** `Rows<T>` is new, the LINQ adaptor still translates
  only `Where`, and its `CountAsync` still loads the rows it counts; use `Rows<T>(...).CountAsync()`
  for a count in the database.

These came in with the fix for schema updates on large tables.

- **New columns are added in place.** Adding a property used to rebuild its table, copying every
  row in one statement under the driver's 30 second timeout, so a large table failed to migrate
  and the provider would not start. A table that only lacks columns now gets them by
  `ALTER TABLE`, which leaves the rows alone and is instant at any size. A changed column type
  still rebuilds.
- **Schema statements have a timeout of their own.** `SchemaCommandTimeoutSeconds` (900 by
  default, 0 for no limit) covers a rebuild's copy, added columns and index changes, so a genuine
  rebuild or an index build on a large table is no longer cut off at 30 seconds.
- **Missing soft-delete columns are repaired.** A soft-deletable table without `IsDeleted` or
  `DeletedAt` was reported as not current but never changed, and initialise still returned `Ok`.
  The columns are added now. On SQL Server, `IsDeleted` is `NOT NULL` with a default of 0: adding it
  is metadata only on Enterprise, Developer and Azure SQL, but Standard and Express write the
  default into every row, which takes time on a large table.
- **Changing `ColumnName` no longer drops the old column's values.** The new column is added empty
  and the old one is left in the table (see Renaming).

This came in with the keyword column fix.

- **A SQL Server table with a column named like a keyword can be rebuilt.** A property named `By`,
  `Order` or `Key` made a table that was created fine but failed every later rebuild ("Incorrect
  syntax near the keyword 'By'"), so the provider would not start once its entity changed. The
  rebuild's row copy brackets every name now. Nothing changes for other tables.

These came in with the fixes for the save path findings.

- **A cancelled save throws `OperationCanceledException`.** `INucleusRuntime.SaveAsync`, which
  `CoreObject.SaveAsync(CancellationToken)` routes through, dropped its token, so a save could not
  be cancelled. The token now reaches the context. A cancelled save rolls back and throws
  `OperationCanceledException` as it is, where the manager's `SaveAsync` wrapped every failure in
  a plain `Exception`. See the next entry for the other failures that are no longer wrapped.
  `ObjectDataManager` gains
  `SaveAsync(obj, CancellationToken)` and `SaveAsync(objs, CancellationToken)`.
- **A manager save throws typed failures as they are.** `ObjectDataManager.Save` and `SaveAsync`
  wrapped every failure in `Exception("Error saving objects")`, so `catch (UniqueViolationException)`
  never matched them. A `UniqueViolationException`, an `OperationCanceledException` (the synchronous
  `Save` wrapped it too), a `DataProviderException` (with each provider's subclass) and a
  `DataContextException` now reach the caller unwrapped, as they do from a context. Any other
  failure is still wrapped. Code that caught `Exception` still does; code that read
  `InnerException` to find one of these four gets the exception itself instead.
- **A loaded parent saves its child collections on every path.** Only the manager's `SaveAsync` set
  the object's `DataProvider`, which its child collections build their writes through, so a loaded
  parent with a child collection threw through `Save` and every `IDataContext` save. The provider
  now sets it as it builds each write.
- **A batch at exactly `MaxParametersPerQuery` runs as one command**, and a split batch fills each
  command up to the limit. It used to split one parameter early.
- **A save of new and existing objects over the limit no longer throws.** The bulk engine writes
  its inserts and its updates as two writes in the same transaction.

These came in with the fixes for the SQL Server review findings.

- **SQL Server selects read committed data.** `Query.WithNoLock` was inverted: every select ran
  `WITH(NOLOCK)` and read uncommitted rows unless the flag was set, which removed the hint. A
  select now reads committed data, as on PostgreSQL, so it can wait for a row another transaction
  is writing. Set `WithNoLock = true` on a query to read uncommitted rows.
- **`ColumnName` names the column.** It used to reach predicates only, while the table, every
  write and every read used the property name, so a property with a different `ColumnName` was
  filtered on a column that did not exist. A loaded `DataSet` or `StreamingRow` labels its values
  by property name, which differs from the column name only for such a property.
- **`string.Contains` is a substring match unless the column is `FullTextSearch`.** On SQL Server
  it was always `CONTAINS()`, which fails on a column without a full-text index. It is now
  `LIKE '%value%'` on such a column, as on PostgreSQL, and still a full-text search on a
  `FullTextSearch` column.
- **Wildcards in a value match literally.** `StartsWith("50%")` used to match every value
  starting `50` on both providers, because `%`, `_` and (on SQL Server) `[` in the value acted as
  wildcards. A pattern predicate's value is now bound with those characters escaped, so it matches
  only `50%...`.
- **`SetValueOverride` and `ReturnOutputToProperty` work on every column.** Only the built-in
  `Timestamp` was ever computed by the server; on any other column `SetValueOverride` was ignored
  and the client value written, and nothing was read back. Both now apply to every column on both
  providers.

These came in with the shared provider layer.

- **Provider types are internal.** `TFM.Nucleus.Data.SQL` exposes only `AddNucleusSQLDataProvider`,
  `SQLConfiguration` and `SqlDataProviderException`. `SQLDataProvider`, `SqlDataContext`, the query
  classes, the schema classes, `ParameterExtensions`, `ConfigureSQLOptions` and
  `SqlDataContextException` are gone from the public surface; the query and schema model are in
  `TFM.Nucleus.Data.Queries` and `TFM.Nucleus.Data.Schema`. `IDataConnection` and `IDataService`,
  which nothing implemented, are deleted.
- **A unique violation is a `UniqueViolationException`.** It derives from `Exception`, keeps the old
  message and carries the SQL Server exception as its inner exception.
- **A context that fails to open releases its pool slot**, and an async dispose removes the context
  from the pool, as a sync dispose always did. Before, each held a slot or an entry for the life of
  the process.
- **Cancellation propagates as `OperationCanceledException`.** A cancelled read or write used to
  surface wrapped in a plain `Exception` when the driver threw inside the command; it now
  propagates as thrown, after a transactional context is marked for rollback.

These came in with delete by predicate and child collection exclusion. Each fixes a defect, but each
can change what existing code sees.

- **Predicates no longer drop terms.** A bare bool column (`x => x.IsArchived`) is now a condition;
  it used to be ignored and match every row. `x => false` matches nothing; it used to match
  everything. `A && (B || C)` and `(A || B) && C` now group as written. A term that cannot be
  translated throws instead of being dropped. Queries that silently returned too many rows now
  return the right ones.
- **Loads attach child collections on every path.** `LoadWhere`, `LoadByID` and paged loads used to
  discard the child rows they fetched and return empty collections.
- **Deleting a parent removes all of its child rows**, not only the ones loaded in memory.
- **A chained `Where` applies every filter.** Only the last one used to apply.

## Features worth knowing

- **Change tracking.** Every entity keeps committed and pending property dictionaries. Saves write only the columns that actually changed.
- **Schema management.** Tables, indexes and full-text catalogs are created and migrated from the entity attributes. Soft-deletable types automatically gain `IsDeleted` and `DeletedAt` columns.
- **Several databases in one application.** Register one provider per abstract entity root. Each entity routes to the provider of its nearest registered root, and each provider owns its own connection, configuration and schema partition. Graphs that span providers save in separate transactions, so there is no distributed atomicity.
- **Layered cache.** `[Cacheable]` types keep the whole collection plus a Guid index in memory, with single-flight population, a hard TTL, serve-stale-while-revalidate, and eviction on save or delete. Hand-rolled, with no external caching package.
- **Change notifications.** `IObjectDataManager.SubscribeToChangesAsync<T>()` returns a provider-neutral `IChangeSubscription`. The SQL Server implementation uses SqlDependency and Service Broker, which needs `ALTER DATABASE ... SET ENABLE_BROKER` and the `SUBSCRIBE QUERY NOTIFICATIONS` permission. The PostgreSQL implementation LISTENs on a channel each table's trigger notifies, which needs nothing beyond the schema Nucleus creates. SQLite has none.
- **Paging.** `PagedResult<T>` batches the rows and the unpaged count into one round trip. SQL Server pages with a `ROW_NUMBER()` window, which works at every compatibility level; PostgreSQL and SQLite with `LIMIT` and `OFFSET`.
- **Bulk writes.** Large batches route through a temp table, loaded with `SqlBulkCopy` on SQL Server and COPY BINARY on PostgreSQL, then one insert or one changed-column bitmask update. SQLite, which writes in-process, runs them as commands one after another on one transaction.
- **Minimal dependencies.** The Microsoft.Extensions DI, Options and Logging abstractions, plus the provider's ADO.NET driver. JSON uses the in-box `System.Text.Json`.

## Provider seam

Applications code against `TFM.Nucleus.Data` and touch a provider package only to register and configure it, so the same entities run on whichever database a deployment chooses.

`TFM.Nucleus.Data` holds everything that does not depend on the engine:

- **The query model** (`TFM.Nucleus.Data.Queries`): `SelectQuery`, `InsertQuery`, `UpdateQuery` and `DeleteWhereQuery` decide what a query means: its condition tree, order, page, child collection queries, soft-delete filter and the children-first order of a delete cascade. `WhereRenderer` is the one walk of a condition tree; `ParameterBinding` names the parameters an IN term expands to.
- **The schema model** (`TFM.Nucleus.Data.Schema`): `TableSchema` and `ColumnSchema` describe each entity's table, with what each column stores (`ColumnTypeIntent`) and its size. `SchemaManager` compares them with the database and creates tables, adds missing columns in place, rebuilds a table whose column types changed, and syncs indexes.
- **The provider SDK** (`TFM.Nucleus.Data.Providers`): `RelationalDataProvider` and `DbDataContext<TSelf>` are the provider and context over System.Data.Common, with pooling, the transaction tree, reads, deletes, the write paths and failure translation. `BulkWritePlanner` stages bulk writes, `ChangeSubscriptionBase<T>` and `ListenerRefCounter` back change notifications, and `ProviderRegistration` is the registration every provider's `Add...` extensions share. An engine that lets one connection write at a time sets `DbDataContext.AllowsConcurrentWriters` to false, and an oversized write then runs on its own context instead of fanning out; one whose indexes cannot carry included columns sets `ISchemaDialect.SupportsIncludedColumns` to false.

A provider package implements the dialect: an `IQueryRenderer` that writes each query, an `ISchemaDialect` that writes DDL and maps column types, an `ICatalogReader` over the engine's catalog and an `ISchemaStore` that ties them together. It derives its provider and context from the SDK base classes, recognises its engine's unique violations, and exposes only its registration and configuration. Unit tests enforce the boundary: one scans the compiled strings of the neutral and store assemblies for SQL and for provider names, one checks they reference no provider, and one holds each provider's public surface to its registration and configuration.

## Repository layout

```
src\TFM.Nucleus.Abstractions\    contracts, no SQL
src\TFM.Nucleus\                 entity core
src\TFM.Nucleus.Data\            provider-neutral data layer
src\TFM.Nucleus.Data.SQL\        SQL Server provider
src\TFM.Nucleus.Data.PostgreSQL\ PostgreSQL provider
src\TFM.Nucleus.Data.SQLite\     SQLite provider
src\TFM.Nucleus.Testing\         live-database test support
tests\Shared\                    test support linked into both test projects: source paths and the benchmark harness
tools\Run-Benchmarks.ps1         runs every benchmark suite in its own process, and gates or accepts the baselines
tests\TFM.Nucleus.Tests\         unit tests, no database
tests\TFM.Nucleus.IntegrationTests\  live database tests: every suite runs once per provider (SqlServer\, Postgres\ and Sqlite\ hold the subclasses and the engine-only tests)
```

## Build and test

```bash
dotnet build TFM.Nucleus.slnx --configuration Release
dotnet test  TFM.Nucleus.slnx --configuration Release
```

The unit tests need no database and cover golden-SQL snapshots (raw goldens pin every space of the SQL Server output), the public API snapshot of every package, expression translation, materialiser equivalence, the cache, multi-provider routing and the provider-seam tripwires. The two snapshots regenerate only when `NUCLEUS_ACCEPT_GOLDENS=1` is set, so a change to either is a reviewed diff. The integration tests run every suite once per provider. The SQL Server suites activate when `NUCLEUS_TEST_SERVER`, `NUCLEUS_TEST_DATABASE`, `NUCLEUS_TEST_USERNAME` and `NUCLEUS_TEST_PASSWORD` (or the matching `Nucleus:*` user secrets) point at a disposable test database; the PostgreSQL suites when `NUCLEUS_PG_TEST_SERVER`, `NUCLEUS_PG_TEST_DATABASE`, `NUCLEUS_PG_TEST_USERNAME` and `NUCLEUS_PG_TEST_PASSWORD` (or `Nucleus:Postgres:*`) do; the SQLite suites when `NUCLEUS_SQLITE_TEST_DATABASE` (or `Nucleus:Sqlite:Database`) names a file path, which the provider creates; each .NET runtime gets its own file beside it (`nucleus.net8.db`, `nucleus.net10.db`), so the two framework runs never wait on each other's write lock. A provider without settings skips cleanly. A disposable PostgreSQL is one command away:

```bash
docker run -d --name nucleus-pg -e POSTGRES_USER=nucleus -e POSTGRES_PASSWORD=<password> -p 15432:5432 postgres:16
```

with `Nucleus:Postgres:Server` set to `localhost:15432`; the provider creates the database on the first run.

### Benchmarks

The benchmarks catch a regression on the save and read paths. They are opt-in: every benchmark skips unless `NUCLEUS_BENCHMARK=1` is set, so a normal `dotnet test` and CI never run them. A full run takes about 20 minutes, so run them for an architectural change (the save or load pipeline, the query builders, the schema model, the provider SDK, the entity base), not for each feature or bug fix. Each scenario's allocations and median time are compared with a committed baseline. Allocations are gated: they repeat to within about 1% from run to run, so a scenario fails when it allocates more than its baseline by more than the tolerance and the floor, and still does when it is measured once more. Time is reported, with its change against the baseline, but never fails: on a development database the same code measured 7 ms and 13 ms in back-to-back runs, so a stored baseline cannot hold it to 20%. Compare times by running the two builds one after the other in one session. A scenario with no baseline fails. Getting better never fails; the table says so and suggests accepting.

| Kind | Where | What it covers | Gated |
|---|---|---|---|
| Database | `tests/TFM.Nucleus.IntegrationTests/Benchmarks`: `SaveBenchmarks` and `ReadBenchmarks`, per provider | Saves (one at a time, a batch, the split sync path, the bulk engine, parent and child graphs inserted, updated, pruned and deleted, three levels, the save builder alone) and reads (flat, sync, streamed, paged, by ID, graphs with and without children, lazy collections listed, included and read, children read and counted by parent) | Allocations, against `Baselines/<machine>/`; time reported |
| Database-free | `tests/TFM.Nucleus.Tests/Benchmarks/InProcessBenchmarks.cs` | The save graph walk, child validation, the write and delete builders, binding rows to a graph, materialising, translating a predicate | Allocations, against `Baselines/any/`; time reported, against `Baselines/<machine>/` |

A baseline file is named `<suite>.<provider>.<framework>.<configuration>.json` (`inprocess.<framework>.<configuration>.json` for the database-free kind). The database baselines are filed per machine, because a driver's allocations and every time depend on the machine and its database; the database-free allocations are deterministic and are filed once for every machine. Allocations fail above 10% and 4 KB; a time is flagged in the table above 20% and 2 ms (`NUCLEUS_BENCHMARK_TOLERANCE` changes the percentage). Allocations are counted across the whole process, so only one benchmark measures at a time on a machine, whatever the process or provider, and a test stack boots only between measurements. Before measuring, a database suite deletes any rows a stopped run left and compacts the tables (`VACUUM` on PostgreSQL and SQLite), so a scan reads the same pages each time.

A database suite allocates 15-20% more for the same work after another suite's heavy work in the same process, so each database suite runs in a test process of its own. `tools/Run-Benchmarks.ps1` does that: it builds Release, then runs the database-free suite and every provider's Save and Read suite, each in its own process, for each framework, and exits non-zero if any fails. A second database suite in one process skips, naming the script, rather than measuring wrong.

```powershell
# Run and gate every benchmark.
./tools/Run-Benchmarks.ps1

# One provider, one suite, one framework.
./tools/Run-Benchmarks.ps1 -Provider Sqlite -Suite Save -Framework net10.0

# Write the baselines instead, after a change whose cost is deliberate. Review the diff and give
# the reason in the commit message.
./tools/Run-Benchmarks.ps1 -Accept
```

A new machine needs its own database baselines before its runs can pass: accept once on it. Benchmarks measure Release builds only, because Debug code allocates differently.

## Documentation

- [docs/conventions.md](docs/conventions.md) - code and repository conventions
- [docs/versioning.md](docs/versioning.md) - the version scheme
- [docs/publishing.md](docs/publishing.md) - feeds, credentials and releases
- [docs/code-review-findings.md](docs/code-review-findings.md) - known defects and cleanup backlog

## Licence

MIT. See [LICENSE.txt](LICENSE.txt).
