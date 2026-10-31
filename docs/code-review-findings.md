# Code review findings

Findings from the review carried out during the monorepo restructure of August 2026, and the state
of each one after the fix pass that followed.

Everything under "Fixed" has regression cover in `tests/TFM.Nucleus.Tests/RegressionTests.cs` unless
noted otherwise. Everything under "Open" is still true of the code.

## Open

### Found during the provider refactor (October 2026)

The SQL Server output was pinned byte for byte before the query and schema model moved into
`TFM.Nucleus.Data`, so these were kept exactly as they were and are recorded here instead. Each
is a separate change, with a golden update that shows exactly what moves.

- **Every SQL Server select is a dirty read by default.** `Query.WithNoLock` defaults to false and
  `SqlServerQueryRenderer` writes `WITH(NOLOCK)` when it is false, so the flag is inverted: every
  root, paged and count select reads uncommitted rows unless a caller sets `WithNoLock = true`,
  which removes the hint. Nothing in the repo sets it. Fixing it changes what both production
  sites read under concurrent writes.
- **Chained updates are joined with a space.** `SqlServerQueryRenderer.Update` appends each linked
  query with a space rather than `;`. T-SQL accepts it; it is still wrong, and the raw golden
  `update.many` pins it.
- **The OUTPUT clauses are dead.** Nothing constructs an `OutputParameter`, so the insert and
  update OUTPUT branches never render, `ReturnOutputToProperty` has no effect, and `Timestamp` is
  never read back (the entity keeps the client's `DateTimeOffset.Now`). The update branch is also
  malformed. Remove both, or implement read-back, as one change.
- **TEXTIMAGE_ON never fires.** `SqlServerSchemaDialect.CreateTableStatements` looks for
  `nvarchar(max)`, `varchar(max)` and `varbinary(max)` case-sensitively, but the type mapper
  writes `[NVarChar](max)`. The clause is optional, so tables are correct; the check is dead.
- **`DatabaseColumn.ColumnName` is half implemented.** `ExpressionParser` uses it for a predicate's
  column, but the schema names every column after its property, so a property with a different
  `ColumnName` produces predicates on a column that does not exist. Either honour it in
  `TableSchema` or remove it.
- **LIKE wildcards in values are not escaped on SQL Server.** `StartsWith("50%")` matches every
  value starting `50`. The value is bound, so this is a correctness issue, not an injection.
- **`SQLConfiguration.BulkCopyBatchSize` is not applied.** Bulk statements run in fixed batches of
  50,000 rows. The README says so.
- **A catalog column of a type Nucleus does not know fails the whole schema check**, in any table
  of the database, including tables Nucleus does not own (a `sql_variant` column, say). The type
  is parsed when the catalog is read rather than when a Nucleus column is compared.
- **The SQL Server catalog queries interpolate a table name** (`ColumnsQuery`, the full-text
  check) instead of binding it. The names are Nucleus's own, so this is hygiene rather than an
  injection.
- **Some neutral names still carry SQL Server vocabulary**: `IPropertySchema.HasFullTextIndex` and
  `IsNonClusteredIndex`, and the `ServerFunctionKind` and `ServerFunction` tokens `SysDateTime`
  and `SysDateTimeOffset`. Alias them by meaning, as the attribute fields were.

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
them and choose which to delete. Ordering happens in memory because the provider translates none,
so avoiding the load needs column projection, which Nucleus does not have.

## Fixed

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
