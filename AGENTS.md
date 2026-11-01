# AGENTS.md

Repo level notes for anyone, human or agent, working in this repo.

## What this repo is

The Nucleus entity framework: a custom ORM for .NET, shipped as nine packages that version and
release together.

| Package | Source | Role |
|---|---|---|
| `TFM.Nucleus.Abstractions` | `src\TFM.Nucleus.Abstractions` | Contracts and the neutral query interfaces. Zero SQL. |
| `TFM.Nucleus` | `src\TFM.Nucleus` | Entity core: `CoreObject`, schema reflection, change tracking. |
| `TFM.Nucleus.Data` | `src\TFM.Nucleus.Data` | Provider-neutral data layer, the shared query and schema model, and the provider SDK. Zero SQL. |
| `TFM.Nucleus.Data.SQL` | `src\TFM.Nucleus.Data.SQL` | SQL Server provider. |
| `TFM.Nucleus.Data.PostgreSQL` | `src\TFM.Nucleus.Data.PostgreSQL` | PostgreSQL provider (PostgreSQL 16 or later, over Npgsql). |
| `TFM.Nucleus.Bindery` | `src\TFM.Nucleus.Bindery` | TFM.Bindery's page store over Nucleus entities. Provider neutral; depends on `TFM.Bindery` from the feed. |
| `TFM.Nucleus.S3Library` | `src\TFM.Nucleus.S3Library` | TFM.S3Library's asset metadata store over Nucleus entities. Provider neutral; depends on `TFM.S3Library.Abstractions` from the feed. |
| `TFM.Nucleus.Stencil` | `src\TFM.Nucleus.Stencil` | TFM.Stencil's template bundle store (used by TFM.Mailroom and TFM.Paperwork) over Nucleus entities. Provider neutral; depends on `TFM.Stencil.Abstractions` from the feed. |
| `TFM.Nucleus.Attribution` | `src\TFM.Nucleus.Attribution` | TFM.Attribution's touch and sale store over Nucleus entities. Provider neutral; depends on `TFM.Attribution` from the feed. |

Until August 2026 these lived in four separate repositories, with the shared tests and the only
real documentation in a fifth unversioned folder (`Nucleus.v2`). They were always released in
lockstep on a single version, so they are now one repo. The old folders are superseded; do not
edit them, and do not reintroduce the conditional package-or-project reference pattern they used.

This is the v2 line. The 1.x code, in its original flat pre-monorepo layout, is preserved on the
`archive/v1` branch and is not maintained.

Built on the TFM NuGet package scaffold. The layout, the MSBuild property split, the version
schema and the workflows are the shared TFM standard and are documented in `docs/`.

## Architecture

Dependency graph: `Abstractions` <- (`Nucleus`, `Data`) <- (`Data.SQL`, `Data.PostgreSQL`), and
`Bindery`, `S3Library`, `Stencil` and `Attribution` sit beside the providers over the same three.
Neither provider references the other. Direction is enforced by project references and by
`ProviderSeamTests`, which also scans the compiled string literals of the neutral and store
assemblies for SQL and provider names; `ProviderSurfaceTests` holds each provider's public surface
to its registration and configuration.

`TFM.Nucleus.Bindery` is the first package here that serves another library: `NucleusBinderyStore`
implements TFM.Bindery's `IBinderyStore` and its three optional capabilities over four
`CoreObject` entities (`BinderyDocument`, `BinderyPageVersion`, `BinderyBlockTemplate`,
`BinderyChangeToken`). The entity and table names are the ones the first consumer site used
before the package existed, so its rows carry over; the store never references `Data.SQL`, and
publish, unpublish and delete run on one `ContextOptions.Transactional` context so the change
token moves with the content. Storage failures are rethrown as `BinderyException` with the
original attached, per Bindery's contract. Tests run the framework-neutral
`BinderyStoreContract.Cases` from `TFM.Bindery.Testing` against the integration database
(`tests\TFM.Nucleus.IntegrationTests\Bindery`).

`TFM.Nucleus.S3Library` is the second: `NucleusS3AssetStore` implements TFM.S3Library's
`IS3AssetStore` over three entities (`S3AssetRecord`, `S3AssetPreviousPath`,
`S3AssetVersionClaim`). Two of the contract's rules are enforced by unique indexes rather than by
code, because Nucleus has no optimistic concurrency and cannot report the rows an update affected. The path rule
(no two live records share a path, a deleted one may) is the filtered unique index on
`S3AssetRecord.LivePath`, a column equal to `FullPath` while the record is live and null once it
is soft-deleted. The version rule is the claim table: every save inserts a row
`{AssetId:N}:{new version}` inside the same transaction as the record write, so two writers
racing from one version collide on `UQ_..._S3AssetVersionClaim_Claim` and exactly one commits;
the previous claim is deleted in the same transaction, leaving one row per asset. Reads run
outside a transaction and writes inside one, because a transactional context closes its
connection after a read. A unique violation is the provider-neutral `UniqueViolationException`
found in the inner-exception chain, matched by the entity and column it names, so the package
knows no engine; the claim or asset-id index means `S3AssetConcurrencyException`, the live-path
index means `S3AssetConflictException`, anything else is rethrown as `S3LibraryException` with
the original attached. Ordering, paging, the name
filter and the count run in memory over the rows the SQL-side equalities select, since the
provider translates none of them. Tests run `S3AssetStoreContract.Cases` from
`TFM.S3Library.Testing` against the integration database
(`tests\TFM.Nucleus.IntegrationTests\S3Library`), each suite under its own cross-process lock.

`TFM.Nucleus.Stencil` is the third: `NucleusTemplateStore` implements TFM.Stencil's
`ITemplateStore`, the one store contract TFM.Mailroom and TFM.Paperwork 2.x both take, over two
entities (`StencilTemplateRecord`, one row per template with the file manifest as JSON, and
`StencilTemplateFile`, one row per file's bytes). The store is abstract and partitioned by a
collection name the subclass passes in, because each consumer registers its store by concrete
type and one database holds every consumer's templates; a host writes one sealed subclass per
consumer. Both unique keys are composites stored lowercased (`RecordKey` is
`{collection}:{key}`, `FileKey` is `{recordKey}|{path}`), so uniqueness and lookups are
case-insensitive under any collation while `Key` and `Path` keep the caller's casing. Files are
tied to their record by `RecordKey`, not as a Nucleus child collection: a child can only be saved
through its parent, and the store writes one file at a time. Deletes run by predicate
(`IDataContext.DeleteWhereAsync`), files first by record key and then the record, so no bytes are
loaded and a file row left behind by a content save that raced a delete is swept too; a file save
is a delete by file key and an insert on one transaction, for the same reason. Reads run outside
a transaction and writes inside one, as in S3Library. Listings read record rows only, and the
name filter, ordering and paging run in memory. Stencil.Abstractions defines no exception type,
so a storage failure is logged with the operation and key and rethrown unchanged, and nothing is
retried. Tests run `TemplateStoreContract.Cases` from `TFM.Stencil.Testing` against the
integration database (`tests\TFM.Nucleus.IntegrationTests\Stencil`); each test works in a
collection of its own, so the suite needs no reset and no lock.

`TFM.Nucleus.Attribution` is the fourth: `NucleusAttributionStore` implements TFM.Attribution's
`IAttributionStore` over three entities (`AttributionTouchRecord`, `AttributionSaleRecord`,
`AttributionShareRecord`). The library's own ids live in `TouchId` and `ShareId` because a Nucleus
`ID` cannot be set. Only the library's worker calls the store and it retries, so a touch batch
first reads which of its ids already exist and inserts only the rest, and the unique `TouchId`
index turns two racing attempts into a failure the worker retries. A sale is replaced by two
predicate deletes and an insert on one transactional context. Enums are stored by name, as the
library documents. Bool, int and decimal columns read as nullable with a default, because a row
written without the column reads back null. The library ships no contract package, so the tests in
`tests\TFM.Nucleus.IntegrationTests\Attribution` cover its documented rules directly, on fresh
ids, with no reset and no lock. The first run of the two frameworks in parallel against a database
without the tables can fail schema initialisation in one of them while the other creates them;
rerun, or run one framework first.

`Abstractions` holds every interface the other layers speak: `ICoreObject`, `IDataProvider`,
`IDataContext`, `IObjectSchema`, `IQuery` and the neutral query tokens, plus `NucleusOptions`, the
`DataSet` row buffer and the collection types. Nothing in it knows what a database is.

`TFM.Nucleus` owns the entity model. `CoreObject` keeps committed and pending property
dictionaries rather than fields, which is what makes change tracking and partial updates possible.
`ObjectSchema` and `PropertySchema` reflect an entity type once and cache it, emitting IL
materialisers that construct entities without running constructors. The static API on `CoreObject`
is a thin forwarder over `NucleusRuntime.Current`, which `AddNucleusData()` publishes.

`TFM.Nucleus.Data` orchestrates, and holds everything a provider shares, so a provider package is
only a dialect. The query model (`Queries`: Select, Insert, Update and DeleteWhere queries, the
condition tree, `WhereRenderer`, `ParameterBinding`, `SoftDelete`) decides what a query means, and
a query carries the provider's `IQueryRenderer`, which only writes it. The schema model (`Schema`:
`TableSchema`, `ColumnSchema` with a `ColumnTypeIntent` and size facets, `SchemaComparer`,
`SchemaManager`) builds, compares, creates and rebuilds tables through a provider's
`ISchemaStore` (an `ISchemaDialect`, an `ICatalogReader` and a rebuild transaction). The provider
SDK (`Providers`: `RelationalDataProvider`, `DbDataContext<TSelf>`, `RelationalProviderOptions`,
`BulkWritePlanner`, `ChangeSubscriptionBase<T>`, `ListenerRefCounter`, `DbSchemaTransaction`)
runs pooling, the transaction tree, reads, deletes, both write paths and failure translation
over System.Data.Common. I/O in the dialect interfaces takes an async flag and returns ValueTask,
so one implementation serves the sync and async paths. A provider turns its engine's unique
violation into `UniqueViolationException` (Abstractions) naming the entity and column.

`ObjectDataManager` implements both `IObjectDataManager` and
`INucleusRuntime`, so the static and instance surfaces share one code path. `ObjectDataAdaptor<T>`
is a real LINQ provider; `ExpressionParser` lowers expression trees to the neutral query model,
unwrapping the `Convert` nodes the compiler inserts around an enum or widened column so the
comparison still reads as a column;
`DataProviderRegistry` routes each entity type to the provider of its nearest registered root,
which is how one application spans several databases. The parser never drops a term: every Where in
a chain is ANDed, boolean constants are folded, an OR always becomes its own group, and anything it
cannot translate throws, because the same translation drives deletes. `ExpressionParser.Classify`
lets callers skip a predicate that matches nothing, and `CheckDeletePredicate` refuses a delete that
matches everything. `ChildGraphBinder` is the one place child collections are materialised and
attached, from either a data set's linked result sets or streamed child queries; it binds each
child's parent as a stored value, so loaded children are never dirty. A load can exclude a child
collection by property name, which drops its query and marks the collection unloaded
(`INucleusChildCollection.IsLoaded`); reading an unloaded collection throws, adding does not.

`TFM.Nucleus.Data.SQL` is SQL Server's dialect over that SDK: `SqlServerQueryRenderer` (every
T-SQL query string), `SqlServerSchemaDialect` and `SqlServerTypeMapper` (every DDL string),
`SqlServerCatalogReader`, the full-text extras, the #temp and SqlBulkCopy bulk engine and
SqlDependency notifications. Everything but its registration, `SQLConfiguration` and
`SqlDataProviderException` is internal. Its output is pinned byte for byte by the raw goldens in
`SqlServerGoldens.cs`. A select batches its child collection queries after its own rows, and
`GetDataSet` reads each into `DataSet.LinkedDataSets`; a child selects by the IDs of exactly the
parent rows returned, page and TOP included, while `StreamAsync` sends the root query alone.
Every delete, by object or by predicate, is a `DeleteWhereQuery`: one batch that deletes child
collection rows deepest first through the parent's condition, then the root, then
`SELECT @@ROWCOUNT`, which is the one place Nucleus reports rows affected. It runs through
`DbDataContext.ExecuteDelete`, never the generic execute path, whose regrouping by table would
detach the subqueries from their parents. The soft-delete rule lives once, in `SoftDelete`.

`TFM.Nucleus.Data.PostgreSQL` is PostgreSQL's dialect over the same SDK: `PostgresQueryRenderer`,
`PostgresSchemaDialect` with `PostgresTypes`, `PostgresCatalogReader` (information_schema and
pg_catalog), the extras (a GIN full-text index per FullTextSearch column, and a statement-level
trigger per table that runs `nucleus_notify()` to `pg_notify` the table's channel), the COPY
BINARY bulk engine and `PostgresChangeSubscription` (one LISTEN connection per subscription).
Identifiers are double-quoted so PascalCase survives; a name over 63 bytes goes through
`IdentifierShortener` (Data) before it is written or compared, or PostgreSQL would silently
truncate it and the schema check would rebuild it on every start. Index, constraint and sequence
names are schema-wide in PostgreSQL, so a rebuild renames the old table's indexes and identity
sequence before it creates the new table. Values are bound as their column's type: a
`DateTimeOffset` as `timestamptz` in UTC (PostgreSQL keeps no offset and nothing below a
microsecond), a `DateTime` as `timestamp`, an enum as its integer, JSON as `jsonb`; a `timestamptz`
reads back as a `DateTimeOffset`. Pattern predicates use `ILIKE ... ESCAPE ''`, equality is exact.
A delete reports rows affected through `WITH affected AS (... RETURNING 1) SELECT count(*)`. A
unique violation is SQLSTATE 23505 with the index in `ConstraintName`. Opening a connection
retries a transient failure (`ConnectRetryCount`). Its output is pinned by `PostgresGoldens.cs`,
built from the same query corpus as the SQL Server goldens.

## Build and test

```powershell
dotnet build TFM.Nucleus.slnx --configuration Release
dotnet test  TFM.Nucleus.slnx --configuration Release --no-build
```

Restore needs `GITHUB_USER` and `GITHUB_TOKEN` in the environment: `nuget.config` reads the
GitHub Packages credential from them, and `TFM.Bindery`, `TFM.Bindery.Testing`,
`TFM.S3Library.Abstractions`, `TFM.S3Library.Testing`, `TFM.Stencil.Abstractions` and
`TFM.Stencil.Testing` come from that feed. Everything else is an
in-repo project reference or comes from nuget.org.

Warnings become errors only when `ContinuousIntegrationBuild=true`. To reproduce what CI enforces:

```powershell
dotnet build TFM.Nucleus.slnx --configuration Release -p:ContinuousIntegrationBuild=true
```

The integration tests run every suite once per provider: each neutral suite is an abstract class
with a sealed subclass per provider under `SqlServer\` and `Postgres\`, next to that engine's own
tests. SQL Server needs a disposable database configured through the `NUCLEUS_TEST_SERVER`,
`NUCLEUS_TEST_DATABASE`, `NUCLEUS_TEST_USERNAME` and `NUCLEUS_TEST_PASSWORD` environment variables
or the matching `Nucleus:*` user secrets; PostgreSQL through `NUCLEUS_PG_TEST_*` or
`Nucleus:Postgres:*` (a `postgres:16` container is enough; the provider creates the database).
A provider with no settings skips cleanly, which is what happens in CI. A fixture whose
initialisation fails includes the stack's warnings and errors in its message. The save benchmarks
under `Benchmarks\` skip unless `NUCLEUS_BENCHMARK=1`; they compare variants interleaved, so a
change that claims no cost on the write path is measured against them rather than asserted.

## Deviations from the scaffold

One, recorded in `tests\Directory.Build.props`: the tests use **xunit.v3 2.0.0**, not the scaffold's
xunit 2.9.3, because they were written against v3. Everything else follows the scaffold, and both
test projects multi-target `net8.0;net10.0` like the shipping packages.

`Directory.Build.props` suppresses one warning, IDE0040, and `tests\Directory.Build.props`
suppresses xUnit1051. Both are mechanical style edits rather than defects; see
`docs\code-review-findings.md`. Nothing else is suppressed, so a nullable warning or an analyzer
warning in new code fails CI.

## Rules

- **Do not put SQL in `Abstractions`, `TFM.Nucleus` or `Data`.** The provider seam is the whole
  reason a second database engine is possible. `ProviderSeamTests` will fail if you do.
- **Consumers code against `Abstractions` and `Data` only.** An application, and every store
  package, touches a provider package only to register and configure it, so a deployment picks
  its database by picking a package. A provider package's public types are its `Add...` extensions,
  its configuration class and the exception its registration throws; everything else is
  `internal`. `ProviderSurfaceTests` holds each provider to that list, so widening it is a
  deliberate review decision.
- **No engine names in neutral or store assemblies.** No provider type name, error number or
  registration name, in code or in a message. `ProviderSeamTests` scans the compiled strings of
  `Abstractions`, `TFM.Nucleus`, `Data` and the store packages for SQL keywords and provider
  names, and checks none of them references a provider or its client. When a store needs to know
  what an engine did (a unique violation, say), add a neutral type to `Abstractions` or `Data` and
  have every provider produce it.
- **Shared logic lives once, in `Data`.** A provider implements the dialect interfaces and derives
  from the SDK base classes; it never re-implements a query rule, a tree walk, the schema compare
  or the execution paths. If a second provider needs something the first one has privately, move
  it into `Data` behind a neutral name, not into the second provider as a copy.
- **Name new attribute and server-function vocabulary by meaning, not by an engine feature.**
  `Indexed`, not `NonClusteredIndex`; `CurrentDateTimeOffset`, not `SYSDATETIMEOFFSET`. The old
  SQL Server names stay, obsolete, because entities in production use them.
- **Change the SQL Server goldens or the API snapshot deliberately.** `SqlServerGoldens.cs` and
  `PublicApiSnapshot.cs` are written only by their accept tests, with `NUCLEUS_ACCEPT_GOLDENS=1`;
  the diff is the review.
- **Do not move a shared property into a csproj.** The three level props split (repo, `src`,
  `tests`) is the standard. A csproj carries package identity, description, tags, dependencies and
  nothing else.
- **Do not hardcode a version anywhere.** `VersionMajor` in `Directory.Build.props` is the only
  version value in the repo. CI supplies the rest. See `docs/versioning.md`. The nine packages
  share one version by design. `TFM.Nucleus.Bindery` additionally pins a `TFM.Bindery` version,
  `TFM.Nucleus.S3Library` a `TFM.S3Library.Abstractions` version, `TFM.Nucleus.Stencil` a
  `TFM.Stencil.Abstractions` version and `TFM.Nucleus.Attribution` a `TFM.Attribution` version (two identical per-TFM lines in each csproj); a release of
  any of those libraries that changes its store contract means a bump here.
  The integration test project pins the matching `*.Testing` package at the same version as the
  store's contract pin, or NuGet would lift the contract package underneath the store silently.
- **Pin package references per target framework**, 8.0.x for `net8.0` and 10.0.x for `net10.0`, so
  consumers get assemblies built for their runtime rather than a lowest common denominator.
- **Keep the workflow files free of configuration.** They are byte identical in every TFM package
  repo and read what they need from `Directory.Build.props` at run time (`VersionMajor`,
  `PublishToNuGetOrg`, `TargetFrameworks`). If something seems to need a knob in the workflow, add
  the property to `Directory.Build.props` and read it instead.
- **Evolve the public surface additively.** Two production sites already consume these packages,
  and `CoreObject` is the base class of every entity in them. A breaking change to the entity base,
  an attribute, or a registration method is a major version and a coordinated release.
- **Do not add a `NoWarn` entry to hide new code.** The existing list covers code that predates the
  scaffold. New code compiles clean.
- **Do not name an entity column `IsDeleted`.** Nucleus reserves it for its own soft-delete
  behaviour; a library flag with that meaning takes another name (`S3AssetRecord.SoftDeleted`).
- **Update `README.md` in the same commit** as any change to the public surface. It is packed into
  all nine packages and is the reference documentation consumers see on the feed.
- **No em dashes in authored documentation.** Use hyphens.
