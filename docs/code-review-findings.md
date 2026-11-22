# Code review findings

Findings from the review carried out during the monorepo restructure of August 2026, and the state
of each one after the fix pass that followed.

Everything under "Fixed" has regression cover in `tests/TFM.Nucleus.Tests/RegressionTests.cs` unless
noted otherwise. Everything under "Open" is still true of the code.

## Open

### Limits by design that a consumer must allow for (November 2026)

Not defects, but each changes what a consumer writes, so each is in the guide's known limits and
recorded here so that page's list and this one agree.

- **A child context shares its root's connection.** A tree is one transaction on one connection,
  so its contexts cannot run commands concurrently.
- **A deferred read runs outside the caller's transaction.** The first read of a lazy collection,
  a linked collection or a linked object, and `LoadAsync()`, run on a context of their own
  (`KeyedRowLoader`), bound to the provider and not to the context that loaded the object, because
  that context may be gone by then. They do not see the caller's uncommitted rows and take a
  connection of their own. On SQL Server without READ_COMMITTED_SNAPSHOT, a read of rows the
  caller's own open transaction wrote waits for it until `StatementTimeoutSeconds`, because the
  engine sees no deadlock: the caller's session waits on nothing in the database. Cover:
  `ChildModelIntegrationTests.ALazyRead_RunsOutsideTheCallersTransaction`, on every provider. That
  a collection's first read is synchronous is not a limit: the caller chooses `Include`,
  `LoadAsync()` or the implicit read, and a property cannot be awaited.
- **A transactional context cannot join a `TransactionScope`.** No connection enlists, so it is
  refused with `AmbientTransaction` (see "A provider held two connection pools" under Fixed).
- **SQL Server's notification listener is one connection outside `MaxConnectionPoolSize`.** The
  driver opens it for as long as anything watches for changes. That it is unpooled is the driver's
  behaviour and was not measured here.
- **SQLite takes a file path only.** `SqliteConfiguration.FilePath` refuses `:memory:` and every
  `file:` URI, one naming a file on disk included.

### Found by the documentation review, what is left (November 2026)

A check of the guide against the code before beta was merged to master. The three defects it
found are fixed (see "Found by the documentation review" under Fixed). Still open:

- **Nucleus throws a bare `Exception` in seven places**, against the rule that a failure is never
  a bare `Exception`: a type with no table schema (`RelationalDataProvider`, twice) and five
  context states in `DbDataContext`. Each wants a typed
  exception with a `Kind`.
- **A column's length cannot be set without its type.** `DatabaseColumnAttribute(int?)`, the
  `(ColumnDataType, int?)` constructor and the `DataLength` field are nullable, which attribute
  syntax does not accept, so `[DatabaseColumn(ColumnDataType.Unicode, 200)]` is the one form that
  sets a length. Read from the language rule, not compiled. Listed in the guide's known limits.
- **A failed open that ran out its connect timeout carries the driver's exception two levels
  down.** `ConnectionOpener` wraps it in a `TimeoutException`, which the `DatabaseException` wraps.
  Documented in the guide's Failures section.
- **SQL Server error 334 is a `SqlDataProviderException`, not a `DatabaseException`.** It is
  deliberate (the message says what to change), and the one database failure that is not one.
  Documented in the guide's Failures section.
- **`[LinkWhenLogging]` on a property that is not an entity fails the schema build**, though the
  attribute does nothing else yet.
- **Not tested: the retried opens of the catalog readers and the SQL Server subscription.** They
  go through `ConnectionOpener`, which has its own cover, but no test makes one of those opens fail
  transiently.

### API surface review, what is left (November 2026)

A review of the whole public surface against `PublicApiSnapshot.cs`. The traps are fixed (see
"Fixed"). Still open:

- **`[LinkedObject]`'s `linkedIDProperty` argument is stored and read by nothing.** Found while
  aligning the relationship attributes. Listed in the guide's known limits.
- **A test stack does not publish the ambient runtime.** `NucleusRuntime.Current` is one per
  process and several stacks share a test process, so the static `CoreObject` API under a test
  stack uses whichever runtime was published last, if any. Tests use the stack's manager.
- **The test stack's start methods do not pass a cancellation token to initialisation**, because
  `IDataProvider.InitializeAsync` takes none. `StartSharedStackAsync` takes one, which reaches
  only its wait for the schema lock.
- **Three SDK members take a nullable cancellation token** (`IDataSchema.InitializeAsync`,
  `SchemaManager.InitializeAsync`, `RelationalDataProvider.InitializeAsync`). They work, no
  consumer calls them, and an overload beside them would make a call without arguments ambiguous.
  Left until a major version.
- **`[LinkWhenLogging]` is parsed and not applied.** It is to attach the linked object's type and
  ID to the log entry's linked objects. The implementation did not come across from the library
  Nucleus grew out of and is to be restored. `GetLinkedIDsForLogging` is its unused half and
  stays. Listed in the guide's known limits.
- **The synchronous save never reaches the bulk engine.** `DbDataContext.ExecuteSQL` splits an
  oversized write into commands; only `ExecuteSQLAsync` hands it to the bulk engine. Listed in the
  guide's known limits.
- **`IDataProvider.Initialize(IEnumerable<IObjectSchema>?)` ignores its argument**, like the
  schema methods that were replaced. It is the only synchronous initialise on the interface, and
  a call without arguments already reads correctly, so it was left.

### Save, load and schema audit (November 2026)

An audit of the save, load and schema paths against the "ask the schema first" rule in AGENTS.md,
after the benchmarks were in. Each item is one small step: fix it, test it, tick it off here.
"Verified" means the code was read and the behaviour confirmed; "read" means it was traced from
the code but not reproduced; byte figures are estimates from the code unless marked "measured".
The performance steps touch the save and load pipeline, so run the benchmarks
(`tools/Run-Benchmarks.ps1`) after each group.

#### Bugs

All seven are fixed; see "Audit bugs (November 2026)" under Fixed.

#### Performance steps, most gain first

- **P1. `DataSet` allocates about 240 KB before it holds a row.** Verified. A 10,000-slot outer
  array, a 10,000-slot first block in the constructor, and a second first block when the first row
  arrives (`GetNewRow` sees `right == 0` and calls `NewArray(0)`). This is nearly all of
  `LoadByID`'s 255 KB per call (measured) and of a lazy collection's first read (270 KB measured),
  and every child result set of a graph load pays it again, empty or not. Grow on demand.
- **P2. Cache `ObjectSchema.NucleusChildCollectionProperties`.** Verified. It builds a new array on
  every read (`[.. _nucleusChildCollectionProperties.Values]`), and it is read per object on every
  walk, commit, query build, `ClearChanges` and lazy marking. Build it once in the constructor, as
  `LinkedObjectProperties` already is; the same for `LinkedObjectSchemas`, `AllProperties` and
  `AllDBColumns`.
- **P3. Return early from `NucleusChildCollection.RemovedObjects`.** Verified. It runs a LINQ
  filter and builds a list on every call, even with nothing removed, and every walk calls it per
  collection. Return an empty array when `_removedChildren` is empty.
- **P4. Guard `LogChanges`.** Verified; fixed with B2. Return before building any text when there
  is no logger, the type is suppressed, or the logger is not enabled for Information, and skip
  objects with nothing to commit.
- **P5. Stop allocating per added row.** Verified. `NucleusCollection.Add` builds
  `NotifyCollectionChangedEventArgs` per item with no subscriber, and a load's collection
  expression binds to that `Add`. The other half, an array per streamed row in
  `ObjectDataManager.LoadStreamAsync`, went with the audit bugs.
- **P6. Bind rows without wrapper objects.** Read. `DataSet.BindRows` builds a `DataSetRow`, a
  `DataSetValue[]` and a `DataSetValue` per column for every row (about 450 B a row at 10 columns);
  `Bind<T>()` in the same file already loops the row array by index. Presize each object's property
  dictionary for its column count and create `_newProperties` only when something is edited.
- **P7. Skip walks a save does not need.** Read. A save walks the graph up to six times (validation,
  the manager's link types, the save builder, the query constructors' own recursion, a walk per root
  at commit, the commit's recursion). First step: a cached per-type answer to "can this graph reach
  a forceSave link", so the link-type and commit walks are skipped for children without forceSave
  links. Later step: walk once and pass the graph through validation, building and commit.
- **P8. Make the walk itself cheaper.** Read. The seen-set grows from 3 slots and records every
  child, though a child can sit under only one parent (`BindParent` refuses a second); `foreach`
  boxes the `NucleusChildCollections` iterator and each `ChildObjects` enumerator; validation copies
  the result through several LINQ steps. This and P2-P3 explain validation's 7.8 KB per parent
  (measured).
- **P9. Translate a predicate once per load.** Read. `Classify` and then the parse each normalise
  the expression again (twice per load, three times in `RowQuery.CountAsync` and `DeleteWhere`), and
  `Evaluator` compiles a lambda for every captured variable each time, about 7 KB each (a 5-term
  predicate: 22 KB measured). Normalise once, and read closure fields with reflection instead of
  compiling them.
- **P10. Build the write chain once and in order.** Verified for the first part.
  `InsertQuery.Create` calls `Count()` and `ElementAt(i)` on a lazy query on every loop pass (O(n²)
  for 100 objects or fewer); above 100 it builds in parallel into a `ConcurrentBag`, so statement
  order varies run to run; `UpdateQuery.Create` always uses the bag. Materialise once and loop.
- **P11. Cache per-table write answers.** Read. `Query.AddWriteTerms` filters the table's columns
  for overridden ones per object; the renderers quote every column name and format every variable
  per row; parameter lists are rebuilt several times per command; each `Query` allocates about seven
  collections it may never use.
- **P12. Use the compiled getter in the binder.** Read. `ChildGraphBinder.CollectionOf` and
  `LinkedCollectionOf` call `property.Getter.Invoke` (reflection) per object where
  `IPropertySchema.GetValue` has a compiled delegate; `MarkLazy` allocates a closure per lazy
  property per object; `AttachLoaded` builds a set of IDs even when the collection is empty.
- **P13. Smaller per-call costs.** Read. `SetValue` boxes value types to compare them;
  `NucleusChildCollection.BindChild` allocates a closure per add (verified); a collection subscribes
  to its own `CollectionChanged`, so every mutation builds event arguments; `NotifyParent` builds a
  `PropertyChangedEventArgs` per change per level; `AuditLoggerFor` creates a delegate per root;
  `EvictCache` recomputes the same per-type answer per save; `CoerceBoundValue` looks up the
  property for every integer column of every row.
- **P15. The SQLite write path allocates five to eight times what the others do.** Measured: the
  1,000-row split save allocates about 97 MB on SQLite against 13-14 MB elsewhere, the 20,000-row
  bulk save about 2.5 GB against 120-135 MB. SQLite has no bulk engine, so each row is a statement in
  commands of up to 2,000 parameters, and (inferred from the driver) binding each statement scans
  every parameter of the command. Reuse one prepared single-row command, or write multi-row
  `VALUES`. The largest change here.

Also open, from before: graph saves allocate 6-17% more than before the child model, accepted in
the first baselines; P2, P3, P7 and P8 recover part of it. Unchanged loaded children are no longer
rewritten (see "Saving a parent rewrote every loaded child" under Fixed).

### Found with the child model (November 2026)

- **`IPropertySchema.Create` and `PropertySchema.PropertyCreator` are what is left of
  `[LinkedChildCollection]`.** The attribute and its schema members were removed in the naming
  clean-up. These two made its v1 collection; `Create` now always throws and both are obsolete.
- **A linked, not owned, collection was not built; it now is** (November 2026):
  `NucleusLinkedCollection<TMany, TOne>`, kept by a key the many type
  declares and `[LinkedBy]` names, loaded on the first read of a member or included, its new and
  changed members saved with the one, a removed stored member deleted when its type is
  `[Deletable]`, and a delete of a one with members refused. What changed from the note that stood
  here: the members are saved with the one, because a one and its members are recorded in
  one transaction; and a delete is always refused, with no cascade, since the members outlive
  any one use of them. Open, and in the guide's known limits: the refusal is a check in the delete's
  transaction, not a foreign key, so a member inserted concurrently is not seen; and the one must be
  a root, because a parent's cascade deletes children without loading them. Cover:
  `LinkedCollectionTests` and `LinkedCollectionIntegrationTests` on every provider.

### A linked object's first read is synchronous (November 2026)

The first read of a `[LinkedObject]` is synchronous, on a connection of its own, and there is no
asynchronous form for a reference. A lazy or linked collection's first read is synchronous too,
but it has `LoadAsync()`, so for a collection that is the caller's choice. Rows
that read the same reference without `Include` each load their own instance, unless the type is
`[Cacheable]` and the manager loaded the rows, which answers from the cache. Listed in the guide's
known limits. Found with the linked object read (see Fixed).

### On SQL Server, reads wait behind saves (November 2026)

Readers still wait (not deadlock) behind saves, because locking read committed scans an
unindexed predicate's table and waits on any row a save holds. READ_COMMITTED_SNAPSHOT on the
database removes that wait. Since November 2026 the provider turns it on for a database it creates
(`SQLConfiguration.ReadCommittedSnapshotOnCreate`, after `CREATE DATABASE`, `WITH NO_WAIT`) and
logs a warning at initialise when an existing database has it off; it never alters an existing
database, which would need every other connection closed and would change every application on
it. Cover: `SqlServer\ReadCommittedSnapshotTests`, whose database-creating tests skip when the test
login cannot create a database. Listed in the guide's known limits. What is
left of the deadlocks under concurrent saves (see Fixed).

### `IPropertySchema.SkipOnDbRead` is read by nothing (November 2026)

`IPropertySchema.SkipOnDbRead` is set for a `[ParentObject]` and read by nothing. It is marked
obsolete; it is public surface, so it stays until a major version. Found with links to child types
(see Fixed).

### Timestamp in memory is the client clock (decided October 2026)

The server stamps `Timestamp` on every write, and output parameters can now read a stored value
back, but `CoreObject.Timestamp` deliberately does not: in memory it is the client clock. Until
November 2026 that was the clock at the first save, and an update left a loaded object with its
loaded value. Now every row a save wrote (the object saved, and each dependent that was new or
changed) takes one boxed clock value per save when it commits, and a rollback puts the old value
back, which costs one box per saved root and nothing per row. Reading it back was measured with the save
benchmarks (`tests/TFM.Nucleus.IntegrationTests/Benchmarks`, 15 interleaved rounds, median
overhead against the client clock), against a 2% bar:

| Path | SQL Server net10 | SQL Server net8 | PostgreSQL net10 | PostgreSQL net8 |
|---|---|---|---|---|
| one object at a time (x20) | +2.3% | -2.0% | +4.7% | +12.6% |
| batch of 100 | +10.4% | +12.4% | +263.6% | -4.5% |
| split sync path (1,000) | +13.0% | +14.9% | +16.0% | +15.7% |
| bulk engine (20,000) | +47.3% | +65.5% | +32.8% | +26.1% |

The PostgreSQL batch figures swing with run-to-run noise at a 15 ms median; every other path is
consistently 10% or more slower, so Timestamp keeps the client clock. Re-measure before turning it
on: mark the property `ReturnOutputToProperty` and run the benchmarks with `NUCLEUS_BENCHMARK=1`.

### Found with the PostgreSQL provider (October 2026)

- **Every PostgreSQL table carries a notification trigger**, created at initialise so a
  subscription needs no DDL rights at run time. Each statement that writes a table queues one
  `pg_notify`, whether or not anything listens. NOTIFY is cheap and coalesces within a transaction,
  but a write-heavy table pays it on every statement.

### Found with the SQLite provider (November 2026)

- **A nullable enum column's `ClrType` is the enum, not `int`.** `ColumnSchema.GetClrType` maps an
  enum property to `int`, but `Nullable<TEnum>.IsEnum` is false, so a nullable enum falls through
  to its underlying enum type. SQL Server and PostgreSQL never notice, because their drivers hand
  back an `int` regardless; the SQLite provider reads each value as the column's `ClrType`, so it
  narrows an enum target to its integer itself. Left as it is in Data: changing `ClrType` changes
  what every provider binds for a nullable enum, and that wants its own review.
- **The parallel write path cannot work on a single-writer engine.** An oversized write without a
  bulk engine fanned out to child contexts, each on its own connection and transaction; under
  SQLite each child waits on its parent's write lock until it times out. Data now lets a context
  say so (`DbDataContext.AllowsConcurrentWriters`), and the parts run in order on its own context.
  Since the explicit commit the parts run in order on every engine, and the flag is obsolete.
- **A large multi-statement command is quadratic in Microsoft.Data.Sqlite.** The driver binds each
  statement by searching every parameter of the command, so a command of N single-row statements
  over P parameters costs about N x P. At 30000 parameters an oversized save held the write lock for
  33 s, long enough for the other target framework's run to time out on the shared file; at 2000 it
  took 0.3 s. `SqliteConfiguration` therefore defaults `MaxParametersPerCommand` to 2000. Raising it
  is the first thing to measure if SQLite saves look slow.
- **A lower SQLite limit would write faster still, but deletes stop it.** At 500 parameters a command
  the oversized saves measured about five times faster again (a late unique violation over 12001
  rows: 1 s at 2000, 0.2 s at 500). `DeleteWhereQuery.ForIds` puts a fixed 1000 IDs in each
  statement, and the delete path refuses a chunk over the limit, so any limit under about 1001
  breaks deleting a list of objects. Chunking the IDs by the provider's limit would lift that; it
  changes the delete batches every provider sends, so it is left for its own change.

### The parameterless AddNucleus() enables schema migration

`NucleusOptions.ReadOnlySchema` defaults to `true`, but the convenience `AddNucleus()` overload sets
it to `false`. The shortest registration is therefore the one that allows automatic schema creation
and migration against the target database.

**Deliberately not changed.** Two production sites already call it and depend on the migration
behaviour; flipping the default would silently stop their schema updates, which is a worse failure
than the surprise. Changing it is a product decision, and the safe route is an explicit
`AddNucleus(o => o.ReadOnlySchema = true)` at the call sites first. The behaviour is called out
in the registration reference in `docs/guide/getting-started.md` and in the guide's known limits.

### IDE0040, roughly 450 occurrences

Explicit `public` on interface members, which the shared `.editorconfig` asks to be left implicit.
Suppressed in `Directory.Build.props`. It is a mechanical edit across most files in the repo and was
kept out of the behavioural fixes so that this diff stayed reviewable.

### xUnit1051 in the test projects

Calls taking a `CancellationToken` should pass `TestContext.Current.CancellationToken`. Suppressed
in `tests/Directory.Build.props`. Worth doing, but it is a test-wide edit.

### Features that are declared but not implemented

- `[RetrieveOnDemand]` makes a child collection lazy (November 2026). On any other property it still
  marks lazy per-property loading, which nothing implements; the dead `TryRetrievePropertyFromDB`
  that pretended to back it has been deleted.
- `[InitializePriority]` is parsed into `ObjectSchema<T>.Priority`, which nothing reads; the
  ordering code that consumed it is gone.

Both are listed in the guide's known limits, with `[LinkedObject]`'s `linkedIDProperty`.

### The legacy ChangeCollection is vestigial

`CoreObject.Changes` and the `Change` / `ChangeCollection` types are the v1 change tracking. Nothing
populates them any more: audit summaries are built from the property dictionaries, and
`DataChanged` now reports the same state as `HasChanges`. `Changes` is documented as legacy and is
safe to call, but removing it is a public API change and was left for a major version.

### Commented-out code

Sizeable commented blocks remain in `PropertySchema.cs` and `DBColumn.cs`, and single commented
lines in `IObjectSchema.cs`.

### A caller-owned context commits on dispose and evicts no cache (November 2026)

A transactional `IDataContext` that the caller opens (`IObjectDataManager.CreateContextAsync<T>`,
or `IDataProvider.CreateContextAsync`) commits when it is disposed unless something marked it for
rollback. That is the v1 transaction tree, unchanged since the first SQL Server provider: every
failure the context caught around a command marked the tree, and the root committed or rolled back
when disposed. `Dispose` cannot tell that an exception is in flight, so a failure the context does
not see, such as an exception in the caller's code between two saves, commits the first.

When this was first recorded it said a failed statement does not roll the context back. That was
wrong: `DbDataContext.Failure` marks it. What did not mark it was a failure before any command ran
(a validation rule, a query that could not be built, a refused delete).

The staged fix, part one (v2):
- `Commit` and `CommitAsync` on `IDataContext`, which the manager and the schema update now call;
- any failure of a context's own operation marks it for rollback;
- `RequireCommit` on the provider options, under which a context disposed without a commit rolls
  back; without it, dispose still commits and logs a warning.

Still open until v3 makes `RequireCommit` the only behaviour, which changes every caller-owned
context and so needs a coordinated release. Until then a consumer commits explicitly and turns
`RequireCommit` on. A save or delete through such a context also evicts no `[Cacheable]` cache,
because eviction is the manager's job. Cover: `TransactionCommitIntegrationTests`, on every
provider.

### An asynchronous execute drops a query without a table schema (November 2026)

`DbDataContext.ExecuteSQLAsync` groups a query chain with `Query.GroupByTableSchema`, which skips
every query whose `TableSchema` is null, so none of them runs and nothing says so. The synchronous
`ExecuteSQL` splits the chain without grouping and runs them. Nothing Nucleus builds lacks a table
schema, so only a hand-built query passed to `ExecuteQueryAsync` is affected. Verified by reading
the code; no test reaches it.

### Failures and retries, what is left (November 2026)

What the failure contract below ("A database failure was a bare Exception") does not do yet:

- **The retry policy is off by default and does not cover a caller-owned context.** `RetryAttempts`
  (1 by default) retries the manager's own operations only. A caller-owned context is the caller's
  transaction, so its unit of work is the caller's to repeat. Either way each attempt multiplies
  `ConnectAttempts`. Cover: `RetryPolicyTests`, `RetryPolicyIntegrationTests`.
- **A failed write on a context with no transaction always reports `MayHaveBeenApplied`.** Each
  statement commits as it runs and a command can hold several, so Nucleus does not know how much
  ran. That is true even of a lock wait that ran out before the first statement, which therefore
  is not transient. Cover: `SqliteBusyWaitTests`.
- **`CommitOutcomeUnknown` leaves the objects unsaved.** The manager marks objects saved only once
  the commit returns, so after a commit whose outcome is unknown they are new or changed again,
  though the rows may be there. A blind re-save of a new object then fails on its key. Cover:
  `PostgresCommitFailureTests`.
- **SQLite has no statement time limit.** Microsoft.Data.Sqlite's command timeout is the wait for
  the write lock, not a limit on a running statement, and a cancelled token does not stop a
  statement under way (measured: a 4 s read with a 1 s timeout and a 1 s cancellation ran to its
  end). The provider leaves the command timeout at `BusyTimeoutSeconds` and ignores
  `StatementTimeoutSeconds` and `SchemaCommandTimeoutSeconds`.

## Fixed

### A broken foreign key did not say which reference (November 2026)

- **A key violation was a plain `DatabaseException`.** A caller could tell it was a key, not which
  one. It is now a `ForeignKeyViolationException` (Abstractions), resolved like a unique violation
  on the failure path only: `RelationalDataProvider.TryGetForeignKeyViolation` (virtual, so a
  provider that predates it still compiles) gives the engine's key name (SQL Server's 547 message,
  PostgreSQL's `ConstraintName`), and `TranslateFailure` maps it to the `ForeignKeySchema` with that
  physical name. SQLite's 787 names no key, so its exception names nothing. Cover:
  `ARefusedDelete_NamesTheReferenceThatStillPointsAtTheRow` and
  `ARowPointingAtARowThatIsNotThere_IsRefused_AndNothingIsWritten` on each engine, and the
  `foreign-key-violation` sample.

### Foreign keys on SQLite (November 2026)

- **SQLite keeps keys only in a table's definition**, and checks them only on connections that turn
  them on, which no transaction may change. `ISchemaDialect.ForeignKeysInline` says so: the dialect
  writes a key with its column in `CreateTable` and `AddColumns`, and the update marks a table whose
  keys differ for a rebuild (`TableSchema.RebuildForKeys`, read from the catalog as it stood before
  the update, an adopted table under its old name) instead of adding or dropping a key on its own.
  A rebuild copies rows unchecked; the update counts each rebuilt table's orphans and warns.
  `DbDataContext.PrepareConnectionAsync` runs between open and begin: a SQLite context turns
  `foreign_keys` on (off under `ForeignKeys = false`), and the schema update's context turns it off
  and `legacy_alter_table` on, so a rebuild's rename leaves other tables' `REFERENCES` text alone
  and they point at the new table once it takes the name. Both are set on every open because the
  connections share one pool. While it was built, an update left `RebuildForKeys` set, so a check
  after the upgrade still found the tables not current; the compare now clears it. Cover:
  `SqliteForeignKeyUpgradeTests`, `SqliteForeignKeyIntegrationTests`,
  `WithInlineKeys_ATableWithoutItsKeys_IsRebuiltWithThem_AndNoKeyIsAddedOnItsOwn` and
  `WithInlineKeys_KeysThatMatch_RebuildNothing_AndTheCompareClearsAnEarlierRebuildMark`.

### Foreign keys, and an asynchronous save that reversed a table's rows (November 2026)

- **Nucleus created no foreign keys.** The graph rules (a child needs its parent, a one with
  members is not deleted) were checks in Nucleus, so a member inserted after the delete's check, a
  write from outside Nucleus, or a Nucleus bug (a parent delete once orphaned the children it had
  not loaded) left rows pointing at nothing. `SchemaManager.BuildSchema` now derives a key per
  reference (`ForeignKeySchema`: `[ParentObject]`, stored `[LinkedObject]`, `[LinkedBy]` key,
  `[ForeignKey(typeof(T))]`), the check compares the `FK_` keys the catalog holds
  (`SchemaComparer.DiffForeignKeys`) and the update creates the missing ones after the indexes,
  unchecked over rows that already point at missing rows. A key is kept only where it holds for
  every row: both types' rows kept by this provider (a provider can hold tables for types another
  routes elsewhere, and a key to one would refuse every insert), and a referenced type with no
  stored derived types. Cover: `ForeignKeySchemaTests` and `ForeignKeyIntegrationTests`.
- **A rebuild would have broken on a key.** A rebuild renames the table and drops it after the
  copy; a key pointing at it went with the renamed table and stopped the drop. Keys to and from
  every table the update changes or adopts are dropped in the rebuild's transaction and created
  again after the commit. Cover: `ATableToRebuild_LosesTheKeysFromAndToIt_InTheTransaction_BeforeItIsRenamed`
  and `ARebuildOfATableKeysPointAt_KeepsItsRows_AndTheKeys`.
- **A soft-deleted row would have stopped a hard delete for good.** Soft-deleted rows stay in their
  table and there is no purge, so a NO ACTION key from one would refuse deleting the row it points
  at forever. A hard delete now first sets to null, in the soft-deleted rows only, the references
  to what it deletes (`DeleteTarget.ClearedReferences`), on SQL Server from the level's kept IDs.
  Moving soft-deleted rows to a table of their own was weighed and left for a separate proposal.
  Cover: `AHardDelete_ClearsWhatSoftDeletedRowsPointAt_ButALiveRowStillStopsIt` and the
  `delete.clears` goldens.
- **An asynchronous save inserted a table's rows last first.** `Query.FlattenAndDetach`, which the
  asynchronous path regroups a write by table with, pushed each query's linked queries in order and
  popped the last first, so the rows of a batch ran in reverse, undoing the order the save gave
  rows that point at each other. It now walks in pre-order. Cover:
  `GroupingAWriteByTable_KeepsTheOrderTheRowsWereBuiltIn_AndPutsParentsFirst` and
  `RowsOfABatchThatPointAtEachOther_AreInsertedAfterTheRowsTheyPointAt`.

### Rows written before the rows they point at, and soft children left under a deleted owner (November 2026)

Found while planning foreign keys, which would have turned each of these into a refused statement.

- **A save wrote types in the order it met them.** `SaveGraph.VisitLinks` records a forceSave link
  before the links below it, so a link that points at another new link was written first; the
  roots of a mixed-type save were written in input order, so `Save([resident, keeper])` wrote the
  resident before its new keeper; and members were grouped by type, so a member of a one written
  in a later group came first. Nothing refused it, because there are no foreign keys, but a reader
  in between saw a row pointing at nothing. `ReferenceGraph` (Data) now records which types' rows
  point at which others' (a stored `[LinkedObject]`, a `[LinkedBy]` key, and every child's
  references folded into its root's chain), and `CreateSaveQueries` puts the link, root and member
  writes in a stable topological order over it, so only what must move moves. Cover:
  `ReferenceOrderTests`.
- **New rows of one table that pointed at each other were inserted in any order.** A batch over
  100 rows was built in parallel into a `ConcurrentBag`, which keeps no order. The save now orders
  a batch through `TableSchema.SelfReferences` (a link typed as the entity or a base of it) and
  `InsertQuery.Create` builds a large batch in parallel into an array, in order. Cover:
  `RowsOfABatch_AreInsertedAfterTheRowsTheyPointAt`.
- **A delete of several types ran them in the order it met them.** Only a delete of objects that
  share a base entity type covers several types; it now removes a type before the types it points
  at. Its graph is built from the deleted types' own schemas, because every reference between them
  is declared on one of them, and asking the schema provider for every type would build every
  entity type in the process to delete two.
- **A soft child under a hard owner was left pointing at a deleted row.** A child type's own
  `[Deletable(DeleteBehavior.Soft)]` beats its owner's behaviour, so deleting a hard owner removed
  its row and flagged its children, which kept the deleted row's ID. `ObjectSchema` now refuses a
  child whose effective behaviour is soft under an owner that can be deleted and is deleted hard.
  Cover: `SoftChild_UnderAHardOwner_FailsTheSchema` and
  `SoftChild_UnderAnOwnerNotDeletable_IsAllowed`.
- **The golden of the linked-members check copied its builder.** The corpus built the select by
  hand, so a change to `LinkedMembersOf` would not have moved the golden. Both now call
  `RelationalDataProvider.MembersQuery`. The check counts live members only, as before: a
  soft-deleted member points at its one by a key a hard delete of the one will clear, with the
  foreign keys.

### A failed cache refresh retried on every read, and the cache handed out its list (November 2026)

- **A failed refresh retried on every read, silently.** When a background refresh failed (the
  database down, say), `NucleusCache.Refresh` reset `RefreshScheduled` and the entry was still past
  its interval, so the next cached read started another background load, and so did every read
  after it for as long as the outage lasted; the failure was swallowed, because the cache had no
  logger. It predates the refresh fix above. The entry now carries `NextRefreshTicks`, set one
  `refreshInterval` after a failure and written before the flag is reset, and a refresh starts
  only past it; the failure is logged as a warning through `NucleusCache.Logger`, which the
  manager sets to its own logger. Cover:
  `Refresh_ThatFails_WaitsOneIntervalBeforeTheNext_AndKeepsServingTheCachedRows`,
  `Refresh_ThatFails_LogsOneWarning` and `TheManager_GivesTheCacheItsLogger_UnlessItHasOne`.
- **The public `NucleusCache.GetOrLoad` handed out the cache's own list.** It is reachable through
  `ObjectDataManager.Cache`, so a caller that edited it changed it for every reader, and since the
  cached stream read that list, an edit while a query of every row was enumerated threw
  "Collection was modified". The cache now keeps its rows as an array it builds at the fill and
  no caller holds; `GetOrLoad` and `GetOrLoadAsync` return a copy (the one copy rule, moved from
  the manager into the cache), and the stream reads the array (`RowsAsync`). Cover:
  `GetOrLoad_ReturnsAListOfTheCallersOwn` and
  `EditingAListTheCacheHandedOut_DuringAStream_DoesNotBreakTheStream`.

### Queryable operators read every match, and the cache had one fill path (November 2026)

- **P14. `FirstOrDefaultAsync`, `AnyAsync` and `CountAsync` on a queryable loaded every match and
  its children.** They, and `FirstAsync`, `SingleAsync`, `SingleOrDefaultAsync` and
  `LongCountAsync`, streamed the whole result through `LoadStreamAsync`, which first ran every
  child query, and worked on the rows in memory. The guide's getting started page shows them as
  the way to read. They now run in the database, reached from the extensions through an internal
  interface the adaptor implements (`INucleusQueryOperators<T>`): First reads one row and Single
  two (`ObjectDataManager.LoadTopCore`, which `LoadFirst` now shares), with the query's
  exclusions, inclusions and deleted rows; Any reads one ID and Count counts, as row reads with
  no child query (`AnyCoreAsync`, `CountCoreAsync` over `RowQuery<T>`). Cover:
  `AdaptorOperatorResolutionTests` (the Take each builds), `ManagerLoadIntegrationTests`
  `FirstAndSingle_OnAQuery_ReadTheRowsTheyNeed` and `AnyAndCount_OnAQuery_RunInTheDatabase` on
  every provider, and the `rows.any` goldens.
- **A row read saw live rows only.** `RowQuery<T>.IncludeDeleted()` now reads the rows marked
  deleted too, which Count on a query built with `IncludeDeleted()` needs. Cover:
  `IncludeDeleted_SeesSoftDeletedRows_OnALoadAndOnAQuery` and the
  `rows.*.soft.includedeleted` goldens.
- **A load on the manager for every row of a `[Cacheable]` type did not use the cache.**
  `LoadAsync<T>()` with no predicate read the table, because `NucleusCache` filled only on the
  synchronous path, and `LoadByIDAsync` and `LoadAllAsync` filled a cold cache synchronously,
  blocking the caller's thread. The cache now fills on either path, single-flight across both
  (an asynchronous fill runs on the thread pool, so a synchronous caller can wait for it; it runs
  without a caller's token, so one caller cancelling does not fail the others). An unfiltered,
  live, whole load answers from it: `LoadAsync`, `Load`, `LoadAllAsync`, and the stream
  (`LoadStreamCoreAsync`), which is what `Get<T>()` with no `Where` enumerates for `ToListAsync`
  and `await foreach`. Cover: `NucleusCacheTests`, among them
  `AQueryWithNoWhere_OfACacheableType_StreamsFromTheCache`.
- **A read by ID of a `[Cacheable]` type could miss a stored row.** `FromCache` filled the cache,
  then looked the ID up with `TryGetById` as a second step, which found no entry when a save
  evicted the type in between, and returned null. The lookup now reads the entry the fill
  returned (`NucleusCache.GetOrLoadById`, `GetOrLoadByIdAsync`). Cover:
  `GetOrLoadByIdAsync_AnEvictionDuringTheFill_StillFindsTheRow`.
- **Every caller of `Load<T>()` shared one list.** The cached `NucleusCollection` was handed out
  as it was, so a caller that added to it or removed from it changed it for every reader.
  Every read of every row now returns a list of its own over the shared objects. Cover:
  `LoadAll_CacheableType_ChangingTheListLeavesTheCacheAlone`.
- **The cache's refresh did not run in the background.** Past `refreshInterval` the refresh task
  only put an unstarted `Lazy` in place of the entry, so the next reader loaded the table in-line
  and waited for it, and its `catch` could never fire. The stale entry was never served during a
  refresh, which is what the interval promises. The refresh now loads first and then swaps the
  new entry in, only over the entry it refreshed, so an eviction by a save meanwhile wins and the
  refresh cannot bring back rows read before the save. Cover:
  `Refresh_ServesTheStaleEntry_WhileTheReloadRuns` and
  `Refresh_ThatFinishesAfterAnEviction_DoesNotBringBackWhatItRead`.
- **The `cacheable` doc sample said the wrong read filled the cache.** Its first read was
  `Get<T>().FirstOrDefaultAsync`, which never touched the cache; the `LoadByIDAsync` after it
  filled it. The sample now fills it with `LoadAsync<T>()`.

### A rollback held its locks until the root was disposed (November 2026)

`IDataContext.Rollback` and `RollbackAsync` only marked the tree (the v1 tree design), so the
transaction rolled back at dispose or at a commit, and until then its writes kept their locks and
its objects stayed marked saved. Its XML comment said it rolled back at once. A caller that rolled
back and went on (to log, or to read through the manager) held every row it had written, and on
SQL Server under locking read committed any reader of those rows waited. Found with the deferred
read test, which had to dispose the context to let its read finish. A rollback now ends the
transaction at once, as a root's commit does; on a child it ends the whole tree's transaction, and
a commit of any context of the tree after it throws `TransactionDoomed`. `TryRollback` and
`TryRollbackAsync`, which the manager calls in its catch blocks, roll back the same way and log a
failed rollback instead of throwing it; the schema rebuild's rollback uses them. The failure
paths (`Doom`, `Failure`) still only mark, because an exception filter must not run a command.
A cancelled commit rolls back at once too. Cover:
`TransactionCommitIntegrationTests.ARootRollback_EndsTheTransactionAtOnce` and
`AChildRollback_EndsTheWholeTreeAtOnce` (both paths), and
`ChildModelIntegrationTests.ALazyRead_RunsOutsideTheCallersTransaction`, on every provider.

### The linked-members check read past an uncommitted member under snapshot reads (November 2026)

A delete of a one checks its linked collections for members with a plain select in the delete's
transaction. Under locking read committed that select waits for a member another transaction is
inserting, and sees it once it commits. Under READ_COMMITTED_SNAPSHOT (Azure SQL's default, any
database a DBA set, and now every database the provider creates) it read the last committed rows,
passed the uncommitted member, and the delete went ahead, leaving the member pointing at a deleted
row. `RelationalDataProvider.LinkedMembersOf` now marks both of the check's reads
`Query.WaitForPendingWrites`, which SQL Server renders as `WITH(READCOMMITTEDLOCK)`; PostgreSQL
cannot wait for an uncommitted insert under read committed, so there the open limit ("a member
inserted concurrently is not seen") stands. Cover:
`ReadCommittedSnapshotTests.ADeleteOfAOne_WaitsForAMemberBeingInserted_ThenIsRefused` and
`ACreatedDatabase_HasReadCommittedSnapshotOn_AndTheCheckStillWaits`, which fails with the hint
removed, and the `select.linked.members.ofdelete` golden.

### Found by the documentation review (November 2026)

Three places where the code fell short of what the guide said, found by checking the guide against
the code.

- **A cancelled commit committed on dispose.** `DbDataContext.CommitCore` threw on a cancelled
  token without marking the transaction, so without `RequireCommit` disposing the context committed
  what the caller had just cancelled. `IDataContext.RollbackAsync` threw before it rolled back,
  with the same result. Both now mark the transaction and then throw. Cover:
  `TransactionCommitIntegrationTests.ACancelledCommit_RollsBack_RatherThanCommitOnDispose` and
  `ACancelledRollback_StillRollsBack`, on every provider.
- **Three opens were not retried.** The SQL Server and SQLite catalog readers and the SQL Server
  change subscription opened their connection once; only contexts, and PostgreSQL's reader and
  subscription, went through `ConnectionOpener`. All do now, with `ConnectAttempts` and the
  provider's own classifier (`SqlServerErrors.IsTransient`, `SqliteErrors.IsTransient`). No test
  of its own (see Open).
- **A SQLite schema statement waited 15 minutes for the write lock.** Schema statements set
  `SchemaCommandTimeoutSeconds` as the command timeout, which on SQLite is the wait for another
  connection's write lock. `DbDataContext` now sets it only where `AppliesStatementTimeout` is
  true, and `SqliteCatalogReader` not at all, so the wait stays `BusyTimeoutSeconds`. Cover:
  `SqliteSchemaStatementTests`.

### Found with the linked object read (November 2026)

Found while designing linked collections, by an integration test
that saved, reloaded and read a `[LinkedObject]`. Both failed on SQL Server, PostgreSQL and SQLite.

- **A loaded `[LinkedObject]` could not be read.** A load stored the referenced entity's ID, and
  `CoreObject.GetValue<T>` cast it to the entity type: `InvalidCastException: Unable to cast object of
  type 'System.Guid'`. No test read a reference after a reload; the existing ones only saved new
  objects and filtered by the reference. The first read now loads the entity through the loader of
  the stack that loaded the object (`ILinkedObjectLoader`), and `Include` loads references with the
  rows in the same round trip. Cover: `LinkedObjectReadTests`, and the `ALinkedObject_*` and
  `Include_*` tests of `ChildModelIntegrationTests` on every provider.
- **A loaded object with a `forceSave` link could not be saved.** `SaveGraph.VisitLinks` read each
  link through its property, so the same cast threw on every save of a loaded object of such a
  type, on every save path. It now reads the link as held, and a link never read since the load is
  skipped without being loaded. `ObjectSchema.GetLinkedObjects` had the same read and now returns
  held links only. Cover: `LinkedObjectReadTests.ASaveWalk_*` and
  `ALoadedObject_WithAForceSaveLinkNobodyRead_SavesItsOwnChange`, confirmed failing on the commit
  before the fix.

### SQL Server deadlocks under concurrent saves (November 2026)

Seen once as a deadlocked cleanup delete in `ChildModelIntegrationTests` (the net8.0 and net10.0
runs share the database), then reproduced with `SqlServer\DeadlockStressTests`: 16 workers each
saving, reading, removing from and deleting a roster with lines and breaks deadlocked 6 to 11 times
per 480 cycles on the SQL Server 2008 R2 test server and on SQL Server 2025 (LocalDB).

- **Root cause.** Child queries and cascade deletes reached their parents through a subquery over
  the parent's table (`[Parent] IN (SELECT [ID] FROM parent WHERE ...)`). On small tables both
  servers ran that from the child side: a clustered scan of the child table, probing the parent for
  each row. The statement held a lock on a child row of some other parent while it waited for that
  parent's row; a save of that parent held the parent row (X) and then asked for its own child
  row: the opposite order. Captured as 74 deadlock graphs on LocalDB (every one a parent save
  against a cascade delete or a child-collection read, including plain loads), and on 2008 R2 by
  polling `sys.dm_tran_locks` during the stress (its `system_health` ring buffer XML is cut off
  before recent events): there the grandchild query held a break row while it read the break's line,
  against a save holding the line and converting to X on the break. With 20,000 rosters the
  optimizer drove from the parent and seeked the `Parent` index, and 960 cycles ran clean, which is
  why it shows on new and small tables, or on plans compiled while they were small.
- **Fix.** `SqlServerQueryRenderer` keeps each level's keys in a table variable read from that
  level's table alone, and the next level selects by them; a delete keeps each level's IDs the same
  way and evaluates its condition once (previously once per level, so a parent committed between
  statements could, by reading, be deleted without its children; not tested). No statement reads
  two user tables, so none can hold one table's row while waiting on another's, whatever the plan.
  After the fix: 0 deadlocks in 3,360 cycles plus three rounds of 48 workers alongside every SQL
  Server suite on 2008 R2, and 0 in five such rounds on LocalDB. Cover: `SqlServerKeptIdsTests`
  (every statement names one table), the goldens, and the opt-in stress test. Hints (`FORCESEEK`,
  forced join order) were rejected: version-dependent, and `FORCESEEK` fails outright (8622) when
  the parent index is missing.
- **Connect timeout.** The same load made connection opens fail after `SQLConfiguration`'s fixed
  3 second connect timeout (`SqlException -2` in the post-login phase), 282 of 1,440 cycles in one
  LocalDB round. It is now `ConnectTimeoutSeconds`, still 3 by default. The README claimed "4
  connect retries"; the driver's `ConnectRetryCount` only reconnects a broken idle connection and
  does not retry a first open, so the README now says so. Whether the server or the client's thread
  pool was the bottleneck was not isolated.

### Found with links to child types (November 2026)

Raised by a consumer that needed a root to refer to another root's child, a child type.
Nothing refused a `[LinkedObject]` typed as a child, and nothing tested one.

- **A child loaded on its own could not read its parent.** A child loaded by a link, by `LoadByID`
  or by a query of its type held its parent's ID under `Parent` (the parent column is bound like
  any other; `IPropertySchema.SkipOnDbRead` is set for it but nothing reads it), and the read cast
  the ID to the parent type: `InvalidCastException`. `CoreObject.StoredValueAs` now resolves a
  stored ID under a `[ParentObject]` as it does under a `[LinkedObject]`, and `ChildGraphBinder`
  binds the stack's loader to every child type it completes, so the parent comes from the manager
  or context that loaded the child. Cover: the child tests in `LinkedObjectReadTests`, and the
  `ALinkToAChild_*`, `AChild*_Reads*Parent*` and `Include_LoadsALinkedChildInTheSameRead` tests of
  `ChildModelIntegrationTests` on every provider.
- **A `forceSave` link to a child would save the child as a root.** The link path validates a
  link as a dependent, so the child's own refusal of a root save was skipped, and
  `CreateSaveQueries` wrote it outside its parent. `SchemaManager.BuildSchema` now refuses it.
  Cover: `LinkedObjectReadTests.AForceSaveLinkToAChild_FailsAtSchemaBuild`.

### A failed save kept the values the server returned (November 2026)

Found while designing the retry policy, which replays a save that failed. `CoreObject.AcceptServerValue`,
which every `ReturnOutputToProperty` read-back calls as the write returns, dropped the object's
pending value and stored the server's as committed straight away, before the transaction
committed. A save that failed after a read-back, at a later statement or at the commit, rolled
back in the database but not in memory: the object held a value from a rolled-back write, its own
change was gone, and the next save, a caller's retry included, wrote that value instead of letting
the server produce a new one. A caller-owned context's undo log did
not cover it either, because the value never went through a commit.

Now the value is held on the object until its changes are committed (`CommitChanges`, which the
manager runs once its transaction commits and a caller-owned context runs as its save does), where
it replaces the pending value. Under a transaction the undo log records the property's stored and
pending values first and puts both back on a rollback, keeping any edit made since. The next save's
`PrepareForSave` drops what a save that did not commit left. Cover: `ServerValueTests`, and
`ServerValueIntegrationTests` on every provider for the paths that read back.

### A database failure was a bare Exception (November 2026)

Found while writing the same operations with a time limit, a transient retry and translated
exceptions against Nucleus and three other data libraries. Hardening a Nucleus save or read took as
much code as it did over raw ADO.NET, and the Nucleus version did not work:

- `RelationalDataProvider.TranslateFailure` returned `new Exception(message, exception)` for every
  failure but a unique violation, so `catch (NucleusException)` missed almost every database
  failure, contrary to `NucleusException`'s own documentation.
- The manager's `Save` and `SaveAsync` wrapped that again in `Exception("Error saving objects")`,
  which put the driver's exception two levels down.
- A connection that failed to open threw the driver's exception, or `ConnectionOpener`'s
  `TimeoutException`, untranslated; a failed commit or rollback threw the driver's exception; a
  failure reading a streamed row reached the caller as the driver's exception.
- Catalog reads, schema statements and change subscriptions threw bare `Exception`s, and the
  PostgreSQL subscription wrapped the caller's own cancellation in one.
- `DataProviderErrorKind.DatabaseUnavailable` was never thrown, so a check for it never matched.
- SqlClient and Microsoft.Data.Sqlite do not override `DbException.IsTransient`, which is always
  false for them (checked by reflection on SqlClient 6.0.1 and Microsoft.Data.Sqlite 10.0.15), so a
  caller's check of the driver's flag never retried on SQL Server or SQLite. Nothing classified
  their errors, which is also why `ConnectAttempts` was PostgreSQL's alone.
- Ordinary statements had no time limit of Nucleus's own, only the driver's default. The bulk
  engines and schema statements set their timeouts on the context's shared command and left them
  there, so later statements on that context ran with 60 or 900 seconds.

Now a database failure is a `DatabaseException` (Abstractions) one level over the driver's, with
`Kind`, `IsTransient`, `MayHaveBeenApplied`, `Operation` and `Attempts`, and
`UniqueViolationException` derives from it. Each provider classifies its engine's errors in one
place (`SqlServerErrors`, `PostgresErrors`, `SqliteErrors`, through
`RelationalDataProvider.ClassifyFailure`); `DatabaseFailures` (Data) decides from what Nucleus was
doing what a failed commit and a failed open are and whether the operation may have been applied.
Anything that is not a database failure propagates as it was thrown, and the manager's save no
longer wraps. `ConnectAttempts` is a shared option, retried by the provider's classification.
`StatementTimeoutSeconds` (30, the drivers' default) limits every statement, set as each one is
prepared, and a context can take its own. Cover: `DatabaseFailureTests` and the classifier tests,
`SaveFailureTests`, and `DatabaseFailureIntegrationTests` on every provider.

### A provider held two connection pools of MaxConnectionPoolSize each (November 2026)

The SQL Server and PostgreSQL configurations built a transactional connection string and a
non-transactional one with `Enlist=false`. The drivers pool by connection string, so a provider
kept two pools, each up to `MaxConnectionPoolSize`, and the server could see twice the setting
from one process. Measured on a store's PostgreSQL contract suite: up to 99 idle connections
across the net8.0 and net10.0 runs, against a `max_connections` of 100, which failed with 53300
"too many clients". Three more gaps sat beside it:

- A raw `ConnectionString` never got `MaxConnectionPoolSize`, so the driver's default of 100 was
  the real cap.
- A change subscription held a pooled connection but no context slot. Once there was one pool, a
  context could pass the slot check and then time out in the driver's pool.
- The slot count was static for each context type, so every provider of one engine in a process
  shared one budget.

Now:

- Every connection to the database has one connection string, with `Enlist=false` and the pool
  sized to the setting, raw strings included.
- The startup connections (`master`, and the PostgreSQL maintenance database) are not pooled.
- Each provider has its own `ConnectionSlots`, which its root contexts and change subscriptions
  share.
- With no connection enlisting, a transactional context inside an ambient `TransactionScope`
  would commit outside it, so it is refused (`DataContextErrorKind.AmbientTransaction`).

`PostgresConnectionLimitTests` counts the server's connections from one provider, and
`ConnectionLimitIntegrationTests` holds every engine to the limit and the refusal. The test
package's PostgreSQL cap of 20 stays.

### A context save did not follow its transaction in memory (November 2026)

Two defects with one cause: what an object saved through a caller-owned `IDataContext` held in
memory did not follow that context's transaction.

- **`CoreObject.SaveAsync(IDataContext, ...)` committed in memory before the caller committed.** It
  called `CommitChanges` as soon as the writes ran, so a caller who then rolled back kept objects
  that said they were saved and had forgotten their removals, and had to reload them.
- **`IDataContext.Save` and `SaveAsync` never committed in memory at all**, for one object or a
  batch. After the transaction committed, a new object was still new, so its next save inserted it
  again and failed on the primary key. The guide's store sample saved this way, and the integration
  tests hid it by calling `CommitChanges()` by hand after the context save.

Now the context commits each object it saves as the writes run, so the objects match the open
transaction (a second save of a new object updates it), and records what the commit changed in
the root's `CommitUndoLog`. `DbDataContext.EndTransaction`, the one place a root's transaction
ends, clears the log when the commit returns and reverts it otherwise: after a rollback, a doomed
commit or a failed commit, each object is as it was before its first save in the transaction,
with every change made since still pending, and its removals are pending again. Committing first
and reverting on failure was chosen over holding the commit back until the transaction commits,
because held back, a new object saved twice in one context would be inserted twice. The
manager's batch save of mixed types, its one save through a context's own save, turns this off
(`ISaveAcceptance`) and keeps marking its objects saved after the commit, with the audit logger.
Cover: `CommitUndoTests` (in memory) and `ContextSaveIntegrationTests` (every context save path,
on every provider).

### Found with the explicit commit (November 2026)

- **A read ended a transactional context's transaction.** `GetDataSet` read with
  `CommandBehavior.CloseConnection`, so the connection closed with the reader and took the
  transaction with it. A save after a load on the same transactional context failed, and
  disposing the context threw "This SqlTransaction has completed" (or the Npgsql equivalent).
  Reads now leave the connection open; the context closes it when disposed. Confirmed failing on
  all three engines before the fix. Cover: `AReadInATransaction_LeavesItOpenForTheWritesAfterIt`.
- **A context tree was not atomic.** A child context (`ContextOptions.Parent`, and the parallel
  write path) opened a connection and a transaction of its own, and the root committed each child's
  in turn before its own, so a failure between two commits left part of the tree committed. A
  child also could not see its root's uncommitted rows. A child now runs on its root's connection
  and transaction. Cover: `AChild_RunsInItsRootsTransaction`, `ATree_CommitsWhole_WhenItsRootCommits`.
- **The parallel write path is gone.** An oversized write the bulk engine could not take fanned out
  to child contexts. No write Nucleus builds reached it: `GroupByTableSchema` keeps only queries with
  a table schema, every save groups by insert or update, and those go to the bulk engine on SQL
  Server and PostgreSQL, while SQLite never fanned out. Its parts now run in order on the context,
  as SQLite's did. Checked by making the old branch throw: the whole integration suite and every
  provider's save benchmarks ran on it without reaching it. Timed back to back against the code
  before the change on one machine, reads (SQLite) and saves (PostgreSQL) were within run-to-run
  spread, and every benchmark passed its allocation gate.
- **A failure before any command left the transaction unmarked.** See the open entry on
  caller-owned contexts. Cover: `AFailedValidation_DoomsTheTransaction_SoTheEarlierWriteIsRolledBack`,
  `AFailureInAChild_DoomsTheWholeTree`.

### Saving a parent rewrote every loaded child (November 2026)

`UpdateQuery` linked an update for every child in each loaded collection, and an update always sets
`Timestamp`, so saving a parent touched every child row even when none changed: a parent with 500
loaded children wrote 501 rows to change one, and a child's `Timestamp` no longer said when the
child last changed. Skipping a clean child was not safe on its own, because a grandchild's write is
linked under its child's update.

`RelationalDataProvider.WriteQueries`, which every save path and every child collection's writes
go through, now writes a stored object only when it has changes of its own
(`SaveGraph.IsChanged`). A clean object's child collections are still written, after the object's
level, so a changed grandchild under a clean child is kept at any depth. The same applies to a
forceSave link or a linked collection member that is written only for what is below it. The objects
a save was asked for are always written, so saving an unchanged object still stamps it. Because the
fix is in the query builder, both write paths and every provider's bulk engine follow. Cover in
`ChildSaveIntegrationTests` (the three-level roster on every save path).

### Names that could only be changed by breaking (November 2026)

The last part of the same review, and the one deliberate break: it shipped on the v2 line, and
the exception is written into the rule in `AGENTS.md`. The README's behaviour changes carry the
table a consumer migrates by.

- **Two enums held one list.** `eServerFunction` (what `[DatabaseColumn(SetValueOverride = ...)]`
  took) and `ServerFunctionKind` (what the query model carries) were the same values, and the
  first lacked `UtcNow` and `NewGuid`, so a column could not be stamped with either. They are one
  enum, `ServerFunctionKind`, with `None`.
- **Enum names carried prefixes and capitals**: `eServerFunction`, `EDataSchemaStatus`,
  `QueryType.SELECT`, `OrderByType.ASCENDING`. They are `ServerFunctionKind`, `DataSchemaStatus`,
  `QueryKind.Select`, `SortDirection.Ascending`. A second, unused `DataSchemaStatus` in the Data
  assembly was deleted to free the name.
- **`DataType` collided with `System.ComponentModel.DataAnnotations.DataType`,** which the
  attribute's own file imports. It is `ColumnDataType`.
- **`WhereCollection.Type` shadowed `System.Type`** inside the class. It is `NodeKind`.
- **Ten attributes had no `Attribute` suffix** while seven had it. A second class under the
  suffixed name would have made `[LinkedObject]` ambiguous (CS1614), so the classes were renamed.
  Attribute usage compiles unchanged; an assembly compiled against the old names does not find
  them, so every entity and store package is rebuilt with this release. `[SuppressDB]` is
  `[NotStored]`.
- **`AddNucleus` was the one registration outside `Microsoft.Extensions.DependencyInjection`.** A
  forwarding copy would have made every call ambiguous in a file importing both namespaces, so it
  was moved.
- **Everything obsolete before the review was removed**: the SQL Server names on
  `[DatabaseColumn]` and the server functions, `[LinkedChildCollection]` and the `LinkedChild*`
  schema members, `CoreObject.AllowDeletions` and `IObjectDataManager.DataProvider`. The neutral
  interface members that defaulted to them are now required of an implementer.

The SQL goldens did not change, and every test passed under the new names without a change of
behaviour; the six tests of the removed aliases went with them.

### API surface alignment (November 2026)

The same review, the names. All additive: the old name of each is obsolete and forwards.

- **SQL Server was spelled three ways** (`SQLConfiguration`, `SqlDataProviderException`,
  `SqlServerTestDatabase`). The registration is `AddNucleusSqlServerDataProvider`, in
  `NucleusSqlServerServiceExtensions`; the old class was named `ServiceExtensions`, which any other
  library's class of that name in the same namespace would have collided with. The type
  `SQLConfiguration` keeps its name: options are bound by type, so an alias type would not bind.
- **One value had two names**: `MaxParametersPerQuery` on the options and
  `MaxParametersPerCommand` on the capabilities. The options take the second.
- **Options named after a SQL Server feature** are named for what they do: `BulkTimeoutSeconds`,
  `BulkBatchSize`, `PacketSize`, `Query.AllowDirtyRead`, `ContextOptions.WatchForChanges`.
- **`ConnectRetryCount` counted attempts.** It is `ConnectAttempts`. PostgreSQL's fixed 15 second
  connect timeout is `ConnectTimeoutSeconds`, as SQL Server's became.
- **The exceptions shared no base.** They derive from `NucleusException`.
  `SqlDataProviderException.Type` and its enum duplicated `Kind` value for value and are obsolete.
- **Child and linked collections named the same things differently.** Both interfaces answer to
  `Owner`, `ItemType`, `Items` and `RemovedItems`; `INucleusChildCollection` has `LoadAsync`, which
  only the class had; `IObjectDataAdaptor` has `Include`, which only the class had.
- **`LoadObjectWhere` loaded every match to return the first.** `LoadFirstWhere` asks for one
  row. The static API also gains `LoadByIDAsync` and `LoadAllAsync`.
- **`TryGetChildCollectionProperty` returned its result** instead of a bool and an out value. It
  has an overload in the usual shape.
- **Public types nothing uses** are obsolete: `ITFMHasChanges`, `DataProviderConnectionStatus`,
  `IInitializeWithString<T>`, `IPropertySchema.SkipOnDbRead`, and `Parameter.columnSchema`, which
  differed from `ColumnSchema` only by case. The helper classes that are public only so the other
  Nucleus assemblies can reach them (`StringExtensions`, `TypeExtensions`, `ChildGraphBinder` and
  the like) are hidden from completion.

Cover: `ApiAlignmentTests`, and the first-match, runtime and rollback tests in
`ManagerLoadIntegrationTests`.

### API surface traps (November 2026)

Found by reading the whole public surface, not by a failure. Each replaced member still works and
is marked obsolete, naming its replacement. Cover: `ManagerLoadIntegrationTests` on every provider,
and `ApiTrapTests`.

- **An untyped predicate had to be passed as its body.** `Load<T>(Expression)` and its siblings on
  the manager, the runtime and a context took a predicate's `Body`. The lambda itself compiled,
  since an `Expression<Func<T, bool>>` is an `Expression`, and then failed in
  `ExpressionParser.Normalize`, which unwrapped a Where chain but not a lambda. It unwraps both
  now, and `IDataContext` has typed overloads.
- **Two lists of names sat side by side.** `LoadByID(id, excluded, included)` and five more took
  two `IReadOnlyCollection<string>` in a row; swapped, they compiled and inverted the load. They
  are obsolete in favour of `LoadOptions`.
- **The interface DI registers could not load.** `IObjectDataManager` had `Get<T>()` and
  `Rows<T>()`; load by ID, paged loads and streamed loads were on the concrete class, which the
  modelling rules forbid a store to use, and the README pointed at them anyway. They are on the
  interface now.
- **Soft-deleted rows could not be read.** `IncludeDeleted` existed on the query model and
  nowhere a consumer could reach. It is on `LoadOptions` and on a query (`IncludeDeleted()`).
- **`BulkSave` was a second save in name only.** Both it and `Save` run the statements
  `CreateSaveQueries` builds, and `DbDataContext.ExecuteSQLAsync` sends an oversized table group
  to the bulk engine whichever built it. `BulkSave` only added a batch of mixed types. That is
  now `Save(IEnumerable<ICoreObject?>)` and `BulkSave` is obsolete. The manager's batch save
  used to exist only on the synchronous path; it has an asynchronous form now, which is the one
  that reaches the bulk engine.
- **`CommandTimeoutSeconds` was the wait for a free context.** Its own comment said so. It is
  `PoolWaitTimeoutSeconds`. The wait ignored the cancellation token and a full pool threw a bare
  `Exception`; it now observes the token and throws `DataContextException` with `PoolExhausted`.
- **The provider's schema methods ignored their arguments.** `CheckSchemaIsCurrent(schemas)`,
  `UpdateSchema(schemas)` and `UpdateSchemaAsync(schema)` always worked on every table.
  Overloads without the argument replace them.
- **`DataProviderStatus` on the manager was one provider's status**, while
  `InitializeDataProviderAsync` returned the worst of all of them. `Status` reports the worst.
  `DataProvider` was obsolete on the interface only; it is now obsolete on the concrete manager
  and on `INucleusServiceProvider` too.
- **Five overridable members of `CoreObject` did nothing.** `AllowCaching`, `DisableLogging`,
  `CacheRefreshInterval` and `CacheLifetime` were read by nothing, and `OnAfterInitializeDB` was
  never called. Obsolete. The README documented the last as a working hook.
- **`DataSet.Bind` and `BindRows` skipped the load hook.** Four ways to bind a data set, one of
  which ran `OnAfterLoadFromDB`. The two public ones that did not are obsolete. They were not
  given the hook: `ChildGraphBinder` binds rows the same way and runs the hook itself, later,
  once the children are attached.
- **SQL Server and PostgreSQL threw a bare `Exception` for a missing connection setting**, where
  SQLite threw its provider exception. All three now throw theirs, with `NotConfigured`.
- **`AddNucleus(options)` left `AppName` unset**, and `ConfigureAppName` then threw "AppName must
  be set in NucleusOptions" when a provider started. The README called the overload "the same,
  with explicit options". Registration now fills an unset `AppName` or `AppVersion` from the entry
  assembly on both overloads.
- **`AppVersion` was set and never read.** The README said it was recorded with audit entries.
  `NucleusLogEntry` now carries `AppName` and `AppVersion`.

Documentation found wrong in the same review and corrected: the install text (a provider package
does not pull `TFM.Nucleus`), a quick start that could not build its schema or run its delete, a
testing snippet that did not compile, and "every column is created NULL" (the soft-delete flag
is `NOT NULL` with a default).

### Save validation, PreSave, the parent index and the commit order (November 2026)

Reported by a store, which had worked around the first two. Each is covered by tests that fail
with the fix reverted (24 of the 29 new unit tests; the other five check behaviour that already held).

- **Only an entity's own save validated.** `CoreObject.Save`/`SaveAsync` ran `Validate()`, then
  went through the manager, and no other path validated: `IObjectDataManager.SaveAsync`, the
  manager's `Save` and `BulkSave`, the adaptor's saves, and a context's `Save`, `SaveAsync` and
  `BulkSave` wrote invalid objects without a word. The integration tests called `Validate()` by
  hand before a manager save, which hid it.
  - Every path now validates once per object through `ICoreObject.PrepareForSave` (new, and
    `[EditorBrowsable(Never)]`): `Validate()`, the saveable checks, then `PreSave()`.
  - The manager calls it through `SaveChecks` before its failure wrapping, so a rule's exception
    arrives as thrown. `DbDataContext` calls it in its save and bulk save builders.
  - `CoreObject`'s saves stopped validating themselves, since they route through those paths.
  - Cover: `SaveValidationTests`, `InstanceSaveValidationTests`, and
    `ChildModelIntegrationTests.EverySavePath_RunsTheChildrensRules_AndWritesNothing` on all five
    paths and every provider.
- **`PreSave()` ran on no path**, though the README listed it as a hook. It now runs after
  validation on the object and each new or changed child and forceSave link. `PrepareForSave`
  walks the dependents once and uses that walk for both their rules and their `PreSave()`; a flag
  tells `base.Validate()` to leave the cascade to it. A direct `Validate()` still walks for itself,
  and it no longer copies the dependents before validating them. Cover:
  `SaveValidationTests.ManagerSave_RunsPreSaveOnTheOwnerAndEachNewChild_Once`.
- **Cost, measured against `master` on net10.0.** Flat saves (single, batch, sync split, bulk,
  delete, build) are within 1%. Graph saves through the manager allocate 2-4% more on PostgreSQL
  and 4-8% more on SQL Server, which is the validation walk the manager's save never ran before.
  A direct `Validate()` allocates 4.8% less, and `materialise 1000` is unchanged, so the new flag
  did not make objects bigger. A first version that walked a second time for `PreSave()` cost
  about twice as much. The baselines were not re-accepted: the save suites are 10-22% under
  them on PostgreSQL and SQL Server, on `master` without this change too, from commits made
  after the baselines were written.
- **A child's `Parent` column had no index.** Every collection load and cascaded delete filters on
  it, so each scanned the child table.
  - `ColumnSchema` now sets `Indexed` for the `[ParentObject]` property, making it an ordinary
    `NCI_` column index that is created, compared and synced like a declared one. An existing
    table gains it in place at its next start.
  - Cover: `SchemaIndexTests.ParentColumn_OfAChild_IsAlwaysIndexed`, the `ddl.create` goldens for
    the two child types on every engine, and
    `TableIndexIntegrationTests.ChildTable_HasAPlainIndexOnItsParentColumn` and
    `MissingParentIndex_MakesTheSchemaNotCurrent_AndIsCreatedAgainOnStart` on every provider.
- **The manager marked objects saved before its transaction committed.** `Save`, `SaveAsync` and
  `BulkSave` ran `CommitChanges` and evicted the cache inside the context's scope, and the commit
  happens when the context is disposed. So a failed commit left objects marked saved with nothing
  stored. `SaveAsync` also disposed synchronously, committing on a blocking call.
  - All three now mark saved after the dispose, and `SaveAsync` uses `await using`.
  - Cover: `SaveCommitOrderTests`, through the fake's `DisposeFailure`. A commit that fails after
    its statements succeeded cannot be provoked reliably on the three engines, so no integration
    test covers it.
- **A failed commit leaked the context.** `DbDataContext.FinallyDispose` disposed the transaction,
  closed the connection and left the pool only after a successful commit or rollback, so one that
  threw held a pool slot for the life of the process. It now releases all three in `finally`, and
  the context counts as disposed. Verified by reading the code; no test reaches it without an
  engine whose commit fails.
- **The static API wrapped failures.** `INucleusRuntime.Save` and `Delete` reach the manager by
  `MethodInfo.Invoke`, which wrapped whatever it threw in `TargetInvocationException`. A sync
  `Delete()` of a type that is not `[Deletable]` threw that, not `NotSupportedException`. The
  dispatch now passes `BindingFlags.DoNotWrapExceptions`. Cover:
  `InstanceSaveValidationTests.Delete_OfATypeThatIsNotDeletable_ThrowsTheRefusalItself`.

### The manager interface did not carry what a store needs (November 2026)

DI registers only `IObjectDataManager`, and it had seven members: `DataProvider`, `ObjectSchema`,
`DataProviderStatus`, `InitializeDataProviderAsync`, `Get<T>`, `SaveAsync<T>(T)` without a token,
and `Rows<T>`. Saving with a token, saving a batch, every delete, subscribing to changes and
loading an excluded collection existed only on the concrete `ObjectDataManager`. A store author
reported it and asked whether to cast to the concrete type or save through an `IDataContext`. All
four store repos already opened their contexts with `_data.DataProvider.CreateContextAsync(...)`,
and `DataProvider` is the unrooted default provider, so for a type under a rooted provider they
wrote to the wrong database, and nothing they wrote that way evicted the cache. `NucleusTestStack.Manager`
was the concrete type, so store tests compiled against members production DI could not give them.
The README called `IObjectDataManager.LoadChildCollectionAsync`, `manager.LoadPagedAsync` and
`adaptor.ToPagedResultAsync`, none of which compiled against the interfaces.

The interface now carries `SaveAsync(obj, token)`, `SaveAsync(objs, token)`, `DeleteAsync` for one
object and a batch, `DeleteWhereAsync`, `LoadChildCollectionAsync`, `SubscribeToChangesAsync<T>`
and `CreateContextAsync<T>`. The last two are new and route by `T`. Each new member has a default
body that throws `NotSupportedException`, so an older implementation still compiles. The adaptor
gains `FindAsync(id, token)` and `SaveAsync(token)`, and `ToPagedResultAsync` is an `IQueryable<T>`
extension, so it works after `Where` too. `DataProvider` is obsolete. `NucleusTestStack.Manager` is
an `IObjectDataManager`. Reads stay on `Get<T>()` and `Rows<T>()`. The `Expression`-body loads,
the sync writes and `BulkSave` stay on the concrete class only: no store calls them, and each one
can be added to the interface later without breaking anything. Cover: `ManagerInterfaceTests`.

### Audit bugs (November 2026)

The bugs from the save, load and schema audit (B1 to B7 above), each with a test that fails on
the code before the fix unless noted.

- **B1. `OnAfterLoadFromDB()` never ran on a normal load.** Its only caller was
  `CoreObject<T>.BindDataSet`, which no load path used. Every load now ends in
  `ChildGraphBinder.Complete`, through `ICoreObject.OnAfterLoadFromDB`: it marks excluded
  collections unloaded, then runs the hooks of the loaded children at every depth and then the
  object's, so a hook sees its parent bound and its children finished. A lazy collection's rows
  run theirs when it loads, after their parent is bound. The manager's own `MarkUnloaded` moved
  into the binder, which also ended an array allocated per streamed row. Cover:
  `LoadHookTests`, and `LoadHookIntegrationTests` on every provider (stream, sync, async, paged,
  by ID, data context, lazy, included, excluded).
- **B2. `[SuppressLogging]` was ignored whenever a logger was passed**, which every manager save
  does, so a suppressed type was audited anyway. `LogChanges` now returns before building anything
  when there is no logger, the type is suppressed, or the logger drops Information; children,
  which commit with no logger, no longer build text to throw away. Cover: `AuditLoggingTests`.
- **B3. A synchronous save could send one command over the parameter limit.**
  `Query.NeedsSplitting` moved a nested chain as one unit, so a mixed batch (an update chain
  linked from the inserts) split into a chunk with every update in it. It now flattens the chain
  in the order it renders and packs the statements into chunks, so only a single statement over
  the limit on its own can exceed it. Reproduced on SQL Server: a sync save of 1,100 changed rows
  and one new one failed with "Too many parameters were provided in this RPC request. The maximum
  is 2100". Cover: `QuerySplitTests`, and `MixedBatchIntegrationTests.SyncSaveOfAMixedBatch_SplitsTheUpdateChainToo`
  on every provider.
- **B4. `DataSet`'s indexer let the row one past the end through**, and a negative row, handing back
  an empty slot that failed later with a `NullReferenceException`. It now throws
  `IndexOutOfRangeException` at the call. Cover: `DataSetTests`.
- **B5. `BulkWritePlanner.CanUseBulkPath` tested `CountLinkedQueries() > 0`, which is always true.**
  The term is removed rather than tightened: the only caller sends a group over the parameter
  limit, and the bulk engine is the right path even for a single statement over it. No behaviour
  changed, so no new test.
- **B6. Objects built without their constructor validated as dependents.** The flag that marked a
  root validation was initialised to true, which the schema's emit and reflection builders skip,
  so such an object skipped the root's checks. It is now `_validatingAsDependent`, false by
  default, and restored in a `finally`, so a dependent whose rules throw does not stay one. Cover:
  `ObjectMaterializerTests.BuiltObjects_ValidateAsRoots`.
- **B7. `IObjectDataAdaptor.FindAsync` blocked** on the synchronous `LoadByID`. It now reads
  through the new `ObjectDataManager.LoadByIDAsync`, which shares `LoadByID`'s cache and predicate
  helpers; a [Cacheable] type with a cold cache still fills it synchronously once, as
  `Load<T>()` does. Cover: `FindAsyncTests`.

### A NULL column threw on read into a non-nullable value property (November 2026)

`CoreObject.GetValue<T>` returned `(T?)value`. For an unconstrained generic `T?` on a value type
is just `T`, so a stored null was unboxed into `int` and threw `NullReferenceException`. Every
column is nullable and an unset property is written as NULL, so an `int` property saved without
a value threw on its first read after a reload, while the same object before the save read 0.
A stored null (from `BindValue`, `AcceptServerValue` or a pending clear) now reads as the type's
default, as a missing value already did and as `RowQuery.Get` does. The cast stays a cast, so a
value of the wrong type still throws `InvalidCastException`. The cost is one null compare per
read, which the unbox already did internally, so no benchmark was run. Cover: the `GetValue_*`
tests in `RegressionTests`, and `CrudIntegrationTests.UnsetValueProperty_LoadsAsDefault` on every
provider.

The write side had the same unbox: `IPropertySchema.SetValue(object?, object?)` cast with
`(TPropertyType?)value`, so a null for a value-type property threw `NullReferenceException`.
Writes are deliberately stricter than reads, because a null there comes from caller code and
writing the default would save 0 over what the caller meant as NULL. It now throws an
`ArgumentException` (`ParamName` "value") naming the type, the property and the nullable
declaration to use. The test is `default(TPropertyType) is not null`, a constant per closed
generic, so reference and `Nullable<T>` properties pay nothing and a plain value type pays one
null compare. Cover: the `SetValue_*` tests in `RegressionTests`.

### A manager save wrapped a unique violation (November 2026)

`ObjectDataManager.Save` and `SaveAsync` caught every failure and threw
`new Exception("Error saving objects", ex)`, `UniqueViolationException` included, so the README's
`catch (UniqueViolationException ex) when (ex.Is<T>(...))` never matched a manager save. The async
path exempted `OperationCanceledException`; the sync path wrapped that too. The integration tests
hid it by looking for the violation down the inner-exception chain. Both saves now share one filter,
`PassesThrough`, which lets through what the context already throws unwrapped: a
`UniqueViolationException`, an `OperationCanceledException`, a `DataProviderException` (each
provider's subclass included) and a `DataContextException`. Cover: `SaveFailureTests` on both paths,
and `UniqueIndexIntegrationTests.DuplicateValue_SecondSaveFails`, which now expects the violation
itself, on every provider.

Left as it is on purpose: any other failure is still wrapped. That includes the plain
`Exception(message, inner)` the context throws for a database error it does not translate, which a
manager save therefore wraps a second time.

Superseded by "A database failure was a bare Exception" (November 2026): the context translates
every database error into a `DatabaseException`, and a manager save no longer wraps anything, so
`PassesThrough` is gone.

### Found by the benchmarks, fixed (November 2026)

Measured by running the new benchmarks on the commit before the child model and on the child
model, one after the other. Pinned by the baselines under `Benchmarks/Baselines`.

- **Every save walked a graph per saved object, even for a type with nothing to walk.**
  `CoreObject.CommitChanges` and `HasChangedLinkedObjects` built a `SaveGraph` (two hash sets and
  four lists) for each root, which added about 2.5 KB per saved object: flat saves allocated 17-19%
  more than before the child model, and the 20,000-row bulk save 37-41% more, with PostgreSQL's
  bulk and split saves 26-27% slower. `SaveGraph.CanReachDependents` decides once per type whether a
  save can reach a child or a forceSave link, and every walk skips a type that cannot. Flat saves
  are back within 3% of before.
- **The async and streamed loads allocated a delegate per row.** `ObjectDataManager` passed its
  lazy loader as a method group inside the row loop, so each row allocated a new delegate (64 bytes,
  64 KB per 1,000 rows). The manager now holds one.

### Entity reference predicates (November 2026)

Regression cover is in `tests/TFM.Nucleus.Tests/ExpressionParserTests.cs`, the `where.parent.*` and
`delete.where.parent` goldens, and, on every provider,
`tests/TFM.Nucleus.IntegrationTests/ChildModelIntegrationTests.cs`.

- **A predicate could not filter by an entity reference.** `ExpressionParser` took only a column
  of the parameter itself, so `x => x.Parent.ID == id` threw ("At least one side of comparison must
  be a member access") and `x => x.Parent == roster` bound the entity object as the value. A page or
  a filtered part of a large child collection could not be read as the child type by its parent.
  A reference (the parent or a `[LinkedObject]`) read through its `ID` now names the reference's
  column, and an entity value, or a list of them, binds as its ID. Any other member read through a
  reference or a column throws, naming the term.
- **A comparison written value first selected the opposite range.** `HandleComparison` found the
  column on either side but kept the operator as written, so `3 < x.Quantity` rendered as
  `Quantity < 3`. The operator is now mirrored when the column is on the right.

### Child model findings (November 2026)

Regression cover is in `tests/TFM.Nucleus.Tests/ChildModelTests.cs` and, on every provider,
`tests/TFM.Nucleus.IntegrationTests/ChildModelIntegrationTests.cs`.

- **Children removed from a collection were never deleted.** `RemovedChildren` was recorded and
  never read. `SaveGraph` collects the stored children removed from every collection a save
  reaches, and `RelationalDataProvider.CreateSaveQueries`, the one save builder every save path now
  uses, deletes them first, by ID, in the save's transaction, cascading to what they own. The
  commit forgets them; a rollback keeps them for a retry. Removing then adding back keeps the child,
  where it used to throw `ParentObjectAlreadyBound`. Deletes are declared: `[Deletable]` on the
  child type, the collection property or an owner, resolved once per type by `ObjectSchema`, and a
  stored child removed from an undeclared collection throws at the call.
- **Deleting a `[Deletable]` parent threw for an undeclared child type.** What a declaration enables
  now reaches the whole owned graph, with the nearest declaration's behaviour. A child type's
  `[Deletable(false)]` under an enabling declaration fails the schema instead of the delete.
- **`[LinkedChildCollection]` was a v1 leftover.** No load, save or delete path read it; it fed only
  the validation cascade, `ClearChanges`, the root commit cascade and the notification lookup, so a
  `NucleusChildCollection` was never validated and raised `PropertyChanged` with no name. Those now
  follow the collection; the attribute is obsolete.
- **Child collections were never validated.** The root's `Validate()` now runs every new or changed
  child's, at every depth, and each forceSave link's, from the same `SaveGraph`.
- **`[LinkedObject(forceSave: true)]` lost data.** No query wrote the linked object, but the root's
  commit committed its pending values, so a later save of it wrote nothing. The save builder now
  writes each new, changed or dirty-below link before the roots, and refuses a link this provider
  does not store before anything runs; the commit commits exactly those.
- **A child collection's children were committed twice**, once by the collection walk and once as
  "changed linked objects", which logged their audit lines twice. Children now commit once, as
  non-roots.
- **Two collections of one child type failed with a bare `ArgumentException`.** It is an
  `ObjectSchemaException` naming both properties.
- **Child collections nested one level only.** `GetChildCollection` required the parent to be a
  `CoreObject<TParent>`. The constraint is now `CoreObject, ICoreObject<TParent>`. Relaxing a
  constraint on a non-virtual method keeps every existing call compiling and binding, so this was
  not the major version the open finding assumed.
- **Large child collections had to be excluded by every caller.** `[RetrieveOnDemand]` makes one
  lazy: loads leave it out, it loads itself on first read through the loader of the stack that
  loaded its parent (`ChildCollectionLoader`, not the ambient runtime, which a process with several
  managers would have read from the wrong database), and `Include` brings it back in for a listing.

### No way to choose columns, order, page or group in the database (November 2026)

The LINQ adaptor translates one `Where` and nothing else, and `CountAsync` counted by loading
every row, so any report over a large table read every column of every matching row and grouped
in memory. A consumer's admin page that summarised a week of an activity table (about 1M rows,
840 MB of long text) took 277 seconds to render; the same summary as one grouped query took 0.4
seconds. Column indexes were single-column only, so such a read could not be covered either.

`IObjectDataManager.Rows<T>(predicate)` (a default interface member, so other implementations
still compile) returns a `RowQuery<T>`: chosen columns, ordered and paged in the database, or
groups with `Count`, `CountDistinct`, `Min`, `Max` and `Sum`, read as `ResultRow`s rather than
entities so a partial row can never be saved over a full one. It is built on `SelectQuery` (new
`GroupBy`, `Measures` and `ResultColumns`), so the filter, the live-row filter and the paging
rules are the ones every read already uses; each renderer adds only the grouped select, and
`DbDataContext` labels and converts a row read by its own result columns
(`ReadResultRow`, which the PostgreSQL context overrides to convert dates as `ReadRow` does).
`[TableIndex(name, keys..., Include = [...])]` declares an index over several columns, `NIX_<table>_<name>`,
compared (keys in order, included columns as a set) and synced with the column indexes, never
rebuilding the table. Cover: the `rows.*` and `ddl.tableindex` goldens on both providers,
`TableIndexTests` (unit), and `RowQueryIntegrationTests` and `TableIndexIntegrationTests` on
both providers.

### Schema updates rebuilt large tables and timed out (November 2026)

Every schema change rebuilt its table, even a new nullable property: `SchemaManager.UpdateCore`
renamed the table, created it again and copied every row in one `INSERT ... SELECT` inside one
transaction. No command timeout was set on it, so the copy ran under the driver default of 30
seconds, and on a large table it timed out, rolled back and left the provider unable to start.

A table that only lacks columns now has them added in place (`ISchemaDialect.AddColumns`, a
default interface member so existing implementations still compile and simply rebuild).
`SchemaComparer.PlanUpdate` decides: a missing table is created, a column of the wrong type or a
missing `ID` means a rebuild, and anything else is an add. SQL Server declines a FILESTREAM
column, which needs a filegroup the table may not have, and that table is rebuilt. A legacy table
gaining `Timestamp` is backfilled, as its rebuild was. Every schema statement now runs under
`RelationalProviderOptions.SchemaCommandTimeoutSeconds` (900 by default), in the rebuild
transaction and in both catalog readers, so a genuine rebuild or an index build is not cut off.
Cover: the `ddl.addcolumns*` goldens on both providers, `SchemaUpdatePlanTests` and
`SchemaManagerUpdateTests` (unit), and `AddColumnIntegrationTests` on both providers, which tell
an in-place change from a rebuild by the table's object id; the in-place tests fail with the add
path disabled.

Found on the way:

- **Missing soft-delete columns were never repaired.** `TableSchema.IsCurrent` ignored them, so
  the check reported the table not current, the update skipped it as current, and initialise
  returned `Ok` with `IsDeleted` still missing. `IsCurrent` now counts them, and they are added in
  place. The soft-delete tests fail with the old `IsCurrent`.
- **`KeywordColumnRebuildTests` would have stopped testing a rebuild.** It made the table out of
  date by dropping a column, which is now an in-place add. It changes a column's type instead and
  checks the table was recreated; it still fails with the keyword copy fix reverted.
- **The integration runs raced on the schema.** The net8.0 and net10.0 runs share a database, and
  a test changing a table with raw DDL could make the other run's whole-schema check fail. Every
  test that changes a table, starts a stack or checks the whole schema, and the fixture's own
  initialise, now holds `DatabaseFixture.SchemaLockAsync`.

### A table with a keyword column could never be rebuilt on SQL Server (November 2026)

Found by a consumer: adding a column to an entity with a property named `By` stopped the
provider starting with "Incorrect syntax near the keyword 'By'", and the rebuild rolled back. CREATE
TABLE brackets every column name, so such a table is created without trouble, but the rebuild's row
copy (`SqlServerSchemaDialect.CopyTableDataSql`) joined the names bare. Every table with a column
named like a keyword (`By`, `Order`, `Key`) was stuck at the schema it was created with. The copy
now brackets the column names, the `ClusterID` it orders by and both table names, as the rest of the
dialect does (the `ddl.copy` and `ddl.copy.keywords` goldens). `KeywordColumnRebuildTests` takes such
a table back to an older shape holding a row and starts the stack again; it fails with this fix
reverted. The PostgreSQL dialect already quoted every name in its copy.

Names are bracketed and not escaped, here as everywhere else in the dialect: a name holding `]`
would already fail at CREATE TABLE.

### Save path findings fixed after the SQL findings (November 2026)

These were recorded while the SQL findings were fixed, then fixed one commit each. Their cover is
the named tests rather than `RegressionTests.cs`, and each fails with its fix reverted.

- **A loaded parent with a child collection could not be saved.** A child collection builds its
  writes through its parent's `DataProvider`, and only the manager's `SaveAsync` set it, so the sync
  `Save` and every `IDataContext` save threw "has no data provider". The provider now sets it as it
  builds each write (`ChildSaveIntegrationTests`, five save paths on both providers).
- **A mixed batch over the parameter limit threw.** An async `SaveAsync` of new and existing objects
  of one type over `MaxParametersPerQuery` reached `BulkWritePlanner.GroupByTable`, which refused a
  group mixing inserts and updates. It now gives one write of each (`BulkWritePlannerTests`,
  `MixedBatchIntegrationTests`).
- **A group exactly at the limit fanned out.** `Query.NeedsSplitting` split at a count equal to
  `MaxParametersPerQuery`, and its chunks stopped one parameter short of it. Both comparisons are
  inclusive now, matching the bulk route and the delete check, and the unused SQL provider copy of
  `NeedsSplitting` is removed (`QuerySplitTests`).
- **`INucleusRuntime.SaveAsync` dropped its cancellation token.** It now reaches the context, and a
  cancelled save throws `OperationCanceledException` as it is rather than wrapped
  (`RuntimeSaveTokenTests`, `SaveCancellationIntegrationTests`). A command cancelled while the engine
  runs it, which SqlClient can report as a `SqlException`, becomes an `OperationCanceledException`
  too; that branch has no test, since it needs a command cancelled mid-flight.

### SQL findings fixed after the PostgreSQL provider (October 2026)

These were recorded while the SQL Server output was pinned for the provider refactor, then fixed
one commit each. Their cover is the raw goldens (`SqlServerGoldens.cs`, `PostgresGoldens.cs`), which
show each change, and the named tests rather than `RegressionTests.cs`.

- **Every SQL Server select was a dirty read.** `Query.WithNoLock` was inverted: the renderer wrote
  `WITH(NOLOCK)` when it was false, its default. Selects now read committed data, and
  `WithNoLock = true` opts in.
- **Chained SQL Server updates were joined with a space.** They are joined with `;` and a newline,
  like inserts.
- **Output parameters were dead, and `SetValueOverride` worked only on `Timestamp`.** Nothing
  built an `OutputParameter`, the SQL Server update branch was malformed, PostgreSQL refused them,
  and every other overridden column was written with the client value. Writes now apply every
  `SetValueOverride` and read every `ReturnOutputToProperty` column back (`OUTPUT` on SQL Server,
  `RETURNING` on PostgreSQL) on every path, the bulk engines included. SQL Server's refusal on a
  table with a trigger (error 334) is explained. `Timestamp` itself is not read back; see "Timestamp
  in memory is the client clock" above. Covered by `ServerValueIntegrationTests` and
  `OutputTriggerTests`.
- **TEXTIMAGE_ON never fired.** The case-sensitive check could never match the type mapper's
  output, and the clause only named the default filegroup, so the check is removed.
- **`DatabaseColumn.ColumnName` was half implemented.** It now names the physical column on every
  path on both providers, values still bind by property name, and schema build refuses names that
  would collide. Covered by `ColumnNameTests` and `RenamedColumnIntegrationTests`. The PostgreSQL
  comparer also finds a column name over 63 bytes now.
- **`string.Contains` failed on SQL Server on a column without a full-text index.** It is a
  full-text search only on a `FullTextSearch` column and a substring match elsewhere, on both
  providers. Covered by `PatternMatchIntegrationTests`.
- **LIKE wildcards in values were not escaped, on either provider.** Pattern values are bound with
  `%`, `_`, `[` and `\` escaped and every pattern declares `ESCAPE '\'`. Covered by
  `LikePatternTests` and `PatternMatchIntegrationTests`.
- **`SQLConfiguration.BulkCopyBatchSize` was not applied.** The bulk engine uses it. Covered by
  `BulkCopyBatchSizeTests`, which counts the statements sent.
- **An unknown catalog column type failed the whole schema check.** The type is compared, not
  parsed on read; an unknown type on a Nucleus column is a mismatch. Covered by `ForeignTableTests`.
- **The SQL Server catalog interpolated table names.** The rebuild's column read and the full-text
  check bind them. Covered by `SqlServerCatalogTests`.
- **Neutral names carried SQL Server vocabulary.** `IPropertySchema.IsIndexed` and
  `IsFullTextSearch`, and `ServerFunction.CurrentDateTime` and `CurrentDateTimeOffset`, replace the
  old names, which stay obsolete.

### Context and store defects fixed by the provider refactor (October 2026)

- **A context that failed to open kept its pool slot.** The context entered the process-wide pool
  before its connection opened, and stayed there when the open threw, so each failed open shrank
  `MaxConnectionPoolSize` for the life of the process. `DbDataContext` now leaves the pool when
  opening fails.
- **An async dispose left the context in the pool cache.** Only the sync dispose removed it, so an
  application that disposed asynchronously grew the cache without bound. Both paths now share one
  implementation.
- **A raw statement kept the previous statement's parameters.** The schema rebuild ran its rename
  scripts on one command without clearing parameters. SQL Server ignored the extras; a stricter
  driver would not. Every statement now clears them.
- **A store sniffed for SQL Server.** It recognised a unique violation by reflecting on a type
  named `SqlException` and reading the index name out of its message, so it could not work on any
  other engine. Providers now report `UniqueViolationException`, and the store matches its entity
  and column.
- **Cancellation was wrapped in a plain exception** when the driver threw it inside a command, so
  a caller catching `OperationCanceledException` missed it (on SQL Server it depended on where
  SqlClient happened to throw). Cancellation now propagates as thrown, after a transactional
  context is marked for rollback.
- **A store's round-trip test could not pass on PostgreSQL.** It
  compared two stored instants to the tick, and `timestamptz` keeps microseconds, so the
  case failed by the 100 ns a value carries below a microsecond. The store's test now compares
  them as the same instant to the microsecond.

### The predicate parser dropped terms it could not place

A bare bool column (`x => x.Flag`) produced no condition and matched every row; `x.A == 1 || x.Flag`
lost the whole OR; `x => false` and any constant sub-condition vanished, so `x => false` loaded the
table. `A && (B || C)` rendered as `(A AND B) OR C` because OR operands were added straight to the
enclosing collection, and `(A || B) && C` rendered as `A OR B AND C` because `WhereClause` did not
parenthesise a nested OR. `.Where(a).Where(b)` applied only `b`. Each widened the selection, which
for a load returned too many rows and, once deletes by predicate existed, would have deleted them.
The parser now folds boolean constants, nests every OR, translates bare bool columns, ANDs every
Where in a chain, and throws for anything it cannot translate. Cover in `ExpressionParserTests`.

### Non-streaming loads discarded their child collections

`SelectQuery` batched a SELECT per child collection, but `GetDataSet` read only the first result
set, so `LoadWhere`, `LoadByID` and paged loads transferred every child row and returned empty
collections. The streaming path attached children but re-queried them, having already sent the
batched copies to be drained. Child result sets are now read into `DataSet.LinkedDataSets` and
attached by `ChildGraphBinder`, and `StreamAsync` sends the root query alone. Child subqueries
also ignored the parent's page and TOP, so a paged load fetched the children of every matching
parent; they now select the returned parents only. Cover in `ChildQuerySqlTests` and
`ChildCollectionLoadIntegrationTests`.

### Loaded children were dirty

Attaching a child went through `Add`, which bound the parent with `SetValue` and staged a change on
every loaded child. Loaded children are now attached with their parent bound as a stored value.

### Deleting a parent orphaned the children it had not loaded

The delete cascaded only over children held in memory, and there are no foreign keys, so a parent
loaded without its children (every non-streaming load, per above) left their rows behind. Every
delete now removes child rows in SQL by parent ID. Cover in `DeleteWhereIntegrationTests`.

### Infinite recursion in ObjectSchema.Get(dynamic)

`public IObjectSchema Get(dynamic _) => this.Get(_);` called itself unconditionally and satisfied no
interface. Deleted.

### CoreObject.Changes was null on every object loaded from the database

`Changes` was assigned only in the constructor, but the materialiser builds objects with
`RuntimeHelpers.GetUninitializedObject`, which runs no constructor. `ClearChanges()` and
`DataChanged` threw `NullReferenceException` on any loaded object. Now created on demand.

### PropertySchema.GetDBValue rejected nullable value types

The write path compared `DBWriteType == rawValue.GetType()` by reference. For an `int?` property
`DBWriteType` is `Nullable<Int32>` while the boxed value's type is `Int32`, so it threw
`InvalidObjectTypeException` for every non-null nullable value. It now normalises through
`Nullable.GetUnderlyingType` and tests assignability.

### Type tests that were always false

- `PropertySchema`: `Type is ICoreObject` asked whether a `System.Type` instance implements
  `ICoreObject`, so `LinkedObjectSchemas` was an array of nulls. Now `Type.IsAssignableTo(...)`,
  and the schema arrays filter out entries that do not resolve.
- `CoreObject.IsDerivedClass`: `IsSubclassOf` never matches an interface, so both overloads always
  returned false. Now uses `IsAssignableFrom` and excludes abstract types and interfaces.

### Writes to non-public setters were silently dropped

`GetSetMethod()` returns public setters only, and `SetValue` was `Setter?.Invoke(...)`, so a
property with a protected or private setter accepted a write that did nothing. Both accessors now
fall back to `nonPublic: true`, and a genuinely unwritable property throws instead of no-opping.

### BindDataSet produced objects that re-inserted

It hydrated through `SetRawCurrentValue`, which for a new object writes into the committed
dictionary and never clears `_isNew`, so every object it returned was INSERTed again on save
instead of updated. It now binds through `BindValue`, which clears `_isNew` when the ID arrives, and
calls `OnAfterLoadFromDB`.

### Duplicate key crash on shadowed members

The `ObjectSchema<T>` constructor used `Dictionary.Add` for the property, database-property and
field caches, so any derived class redeclaring a base member with `new` threw `ArgumentException`.
Now `TryAdd`, which keeps the most derived member.

### ObjectSchema.Initialize was not thread safe

It used `Interlocked.Equals(...)`, which is the inherited `object.Equals` and carries no memory
barrier, around a busy-wait on a non-volatile field that reset the other thread's flag on timeout.
Replaced with a lock and `Volatile` reads. Assembly load failures are no longer swallowed
indiscriminately: only the expected load exceptions are caught, and a
`ReflectionTypeLoadException` now uses the types that did load.

### Dirty checking compared against the wrong value

`SetValue` compared only against the pending value, so assigning a property back to its committed
value still marked it changed, wrote a needless column and logged "updated X from A to A".
It now compares against the value the property actually reads as, and assigning back to the
committed value clears the pending edit.

### CommitChanges overwrote the server timestamp

It assigned `DateTimeOffset.Now` unconditionally, discarding the `SYSDATETIMEOFFSET` value the
provider had written back. The local clock is now only a fallback when the server supplied nothing.

### ClearChanges did nothing for child objects

The child branch cast through `ITFMHasChanges`, which `CoreObject` does not implement, so the cast
always yielded null and every child was skipped. It also dereferenced an unmaterialised collection
property. Both fixed.

### SaveAsync(IDataContext, CancellationToken) ignored its token

`IDataContext` had a token overload for every async member except `SaveAsync` and `DeleteAsync`.
Those overloads were added, the SQL provider implements them, and `CoreObject.SaveAsync` now passes
the token through.

### ExpressionExtensions.ChangeTypeOfT handled only six node types

Every other node type was returned unmodified and therefore still referenced the old parameter,
failing with "variable of type X referenced from scope". Rewritten as an `ExpressionVisitor`, which
handles all node types, and it now rebinds instance method calls rather than assuming static ones.

### Array Contains in a predicate broke on C# 13 and later

From C# 13 the compiler resolves `someArray.Contains(x)` to the span-based
`MemoryExtensions.Contains`. `Evaluator` tried to fold the span conversion into a constant, which
throws `NotSupportedException` for a ref struct. `Evaluator` now refuses to fold ref-struct-typed
subtrees, and `ExpressionParser.HandleContains` unwraps the span conversion, so both spellings
translate to the same `IN` clause. The `LangVersion 12.0` pin on the test projects is gone.

### An enum column could not be compared in a predicate

C# lowers `c.Status == CartStatus.Active` to `Convert(c.Status, Int32) == Constant(1)`, and a
nullable enum to a `Nullable<Int32>` conversion on both sides. `ExpressionParser.GetMemberExpression`
recognised only a bare member whose expression is the lambda parameter, so neither side looked like
a column and every such predicate failed with "At least one side of comparison must be a member
access". The parser now strips `Convert` and `ConvertChecked` nodes before looking for the column,
picks the value side by matching the stripped operands rather than the original ones, and binds an
enum, or a collection of enums, as its underlying integral value. Ref-struct conversions are still
left to `UnwrapSpanConversion`.

### A nullable enum column threw on its first read after loading

The reader returns the column's `int`, which `BindValue` wrote straight into the property bag. The
CLR unboxes a boxed `int` into `WidgetKind` but not into `WidgetKind?`, so `GetValue<T>`'s cast threw
`InvalidCastException` the moment the property was read. Filtering on such a column was therefore
only half a feature. `BindValue` now widens an integral value into the declared enum, nullable or
not, which leaves `GetValue<T>` a plain cast and costs a schema lookup only on integer columns.

### System.Linq.AsyncEnumerable made LINQ operators ambiguous on net10.0

`ObjectDataAdaptor<T>` is both `IQueryable<T>` and `IAsyncEnumerable<T>`, so on .NET 10 an
unqualified `.Where(...)` was ambiguous between `Queryable.Where` and the new
`AsyncEnumerable.Where` and did not compile. `IObjectDataAdaptor<T>` now declares `Where` as an
instance method, which beats both extension methods. The `net8.0` pin on the test projects is gone
and the whole suite runs on both target frameworks.

The async operators had the same ambiguity and were missed, because every test called `Where`
first, which returns a plain `IQueryable<T>`: `manager.Get<T>().ToListAsync()` did not compile on
net10.0 (found by a consumer, November 2026). `IObjectDataAdaptor<T>` now declares `ToListAsync`,
`ToArrayAsync`, `FirstAsync`, `FirstOrDefaultAsync`, `SingleAsync`, `SingleOrDefaultAsync`,
`AnyAsync`, `CountAsync` and `LongCountAsync` with default bodies that call the extensions. The
composing operators (`OrderBy`, `Take`, `Skip`, `Select`) are still ambiguous on the adaptor, and
are left so: the parser translates only `Where`, so they would compile only to throw. Cover:
`AdaptorOperatorResolutionTests`, whose net10.0 build failed with CS0121 on 18 calls before the fix.

### IPropertySchema.Create threw for most properties

`PropertyCreator` is only built for `[LinkedChildCollection]` properties, but `Create` invoked it
unconditionally behind a `#pragma warning disable CS8602` that suppressed the warning about exactly
the null dereference that followed. It now throws a descriptive `PropertySchemaException`.

### NucleusLoggerProvider lost log batches

`Flush` dequeued into a local list before saving, so a failure discarded the whole batch. The batch
is now returned to the buffer for the next attempt. `_buffer.Count`, which is not O(1) on
`ConcurrentQueue` and was evaluated on every log call, is replaced by a tracked counter. The class
comment claimed entries logged before initialisation were dropped; they are retained, and the
comment now says so.

### Registration methods were not idempotent

`AddNucleus` and `AddNucleusLogging` used `AddSingleton`, so calling either twice registered twice
and, for the logger, persisted every log line twice. Now `TryAddSingleton` and `TryAddEnumerable`.

### NucleusChildCollection never raised PropertyChanged

The collection exposes `INotifyPropertyChanged` and callers can subscribe, but nothing ever raised
the event, so every subscriber stayed silent. `NotifyParent` now raises it alongside notifying the
parent.

### Exception handling

- The schema exception types dropped their message and inner exception instead of passing them to
  the base, so stack traces of the real failure were lost. All three now chain properly and accept
  an inner exception, and the bare `throw new Exception(...)` sites use them.
- `ObjectSchema.TryGet` caught everything and returned false, making a genuine schema bug
  indistinguishable from "not a Nucleus type" and silently skipping misconfigured entities at
  startup. It now answers only the "is this an entity type" question and lets real faults throw.
- `catch (Exception) { throw; }` in `Extract` and `catch (Exception) { return null; }` in
  `TryGetBackingField` are gone. The latter hid the "more than one match" case for shadowed
  members, which is now handled by taking the most derived match.
- The audit log walked `ObjectSchema.GetProperty(key)` for every changed key and threw
  `PropertyNotFoundException` for any column no longer on the class. It now skips unknown keys.
- `EnsureSaveable` told callers to call a `_Validate()` method that does not exist. The message now
  describes what to actually do.

### Dead code

Roughly 560 lines removed: the three unreachable `DynamicMethod` materialisers in `CoreObject` and
their helpers (`CreateDictionaryFromDT` twice, `ExtractCollection`, `TryRetrievePropertyFromDB`,
`UpdateProperty`, `Base_PropertyChanged`, `Init<T>`, `_CurrentCache`, the `data_Context` pair), the
`TFMInitializeWithString` type they were the only callers of, `ObjectCollection<T>`, `IUser`,
`IUserProvider`, `[SuppressCopying]`, and the unused `_parent` field on `CoreChildObject`. The
accidental `using static System.Runtime.InteropServices.JavaScript.JSType` import at the top of
`CoreObject.cs` is gone too.

Deleting this removed nearly every nullable-analysis warning in the repo. The suppression list in
`Directory.Build.props` went from twelve codes to one, and the remaining real warnings in the SQL
provider were fixed rather than suppressed.

### NucleusTypeConverter advertised a conversion it could not do

`ConvertFrom` reflected for a nonexistent static `Load` method on the descriptor context's own type
and threw for every input, while `CanConvertFrom` claimed it worked. It now supports only the
entity-to-Guid direction it can actually perform and points callers at `LoadByID<T>`.

### Attribute usage was inconsistent

Half the attributes declared `AttributeUsage` and half defaulted to `AttributeTargets.All` with
`Inherited = true`. All now declare their targets explicitly. `[SuppressLogging]` stays inherited,
deliberately: a base type that opts out of logging is making a decision about its data, and a
derived type should have to opt back in rather than start logging it by default.

### Performance

- `CoreObject.ObjectSchema` did a dictionary lookup keyed on `GetType()` on every `SetValue`,
  `ToString` and `CommitChanges`. Now cached per instance, in a field that is left null so an object
  materialised without a constructor still resolves it on first use.
- `PropertySchema.GetValue` and `SetValue` went through `MethodInfo.Invoke` on every property
  access. They now use delegates bound once at schema build time, falling back to reflection if the
  runtime will not bind one.
- `LinqExtensions.GetValue` compiled a fresh lambda on every call. Constants and closure field reads
  are now read directly, which covers the overwhelming majority of translated predicates.
