# Nucleus

Nucleus is a custom entity framework (ORM) for .NET by Two Fat Monkeys. Domain objects derive from a common `CoreObject` base; the framework discovers their schema from reflection and attributes, tracks property changes, translates LINQ expressions into parameterised queries, materialises objects from result sets, and creates and migrates the physical database schema automatically.

This repository holds the whole family. Nine packages ship from it, all on one version.

## Packages

| Package | Role |
|---|---|
| `TFM.Nucleus.Abstractions` | Contracts: `ICoreObject`, `IDataProvider` and `IDataContext`, the neutral query model (`IQuery`, `IQueryParameter`, `Operator`), schema interfaces, the `DataSet` row buffer, `NucleusCollection` and `NucleusChildCollection`, `PagedResult<T>`, `IChangeSubscription`, `INucleusRuntime`, `NucleusOptions`. Zero SQL. |
| `TFM.Nucleus` | The core: `CoreObject` entity base classes, attribute-driven schema reflection (`ObjectSchema` and `PropertySchema` with IL-emitted materialisers), change tracking, the static convenience API, `NucleusLoggerProvider`, and DI registration. |
| `TFM.Nucleus.Data` | Provider-neutral data layer: `ObjectDataManager` (save, load and delete orchestration, cascade, child-graph hydration), `ObjectDataAdaptor<T>` (a LINQ provider with async operators), `ExpressionParser`, `DataProviderRegistry` for multi-provider routing, `NucleusCache`. It also holds the query model and the schema model every provider shares, and the provider SDK a provider package builds on (see [Provider seam](#provider-seam)). Contains no SQL, proven by a tripwire test. |
| `TFM.Nucleus.Data.SQL` | SQL Server provider: T-SQL rendering, the SQL Server schema dialect and catalog, the SqlBulkCopy engine and SqlDependency change notifications. Its public surface is its registration (`AddNucleusSQLDataProvider`) and `SQLConfiguration`. |
| `TFM.Nucleus.Data.PostgreSQL` | PostgreSQL provider (PostgreSQL 16 or later, over Npgsql): query rendering, the PostgreSQL schema dialect and catalog, the COPY bulk engine and LISTEN/NOTIFY change notifications. Its public surface is its registration (`AddNucleusPostgresDataProvider`) and `PostgresConfiguration`. See [PostgreSQL](#postgresql). |
| `TFM.Nucleus.Bindery` | Keeps [TFM.Bindery](https://github.com/twofatmonkeys/TFM.Bindery.NET)'s pages in Nucleus tables: `NucleusBinderyStore` implements the CMS's page store with version history, direct lookups and change tokens over four entities. Provider neutral; see [Bindery](#bindery). |
| `TFM.Nucleus.S3Library` | Keeps [TFM.S3Library](https://github.com/twofatmonkeys/TFM.S3Library)'s asset metadata in Nucleus tables: `NucleusS3AssetStore` implements the library's `IS3AssetStore` over three entities, with the version check and the live-path rule enforced by unique indexes. Provider neutral; see [S3Library](#s3library). |
| `TFM.Nucleus.Stencil` | Keeps [TFM.Stencil](https://github.com/twofatmonkeys/TFM.Stencil) template bundles (TFM.Mailroom email templates, TFM.Paperwork PDF templates) in Nucleus tables: `NucleusTemplateStore` implements `ITemplateStore` over two entities, partitioned by collection so every consumer shares one database. Provider neutral; see [Stencil](#stencil). |
| `TFM.Nucleus.Attribution` | Keeps [TFM.Attribution](https://github.com/twofatmonkeys/TFM.Attribution)'s marketing touches and frozen sales in Nucleus tables: `NucleusAttributionStore` implements `IAttributionStore` over three entities, with touch writes that are safe to retry and sale replacement on one transaction. Provider neutral; see [Attribution](#attribution). |

Dependency graph: `Abstractions` <- (`Nucleus`, `Data`) <- (`Data.SQL`, `Data.PostgreSQL`), with `Bindery`, `S3Library`, `Stencil` and `Attribution` beside the providers over the same three. The core and neutral layers never reference a concrete provider, and neither provider references the other, so an application picks its database by picking a provider package, and the Bindery, S3Library, Stencil and Attribution stores work over either.

## Install

The packages are published to GitHub Packages. Add the feed once, then reference the provider package for your database: it pulls the other three transitively.

```bash
dotnet add package TFM.Nucleus.Data.SQL          # SQL Server
dotnet add package TFM.Nucleus.Data.PostgreSQL   # PostgreSQL 16 or later
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

`InitializeDataProviderAsync` connects, then creates or migrates the database, tables, indexes and full-text indexes (and, on PostgreSQL, the change notification triggers) to match your entity classes. Set `NucleusOptions.ReadOnlySchema` to `true` to make it verify without writing.

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
}

[Deletable(DeleteBehavior.Soft)]
public class Order : CoreChildObject<Order, Customer>
{
    [DatabaseColumn]
    public decimal Total { get => GetValue<decimal>(); set => SetValue(value); }
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

// Delete by predicate: no row is loaded, child rows go too, the count comes back
var removed = await CoreObject.DeleteWhereAsync<Customer>(c => c.Name == "Old");
```

## PostgreSQL

`TFM.Nucleus.Data.PostgreSQL` runs the same entities, queries and store packages on PostgreSQL 16
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
rebuild's copy in insertion order, and a column per `[DatabaseColumn]` property. A table whose
columns change is rebuilt in one transaction: PostgreSQL's DDL is transactional, so a failed rebuild
leaves nothing behind.

## Bindery

`TFM.Nucleus.Bindery` is the page store for the TFM.Bindery CMS. It ships four entities
(`BinderyDocument`, holding a page's draft and published JSON in one row; `BinderyPageVersion`,
the snapshots the editor's history panel restores from; `BinderyBlockTemplate`; and
`BinderyChangeToken`, one row bumped on every publish so replicas converge) and
`NucleusBinderyStore`, which implements `IBinderyStore`, `IBinderyVersionStore`,
`IBinderyPageLookup` and `IBinderyChangeTokenSource`.

```csharp
builder.Services.AddNucleus();
builder.Services.AddNucleusData();
builder.Services.AddNucleusSQLDataProvider();
builder.Services.AddBindery<NucleusBinderyStore>(bindery => bindery.Options.AuthorizationPolicy = "Editors");
```

The tables are created and migrated at `InitializeDataProviderAsync` like every other entity, so
`NucleusOptions.ReadOnlySchema` must be false on the first run (`AddNucleus()` sets it so). Publish,
unpublish and delete each run inside one transactional context together with the change token, so
a reader never sees a half-published page. Storage failures surface as Bindery's own
`BinderyException` with the original attached. The store passes `BinderyStoreContract.Cases` from
`TFM.Bindery.Testing` against the integration test database.

## S3Library

`TFM.Nucleus.S3Library` is the metadata store for the TFM.S3Library media library. It ships three
entities (`S3AssetRecord`, one row per asset with the collections as JSON; `S3AssetPreviousPath`,
one row per path an asset used to answer to, so the library's redirect lookup is indexed; and
`S3AssetVersionClaim`, the version gate) and `NucleusS3AssetStore`, which implements
`IS3AssetStore`.

```csharp
builder.Services.AddNucleus();
builder.Services.AddNucleusData();
builder.Services.AddNucleusSQLDataProvider();
builder.Services.AddS3Library<NucleusS3AssetStore>();
```

The tables are created and migrated at `InitializeDataProviderAsync` like every other entity.
Nucleus has no optimistic concurrency and cannot report the rows an update affected, so the store enforces the
contract's two hard rules with unique indexes instead: `S3AssetRecord.LivePath` carries the full
path while a record is live and null once it is soft-deleted, so the filtered unique index on it
is exactly "no two live records share a path"; and every save inserts a claim row for the version
it moves to, in the same transaction as the record write, so two writers racing from one version
collide on the claim index and exactly one commits. The provider reports a broken unique index as
a [`UniqueViolationException`](#unique-violations) naming the entity and column, and the store
turns one on the claim into the library's `S3AssetConcurrencyException`, one on the live path into
`S3AssetConflictException`, and any other storage failure into `S3LibraryException` with the
original attached. Ordering,
paging, the name filter and the count happen in memory over the rows the SQL-side filters select.
The store passes `S3AssetStoreContract.Cases` from `TFM.S3Library.Testing` against the integration
test database.

## Stencil

`TFM.Nucleus.Stencil` is the template store for TFM.Stencil-based document packages: TFM.Mailroom
for email templates and TFM.Paperwork (2.0 and later) for PDF templates. It ships two entities
(`StencilTemplateRecord`, one row per template with its file manifest as JSON, and
`StencilTemplateFile`, one row per file holding its bytes) and `NucleusTemplateStore`, which
implements `ITemplateStore`.

`NucleusTemplateStore` is abstract. Each consumer registers its store by its concrete type, and one
database holds every consumer's templates, so a host writes one sealed subclass per consumer, each
naming its own collection:

```csharp
public sealed class MailroomTemplates(IObjectDataManager data, ILogger<MailroomTemplates>? logger = null)
    : NucleusTemplateStore(data, "mailroom", logger);

public sealed class PaperworkTemplates(IObjectDataManager data, ILogger<PaperworkTemplates>? logger = null)
    : NucleusTemplateStore(data, "paperwork", logger);

builder.Services.AddNucleus();
builder.Services.AddNucleusData();
builder.Services.AddNucleusSQLDataProvider();
builder.Services.AddMailroom<MailroomTemplates>();
builder.Services.AddTfmPaperwork<PaperworkTemplates>();
```

The tables are created and migrated at `InitializeDataProviderAsync` like every other entity. The
collection partitions them: the same template key in two collections is two templates, and a
listing only ever sees its own collection. A collection name is compared case-insensitively and
cannot contain `:` or `|`.

Keys and paths match case-insensitively, as in Stencil's reference store: both unique keys
(`{collection}:{key}` on the record, `{recordKey}|{path}` on each file) are stored lowercased, so
the rule holds under any collation, while the template's key and each file's path read back with
the casing they were saved with. Listings and counts read record rows only, never file content;
the name filter, ordering and paging happen in memory. Deletes run by predicate, so removing a
template never loads its bytes, and they sweep by key, so content the manifest never listed goes
with the template. Stencil defines no exception type of its own, so a storage failure is logged
with the operation and key and rethrown unchanged.

The store passes `TemplateStoreContract.Cases` from `TFM.Stencil.Testing` against the integration
test database.

## Attribution

`TFM.Nucleus.Attribution` is the store for TFM.Attribution, which records where each visit came
from (utm tags, click ids, referrer) and freezes each sale's share of the credit. It ships three
entities (`AttributionTouchRecord`, one row per touch; `AttributionSaleRecord`, one row per sale;
`AttributionShareRecord`, one row per share of a sale) and `NucleusAttributionStore`, which
implements `IAttributionStore`:

```csharp
builder.Services.AddNucleus();
builder.Services.AddNucleusData();
builder.Services.AddNucleusSQLDataProvider();
builder.Services.AddAttribution<NucleusAttributionStore>(builder.Configuration);
```

The tables are created and migrated at `InitializeDataProviderAsync` like every other entity. Every
column of the library's `AttributionTouch` has a column of its own, sized by the library's
`MaxLength` constants, and values are clipped to them on the way in. The touch's channel and
origin, and a share's position, are stored by name, as the library asks: a rename is a data
migration. The library's own ids live in `TouchId` and `ShareId`, because a Nucleus `ID` cannot be
set; `Weight` keeps six places (`[DatabaseColumn(Scale = 6)]`).

Only the library's background worker calls the store, and it retries an attempt that fails or runs
past its timeout. `AddTouchesAsync` is therefore safe to repeat: it skips any touch id already
stored and inserts the rest in one transaction, and the unique index on `TouchId` turns two
attempts racing each other into a failure the worker retries. `ReplaceSaleAsync` deletes the sale
and its shares by predicate and inserts the new record on one transactional context, so a reader
sees the old record or the new one. `GetSaleAsync` reads a frozen sale back for the host's reports;
it is not part of the library's contract. Session id lookups are chunked into IN lists of 1,000.
A storage failure is logged with the operation and rethrown unchanged, and the worker decides
whether to retry. The touch table is hard deletable so a host can purge old touches, or a backfill
run, by predicate; the store itself never deletes one.

TFM.Attribution ships no contract test package, so the store's rules are tested directly against
the integration test database (`tests\TFM.Nucleus.IntegrationTests\Attribution`).

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
| `ReturnOutputToProperty` | `false` | Reads the value the server stored back into the property, in the same statement as the write (`OUTPUT` on SQL Server, `RETURNING` on PostgreSQL), on every write path including the bulk engine. A write that reads back runs as a reader rather than a non-query, which costs batched and bulk saves measurably (see `Timestamp` above). On SQL Server a table with an enabled trigger refuses `OUTPUT` (error 334), and the write fails with a message saying so. |

**Renaming.** To rename a property and keep its data, rename the property and set `ColumnName` to the old name: the table does not change. Changing `ColumnName` on a table that already has rows is different. The schema sync sees a missing column and rebuilds the table, copying only the columns whose names the old and new tables share, so **the values in the old column are lost**. Rename the column in the database yourself before deploying the new name. Two properties may not map to one column (names are compared without regard to case), `ID` and `Timestamp` keep their names, and no property may take `ClusterID`, or `IsDeleted` or `DeletedAt` on a soft-deletable type. Each of these fails at provider initialisation.

Each property maps to a column type by its CLR type, or by `DataType` when one is given:

| Property | SQL Server | PostgreSQL |
|---|---|---|
| `string` | `varchar(255)`, or the given length; `-1` is `varchar(max)` | `varchar(255)`, or the given length; `-1` is `text` |
| `string` with `DataType.Unicode` | `nvarchar(510)`, or the given length | `varchar(510)`, or the given length |
| `int`, any enum (stored as its integer) | `int` | `integer` |
| `long` | `bigint` | `bigint` |
| `double` | `float` | `double precision` |
| `bool` | `bit` | `boolean` |
| `decimal` | `decimal(15,5)`, or as `Scale` and `DataLength` give | `numeric(15,5)`, the same way |
| `DateTime` | `datetime2(7)` | `timestamp` |
| `DateTime` with `DataType.Date` | `date` | `date` |
| `DateTimeOffset` | `datetimeoffset(7)` | `timestamptz` (UTC, microseconds) |
| `Guid`, or an entity stored by its `ID` | `uniqueidentifier` | `uuid` |
| `byte[]` | `varbinary(max)` | `bytea` |
| a type named `TFMJsonString` | `json` | `jsonb` |

The SQL Server names `NonClusteredIndex`, `IndexSortDESC`, `FullTextIndex` and `Filestream`, and `eServerFunction.SYSDATETIME` and `SYSDATETIMEOFFSET`, still work and set the same flags as their neutral replacements, but are marked obsolete. The same holds below the attributes, for a provider author: `IPropertySchema.IsIndexed` and `IsFullTextSearch` replace `IsNonClusteredIndex` and `HasFullTextIndex`, and `ServerFunction.CurrentDateTime` and `CurrentDateTimeOffset` (with the matching `ServerFunctionKind` values) replace `SysDateTime` and `SysDateTimeOffset`. `DataType.Unicode` keeps its name: it means the string holds Unicode text, and a provider whose strings are always Unicode treats it like any string.

`Unique` creates a filtered unique index named `UQ_<table>_<column>` with `WHERE [column] IS NOT NULL`, so any number of rows may leave the column NULL while every non-null value must be distinct. Setting `Indexed` as well does not add a second index, and `IndexDescending` applies to whichever index is created. A write that breaks the index throws a [`UniqueViolationException`](#unique-violations). On SQL Server the index is nonclustered, and because it is filtered, SQL Server requires `SET QUOTED_IDENTIFIER ON` for every INSERT, UPDATE and DELETE against the table. SqlClient, and therefore Nucleus, sets it on by default, but SQL Agent T-SQL steps, `bcp` without `-q`, `sqlcmd` without `-I` and some legacy ODBC connections do not and will fail with error 1934 until they do.

On initialise the provider compares the `NCI_*` and `UQ_*` indexes in the database with the entity model and drops or creates them to match, without rebuilding the table. Indexes with any other name are never touched. If an existing column is changed to `Unique` and already holds duplicate values, the unique index cannot be created: initialisation fails with a logged error naming the table and column, no rows are modified, and the duplicates must be resolved by hand before initialising again.

### Class-level behaviour

| Attribute | Effect |
|---|---|
| `[Cacheable(refreshInterval, cacheLifetime)]` | Keeps the whole collection plus a Guid index in memory. Both values are in seconds and default to 120 and 900. |
| `[Deletable(DeleteBehavior.Hard)]` | Deletes physically remove rows. |
| `[Deletable(DeleteBehavior.Soft)]` | Deletes flag the row through `IsDeleted` and `DeletedAt` system columns, and reads filter them out unless `IncludeDeleted` is set on the query. |
| `[SuppressDB]` | Excludes the type from schema management entirely. |
| `[InitializePriority(n)]` | Intended to order type initialisation, descending. Parsed but not currently applied. |

Deletion is opt-in. A type without `[Deletable]` cannot be deleted and any attempt throws.

### Relationships

| Attribute | Applies to | Effect |
|---|---|---|
| `[LinkedObject]` | a property holding another entity | Stores the referenced entity's `ID` in this table. Overloads take `forceSave` to cascade the save into the referenced object, and a `linkedIDProperty` name. |
| `[LinkedChildCollection]` | a collection property | Marks a child collection that is hydrated and cascaded as part of the parent's graph. |
| `[ParentObject(parentIDProperty)]` | the `Parent` property of a child entity | Identifies the owning entity. `CoreChildObject` already applies it. |

### Logging and auditing

| Attribute | Effect |
|---|---|
| `[SuppressLogging]` | Omits the type or property from audit summaries. Note it is inherited, so applying it to a base class silences every descendant. |
| `[AbbreviatedLogging]` | Records that the property changed without recording the values, for long or sensitive data. |
| `[LinkWhenLogging]` | Includes the linked object's ID in the audit entry. |
| `[RetrieveOnDemand]` | Intended for lazy per-property loading. Not currently implemented. |

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
| `DeleteWhere<T>(Expression<Func<T, bool>>)`, `DeleteWhereAsync<T>(..., CancellationToken)` | `int`, `Task<int>` | Deletes every matching row without loading any, with its child collection rows, in one transaction. Returns the rows of `T` affected. See [Child collections and deleting](#child-collections-and-deleting). |
| `BulkSave<TObject>(IEnumerable<TObject>)` | `void` | Routes large batches through the provider's bulk engine. |

Instance members on every entity:

| Member | Notes |
|---|---|
| `Save()`, `SaveAsync()`, `SaveAsync(CancellationToken)` | Runs `Validate()`, then writes only the changed columns. |
| `SaveAsync(IDataContext, CancellationToken)` | Saves inside a caller-owned context and transaction; the caller commits or rolls back. |
| `Delete()`, `DeleteAsync(CancellationToken)` | `[Deletable]` types only. Removes the object's child collection rows by parent ID in the same statement batch, whether or not they were loaded. Throws before anything executes if a child type is not `[Deletable]` or is stored by another provider. |
| `IsNew`, `IsReadOnly`, `HasChanges`, `DataChanged` | Change-tracking state. |
| `ToJson()`, `DisplayName` | Serialisation and display helpers. |

Equality, `GetHashCode` and the explicit conversion to `Guid` are all based on `ID`.

## Child collections and deleting

A `NucleusChildCollection` property is loaded with its parent on every load path: `LoadWhere`,
`LoadByID`, paged loads, and the async LINQ operators. The child rows arrive in the same round trip
for the non-streaming paths, and loaded children come back unchanged, with `Parent` bound and
nothing staged. When a load has a `Take` or a page, the child query selects the children of the
returned parents only.

**Leaving a collection out.** `Exclude(x => x.Items)` on a query, or the `exclude` selectors on
`LoadByID` and `LoadWhere`, drop that collection's query. Its rows are never queried or
transferred, and the collection comes back with `IsLoaded` false. Reading it (enumerating, `Count`,
the indexer, `Contains`, `Remove`, `Clear`) throws `InvalidOperationException` rather than
answering as if the parent had no children. Adding still works, and added children save with the
parent. `collection.LoadAsync()` fetches the stored children by the parent's ID and merges them by
ID with any added since; `IObjectDataManager.LoadChildCollectionAsync(collection)` does the same
without the ambient runtime. An excluded load never reads from or populates the `[Cacheable]` cache.
Serialising a parent whose collection is unloaded throws for the same reason.

**Deleting by predicate.** `DeleteWhere` and `DeleteWhereAsync` exist on `CoreObject` (static), the
object data manager (each in a transaction of its own, with cache eviction), and `IDataContext`
(in a caller-owned context and transaction, without cache eviction). No row is loaded. The predicate
uses the same translation as `LoadWhere`, with two guards: a predicate that matches every row, such
as `x => true`, throws `ArgumentException`, and one that matches none, such as `x => false`,
returns 0 without a round trip. Soft-deletable types are flagged, and only rows not already
deleted count.

**The cascade.** Deleting a parent, by object or by predicate, deletes its child collection rows at
every depth in one batch: deepest first, each level selecting by its parent's condition, and each
type keeping its own hard or soft behaviour. It no longer depends on which children were loaded.

## Lifecycle hooks

Override these on an entity to run logic at defined points:

| Hook | When it runs |
|---|---|
| `Validate()` | At the start of every save. Throw to reject the save. |
| `PreSave()` | Before the write is issued. |
| `OnAfterLoadFromDB()` | After the entity is materialised from a result set. |
| `OnAfterInitializeDB()` | After schema initialisation for the type. |

## Async query operators

`ObjectDataAdaptor<T>` is a LINQ provider that translates `Where`: every `Where` in a chain applies, combined with AND, and any other operator before a `Where` (`OrderBy`, `Select` and so on) throws `NotSupportedException` rather than being ignored. Order, page and project in memory, or use `ToPagedResultAsync`. `Where` is declared on the adaptor itself rather than coming from `Queryable`, because the adaptor is both an `IQueryable<T>` and an `IAsyncEnumerable<T>` and .NET 10 would otherwise make an unqualified call ambiguous. The async operators live in `TFM.Nucleus.Data`: `ToListAsync`, `FirstOrDefaultAsync`, `SingleOrDefaultAsync`, `AnyAsync`, `CountAsync`, `LongCountAsync`, each with an optional predicate and cancellation token, plus `ToPagedResultAsync(skip, take, cancellationToken)` which returns rows and the total count in a single round trip. An enum column compares directly in a predicate, including a nullable one and an `IN` over a collection of enum values, and is bound as the underlying integer the column stores.

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
| `MaxConnectionPoolSize` | `100` | Connection pool ceiling. |
| `SQLPacketSize` | `1500` | Network packet size. |
| `MaxThreadsForLargeQuery` | `12` | The most parallel contexts an oversized write without a table schema fans out to. |
| `MaxParametersPerQuery` | `2000` | The most parameters one command carries: a batch over it is split, and a larger insert or update batch goes to the bulk engine. |
| `CommandTimeoutSeconds` | `10` | How long an async context waits for a free slot under `MaxConnectionPoolSize`. Commands themselves use the driver's default timeout. |
| `BulkCopyTimeoutSeconds` | `60` | Timeout for bulk copies and bulk statements. |
| `BulkCopyBatchSize` | `50000` | The most staged rows one bulk INSERT or UPDATE statement writes; a larger batch runs as several statements in the same transaction. Must be 1 or more. |

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
| `MaxConnectionPoolSize` | `100` | Connection pool ceiling. |
| `MaxThreadsForLargeQuery` | `12` | As for SQL Server. |
| `MaxParametersPerQuery` | `30000` | The most parameters one command carries (PostgreSQL allows 65,535): a batch over it is split, and a larger insert or update batch goes to the bulk engine. |
| `CommandTimeoutSeconds` | `10` | How long an async context waits for a free slot under `MaxConnectionPoolSize`. |
| `BulkCopyTimeoutSeconds` | `60` | Timeout for each COPY and bulk statement. |

## Unique violations

A write that breaks a unique index throws `UniqueViolationException` (namespace `TFM.Nucleus.Data`, in the Abstractions package), whichever provider stores the data. `EntityType`, `PropertyName` and `ColumnName` name the `[DatabaseColumn(Unique = true)]` property and column whose index was broken, `IndexName` its Nucleus index name, and `Is<T>(name)` tests for one by the property's name or the column's; all of them are null for an index Nucleus does not manage, such as a primary key. The message is the one an ordinary storage failure carries, and the provider's own exception is the inner exception, so code that caught `Exception` before still does.

```csharp
catch (UniqueViolationException ex) when (ex.Is<Customer>(nameof(Customer.Email)))
{
    // another writer took the address first
}
```

## Behaviour changes

These came in with the fixes for the save path findings.

- **A cancelled save throws `OperationCanceledException`.** `INucleusRuntime.SaveAsync`, which
  `CoreObject.SaveAsync(CancellationToken)` routes through, dropped its token, so a save could not
  be cancelled. The token now reaches the context. A cancelled save rolls back and throws
  `OperationCanceledException` as it is, where the manager's `SaveAsync` wrapped every failure in
  a plain `Exception`; other failures are still wrapped. `ObjectDataManager` gains
  `SaveAsync(obj, CancellationToken)` and `SaveAsync(objs, CancellationToken)`.
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
- **Change notifications.** `IDataProvider.SubscribeToChangesAsync<T>()` returns a provider-neutral `IChangeSubscription`. The SQL Server implementation uses SqlDependency and Service Broker, which needs `ALTER DATABASE ... SET ENABLE_BROKER` and the `SUBSCRIBE QUERY NOTIFICATIONS` permission. The PostgreSQL implementation LISTENs on a channel each table's trigger notifies, which needs nothing beyond the schema Nucleus creates.
- **Paging.** `PagedResult<T>` batches the rows and the unpaged count into one round trip. SQL Server pages with a `ROW_NUMBER()` window, which works at every compatibility level; PostgreSQL with `LIMIT` and `OFFSET`.
- **Bulk writes.** Large batches route through a temp table, loaded with `SqlBulkCopy` on SQL Server and COPY BINARY on PostgreSQL, then one insert or one changed-column bitmask update.
- **Minimal dependencies.** The Microsoft.Extensions DI, Options and Logging abstractions, plus the provider's ADO.NET driver. JSON uses the in-box `System.Text.Json`.

## Provider seam

Applications code against `TFM.Nucleus.Data` and touch a provider package only to register and configure it, so the same entities run on whichever database a deployment chooses.

`TFM.Nucleus.Data` holds everything that does not depend on the engine:

- **The query model** (`TFM.Nucleus.Data.Queries`): `SelectQuery`, `InsertQuery`, `UpdateQuery` and `DeleteWhereQuery` decide what a query means: its condition tree, order, page, child collection queries, soft-delete filter and the children-first order of a delete cascade. `WhereRenderer` is the one walk of a condition tree; `ParameterBinding` names the parameters an IN term expands to.
- **The schema model** (`TFM.Nucleus.Data.Schema`): `TableSchema` and `ColumnSchema` describe each entity's table, with what each column stores (`ColumnTypeIntent`) and its size. `SchemaManager` compares them with the database and creates, rebuilds and indexes tables.
- **The provider SDK** (`TFM.Nucleus.Data.Providers`): `RelationalDataProvider` and `DbDataContext<TSelf>` are the provider and context over System.Data.Common, with pooling, the transaction tree, reads, deletes, the write paths and failure translation. `BulkWritePlanner` stages bulk writes, and `ChangeSubscriptionBase<T>` and `ListenerRefCounter` back change notifications.

A provider package implements the dialect: an `IQueryRenderer` that writes each query, an `ISchemaDialect` that writes DDL and maps column types, an `ICatalogReader` over the engine's catalog and an `ISchemaStore` that ties them together. It derives its provider and context from the SDK base classes, recognises its engine's unique violations, and exposes only its registration and configuration. Unit tests enforce the boundary: one scans the compiled strings of the neutral and store assemblies for SQL and for provider names, one checks they reference no provider, and one holds each provider's public surface to its registration and configuration.

## Repository layout

```
src\TFM.Nucleus.Abstractions\    contracts, no SQL
src\TFM.Nucleus\                 entity core
src\TFM.Nucleus.Data\            provider-neutral data layer
src\TFM.Nucleus.Data.SQL\        SQL Server provider
src\TFM.Nucleus.Data.PostgreSQL\ PostgreSQL provider
src\TFM.Nucleus.Bindery\         TFM.Bindery page store
src\TFM.Nucleus.S3Library\       TFM.S3Library asset store
src\TFM.Nucleus.Stencil\          TFM.Stencil template store
src\TFM.Nucleus.Attribution\      TFM.Attribution touch and sale store
tests\TFM.Nucleus.Tests\         unit tests, no database
tests\TFM.Nucleus.IntegrationTests\  live database tests: every suite runs once per provider (SqlServer\ and Postgres\ hold the subclasses and the engine-only tests)
```

## Build and test

```bash
dotnet build TFM.Nucleus.slnx --configuration Release
dotnet test  TFM.Nucleus.slnx --configuration Release
```

The unit tests need no database and cover golden-SQL snapshots (raw goldens pin every space of the SQL Server output), the public API snapshot of every package, expression translation, materialiser equivalence, the cache, multi-provider routing and the provider-seam tripwires. The two snapshots regenerate only when `NUCLEUS_ACCEPT_GOLDENS=1` is set, so a change to either is a reviewed diff. The integration tests run every suite once per provider. The SQL Server suites activate when `NUCLEUS_TEST_SERVER`, `NUCLEUS_TEST_DATABASE`, `NUCLEUS_TEST_USERNAME` and `NUCLEUS_TEST_PASSWORD` (or the matching `Nucleus:*` user secrets) point at a disposable test database; the PostgreSQL suites when `NUCLEUS_PG_TEST_SERVER`, `NUCLEUS_PG_TEST_DATABASE`, `NUCLEUS_PG_TEST_USERNAME` and `NUCLEUS_PG_TEST_PASSWORD` (or `Nucleus:Postgres:*`) do. A provider without settings skips cleanly. A disposable PostgreSQL is one command away:

```bash
docker run -d --name nucleus-pg -e POSTGRES_USER=nucleus -e POSTGRES_PASSWORD=<password> -p 15432:5432 postgres:16
```

with `Nucleus:Postgres:Server` set to `localhost:15432`; the provider creates the database on the first run.

The save benchmarks (`tests/TFM.Nucleus.IntegrationTests/Benchmarks`) time each write path (one object at a time, a batch, the split sync path and the bulk engine) on every configured provider. They skip unless `NUCLEUS_BENCHMARK=1` is set, and print their medians to the test output:

```bash
NUCLEUS_BENCHMARK=1 dotnet test tests/TFM.Nucleus.IntegrationTests --configuration Release --filter "FullyQualifiedName~SaveBenchmarks" --logger "console;verbosity=detailed"
```

## Documentation

- [docs/conventions.md](docs/conventions.md) - code and repository conventions
- [docs/versioning.md](docs/versioning.md) - the version scheme
- [docs/publishing.md](docs/publishing.md) - feeds, credentials and releases
- [docs/code-review-findings.md](docs/code-review-findings.md) - known defects and cleanup backlog

## Licence

MIT. See [LICENSE.txt](LICENSE.txt).
