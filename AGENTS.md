# AGENTS.md

Repo level notes for anyone, human or agent, working in this repo.

## What this repo is

The Nucleus entity framework: a custom ORM for .NET, shipped as seven packages that version and
release together.

| Package | Source | Role |
|---|---|---|
| `TFM.Nucleus.Abstractions` | `src\TFM.Nucleus.Abstractions` | Contracts and the neutral query interfaces. Zero SQL. |
| `TFM.Nucleus` | `src\TFM.Nucleus` | Entity core: `CoreObject`, schema reflection, change tracking. |
| `TFM.Nucleus.Data` | `src\TFM.Nucleus.Data` | Provider-neutral data layer, the shared query and schema model, and the provider SDK. Zero SQL. |
| `TFM.Nucleus.Data.SQL` | `src\TFM.Nucleus.Data.SQL` | SQL Server provider. |
| `TFM.Nucleus.Data.PostgreSQL` | `src\TFM.Nucleus.Data.PostgreSQL` | PostgreSQL provider (PostgreSQL 16 or later, over Npgsql). |
| `TFM.Nucleus.Data.SQLite` | `src\TFM.Nucleus.Data.SQLite` | SQLite provider for database files (SQLite 3.35 or later, bundled, over Microsoft.Data.Sqlite). |
| `TFM.Nucleus.Testing` | `src\TFM.Nucleus.Testing` | Live-database test support, framework neutral: test database settings, a booted stack per provider, cross-process locks, raw setup statements. What this repo's integration tests and every store repo's tests run on. |

Until August 2026 these lived in four separate repositories, with the shared tests and the only
real documentation in a fifth unversioned folder (`Nucleus.v2`). They were always released in
lockstep on a single version, so they are now one repo. The old folders are superseded; do not
edit them, and do not reintroduce the conditional package-or-project reference pattern they used.

This is the v2 line. The 1.x code, in its original flat pre-monorepo layout, is preserved on the
`archive/v1` branch and is not maintained.

Built on the TFM NuGet package scaffold. The layout, the MSBuild property split, the version
schema and the workflows are the shared TFM standard and are documented in `docs/`.

## Architecture

Dependency graph: `Abstractions` <- (`Nucleus`, `Data`) <- (`Data.SQL`, `Data.PostgreSQL`,
`Data.SQLite`), and `Testing` sits above the providers: it registers them and holds the raw SQL a
test sets a table up with, so it is deliberately outside `ProviderSeamTests`, like a provider.
No provider references another. Direction is enforced by project references and by
`ProviderSeamTests`, which also scans the compiled string literals of the three neutral
assemblies for SQL and provider names; `ProviderSurfaceTests` holds each provider's public surface
to its registration and configuration.

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
`SchemaManager`) builds, compares, creates and updates tables through a provider's
`ISchemaStore` (an `ISchemaDialect`, an `ICatalogReader` and a rebuild transaction). A table that
only lacks columns has them added in place (`ISchemaDialect.AddColumns`, decided by
`SchemaComparer.PlanUpdate`); only a changed column type rebuilds and copies the rows, because a
rebuild takes time in proportion to the table. Schema statements run under
`SchemaCommandTimeoutSeconds`, not the driver default. A table's name is resolved once, in
`TableSchema.LogicalName`: `[DatabaseTable]` (not inherited), then the type's `[TableNaming]`
(inherited), then the provider's `RelationalProviderOptions.TableNaming` (`TableNamingPolicy`,
full name by default), which reaches `SchemaManager` through `ISchemaStore` like `ReadOnlySchema`;
everything else reads `TableSchema.TableName`. `BuildSchema` refuses two types with one table name
and an old name that is another type's table. A missing table with exactly one old table present
(`[LegacyTableNames]`, or its full-name form when `RenameFullNameTables` is set) is renamed by
`ISchemaDialect.AdoptTable` inside the schema transaction, compared again and updated like any
existing table (the catalog is read for it before the transaction runs anything, because on SQL
Server the catalog reader waits on the transaction's own key drops); several old tables fail the update rather than being merged. The provider
SDK (`Providers`: `RelationalDataProvider`, `DbDataContext<TSelf>`, `RelationalProviderOptions`,
`BulkWritePlanner`, `ChangeSubscriptionBase<T>`, `ListenerRefCounter`, `DbSchemaTransaction`)
runs pooling, the transaction tree, reads, deletes, both write paths and failure translation
over System.Data.Common; `ProviderRegistration` is the registration every provider's `Add...`
extensions share, so a provider's extensions only name its types. `MaxConnectionPoolSize` is the
one connection limit: a provider's `ConnectionSlots` (one per provider, not per context type)
counts its root contexts and live change subscriptions, and every connection to the database has
one connection string with `Enlist=false` and the driver pool sized to the setting, raw strings
included, so the driver keeps one pool. A budget with no slot free is a `DatabaseException` of kind
`PoolExhausted`, never transient, because the caller is told the database could not serve it,
not that a context was misused. The startup connections (`master`, the PostgreSQL
maintenance database) are unpooled. With nothing enlisting, a transactional root opened inside an
ambient `TransactionScope` throws `AmbientTransaction` rather than commit outside it.
`ConnectionOpener` gives every attempt of an open one budget, the connection's own connect
timeout, and retries an open `ConnectAttempts` times (a shared option) when the provider's
`ClassifyFailure` calls the failure transient. Every open goes through it: a context's, a change
subscription's and a catalog reader's. The defaults fail fast and alike: nothing waits
more than 3 s (pool wait, connect, SQLite busy timeout). Every statement a context runs gets
`StatementTimeoutSeconds` (30, the drivers' default), or its `ContextOptions.StatementTimeoutSeconds`,
set as each command is prepared, because the command is shared and the bulk engines and schema
statements set their own on it; SQLite's command timeout is its busy wait, so its context opts out
(`DbDataContext.AppliesStatementTimeout`) for statements and schema statements alike, and its
catalog reader sets none. A transactional context is
committed by `Commit`/`CommitAsync`, which on a root commits at once; any failure of its own
operations (a statement, a validation rule, a query it cannot build) marks the tree for rollback
through `DbDataContext.Doom`, an exception filter, so the success path pays nothing. A root's
`Rollback`/`RollbackAsync` rolls back at once, as its commit commits at once: the transaction
ends, its locks are released and the undo log is reverted, with the context still open. A child's
rollback ends the whole tree's transaction the same way, and a commit of any context of the tree
after it throws `TransactionDoomed`. `TryRollback` is the catch block's form and never throws. The failure paths
only mark (`MarkForRollback`), because an exception filter must not run a command. A commit or
rollback given a cancelled token rolls back, before it throws. A context
disposed without a commit rolls back under `RelationalProviderOptions.RequireCommit` and
otherwise still commits with a warning (until v3, which makes rolling back the only behaviour).
Every Nucleus caller commits explicitly. A child (`ContextOptions.Parent`) runs on its root's
connection and transaction, so a tree is one transaction; it only votes with its own commit. An
oversized write the bulk engine cannot take runs its parts in order on its own context, on every
engine: there is no parallel write path, because parts on connections of their own could not
commit as one. A read leaves the connection open, because closing it with the reader ended the
transaction. A dialect whose indexes carry no
included columns sets `ISchemaDialect.SupportsIncludedColumns` false: its `[TableIndex]` indexes
are created and compared by their keys alone. I/O in the dialect interfaces takes an async flag and returns ValueTask,
so one implementation serves the sync and async paths. A provider turns its engine's unique
violation into `UniqueViolationException` (Abstractions) naming the entity and the column or the
`[TableIndex]` (`IsIndex<T>`); `UniqueIndexResolver` (Data) maps the engine's index name back to
the rule once, on the failure path only. A broken foreign key is a `ForeignKeyViolationException`
(Abstractions) naming the reference on the side that points, mapped the same way from the key name
a provider's `TryGetForeignKeyViolation` gives (none on SQLite, which does not name the key). It derives from `DatabaseException` (Abstractions), which
every database failure is, on every path (open, read, write, delete, commit, rollback, schema,
subscription), one level over the driver's exception: `Kind`, `IsTransient` (worth another
attempt and nothing applied), `MayHaveBeenApplied`, `Operation` and `Attempts`. A provider names its
engine's errors once (`SqlServerErrors`, `PostgresErrors`, `SqliteErrors`, through
`RelationalDataProvider.ClassifyFailure`), because SqlClient and Microsoft.Data.Sqlite never set
`DbException.IsTransient`; `DatabaseFailures` (Data) decides from the operation what a failed commit
(`CommitOutcomeUnknown` unless the engine rejected the transaction) and a failed open are, and
`DbDataContext.Failure` that a write may have been applied only without a transaction. A failure
that is not the database's (`DatabaseFailures.IsDatabaseFailure`) propagates as it was thrown, and
the manager's save wraps nothing. A provider that could not initialise keeps why
(`RelationalDataProvider.NotReady`): a caller refused a context gets a `DatabaseException` built
from the failure when the database failed, the provider's exception with its kind and message when
the deployment is wrong, each new per caller. A `[TableIndex]` can be `Unique`, filtered to rows whose
keys are all non-null like a column's `Unique`, and a duplicate key while building either kind
fails initialisation without touching the rows. A child's parent column is always indexed:
`ColumnSchema` sets `Indexed` for the `[ParentObject]` property, so it is an ordinary column index
(`NCI_<table>_Parent`) on every path that reads `IndexedColumns`, and an existing table gains it in
place at its next start.

Foreign keys are derived by `SchemaManager.BuildSchema` (`ForeignKeySchema` on
`TableSchema.ForeignKeys`): a child's parent, a stored `[LinkedObject]`, a `[LinkedBy]` key and a
`[ForeignKey(typeof(T))]` column (Abstractions), each `NO ACTION` unless `[ForeignKey]` says
`SetNull`, and `[ForeignKey(false)]` drops one. A key is kept only when the store asks for keys
(`ISchemaStore.ForeignKeys`, from `RelationalProviderOptions.ForeignKeys`, on by default), the
dialect writes them (`ISchemaDialect.SupportsForeignKeys`), the registry routes
both types to this provider (`SchemaManager.Routes`, which `RelationalDataProvider` sets from the
`DataProviderRegistry` that names itself to its providers) and the referenced type is concrete with
no stored derived type; a `[ForeignKey(typeof(T))]` that cannot hold fails the build. A key column
is indexed (`ColumnSchema.HasForeignKey`). The check reads the `FK_` keys
(`ICatalogReader.ReadForeignKeysAsync`) and `SchemaComparer.DiffForeignKeys` compares them; the
update drops the keys to and from every table it changes or adopts inside the schema transaction,
before any rename, and after the commit and the index sync creates the missing keys, counting
orphans first (`ISchemaDialect.CountOrphans`) and creating a key over orphans unchecked with a
warning. A hard delete clears, in soft-deleted rows only, the references to what it deletes
(`TableSchema.SoftReferencesIn`, `DeleteTarget.ClearedReferences`), so a soft-deleted row never
stops it. Keys are immediate on every engine, because SQL Server has none deferred, which is why
saves are ordered by `ReferenceGraph`. A dialect whose keys are part of the table's definition
(`ISchemaDialect.ForeignKeysInline`, SQLite) writes them in `CreateTable` and `AddColumns`, and a
table whose keys differ is marked `TableSchema.RebuildForKeys` and rebuilt; nothing adds or drops a
key on its own, and the update reports the orphans each rebuild copied.

`ObjectDataManager` implements both `IObjectDataManager` and
`INucleusRuntime`, so the static and instance surfaces share one code path. DI registers only
`IObjectDataManager`, so it carries every operation a store needs (writes with tokens, deletes,
`CreateContextAsync<T>` and `SubscribeToChangesAsync<T>` routed by type). Reads are its loads
(`LoadByIDAsync`, `LoadAsync`, `LoadPagedAsync`, `LoadStreamAsync`, each taking a typed predicate
and a `LoadOptions`: names to exclude, names to include, and whether soft-deleted rows are seen),
`Get<T>()` and `Rows<T>()`. Every public load forwards to one private core per shape
(`LoadCore`, `LoadCoreAsync`, `LoadPagedCoreAsync`, `LoadStreamCoreAsync`, `LoadByIDCore`,
`LoadTopCore` for the first rows), which the adaptor calls too. `NucleusCache` holds a
`[Cacheable]` type's every row as an array only it holds, filled single-flight on either path (an
asynchronous fill runs on the thread pool, without a caller's token); an unfiltered, live, whole
load answers from it in `LoadCore`/`LoadCoreAsync` and the stream in `LoadStreamCoreAsync` (which
reads the array, `RowsAsync`), every list it hands out, `GetOrLoad` included, is a copy over the
shared objects, and a read by ID looks up the entry its fill returned (`GetOrLoadById`), never the
cache a second time. Its refresh loads first and swaps the entry in only over the one it
refreshed, so an eviction meanwhile wins; a failed refresh keeps the entry, logs a warning through
the manager's logger (`NucleusCache.Logger`) and lets the next start one interval later. A member added to an interface gets a default body, so another
implementation still compiles: it forwards to an older member where one does the same work
(`IDataContext.Save(IEnumerable)` to `BulkSave`), and throws `NotSupportedException` otherwise.
`DataProvider` and `DataProviderStatus` on it are obsolete: they are the unrooted default provider
and its status, and misroute a rooted type; `Status` is the worst status across providers. A load
that takes an untyped `Expression` accepts a predicate whole or as its body
(`ExpressionParser.Normalize` unwraps a lambda). `ObjectDataAdaptor<T>`
is a real LINQ provider; it is both `IQueryable<T>` and `IAsyncEnumerable<T>`, so `Where` and the
async operators (`ToListAsync` and the rest) are declared on `IObjectDataAdaptor<T>`, or .NET 10's
`System.Linq.AsyncEnumerable` makes each call on `Get<T>()` ambiguous. First and Single read one
and two rows, and Any and Count are row reads (one ID, a count) with no child query: the
extensions reach the adaptor through the internal `INucleusQueryOperators<T>`, because their
element type has no entity constraint. `ExpressionParser` lowers expression trees to the neutral query model,
unwrapping the `Convert` nodes the compiler inserts around an enum or widened column so the
comparison still reads as a column;
`DataProviderRegistry` routes each entity type to the provider of its nearest registered root,
which is how one application spans several databases. The parser never drops a term: every Where in
a chain is ANDed, boolean constants are folded, an OR always becomes its own group, and anything it
cannot translate throws, because the same translation drives deletes. `ExpressionParser.Classify`
lets callers skip a predicate that matches nothing, and `CheckDeletePredicate` refuses a delete that
matches everything. A load's expression is first taken apart by `ExpressionParser.Split` into its
filter and a `QueryShape` (OrderBy, ThenBy, Skip, Take), which every load core applies to its
select; a plain Where chain comes back as it was, allocating nothing. A Where or an order after a
cut, and any other operator, throws. Only loads split: the filter and delete paths still parse the
expression whole, so a delete refuses an order or a Take rather than deleting every match. A
cut query is never answered from the `[Cacheable]` cache, First and Single compose with the
query's cut, and Count and Any of a cut count the filter and work out what the cut leaves.
`Evaluator.PartialEval` leaves an operator over a query source alone, or `Get<T>().Take(5)` was
run and replaced by its result before translation. `ChildGraphBinder` is the one place child collections are materialised and
attached, from either a data set's linked result sets or streamed child queries; it binds each
child's parent as a stored value, so loaded children are never dirty. A load can exclude a child
collection by property name, which drops its query and marks the collection unloaded
(`INucleusChildCollection.IsLoaded`); reading an unloaded collection throws, adding does not. A
`[RetrieveOnDemand]` collection is lazy: `SelectQuery` leaves its child query out unless
`IncludeChildCollection` puts it back, and `ChildGraphBinder` marks it lazy with the loader of the
stack that loaded the parent (`ChildCollectionLoader` over that manager's or context's provider),
never the ambient runtime, so a process with several managers reads each from its own database. The
collection loads itself on first read, under a lock, on the synchronous path.

A `[LinkedObject]` is stored, and loaded, as the referenced entity's ID. `ChildGraphBinder.Complete`
binds every loaded object of a type with linked objects, or of a child type, to an `ILinkedObjectLoader` (Abstractions)
for the stack that loaded it: the manager itself (`LoadByID` for the type, routed and cached), or a
data context's `ProviderLinkedObjectLoader` (Data, its own provider on a context of its own). The
first read of the property (`CoreObject.GetValue`, off the common path) loads the entity through it
under a lock and keeps it as the stored value, so the object is not dirty. Nothing else may read a
link through its property: `SaveGraph` and `GetLinkedObjects` read it as held
(`GetPropertyValue`), because a link never read has no changes and reading it would load it. An
included reference (`IncludeLinkedObject`) is a linked query like a child query, marked
`LinkedObjectProperty`, whose `ParentKeyColumn` is the reference column: the renderers' parent
subquery selects that column of exactly the parent rows returned instead of their `ID`, and the
binder files the entities by ID and binds them into every row that holds that ID. A link may name
a child type. A child loaded without its parent (by a link, `LoadByID` or a query of its type)
holds the parent's ID under `Parent`, and the first read resolves it the same way; a child loaded
with its parent holds the parent object. `SchemaManager.BuildSchema` refuses `forceSave` on a link
to a child type, because the save would write the child outside its parent.

A `NucleusLinkedCollection<TMany, TOne>` (Abstractions) is the one-to-many between two roots: the
many type declares the key and the one's property names it with `[LinkedBy]`. `ObjectSchema`
refuses the per-type rules in its constructor (a one is a root and not `[Cacheable]`, two
collections never share a key) and resolves each key lazily (`GetLinkedCollectionKey`), because the
many type may hold a collection of the one; `SchemaManager.BuildSchema` forces the keys and refuses a
many type the provider does not store. Collections are keyed by property name, never by element
type. A collection exists only once its property is read (`CoreObject.NucleusLinkedCollections`
lists those), loads on the first read of a member through the one's bound `ILinkedObjectLoader`, and
an include is a child-shaped linked query marked `LinkedCollectionProperty` on the key column.
`KeyedRowLoader` (Data, internal) is the one read of rows a load left for later, by a key column:
child collections, linked collections and linked objects all load through it. `SaveGraph` holds a
collection's new or changed members (`Members`, written after the roots) and its removed stored
members (`RemovedMembers`, deleted first by ID as direct deletes); commit commits them as it does
links. `DbDataContext.ExecuteDeleteCore` refuses a delete of a one with members before any chunk
runs, by a select of member IDs whose key is in the rows the delete's own condition matches
(`RelationalDataProvider.LinkedMembersOf`), in the delete's transaction.

`SaveGraph` (Abstractions) is the one walk of what a save reaches: the held children at every
depth, the `forceSave` links it must write, and the stored children removed from collections.
Validation (`CoreObject.Validate`), the writes and the commit all read it, so they cannot disagree.
`RelationalDataProvider.CreateSaveQueries` is the one save builder every save path uses (the
manager and `IDataContext.Save`/`SaveAsync`, for one object or a batch): removal deletes first, as top-level
`DeleteWhereQuery`s so the context runs them on its delete path, then the link writes, then the
roots, then the linked collection members, with those writes put in a stable topological order by
`ReferenceGraph` (Data, internal: which types' rows point at which others', a child's references
folded into its root's chain), so a type is written after the types its rows point at. New rows
of one table that point at each other are ordered through `TableSchema.SelfReferences`. A delete
of several types removes each before the types it points at. A row the save reaches (a child, a link or a member) with no changes of its own is not
written, only the changed rows below it, because its update would only stamp `Timestamp`; the roots
are always written (`RelationalDataProvider.WriteQueries`). Every save path validates before it builds anything, once per object: the manager's `Save`
and `SaveAsync` through `SaveChecks` (Data), ahead of their failure wrapping so a rule's exception
reaches the caller as thrown, and `DbDataContext` in its save and batch save builders, which also
covers the manager's save of a batch of mixed types. There is one save: `BulkSave` is the obsolete
name of the batch save, and which engine writes a batch is decided in `DbDataContext.ExecuteSQLAsync`
by its parameter count, never by the caller. The synchronous path has no bulk engine. Each calls `ICoreObject.PrepareForSave`, which `CoreObject` runs as
`Validate()`, the saveable checks, then `PreSave()` on the object and its changed dependents, from
one walk of the dependents (a flag keeps `base.Validate()` from walking them a second time).
`CoreObject`'s own saves do not validate, because they route through the manager or a context;
validating there too would run the rules twice. The manager marks objects saved and evicts the
cache only after its context has committed and been disposed, so a failed commit leaves them
unsaved. A save through a context's own `Save`/`SaveAsync` (and `CoreObject.SaveAsync(IDataContext)`)
marks its objects saved as the writes run, through `ICoreObject.CommitChangesInTransaction`, which
records what each object's commit changed in the root context's `CommitUndoLog` (Abstractions);
`DbDataContext.EndTransaction` clears the log when the commit returns and reverts it after a
rollback or a failed commit, which leaves each object as it was before its first save in the
transaction with later changes still pending. The manager's mixed-type batch save, its one save
through a context's own save, turns that off (`ISaveAcceptance`, internal) so its `MarkSaved`, with
the audit logger, stays the one place its objects are marked. A context the manager hands to a
caller (`CreateContextAsync<T>`, transactional unless its options set `UseTransaction` false) reports its work to an
`IContextObserver` (Data, internal) the manager sets on its root: the types its saves and deletes
wrote, read before the saved objects are marked, and their audit entries, held until the
transaction commits, when the caches of those types are evicted and the entries written, and
dropped when it does not. A context a provider hands out has no observer and costs nothing for
it. The delete policy is resolved once per type by `ObjectSchema` (`IsDeletable` for a direct
delete, `IsDeletableThroughParent` and `EffectiveDeleteBehavior` for the owned graph), walking up
the fixed `ParentObjectProperty` chain; `SoftDelete.Applies` reads the effective behaviour. A
child whose effective behaviour is soft under an owner that is deleted hard fails the schema
build, because the owner's delete would leave the flagged children pointing at a deleted row.

`TFM.Nucleus.Data.SQL` is SQL Server's dialect over that SDK: `SqlServerQueryRenderer` (every
T-SQL query string), `SqlServerSchemaDialect` and `SqlServerTypeMapper` (every DDL string),
`SqlServerCatalogReader`, the full-text extras, the #temp and SqlBulkCopy bulk engine and
SqlDependency notifications. Everything but its registration, `SQLConfiguration` and
`SqlDataProviderException` is internal. Its output is pinned byte for byte by the raw goldens in
`SqlServerGoldens.cs`. A select batches its child collection queries after its own rows, and
`GetDataSet` reads each into `DataSet.LinkedDataSets`; a child selects by the IDs of exactly the
parent rows returned, page and TOP included, while `StreamAsync` sends the root query alone.
Every delete, by object or by predicate, is a `DeleteWhereQuery`: one batch that deletes child
collection rows deepest first, then the root, then
`SELECT @@ROWCOUNT`, which is the one place Nucleus reports rows affected. It runs through
`DbDataContext.ExecuteDelete`, never the generic execute path, whose regrouping by table would
detach the statements from the IDs they share. The soft-delete rule lives once, in `SoftDelete`.
No SQL Server statement reads a parent's table through a child's: a linked query (in a batch or
written on its own, as a streamed load and the linked-members check write it) selects by its
parent's keys kept in a table variable (`@__parents`, `@__parents1`, ...) read from the parent's
table alone, and a delete keeps each level's IDs the same way (`@__targets`, ...). Reached through
a subquery, SQL Server ran small tables from the child side, held another parent's child row
while it read that parent, and deadlocked against a save of the parent, at every level of a
graph. `SqlServerKeptIdsTests` holds every statement to one table; the opt-in
`SqlServer\DeadlockStressTests` (`NUCLEUS_DEADLOCK_STRESS=1`, or a worker count) reproduces the
deadlock if the shape regresses. A database the provider creates gets READ_COMMITTED_SNAPSHOT
(`SQLConfiguration.ReadCommittedSnapshotOnCreate`, set right after `CREATE DATABASE` with
`NO_WAIT`); an existing one is never altered, and initialise logs a warning when it has the
setting off. A query marked `Query.WaitForPendingWrites` (the delete's linked-members check, both
reads) is rendered `WITH(READCOMMITTEDLOCK)`, so it waits for a member being inserted whatever the
database's setting; the other providers ignore the flag. `SqlServer\ReadCommittedSnapshotTests`
covers both, and its database-creating tests skip when the test login cannot create a database.

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
retries a transient failure (`ConnectAttempts`). Its output is pinned by `PostgresGoldens.cs`,
built from the same query corpus as the SQL Server goldens.

`TFM.Nucleus.Data.SQLite` is SQLite's dialect over the same SDK, for one database file on a local
disk: `SqliteQueryRenderer`, `SqliteSchemaDialect` with `SqliteTypes`, `SqliteCatalogReader`
(`sqlite_schema` and the `pragma_table_info`, `pragma_index_list` and `pragma_index_xinfo`
functions) and `SqliteDataContext`. It has no change notifications (`SubscribeToChangesAsync`
throws `NotSupportedException`), no extras and no bulk engine. Every connection is built by
`SqliteConfiguration.BuildConnection`, which registers the C# functions in `SqliteFunctions` that
the rendered SQL calls: Unicode-folding pattern terms and `lower`, a substring `Contains` for a
FullTextSearch column, and the server functions, which write the same text forms `SqliteValues`
binds. `SqliteValues` is the one place values become stored forms and back: a Guid as lower-case
text, a DateTime and a DateTimeOffset (in UTC) as fixed-width text that sorts as the instants do, a
bool and an enum as integers, a decimal as a real (about 15 significant digits, read back rounded
to the scale). Declared types keep their size facets for the schema check and are chosen so their
affinity leaves those forms alone. There is no ClusterID: a rebuild copies in rowid order. SQLite
cannot rename an index and its index names are database-wide, so `RenameForRebuild` drops the
indexes the new table will create and `AdoptTable` only renames the table, leaving the index sync
to replace old-named indexes; the primary key is unnamed because a rename would leave its name
stale. A unique violation (extended code 2067, or 1555 for the key) names columns, not the index, so
the provider maps them back to the index through its table schemas. Initialise refuses a SQLite
older than 3.35 and puts the file in write-ahead log mode (`UseWriteAheadLog`); transactions begin
IMMEDIATE and writers wait `BusyTimeoutSeconds`. `MaxParametersPerCommand` is 2000, far under
SQLite's 32766, because the driver binds each statement of a command by searching all its
parameters, so a large multi-statement command is quadratic and holds the write lock throughout
(an oversized save measured 33 s at 30000 against 0.3 s at 2000). Foreign keys are inline
(`REFERENCES` in each key column's definition), and every connection a context opens sets
`foreign_keys` (on, or off under `ForeignKeys = false`) and `legacy_alter_table` off before its
transaction begins (`SqliteDataContext.PrepareConnectionAsync`); the schema update's context sets
`foreign_keys` off and `legacy_alter_table` on, so a rebuild can replace a table others point at
and its rename leaves their `REFERENCES` text alone. Its output is pinned by
`SqliteGoldens.cs`, built from the same query corpus.

## Build and test

```powershell
dotnet build TFM.Nucleus.slnx --configuration Release
dotnet test  TFM.Nucleus.slnx --configuration Release --no-build
```

Restore needs `GITHUB_USER` and `GITHUB_TOKEN` in the environment: `nuget.config` reads the
GitHub Packages credential from them. Since the store packages moved out (November 2026) nothing
here restores from that feed; everything is an in-repo project reference or comes from nuget.org.

Warnings become errors only when `ContinuousIntegrationBuild=true`. To reproduce what CI enforces:

```powershell
dotnet build TFM.Nucleus.slnx --configuration Release -p:ContinuousIntegrationBuild=true
```

The integration tests run every suite once per provider: each neutral suite is an abstract class
with a sealed subclass per provider under `SqlServer\`, `Postgres\` and `Sqlite\`, next to that
engine's own tests. SQL Server needs a disposable database configured through the `NUCLEUS_TEST_SERVER`,
`NUCLEUS_TEST_DATABASE`, `NUCLEUS_TEST_USERNAME` and `NUCLEUS_TEST_PASSWORD` environment variables
or the matching `Nucleus:*` user secrets; PostgreSQL through `NUCLEUS_PG_TEST_*` or
`Nucleus:Postgres:*` (a `postgres:16` container is enough; the provider creates the database);
SQLite through `NUCLEUS_SQLITE_TEST_DATABASE` or `Nucleus:Sqlite:Database`, a file path the provider
creates. Each .NET runtime has a database of its own on every engine
(`NucleusTestDatabase.DatabaseName`: the configured name with `_net8` or `_net10` added, and on
SQLite `.net8` or `.net10` before the file's extension), because the net8.0 and net10.0 runs of a
suite run at the same time: sharing one, a run read the catalog while the other was half way
through creating a table, and SQLite, which does not queue writers, starved a writer past its busy
timeout. The database is created when it is missing, on SQL Server by `SqlServerTestDatabase`
itself, at compatibility level 100 (`MinimumCompatibilityLevel`, the lowest Nucleus supports) with
READ_COMMITTED_SNAPSHOT on, so the test login needs the right to create a database or the two
databases made for it.
A provider with no settings skips cleanly, which is what happens in CI. Settings, stack boot, locks
and raw statements live in `TFM.Nucleus.Testing` (`NucleusTestDatabase.SqlServer`, `.Postgres` and
`.Sqlite`);
`DatabaseFixture` is only the xunit v3 shim that boots the collection's stack and turns "not
configured" into `Assert.SkipWhen`. Store repos write their own thin shim over the same package. A
stack whose initialisation fails includes its warnings and errors in the message. A PostgreSQL
stack caps its provider's pool at 20 (`PostgresTestDatabase.TestPoolSize`), because two runs at
the default of 100 exhausted the server. A fixture boots
its shared stack with `StartSharedStackAsync`, under the schema lock, because two runs once raced
to create the same tables and one of them failed. Two test processes of one runtime (the two
test projects, or a store repo's suite) still share a database, so a test that changes a table
with raw DDL, starts a second stack (which updates the schema) or checks the whole schema holds
`DatabaseFixture.SchemaLockAsync` for its duration; the lock is per provider and runtime, which is
per database, and is not reentrant, so take it once per test.

`tests\Shared\` is test support compiled into every test project through `tests\Directory.Build.props`
(linked, not copied): `TestSourcePaths`, which the accept switches write through, and the
benchmark harness under `Benchmarking\` (`BenchmarkRunner`, `BenchmarkGate`, `BenchmarkBaseline`,
`BenchmarkLock`, `BenchmarkSuite`). The benchmarks catch regressions on the save and read paths.
They are opt-in: every one skips unless `NUCLEUS_BENCHMARK=1`, so a normal test run and CI never
run them.
- Run them for an architectural change: the save or load pipeline, the query builders or
  renderers, the schema model, the provider SDK, the entity base or change tracking. Do not run
  them for a feature or a bug fix that leaves those alone; a full run takes about 20 minutes.
- The database benchmarks (`TFM.Nucleus.IntegrationTests\Benchmarks\`, `SaveBenchmarks` and
  `ReadBenchmarks`, a sealed subclass per provider) gate allocations against
  `Benchmarks\Baselines\<machine>\`.
- The database-free benchmarks (`TFM.Nucleus.Tests\Benchmarks\InProcessBenchmarks.cs`) gate
  allocations against `Benchmarks\Baselines\any\`. Their allocation counts repeat to the byte, so
  a 10% rise is a real change, not noise.
- Allocations are gated; time is reported with its delta and never fails, because on a development
  database it moves by half between back-to-back runs. A scenario fails when its allocations are
  over the baseline by more than the tolerance and the floor twice in a row, or when it has no
  baseline. Allocations are counted process-wide, so only one benchmark measures at a time per
  machine (`BenchmarkLock`), and a fixture boots its stack under the same lock while benchmarks run.
  A database suite purges this machine's stale `bench-<machine>-` rows and settles the tables
  (`DatabaseFixture.SettleAsync`) before it measures.
- Run them with `tools\Run-Benchmarks.ps1` (`-Accept` writes the baselines). A database suite
  allocates 15-20% more after another suite's heavy work in the same process, so the script gives
  each provider's Save and Read suite a process of its own, and a second database suite in one
  process skips rather than measuring wrong.
- A variant comparison (`DatabaseBenchmarks.Variants`) measures a setting interleaved against the
  code as it is.

## Deviations from the scaffold

Two. The tests use **xunit.v3 2.0.0**, not the scaffold's xunit 2.9.3, because they were written
against v3 (recorded in `tests\Directory.Build.props`). And there is a test project that matches
no shipping project: `tests\TFM.Nucleus.DocSamples` holds the documentation's examples as code and
the tests that run them. Everything else follows the scaffold, and every test project
multi-targets `net8.0;net10.0` like the shipping packages.

`TFM.Nucleus.DocSamples` is referenced by no other test project, deliberately: entity types are
found by scanning the loaded assemblies, so its sample entities would join the schema of every
stack in a process that loaded it. Its own tests run the samples against a SQLite file they
create in the temp folder and delete, so they need no configuration and run in CI.

`Directory.Build.props` suppresses one warning, IDE0040, and `tests\Directory.Build.props`
suppresses xUnit1051. Both are mechanical style edits rather than defects; see
`docs\code-review-findings.md`. `src\Directory.Build.props` suppresses CS1591, so a public
member without an XML comment does not fail the build. Nothing else is suppressed, so a nullable
warning or an analyzer warning in new code fails CI.

## Modelling rules for consumers

- Rows owned by one parent are a `NucleusChildCollection<TChild, TParent>` on the parent and a
  `CoreChildObject<TChild, TParent>` child. Never model them as a root entity with a parent ID
  column plus a private `List<T>`, and never write `Attach*`, `Remove*Line`,
  `AcknowledgeRemovals` or removal tracking in an entity or store. Those are antipatterns here.
- Save the root; the graph follows, and the root's `Validate()` runs the children's. Do not loop
  over children calling `SaveAsync`, do not restate a child's rules on the parent, and do not look for a
  bulk save: there is one save, and an oversized batch goes to the bulk engine by itself.
- Deletes are declared with `[Deletable]` on the child type, on the collection property or on an
  owner. The child type's `[Deletable(false)]` wins, and a conflict is a startup error. Removing a
  stored child from an undeclared collection throws. Delete the root, or `DeleteWhere` on the
  root; children cascade. Do not delete children by predicate before deleting a parent.
- A child collection that can grow large is `[RetrieveOnDemand]`, and a listing that needs its
  children `Include`s them. Never read a lazy collection per parent in a loop.
- A listing that reads a `[LinkedObject]` on every row `Include`s it (`Include(m => m.Keeper)`).
  Never load the referenced entities by their IDs by hand, and never keep a raw `Guid` column
  beside a reference to avoid the read.
- Roots that belong to another root (a troop's monkeys) are a `NucleusLinkedCollection<TMany, TOne>`
  on the one, with `[LinkedBy]` naming an indexed key on the many. Never hold them in a private
  `List<T>` loaded by hand, and never save them in a loop beside the one: add them to the
  collection and save the one. A listing that reads the collection on every row `Include`s it.
- Listings that do not need children use `Exclude(...)`; do not split a parent and its children
  into two queries and join them in memory.
- Take `IObjectDataManager`, never the concrete `ObjectDataManager`, and never reach through its
  `DataProvider`. A transaction across several types is `CreateContextAsync<T>`, ended with
  `await context.CommitAsync(ct)`. Never rely on dispose to commit: it does only until v3, or until
  the provider sets `RequireCommit`, and it commits work a failure in your own code should have
  stopped. If a store needs something the
  interface lacks, that is a Nucleus defect: add it to the interface rather than casting.
- When Nucleus does not do one of these correctly, that is a Nucleus defect: report it (and
  record it in `docs/code-review-findings.md` and in `docs/guide/known-limits.md`) rather than
  working around it in the consumer.
- Every open finding that changes what a consumer must do is listed in
  `docs/guide/known-limits.md`, not only in `docs/code-review-findings.md`.

## Rules

- **Ask the schema first, at the top of the method.** The `ObjectSchema` answers once per type
  what an object can hold, and cheap flags answer what it holds now. Any method that walks the
  graph, iterates properties or collections, or builds something per object checks them before
  it allocates or loops, and returns early when there is nothing to do:
  - per type: `HasNucleusChildCollections`, `HasLinkedObjects`, `HasParentObject`, and derived
    per-type answers such as `SaveGraph.CanReachDependents`;
  - per object: `IsNew`, `IsReadOnly`, `HasChanges`.

  Compute a new per-type answer once and cache it by type, as `CanReachDependents` does, rather
  than working it out per object. Look out for allocations hidden behind cheap-looking calls:
  - an iterator property such as `NucleusChildCollections` allocates its enumerator on every
    call, even when the check inside it yields nothing, so test the schema flag before calling it;
  - a method group passed in a loop allocates a delegate each time, so hoist it or cache it;
  - `[this]` and other collection expressions allocate.

  Two regressions came from missing guards. Every save walked a `SaveGraph` per object even for
  types with nothing to walk, about 2.5 KB per object and +40% on a bulk save. A load allocated a
  delegate per row. See `docs\code-review-findings.md`.
- **Do not put SQL in `Abstractions`, `TFM.Nucleus` or `Data`.** The provider seam is the whole
  reason a second database engine is possible. `ProviderSeamTests` will fail if you do.
- **Consumers code against `Abstractions` and `Data` only.** An application, and every store
  (they live in the repos of the libraries they serve), touches a provider package only to register and configure it, so a deployment picks
  its database by picking a package. A provider package's public types are its `Add...` extensions,
  its configuration class and the exception its registration throws; everything else is
  `internal`. `ProviderSurfaceTests` holds each provider to that list, so widening it is a
  deliberate review decision.
- **No engine names in neutral or store assemblies.** No provider type name, error number or
  registration name, in code or in a message. `ProviderSeamTests` scans the compiled strings of
  `Abstractions`, `TFM.Nucleus` and `Data` for SQL keywords and provider names, and checks none of them references a provider or its client. When a store needs to know
  what an engine did (a unique violation, say), add a neutral type to `Abstractions` or `Data` and
  have every provider produce it.
- **Shared logic lives once, in `Data`.** A provider implements the dialect interfaces and derives
  from the SDK base classes; it never re-implements a query rule, a tree walk, the schema compare
  or the execution paths. If a second provider needs something the first one has privately, move
  it into `Data` behind a neutral name, not into the second provider as a copy.
- **Spell identity `ID` and database `DB` in member names** (`LoadByID`, `IsDBColumn`). Existing
  members that spell them otherwise stay as they are; do not add a second name to match.
- **Every exception Nucleus defines derives from `NucleusException`** (Abstractions). A new one
  does too, and reports what failed through a `Kind` where it has several causes. A database
  failure is a `DatabaseException`, never a bare `Exception`; a new path that reaches the database
  translates its failures through the provider (`TranslateFailure`, or `DatabaseFailures` with the
  provider's classifier), and lets anything else propagate as it was thrown.
- **Nucleus retries an open always, and an operation only when the application asks.** This is
  the library's retry design; what a caller retries is in the guide's
  [Retries](docs/guide/saving.md#retries).
  - An open is retried up to `ConnectAttempts` times, only for a failure the provider classifies
    as transient, all within the connection's connect timeout.
  - An operation Nucleus runs on a context of its own (the manager's saves, deletes and loads,
    streams until their first row, row reads, `KeyedRowLoader`) is retried only under the
    provider's `RetryAttempts`, 1 (off) by default, and only for a `DatabaseException` with
    `IsTransient` true, which is never true when the operation may have been applied. Each attempt
    runs on a new context, and a save's checks and `PreSave` run once, before the first.
  - Never retried: anything on a caller's context (its transaction is the caller's), a stream
    that has handed out a row, and a commit whose outcome is unknown.
  - The last attempt's failure is reported with `IsTransient` false and `Attempts`, so a caller
    does not stack a retry on the policy.
  - All of it goes through `OperationRetry` (Data); a new operation on a context of its own uses
    it rather than a loop of its own, with a struct state and a static lambda, so the policy costs
    nothing when it is off.
  - Only the application decides that an operation is worth repeating, which is why the policy is
    opt-in. A retry that repeated a write whose outcome is unknown, or hid the failure, would be
    wrong, which is why those are never retried.
- **Name new attribute and server-function vocabulary by meaning, not by an engine feature.**
  `Indexed`, not `NonClusteredIndex`; `CurrentDateTimeOffset`, not `SYSDATETIMEOFFSET`. The old
  SQL Server names were removed in the November 2026 naming clean-up.
- **A new attribute class is named with the `Attribute` suffix, and a new enum with no prefix
  and PascalCase members.** `NotStoredAttribute`, `QueryKind.Select`.
- **Change the SQL Server goldens or the API snapshot deliberately.** `SqlServerGoldens.cs` and
  `PublicApiSnapshot.cs` are written only by their accept tests, with `NUCLEUS_ACCEPT_GOLDENS=1`;
  the diff is the review.
- **Re-accept a benchmark baseline deliberately.** The files under `Benchmarks\Baselines\` are
  written only with `NUCLEUS_BENCHMARK_ACCEPT=1`. A regression is fixed, not accepted, unless its
  cost is the point of the change; then the commit message gives the reason and the numbers. Never
  raise a tolerance to make a run pass.
- **Do not move a shared property into a csproj.** The three level props split (repo, `src`,
  `tests`) is the standard. A csproj carries package identity, description, tags, dependencies and
  nothing else.
- **Do not hardcode a version anywhere.** `VersionMajor` in `Directory.Build.props` is the only
  version value in the repo. CI supplies the rest. See `docs/versioning.md`. The seven packages
  share one version by design.
- **Pin package references per target framework**, 8.0.x for `net8.0` and 10.0.x for `net10.0`, so
  consumers get assemblies built for their runtime rather than a lowest common denominator.
  Exception: `Microsoft.Data.SqlClient` is one unconditioned reference, because the package
  multi-targets itself and one version serves both frameworks.
- **Keep the workflow files free of configuration.** They are byte identical in every TFM package
  repo and read what they need from `Directory.Build.props` at run time (`VersionMajor`,
  `PublishToNuGetOrg`, `PublishPrereleaseToNuGetOrg`, `TargetFrameworks`). If something seems to
  need a knob in the workflow, add the property to `Directory.Build.props` and read it instead.
- **Three release branches, by convention.** `tag-and-release.yml` releases stable from `master`
  and prereleases from `beta` (`-beta.M`) and `alpha` (`-alpha.M`), and fails on any other ref. The
  branch names are fixed across every TFM package repo, not a per repo setting. A prerelease takes
  the `N` master would cut next, so it sorts below that stable release, and like a stable release it
  publishes all seven packages on one version. It gets a tag and a prerelease GitHub release but no
  `release/` branch, and reaches nuget.org only when both nuget.org switches are `true`. See
  `docs/versioning.md`.
- **Evolve the public surface additively.** Two production sites already consume these packages,
  and `CoreObject` is the base class of every entity in them. A breaking change to the entity base,
  an attribute, or a registration method is a major version and a coordinated release.
  Exception, November 2026: the naming clean-up (enum names, the `Attribute` suffix, `DataType`
  to `ColumnDataType`, `AddNucleus` moved to the dependency injection namespace, removal of the
  members obsolete before it) shipped on the v2 line. Reason: the API surface was being
  straightened out ahead of publication, while every consumer (two sites and the four store
  packages) was released by the same team in one coordinated release. This was a one-off. The
  rule stands for everything after it; the repo is public and the packages will be published to
  nuget.org once the surface is settled, which limits future breaking changes further.
  Exception, November 2026: pool exhaustion and a database unreachable at first use became
  `DatabaseException`s, the kinds thrown nowhere were removed, and a context from
  `CreateContextAsync<T>()` became transactional by default (`ContextOptions.UseTransaction`
  became a `bool?` for it). Reason: "the database failed" was
  three exception types and a forgotten option silently dropped the transaction; both were
  defects in the surface, fixed before publication while every consumer is released by the same
  team. The rule stands for everything after it.
- **Do not add a `NoWarn` entry to hide new code.** The existing list covers code that predates the
  scaffold. New code compiles clean.
- **Page by ClusterID, and index it everywhere.** A paged read, and a limited root with linked
  queries, that the caller did not order is ordered by insertion: `ClusterID` on SQL Server and
  PostgreSQL, `rowid` on SQLite. A caller's order on a paged or limited read is followed by the
  same term, so ties cannot move a row between pages or give a parent's children to another
  parent. Never add an `ORDER BY ID` fallback: a Guid order is stable but means nothing. Every
  engine indexes ClusterID (SQL Server's clustered `CIX_`, PostgreSQL's unique `CIX_`, SQLite's
  rowid key), so a page never sorts the whole matching set; a new engine or table path does too.
- **Do not name an entity column `IsDeleted`.** Nucleus reserves it for its own soft-delete
  behaviour; a library flag with that meaning takes another name, such as `SoftDeleted`.
- **Update the guide in the same commit** as any change to the public surface: the page under
  `docs\guide\` that covers it, `behaviour-changes.md` when behaviour changes, and `README.md`
  when it names the member. The README is packed into all seven packages and is what a consumer
  sees on the feed, so it stays short (what Nucleus is, install, a quick start, the page index)
  and its links are absolute, because a relative link is dead on the feed.
- **An example in the documentation is a sample that runs.** Every C# block in the README and the
  guide is a copy of a `#region` in `tests\TFM.Nucleus.DocSamples`, named by the
  `<!-- sample: name -->` line above it. Write or change the example in the sample, run it, then
  copy it across with `NUCLEUS_ACCEPT_GOLDENS=1` (`DocSampleTests.Accept...`). Never type code into
  a page: `DocSampleTests` fails on a block with no sample, a block that differs from its sample,
  and a sample no page shows.
- **No em dashes in authored documentation.** Use hyphens.
- **Examples are hypothetical; no real consumer is named.** No document, XML comment, test or
  message in this repo names a real application, site, customer, server or machine, and no
  example is copied from consumer code. Examples use the TFM sample domain in
  `docs\sample-domain.md`. A finding records what happened, not who hit it. One exception: the
  sibling TFM packages that hold the stores are named in the stores table and its migration
  note, because a consumer needs the package id to install it.
