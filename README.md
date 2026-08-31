# Nucleus

Nucleus is a custom entity framework (ORM) for .NET by Two Fat Monkeys. Domain objects derive from a common `CoreObject` base; the framework discovers their schema from reflection and attributes, tracks property changes, translates LINQ expressions into parameterised queries, materialises objects from result sets, and creates and migrates the physical database schema automatically.

This repository holds the whole family. Four packages ship from it, all on one version.

## Packages

| Package | Role |
|---|---|
| `TFM.Nucleus.Abstractions` | Contracts: `ICoreObject`, `IDataProvider` and `IDataContext`, the neutral query model (`IQuery`, `IQueryParameter`, `Operator`), schema interfaces, the `DataSet` row buffer, `NucleusCollection` and `NucleusChildCollection`, `PagedResult<T>`, `IChangeSubscription`, `INucleusRuntime`, `NucleusOptions`. Zero SQL. |
| `TFM.Nucleus` | The core: `CoreObject` entity base classes, attribute-driven schema reflection (`ObjectSchema` and `PropertySchema` with IL-emitted materialisers), change tracking, the static convenience API, `NucleusLoggerProvider`, and DI registration. |
| `TFM.Nucleus.Data` | Provider-neutral data layer: `ObjectDataManager` (save, load and delete orchestration, cascade, child-graph hydration), `ObjectDataAdaptor<T>` (a LINQ provider with async operators), `ExpressionParser`, `DataProviderRegistry` for multi-provider routing, `NucleusCache`. Contains no SQL, proven by a tripwire test. |
| `TFM.Nucleus.Data.SQL` | SQL Server provider: query rendering, connection and transaction context trees, the bulk engine, schema create, compare and migrate, and SqlDependency change notifications. |

Dependency graph: `Abstractions` <- (`Nucleus`, `Data`) <- `Data.SQL`. The core and neutral layers never reference a concrete provider, so a PostgreSQL or MySQL provider plugs into the same seam.

## Install

The packages are published to GitHub Packages. Add the feed once, then reference the provider package: it pulls the other three transitively.

```bash
dotnet add package TFM.Nucleus.Data.SQL
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

`InitializeDataProviderAsync` connects, then creates or migrates the database, tables, indexes and full-text catalogs to match your entity classes. Set `NucleusOptions.ReadOnlySchema` to `true` to make it verify without writing.

Define entities. Properties are backed by the framework's change-tracking dictionaries through `GetValue`/`SetValue` rather than by fields:

```csharp
[Cacheable(refreshInterval: 120, cacheLifetime: 900)]
public class Customer : CoreObject<Customer>
{
    [DatabaseColumn(NonClusteredIndex = true)]
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
```

## Entity base classes

| Base class | Use for |
|---|---|
| `CoreObject<T>` | A normal root entity. `T` is the entity's own type, which gives the static API its generic constraint. |
| `CoreChildObject<T, TParent>` | A child entity that always belongs to a parent. Exposes `Parent` and participates in cascade saves and deletes. |
| `CoreChildObject<TParent>` | The non self-referencing form of the above. |

Every entity gets `ID` (a `Guid` primary key, assigned on construction) and `Timestamp` (a `DateTimeOffset` set by the server) for free.

## Attribute reference

### Column mapping

`[DatabaseColumn]` marks a property as persisted. Without it, a property is in-memory only. The column name defaults to the property name through `[CallerMemberName]`, so `[DatabaseColumn]` on its own is the common case.

Constructors: `()`, `(DataType)`, `(int? dataLength)`, `(DataType, int? dataLength)`, each with an optional trailing column name.

| Field | Default | Meaning |
|---|---|---|
| `ColumnName` | property name | Physical column name. |
| `DataType` | `NotSpecified` | One of `NotSpecified`, `Unicode`, `Date`, `Integer`, `Binary`, `LargeBinary`. `NotSpecified` infers the type from the CLR property type. |
| `DataLength` | inferred | Column length. `-1` maps to `max`. |
| `DecimalPlaces` | none | Scale for decimal columns. |
| `PrimaryKey` | `false` | Marks the primary key. `ID` already is one. |
| `Unique` | `false` | Adds a unique constraint. |
| `NonClusteredIndex` | `false` | Adds a non-clustered index on the column. |
| `IndexSortDESC` | `false` | Sorts that index descending. |
| `FullTextIndex` | `false` | Includes the column in the table's full-text index, which enables `CONTAINS` predicates. |
| `Filestream` | `false` | Stores a `byte[]` column as FILESTREAM. |
| `SetValueOverride` | `NULL` | Has the server compute the value on write, for example a server timestamp, instead of sending the client value. |
| `ReturnOutputToProperty` | `false` | Reads the server-computed value back into the property after the write. |

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
| `BulkSave<TObject>(IEnumerable<TObject>)` | `void` | Routes large batches through the provider's bulk engine. |

Instance members on every entity:

| Member | Notes |
|---|---|
| `Save()`, `SaveAsync()`, `SaveAsync(CancellationToken)` | Runs `Validate()`, then writes only the changed columns. |
| `SaveAsync(IDataContext, CancellationToken)` | Saves inside a caller-owned context and transaction; the caller commits or rolls back. |
| `Delete()`, `DeleteAsync(CancellationToken)` | `[Deletable]` types only. Cascades to loaded child collections when every child type is also deletable, and all guards are checked before anything executes. |
| `IsNew`, `IsReadOnly`, `HasChanges`, `DataChanged` | Change-tracking state. |
| `ToJson()`, `DisplayName` | Serialisation and display helpers. |

Equality, `GetHashCode` and the explicit conversion to `Guid` are all based on `ID`.

## Lifecycle hooks

Override these on an entity to run logic at defined points:

| Hook | When it runs |
|---|---|
| `Validate()` | At the start of every save. Throw to reject the save. |
| `PreSave()` | Before the write is issued. |
| `OnAfterLoadFromDB()` | After the entity is materialised from a result set. |
| `OnAfterInitializeDB()` | After schema initialisation for the type. |

## Async query operators

`ObjectDataAdaptor<T>` is a real LINQ provider, so the usual operators compose and execute server-side. `Where` is declared on the adaptor itself rather than coming from `Queryable`, because the adaptor is both an `IQueryable<T>` and an `IAsyncEnumerable<T>` and .NET 10 would otherwise make an unqualified call ambiguous. The async operators live in `TFM.Nucleus.Data`: `ToListAsync`, `FirstOrDefaultAsync`, `SingleOrDefaultAsync`, `AnyAsync`, `CountAsync`, `LongCountAsync`, each with an optional predicate and cancellation token, plus `ToPagedResultAsync(skip, take, cancellationToken)` which returns rows and the total count in a single round trip.

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

### NucleusOptions

| Option | Default | Meaning |
|---|---|---|
| `ReadOnlySchema` | `true` | When true the schema is verified but never written. The parameterless `AddNucleus()` sets it false. |
| `AppName` | entry assembly name | Reported to SQL Server as the application name. |
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
| `MaxThreadsForLargeQuery` | `12` | Parallelism for large reads. |
| `MaxParametersPerQuery` | `2000` | Batches are split at this many parameters. |
| `CommandTimeoutSeconds` | `10` | Per-command timeout. |
| `BulkCopyTimeoutSeconds` | `60` | Timeout for bulk operations. |
| `BulkCopyBatchSize` | `50000` | Rows per bulk copy batch. |

Connections are built with `TrustServerCertificate`, MARS enabled, a 3 second connect timeout and 4 connect retries.

## Features worth knowing

- **Change tracking.** Every entity keeps committed and pending property dictionaries. Saves write only the columns that actually changed.
- **Schema management.** Tables, indexes and full-text catalogs are created and migrated from the entity attributes. Soft-deletable types automatically gain `IsDeleted` and `DeletedAt` columns.
- **Several databases in one application.** Register one provider per abstract entity root. Each entity routes to the provider of its nearest registered root, and each provider owns its own connection, configuration and schema partition. Graphs that span providers save in separate transactions, so there is no distributed atomicity.
- **Layered cache.** `[Cacheable]` types keep the whole collection plus a Guid index in memory, with single-flight population, a hard TTL, serve-stale-while-revalidate, and eviction on save or delete. Hand-rolled, with no external caching package.
- **Change notifications.** `IDataProvider.SubscribeToChangesAsync<T>()` returns a provider-neutral `IChangeSubscription`. The SQL Server implementation uses SqlDependency and Service Broker, which needs `ALTER DATABASE ... SET ENABLE_BROKER` and the `SUBSCRIBE QUERY NOTIFICATIONS` permission.
- **Paging.** `PagedResult<T>` batches the rows and a `COUNT_BIG(*)` into one round trip, and `ROW_NUMBER()` window paging works at every SQL Server compatibility level.
- **Bulk writes.** Large batches route through temp-table `SqlBulkCopy` inserts and changed-column bitmask updates.
- **Minimal dependencies.** The Microsoft.Extensions DI, Options and Logging abstractions, plus the provider's ADO.NET driver. JSON uses the in-box `System.Text.Json`.

## Provider seam

All SQL text lives in the provider assembly. The neutral layers speak `IQuery`, `IQueryParameter`, `Operator` and `ServerFunctionKind` tokens, and a unit test scans the compiled strings of the neutral assemblies for SQL to keep it that way. A new provider implements `IDataProvider` and `IDataContext`, renders the neutral model in its own dialect, maps the server-function tokens and reports its `DataProviderCapabilities`. Nothing in the core or data layers changes.

## Repository layout

```
src\TFM.Nucleus.Abstractions\    contracts, no SQL
src\TFM.Nucleus\                 entity core
src\TFM.Nucleus.Data\            provider-neutral data layer
src\TFM.Nucleus.Data.SQL\        SQL Server provider
tests\TFM.Nucleus.Tests\         unit tests, no database
tests\TFM.Nucleus.IntegrationTests\  live SQL Server tests
```

## Build and test

```bash
dotnet build TFM.Nucleus.slnx --configuration Release
dotnet test  TFM.Nucleus.slnx --configuration Release
```

The unit tests need no database and cover golden-SQL snapshots, expression translation, materialiser equivalence, the cache, multi-provider routing and the provider-seam tripwire. The integration tests activate when `NUCLEUS_TEST_SERVER`, `NUCLEUS_TEST_DATABASE`, `NUCLEUS_TEST_USERNAME` and `NUCLEUS_TEST_PASSWORD` (or the matching `Nucleus:*` user secrets) point at a disposable test database, and skip cleanly otherwise.

## Documentation

- [docs/conventions.md](docs/conventions.md) - code and repository conventions
- [docs/versioning.md](docs/versioning.md) - the version scheme
- [docs/publishing.md](docs/publishing.md) - feeds, credentials and releases
- [docs/code-review-findings.md](docs/code-review-findings.md) - known defects and cleanup backlog

## Licence

MIT. See [LICENSE.txt](LICENSE.txt).
