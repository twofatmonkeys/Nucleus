# Code review findings

Findings from the review carried out during the monorepo restructure of August 2026, and the state
of each one after the fix pass that followed.

Everything under "Fixed" has regression cover in `tests/TFM.Nucleus.Tests/RegressionTests.cs` unless
noted otherwise. Everything under "Open" is still true of the code.

## Open

### Found while fixing the save path findings (November 2026)

- **Child collections nest one level only.** `GetChildCollection<TChild, TParent>` requires the
  parent to be a `CoreObject<TParent>`, and `CoreChildObject<T, TParent>` derives from `CoreObject`,
  so a child cannot hold a child collection of its own: it does not compile. The save and load
  paths would bind a deeper level (a child collection's writes go through the same provider method
  as its parent's), but nothing above the entity base allows one. A limitation rather than a
  defect; changing it touches the entity base, which is a major version.

### Timestamp in memory is the client clock (decided October 2026)

The server stamps `Timestamp` on every write, and output parameters can now read a stored value
back, but `CoreObject.Timestamp` deliberately does not: in memory it is the client clock at the
first save, and an update does not refresh it. Reading it back was measured with the save
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

### The parameterless AddNucleus() enables schema migration

`NucleusOptions.ReadOnlySchema` defaults to `true`, but the convenience `AddNucleus()` overload sets
it to `false`. The shortest registration is therefore the one that allows automatic schema creation
and migration against the target database.

**Deliberately not changed.** Two production sites already call it and depend on the migration
behaviour; flipping the default would silently stop their schema updates, which is a worse failure
than the surprise. Changing it is a product decision, and the safe route is an explicit
`AddNucleus(o => o.ReadOnlySchema = true)` at the call sites first. The behaviour is now called out
in the registration reference in `README.md`.

### IDE0040, roughly 450 occurrences

Explicit `public` on interface members, which the shared `.editorconfig` asks to be left implicit.
Suppressed in `Directory.Build.props`. It is a mechanical edit across most files in the repo and was
kept out of the behavioural fixes so that this diff stayed reviewable.

### xUnit1051 in the test projects

Calls taking a `CancellationToken` should pass `TestContext.Current.CancellationToken`. Suppressed
in `tests/Directory.Build.props`. Worth doing, but it is a test-wide edit.

### Features that are declared but not implemented

- `[RetrieveOnDemand]` marks a property for lazy per-property loading. Nothing implements it. The
  attribute and `IPropertySchema.RetrieveOnDemand` remain so the intent is not lost; the dead
  `TryRetrievePropertyFromDB` that pretended to back it has been deleted.
- `[InitializePriority]` is parsed into `ObjectSchema<T>.Priority`, but the ordering code that
  consumed it is still commented out.

### The legacy ChangeCollection is vestigial

`CoreObject.Changes` and the `Change` / `ChangeCollection` types are the v1 change tracking. Nothing
populates them any more: audit summaries are built from the property dictionaries, and
`DataChanged` now reports the same state as `HasChanges`. `Changes` is documented as legacy and is
safe to call, but removing it is a public API change and was left for a major version.

### Commented-out code

Sizeable commented blocks remain in `PropertySchema.cs`, `IObjectSchema.cs`, `SQLConfiguration.cs`
and `DBColumn.cs`.

### Saving a parent rewrites every loaded child

`UpdateQuery` links an update for every child in each loaded collection, and an update always sets
`Timestamp`, so saving a parent touches every child row even when none changed. Skipping unchanged
children is not safe as things stand: `HasChanges` covers an object's own columns only, and a
grandchild's update is linked from its child's, so a clean child with a changed grandchild would
lose the grandchild's write. The fix needs a recursive dirty check first. Loaded children are no
longer dirty themselves (see "Loaded children were dirty" below), so the only write is the
timestamp.

### Children removed from a collection are never deleted

`NucleusChildCollection.RemovedChildren` records removals and says it is "consumed by the delete
pipeline", but nothing reads it. Removing a child from a collection and saving the parent leaves
the child's row in place.

### Bindery page deletes match the page id by collation

`NucleusBinderyStore.DeletePageAsync` deletes versions with `PageId = @pageId`, and `VersionsOf`
reads them the same way. Under a case-insensitive collation that also matches a page id differing
only in case. The document lookup filters ordinally; the version paths do not. This predates the
switch to delete by predicate, which kept the same condition. It is now provider dependent: SQL
Server's default collation matches without regard to case, PostgreSQL's equality does not, so the
same store deletes different rows for such a page id on the two engines.

### Pruning versions still loads their content

`NucleusBinderyStore.PruneVersionsAsync` loads every version of a page, JSON included, to order
them and choose which to delete. Ordering happens in memory because the provider translates none.
Row reads (November 2026, below) now give it a way out: `Rows<BinderyPageVersion>(...)
.Select(v => v.ID, ...).OrderByDescending(...)` reads only the ids and the ordering columns. The store
has not been changed to use them yet.

## Fixed

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

Found by the RedPoint website: adding a column to an entity with a property named `By` stopped the
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
- **S3Library sniffed for SQL Server.** It recognised a unique violation by reflecting on a type
  named `SqlException` and reading the index name out of its message, so it could not work on any
  other engine. Providers now report `UniqueViolationException`, and the store matches its entity
  and column.
- **Cancellation was wrapped in a plain exception** when the driver threw it inside a command, so
  a caller catching `OperationCanceledException` missed it (on SQL Server it depended on where
  SqlClient happened to throw). Cancellation now propagates as thrown, after a transactional
  context is marked for rollback.
- **TFM.Stencil's round-trip case could not pass on PostgreSQL.** `Save_ThenGet_RoundTripsEveryField`
  compared `CreatedUtc` and `UpdatedUtc` to the tick, and `timestamptz` keeps microseconds, so the
  case failed by the 100 ns a value carries below a microsecond. TFM.Stencil 1.26.304.1 compares
  them as the same instant to the microsecond; the Stencil pins moved to it together.

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
