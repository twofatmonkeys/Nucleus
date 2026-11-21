# Behaviour changes

What changed in behaviour, newest first, one section per piece of work. When upgrading, read down
from the top until you reach a section you have already taken.

## Explicit commit

A caller-owned transactional context used to commit when it was disposed, unless something rolled
it back. That is the opposite of `DbTransaction`, `TransactionScope` and EF Core, and an exception
in the caller's own code between two saves committed the first. This release starts the change to
an explicit commit; v3 finishes it.

- **A context has `Commit` and `CommitAsync`.** On a root context it commits at once. A commit of a
  transaction that a failure or a rollback marked rolls back instead and throws a
  `DataContextException` of kind `TransactionDoomed`. Anything run after the commit throws kind
  `TransactionCompleted`; a rollback after it does nothing. On a context without a transaction it
  does nothing. Add `await context.CommitAsync(ct)` at the end of the work in every caller-owned
  context.
- **A context disposed without a commit still commits, and logs a warning.** Set `RequireCommit`
  on the provider's options to make it roll back instead. Turn it on once every context commits;
  v3 makes it the only behaviour.
- **Any failure of a context's own operation marks it for rollback.** A validation rule, a query
  that cannot be built, a delete refused for its linked members, a row read that fails while
  streaming: each used to leave the transaction unmarked, so disposing the context committed the
  work before it. Only a failed statement marked it before.
- **A read no longer ends a transaction.** A load through a transactional context closed its
  connection with the reader, which ended the transaction, so a save after a load on the same
  context failed and disposing it threw. The connection now stays open until the context is
  disposed.
- **A child context runs on its root's connection and transaction.** A context opened with
  `ContextOptions.Parent` opened a connection and a transaction of its own, which the root
  committed one at a time before its own, so a failure between them left part of the tree
  committed, and the child could not see the root's uncommitted rows. It now shares the root's,
  so the tree commits or rolls back as one. Use one context of a tree at a time. A child marks its
  part complete with `Commit`; with `RequireCommit` set, a child disposed without one rolls the
  tree back. The root's commit throws kind `ChildContextOpen` while a child is still open.
- **An oversized write runs its commands in order.** A write over the parameter limit that the
  bulk engine cannot take fanned out to child contexts in parallel; it now runs on its own context,
  as it always did on SQLite. No write that Nucleus builds took that path (every save's insert and
  update goes to the bulk engine), so saves are unchanged. `MaxThreadsForLargeQuery`,
  `DbDataContext.AllowsConcurrentWriters` and `CreateChildContextAsync` are obsolete and unused.
- **A schema transaction commits explicitly.** `ISchemaTransaction` has `CommitAsync`, which the
  schema update calls once its rebuilds have run.

## The naming clean-up

These changes break a build. They are the one set of breaking changes made without a major
version; `AGENTS.md` records why.

Rebuild every package that holds entities or a store against this version. An assembly compiled
against the old attribute names loses its attributes without an error.

- **Renamed.** Find and replace each in your code.

  | Was | Is |
  |---|---|
  | `DataType` (in `[DatabaseColumn(DataType.Unicode, 50)]`) | `ColumnDataType`. The old name collided with `System.ComponentModel.DataAnnotations.DataType`. |
  | `eServerFunction` | `ServerFunctionKind`. `NULL` is `None`. The two enums were one list kept twice, and a column can now be stamped with `UtcNow` or `NewGuid`. |
  | `EDataSchemaStatus` | `DataSchemaStatus` |
  | `QueryType.SELECT`, `INSERT`, `UPDATE`, `DELETE` | `QueryKind.Select`, `Insert`, `Update`, `Delete` |
  | `OrderByType.ASCENDING`, `DESCENDING` | `SortDirection.Ascending`, `Descending` |
  | `WhereCollection.Type` | `WhereCollection.NodeKind` |
  | `[SuppressDB]` | `[NotStored]` |
  | `TFM.Nucleus.ServiceExtensions` (`AddNucleus`, `AddNucleusLogging`) | `Microsoft.Extensions.DependencyInjection.NucleusServiceExtensions`. Calls are unchanged; a file that had only `using TFM.Nucleus;` for them now needs `using Microsoft.Extensions.DependencyInjection;`. |
- **The attributes carry the `Attribute` suffix.** `Cacheable`, `RetrieveOnDemand`, `LinkedObject`,
  `ParentObject`, `SuppressLogging`, `AbbreviatedLogging`, `LinkWhenLogging` and
  `InitializePriority` are now `CacheableAttribute` and so on. `[LinkedObject]` on an entity
  compiles as it did. What changes is code that names the class: `typeof(LinkedObject)` becomes
  `typeof(LinkedObjectAttribute)`.
- **Removed.** Each was already obsolete.

  | Removed | Use |
  |---|---|
  | `NonClusteredIndex`, `IndexSortDESC`, `FullTextIndex`, `Filestream` on `[DatabaseColumn]` | `Indexed`, `IndexDescending`, `FullTextSearch`, `ExternalStorage` |
  | `SYSDATETIME`, `SYSDATETIMEOFFSET`, `ServerFunction.SysDateTime`, `SysDateTimeOffset` | `CurrentDateTime`, `CurrentDateTimeOffset` |
  | `[LinkedChildCollection]`, `IPropertySchema.IsLinkedChildCollection` | A `NucleusChildCollection` property, found by its type; `IPropertySchema.ChildType` |
  | `IObjectSchema.HasLinkedChildObjects`, `LinkedChildObjectProperties`, `LinkedChildObjectSchemas`, `GetLinkedChildObjects` | `HasNucleusChildCollections`, `NucleusChildCollectionProperties` and each property's `ChildSchema`, `ICoreObject.NucleusChildCollections` |
  | `IPropertySchema.IsNonClusteredIndex`, `HasFullTextIndex` | `IsIndexed`, `IsFullTextSearch` |
  | `CoreObject.AllowDeletions` | `[Deletable]` |
  | `IObjectDataManager.DataProvider` | The routed members: `CreateContextAsync<T>`, `SubscribeToChangesAsync<T>`, `Rows<T>` |
  | `TFM.Nucleus.Data.DataSchemaStatus` (`Current`, `OutOfDate`, `Error`) | Nothing: it was used by nothing, and the name now belongs to the schema status enum |
- **An implementation of a Nucleus interface must supply the neutral members.**
  `IDatabaseColumnAttribute.Indexed`, `FullTextSearch`, `IndexDescending` and `ExternalStorage`,
  and `IPropertySchema.IsIndexed` and `IsFullTextSearch`, defaulted to the removed members and
  are now required. `IObjectDataManager.Rows<T>` defaulted to the removed `DataProvider` and now
  throws unless implemented.
- **`IPropertySchema.Create` always throws,** and it and `PropertySchema.PropertyCreator` are
  obsolete: they made the v1 child collection of a `[LinkedChildCollection]` property.

## The API surface review

Nothing was removed here. Each replaced member still works and is marked obsolete, so a build that
treats warnings as errors will stop on them.

- **Names aligned.** The old name of each still works.

  | Was | Is |
  |---|---|
  | `AddNucleusSQLDataProvider` | `AddNucleusSqlServerDataProvider` |
  | `SQLConfiguration.SQLPacketSize`, `BulkCopyBatchSize` | `PacketSize`, `BulkBatchSize` |
  | `MaxParametersPerQuery` | `MaxParametersPerCommand`, the name the provider's capabilities already used |
  | `BulkCopyTimeoutSeconds` | `BulkTimeoutSeconds`; a negative value is now refused |
  | `PostgresConfiguration.ConnectRetryCount` | `ConnectAttempts`: it always counted attempts |
  | `Query.WithNoLock` | `Query.AllowDirtyRead` |
  | `ContextOptions.UseDependency`, `IDataContext.HasDependency` | `WatchForChanges`, `WatchesForChanges` |
  | `SqlDataProviderException.Type`, `SqlDataProviderExceptionType` | `Kind`, `DataProviderErrorKind` |
  | `CoreObject.LoadObjectWhere` | `LoadFirstWhere`, which reads one row |
  | `IQueryParameter.castAsOutputParameter` | `AsOutputParameter` |
- **One exception base.** Every Nucleus exception derives from `NucleusException`. Each still
  derives from `Exception` through it, so existing catch clauses are unaffected.
- **PostgreSQL's connect timeout is a setting.** `PostgresConfiguration.ConnectTimeoutSeconds`,
  15 seconds as before.
- **The static API loads asynchronously.** `CoreObject.LoadByIDAsync`, `LoadAllAsync` and
  `LoadFirstWhereAsync`. A context has `RollbackAsync`.
- **A test stack's manager resolves from its services** as `IObjectDataManager`, the type
  production code takes. A stack still does not publish the ambient runtime the static API uses.
  `PostgresTestDatabase` and `SqliteTestDatabase` gain a `StartStackAsync` taking their own
  configuration type, as `SqlServerTestDatabase` had.
- **Unused public types are obsolete:** `ITFMHasChanges`, `DataProviderConnectionStatus`,
  `IInitializeWithString<T>` and `IPropertySchema.SkipOnDbRead`.

- **The manager interface loads.** `IObjectDataManager` gains `LoadByIDAsync`, `LoadAsync`,
  `LoadPagedAsync` and `LoadStreamAsync`, taking a typed predicate and a `LoadOptions`
  (`Exclude`, `Include`, `IncludeDeleted`). Until now a store had to cast to the concrete manager
  for them. The concrete overloads that took two lists of names side by side are obsolete: the
  two could be passed in each other's place and still compile.
- **A predicate can be passed whole.** The loads that take an untyped `Expression` needed a
  predicate's `Body`; the lambda itself compiled and then failed to translate. Both now work.
- **Soft-deleted rows can be read.** `LoadOptions.IncludeDeleted` and `.IncludeDeleted()` on a
  query. The documentation said a query could ask for them; nothing a consumer could reach did.
- **There is one save.** `BulkSave` and `BulkSaveAsync` are obsolete on `CoreObject`, the manager
  and a context. `Save` and `SaveAsync` take a batch of mixed types (`IEnumerable<ICoreObject?>`),
  and the provider decides when a batch goes to its bulk engine, as it already did.
- **`PoolWaitTimeoutSeconds` replaces `CommandTimeoutSeconds`.** The option never set a command
  timeout; it is the wait for a free context. A full pool now throws `DataContextException` with
  `Kind` `PoolExhausted` instead of a bare `Exception`, and the wait observes the cancellation
  token.
- **`Status` on the manager** reports the worst status across every provider. `DataProviderStatus`
  reported the unrooted default provider alone and is obsolete, as are `DataProvider` on the
  concrete manager and on `INucleusServiceProvider`.
- **`IDataProvider` checks and updates the schema without arguments.** `CheckSchemaIsCurrent`,
  `CheckSchemaIsCurrentAsync`, `UpdateSchema` and `UpdateSchemaAsync` took schema arguments they
  ignored; those overloads are obsolete.
- **A missing connection setting is the provider's exception.** SQL Server and PostgreSQL threw a
  bare `Exception` for a missing server, database, user name, password or application name. They
  now throw `SqlDataProviderException` and `PostgresDataProviderException` with `Kind`
  `NotConfigured`, as SQLite does.
- **`AddNucleus(options)` always has an application name.** It left `AppName` unset unless the
  options set it, and every provider then refused to start. Both overloads now read an unset
  `AppName` or `AppVersion` from the entry assembly. `AppVersion` is the informational version
  where the assembly has one (`1.4.2`, not `1.4.2.0`).
- **Persisted log entries carry the application.** `NucleusLogEntry` gains `AppName` and
  `AppVersion` columns, so its table gains two nullable columns at the next schema update. A site
  that uses `AddNucleusLogging` with `ReadOnlySchema` on must let the schema update once.
- **Members that never did anything are obsolete.** `CoreObject.AllowCaching`, `DisableLogging`,
  `CacheRefreshInterval` and `CacheLifetime` were read by nothing (use `[Cacheable]` and
  `[SuppressLogging]`), and `OnAfterInitializeDB` was never called. An override of one now warns.
  `DataSet.Bind` and `BindRows` are obsolete too: they bind rows without attaching children or
  running the load hook.

## The SQL Server deadlock fix

- **SQL Server reads and deletes a graph one table per statement.** A child collection, included
  reference or linked collection query reached its parents through a subquery over the parent's
  table. On small tables SQL Server ran it from the child table, holding a child row of some other
  parent while it read that parent's row: the opposite order to a save of the parent, and the two
  deadlocked (error 1205) under concurrent saves, on loads as well as on deletes. Each level's keys
  are now kept in a table variable read from that level's table alone, and the next level selects
  by them. A delete also evaluates its condition once instead of once per level. Anything that
  matches the generated SQL text sees `DECLARE @__parents` / `@__targets` statements in the batch.
- **`SQLConfiguration.ConnectTimeoutSeconds`.** The SQL Server connect timeout was a fixed 3
  seconds; it is now a setting, still 3 seconds by default.

## Links to child types

- **A child loaded on its own reads its parent.** A child loaded by a `[LinkedObject]`, `LoadByID`
  or a query of its type held its parent's ID, and reading `Parent` threw `InvalidCastException`.
  The first read now loads the parent through the stack that loaded the child.
- **`forceSave` on a link to a child type fails the schema.** The save wrote the child as a root,
  outside its parent. Drop `forceSave` and save the parent.
- **The async operators compile on `Get<T>()` under .NET 10.** `manager.Get<T>().ToListAsync()`
  and the other async operators were ambiguous with `System.Linq.AsyncEnumerable` (CS0121) unless
  a `Where` came first. They are now declared on `IObjectDataAdaptor<T>`, with the same results.

## The linked object read

- **A loaded `[LinkedObject]` reads as its entity.** A load stored the referenced entity's ID and
  the property cast it to the entity type, so every read of a loaded reference threw
  `InvalidCastException`. The first read now loads the entity through the stack that loaded the
  object, and `Include(x => x.Reference)` loads it with the rows.
- **Saving a loaded object with a `forceSave` link works.** The save walked the link through the
  property, so every save of such an object after a load threw the same `InvalidCastException`.
  The walk now reads the link as it is held: one never read since the load is skipped without being
  loaded, and one that was read and changed is written as before.
- **`IObjectSchema.GetLinkedObjects` returns the links an object holds.** A link never read since
  the load is left out instead of being loaded (it used to throw).

## Save validation and the parent index

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

## The manager interface fix

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

## The audit fixes

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

## Entity reference predicates

- **A comparison written value first means what it says.** `3 < x.Quantity` used to render as
  `Quantity < 3`: the sides were swapped and the operator kept, so it selected the opposite range.
  It now renders as `Quantity > 3`. A caller who wrote value-first comparisons was getting the
  wrong rows, and gets the right ones now.
- **A member read through a column throws a clearer error.** `x.Name.Length`, `x.Parent.Name` and
  a comparison of two columns throw `NotSupportedException` (inside the parse's
  `InvalidOperationException`) naming the term. They used to fail too, with the "member access"
  message or an unbound parameter error.

## The child model

Declared deletes, removal persistence, child validation, forceSave and lazy collections.

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
  The attribute and the members have since been removed.
- **A parent with two collections of one child type fails when the schema is built.** It used to
  fail with a bare `ArgumentException` from a dictionary.
- **A child entity can hold a child collection.** `GetChildCollection` required the parent to be a
  root entity.

## Unique `[TableIndex]`

- **Nothing changes for existing entities.** A `[TableIndex]` without `Unique` is created and compared
  as before, so its `NIX_` index is left alone.
- **The duplicate-key initialisation error names the index.** When a unique index cannot be built
  over existing duplicates, the logged error and the exception now name the index as well as the
  table and its columns, for a column's `UQ_` index too.

## Table naming

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

## Row reads and `[TableIndex]`

- **Indexes named `NIX_*` are managed.** The schema check and sync now read, create and drop
  `NIX_<table>_<name>` indexes as well as `NCI_*` and `UQ_*`, so a hand-made index whose name
  starts `NIX_` on a Nucleus table is dropped unless an entity declares it. The prefix was chosen
  to be unlike the common `IX_`, which stays untouched like every other name.
- **Nothing else changes for existing code.** `Rows<T>` is new, the LINQ adaptor still translates
  only `Where`, and its `CountAsync` still loads the rows it counts; use `Rows<T>(...).CountAsync()`
  for a count in the database.

## Schema updates on large tables

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

## The save path findings

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
- **A batch at exactly `MaxParametersPerCommand` runs as one command**, and a split batch fills each
  command up to the limit. It used to split one parameter early.
- **A save of new and existing objects over the limit no longer throws.** The bulk engine writes
  its inserts and its updates as two writes in the same transaction.

## The SQL Server review findings

- **SQL Server selects read committed data.** `Query.WithNoLock` was inverted: every select ran
  `WITH(NOLOCK)` and read uncommitted rows unless the flag was set, which removed the hint. A
  select now reads committed data, as on PostgreSQL, so it can wait for a row another transaction
  is writing. Set `AllowDirtyRead = true` on a query to read uncommitted rows (`WithNoLock` is its
  obsolete name).
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

## The shared provider layer

- **Provider types are internal.** `TFM.Nucleus.Data.SQL` exposes only `AddNucleusSqlServerDataProvider`,
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

## Delete by predicate and child collection exclusion

Each of these fixes a defect, but each can change what existing code sees.

- **Predicates no longer drop terms.** A bare bool column (`x => x.IsArchived`) is now a condition;
  it used to be ignored and match every row. `x => false` matches nothing; it used to match
  everything. `A && (B || C)` and `(A || B) && C` now group as written. A term that cannot be
  translated throws instead of being dropped. Queries that silently returned too many rows now
  return the right ones.
- **Loads attach child collections on every path.** `LoadWhere`, `LoadByID` and paged loads used to
  discard the child rows they fetched and return empty collections.
- **Deleting a parent removes all of its child rows**, not only the ones loaded in memory.
- **A chained `Where` applies every filter.** Only the last one used to apply.
