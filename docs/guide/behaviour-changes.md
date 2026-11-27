# Behaviour changes

What changed in behaviour, newest first, one section per piece of work. When upgrading, read down
from the top until you reach a section you have already taken.

## Parameters are numbered per command, so a repeated save reuses its plan

- **No change in what a command does.** The parameters of each command are now named `p0`, `p1`
  and on, where they were named from a counter that ran for the life of the process
  (`p124816`). Two saves of the same shape now send the same SQL text, so SQL Server reuses the
  plan it compiled for the first, where it compiled every multi-row save afresh and kept each
  plan, of up to 57 MB, in its cache. Measured on a 100-row batch saved twice: about 130 ms
  against about 570 ms.
- **SQL text in a log or a trace reads differently**: the names start at `p0` in every command.
  Anything that matched the old names, or relied on a name being unique across commands, needs
  changing.
- **The gain needs the shape to repeat.** A batch of 100 rows and one of 99 are two texts and two
  plans, as before.
- **For a provider author:** a context calls `ParameterBinding.NameForCommand` on a command's
  terms before it builds the text. A term nobody numbered still names itself uniquely.

## A list of values in a predicate can be any length

- **`ids.Contains(x.ID)` no longer fails on a long list.** A list over about 2,000 values was
  refused by SQL Server's 2,100 parameter limit. Each engine now sends a list as one parameter
  (SQL Server above 200 values), so code that split a list and read it in parts can pass the whole
  list, and order, page and count the result in the database. See
  [querying](querying.md#lists-of-values).
- **SQL Server gains four table types** (`NucleusGuidList`, `NucleusIntList`, `NucleusBigIntList`,
  `NucleusTextList`), created at the first start. **A deployment with `ReadOnlySchema` does not
  start until they exist**: its log gives the four `CREATE TYPE` statements to run. A login that
  does not own the schema needs `EXECUTE` on each. See [providers](providers.md#sql-server).
- **An empty list runs no query.** A load over one returns nothing and a `DeleteWhere` returns 0,
  where each ran a statement that matched nothing. `DeleteWhere(x => !none.Contains(x.ID))`, which
  threw `NotSupportedException`, now throws `ArgumentException` as a delete of every row; a load
  with it, which threw, returns every row.
- **Repeated values and nulls in a list are not sent.** The rows matched are the same.
- **A delete of many objects is one statement**, where it was one per 1,000 objects.
- **A list that still binds a value at a time and passes the limit throws `InvalidQueryException`**
  naming the list and the limit, where the driver's error surfaced as a `DatabaseException`.
- **For a provider author:** `IQueryRenderer.BindsAsList` says whether a renderer writes a list
  against one parameter, and `DbDataContext.CreateListParameter` binds it. Both default to the old
  behaviour. `ParameterBinding.MultiValues` now returns the distinct, non-null values.

## An ordered page on SQL Server reads its rows by key

- No change in what a query returns. An ordered, paged read on SQL Server sorts and skips keys and
  then reads the page's rows, which is faster on a deep page or an unindexed order. A listing that
  pages deep is still better paged by key; see [querying](querying.md#what-a-query-translates).

## TFM.Nucleus.Testing: a database per .NET runtime

- **The configured database name is now a base name.** A net8.0 run uses `<name>_net8` and a
  net10.0 run `<name>_net10`, on SQL Server and PostgreSQL as SQLite already did with its file.
  `NucleusTestDatabase.DatabaseName` is the one in use. The first run after upgrading creates the
  two databases and their tables; the database under the bare name is no longer used and can be
  dropped.
- **On SQL Server the package creates a missing database itself**, at compatibility level 100 with
  `READ_COMMITTED_SNAPSHOT` on. The test login needs the right to create a database, or the two
  databases made for it. An existing database is never altered.
- **The suite locks are per runtime**, so a net8.0 run no longer waits on a net10.0 run's lock.

## A grouped row read is ordered and limited in the database

- **`Rows<T>().GroupBy(...)` can be ordered**, by a group key (the existing `OrderBy` and the rest,
  which threw `InvalidOperationException` on a grouped read) or by a measure, through the new
  `OrderBy(string)`, `OrderByDescending(string)`, `ThenBy(string)` and `ThenByDescending(string)`,
  which take the measure's alias. Code that ordered its groups in memory still works; move the
  order onto the read to have the database do it.
- **`Take(n)` on a row read** reads the first n rows of its order, or the first n groups of a
  grouped read. A limited grouped read needs an order, and breaks its ties by the group keys.
- **A grouped read is still not paged.** `ToPageAsync` on one throws as before, with a message that
  now points at `Take`.

## One exception for a database that failed, and a manager context that is the manager's

Breaking changes. Code that catches the old types, or relies on a context with no transaction,
needs the edits below.

- **A full connection budget is a `DatabaseException`** of kind `PoolExhausted` and operation
  `Open`, where it was a `DataContextException` of kind `PoolExhausted`. Replace
  `catch (DataContextException ex) when (ex.Kind == DataContextErrorKind.PoolExhausted)` with
  `catch (DatabaseException ex) when (ex.Kind == DatabaseErrorKind.PoolExhausted)`, or drop it
  where `catch (DatabaseException)` is already there. It is never transient, so the retry policy
  does not repeat it, and it has no inner exception.
- **A database that cannot be reached when a provider first initialises is a `DatabaseException`**,
  with the kind (`ConnectionFailed` or `Timeout`), the operation and the driver's exception of the
  failure. It was the provider's exception of kind `NotInitialized` with no message.
- **A provider that is not ready says why.** A schema that is not current, a database that does
  not exist under a read-only schema and missing settings each reach the caller as the provider's
  exception with that kind and a message; anything else is `NotInitialized` with the reason in
  its message. Before, every one of them was a bare `NotInitialized`.
- **Removed, having never been thrown:** `DataProviderErrorKind.DatabaseUnavailable`,
  `DataContextErrorKind.UnableToCreateNewCommand` and `DataContextErrorKind.Unspecified`.
  `DataContextErrorKind.PoolExhausted` is removed with the change above.
- **On SQL Server, a server that does not answer on the synchronous path is a transient
  `Timeout`.** It was kind `Other` and not transient.
- **A context from `CreateContextAsync<T>` is transactional unless told otherwise.** With no
  options it had no transaction, so each statement committed as it ran and `CommitAsync` did
  nothing; the same went for options that set only something else, such as
  `new ContextOptions { StatementTimeoutSeconds = 300 }`. Both now open a transactional context.
  Code that relied on having none, and never commits, now holds a transaction: until v3 it still
  commits when disposed, with a warning, and rolls back under `RequireCommit`. Add the
  `CommitAsync`, or pass `ContextOptions.Default` (or `UseTransaction = false`) for a context that
  only reads.
- **`ContextOptions.UseTransaction` is a `bool?`.** Null, its default, leaves the choice to what
  opens the context: transactional for the manager, not for a context a provider opens for itself.
  Code that reads the property needs `== true`; code that sets it is unchanged.
- **A context from the manager audits and evicts when it commits.** The changes of the objects it
  saved are audited, and the `[Cacheable]` caches of the types it saved or deleted are evicted,
  once its transaction commits; after a rollback, neither. Before, such a context did neither at
  all, and a load after its commit could answer from a stale cache.

## A value set in Validate() no longer fails the save

- **An override of `Validate()` may set a property after `base.Validate()`.** Setting one there
  cleared the mark `base.Validate()` had just made, so the save threw `InvalidOperationException`
  ("has not been validated ... must call base.Validate()") although the call was there, and only
  when the value really changed: a trim failed for input that needed trimming and passed for the
  rest. A child that did the same was saved without complaint. The save now marks the object
  valid once its override returns, and the value is written.
- **A save of an object whose `Validate()` never calls `base.Validate()` is still refused**, with
  the same exception and message, before anything is written.
- **`TFM.Nucleus.Testing`: `HoldWriteLockAsync` on SQL Server blocks readers when
  `READ_COMMITTED_SNAPSHOT` is on.** It took an exclusive table lock, which a snapshot reader
  passes, so a test of a blocked read saw no wait. It now takes a schema modification lock, which
  also makes a catalog read of that table wait while the lock is held.

## Queries order and page in the database, and pages follow ClusterID

- **`OrderBy`, `OrderByDescending`, `ThenBy`, `ThenByDescending`, `Skip` and `Take` on a query
  run in the database** (see [What a query translates](querying.md#what-a-query-translates)). They
  threw `NotSupportedException` before, so code that ordered or paged in memory after `ToListAsync`
  still works, but now reads every row only because it asks to; move the operators before the
  async operator to read one page. `Select`, `Distinct` and the other operators still throw.
- **`Get<T>().Take(n)` reads n rows.** A `Skip` or `Take` written straight on `Get<T>()`, or before
  a `Where`, was evaluated away before translation, so the query read every matching row.
- **A page with no order is ordered by ClusterID, the order the rows were written in, not by ID.**
  This covers `ToPagedResultAsync`, `LoadPagedAsync`, `Skip`, `Take`, `FirstAsync`, `SingleAsync`
  and a limited read with child queries. A Guid order was stable but meant nothing, and the first
  page of an unordered query had no order at all, so it could share rows with the second. "First"
  with no `OrderBy` is now the first matching row written.
- **An ordered page is followed by ClusterID to break its ties**, so rows with equal keys no
  longer move between pages, and the children of a limited read always belong to the parents it
  returned.
- **`Rows<T>().ToPageAsync` pages a read with no order** in the order the rows were written in. It
  threw `InvalidOperationException` before.
- **On PostgreSQL, NULL sorts below every value**, first ascending and last descending, as on SQL
  Server and SQLite, in a query and in a row read alike. PostgreSQL's own order is the reverse.
- **Every PostgreSQL table gets a unique index on ClusterID** (`CIX_<table>_ClusterID`), so a page
  does not sort every row it matches. The first start builds it on each existing table and logs
  how long each took.
- **A SQL Server or PostgreSQL table without a ClusterID column is rebuilt with one at the next
  start**, through the usual rebuild, which copies its rows and logs a warning first. A table
  Nucleus created always has one, so only a table made some other way is affected.
- **A Where or an order after Skip or Take throws `NotSupportedException`**, because it would act
  on the page rather than the rows. **`ToPagedResultAsync` on a query that already skips or takes
  throws `InvalidOperationException`.** A delete by predicate still takes a filter alone.

## The test database drops key columns, and adoption no longer stalls on SQL Server

- **A start that adopts an old table after dropping a key no longer times out on SQL Server.** The
  update read the catalog for the adoption on a connection of its own after its transaction had
  dropped a key, and that read waited on the transaction until the statement timeout, failing the
  start. Old tables with a foreign key between them, or an adoption in the same start as a change
  to a table with a key, hit it.

- **`DropColumnsSql` drops a column's foreign keys on SQL Server.** It dropped only the column's
  index and default, so since foreign keys it failed on a key column such as a child's `Parent`.
- **`DropColumnsAsync(table, columns)` drops any column on every engine.** SQLite cannot drop a
  column a foreign key uses, so there it rebuilds the table without the columns, keeping the rows
  and the other indexes; `DropColumnsSql` still fails on SQLite for a key column. A test that drops
  a key column calls `DropColumnsAsync`, under the schema lock.

## A broken foreign key names the reference

- **A key violation is a `ForeignKeyViolationException`**, a `DatabaseException` of kind
  `ForeignKeyViolation` as before, which names the reference whose key broke (`EntityType`,
  `PropertyName`, `ColumnName`, `ReferencedType`, `Is<T>(name)`). A `catch (DatabaseException)`
  still catches it. SQLite does not name the key, so there the names are null.

## Foreign keys on SQLite

- **SQLite keeps the same foreign keys as the other engines**, in each key column's definition (see
  [Foreign keys](schema.md#foreign-keys)), so everything listed under the next section applies to
  SQLite too: deleting a row a live row points at, or saving a reference to an unsaved entity, fails
  with `ForeignKeyViolation`.
- **The first start rebuilds every table that has a reference**, copying its rows, because SQLite
  cannot add a key to an existing table. A row that already points at a missing row is copied and
  reported as a warning with the count. Later starts rebuild a table only when its keys change.
- **Every connection a context opens runs `PRAGMA foreign_keys`** before its transaction begins: on
  when the provider keeps keys, off on the schema update's own. A tool that writes to the file on
  its own connection must turn the checks on itself.

## Foreign keys, on by default on SQL Server and PostgreSQL

- **The schema update keeps a foreign key for every reference the model implies**: a child's
  `Parent`, a stored `[LinkedObject]`, a `[LinkedBy]` key and a `[ForeignKey(typeof(T))]` column,
  between two types the provider stores (see [Foreign keys](schema.md#foreign-keys)). The first start
  after upgrading creates them, after the tables and indexes, and logs a warning for each key it
  creates over rows that already point at missing rows. Those rows are left as they are and not
  checked; rows written from then on are. `RelationalProviderOptions.ForeignKeys = false` turns it
  off, and drops the keys Nucleus created.
- **Every key column is indexed.** An existing `[LinkedObject]` or `[ForeignKey]` column without an
  index gains one (`NCI_`) at that start, which takes a while on a large table.
- **A read-only schema fails to start until the keys exist.** The missing keys and indexes make the
  schema not current, as a missing index always has, and `ReadOnlySchema` never updates it. Before
  upgrading a site that starts read-only, run one start with a writable schema (or turn keys off).
- **Deleting a row that a live row points at fails** with a `DatabaseException` of kind
  `ForeignKeyViolation`. It used to succeed and leave the reference pointing at nothing, which
  read as null. Removing a stored child that a link points at fails the same way.
- **Saving a reference to an entity that was never saved fails**, unless the link is `forceSave`. It
  used to store the ID of a row that did not exist.
- **A hard delete clears the references soft-deleted rows hold to what it deletes**, setting them
  to null, so a soft-deleted row does not stop it. A `[LinkedBy]` key declared as a non-nullable
  `Guid` reads back as `Guid.Empty` on such a row.
- **An asynchronous save inserts one table's rows in the order it built them.** It ran them last
  first, so a save that ordered a batch had the order undone.

## A save writes rows after the rows they point at, and a soft child needs a soft owner

- **A save of several types writes each type after the types its rows point at.** A forceSave
  link that points at another forceSave link, a root that points at another root of the same
  save, and a linked collection member that points at a one written in the same save are written
  second, whatever order the save met them in. They were written in the order the save met them,
  so a row could be written before the row it points at. Types that point at each other keep the
  order they were given in.
- **New rows of one table that point at each other are inserted in that order too**, through a
  `[LinkedObject]` typed as the entity itself (a keeper's mentor). A batch of more than 100 new
  rows is now built in the order given; it was built in parallel and its order was not stable.
- **A delete of several types removes each type before the types it points at.** It ran them in
  the order it met them. Only a delete of objects that share a base entity type covers several
  types.
- **A child type that is deleted soft under an owner that is deleted hard fails the schema
  build.** Deleting the owner removed its row and kept the flagged children, pointing at a row
  that no longer existed. Make the owner soft too, or the child hard. The type's own
  `[Deletable(DeleteBehavior.Soft)]` and a soft declaration on its collection are both refused
  under a hard owner; an owner that cannot be deleted at all is not affected.

## A failed cache refresh waits, and the cache hands out copies

- **After a background refresh of a `[Cacheable]` type fails, the next one waits one
  `refreshInterval`, and the failure is logged as a warning** through the manager's logger. The
  next read retried at once, silently, so while the database was down every cached read started
  another load. Reads still get the cached rows meanwhile, and past `cacheLifetime` a read still
  waits for the rows and sees the failure.
- **`NucleusCache.GetOrLoad` returns a list of its own over the shared objects.** It returned the
  cache's own list, reachable through `ObjectDataManager.Cache`, so adding to or removing from it
  changed it for every reader, and doing so while a query of every row was being enumerated made
  that enumeration throw. The objects in the list are still shared.

## Query operators run in the database, and the cache fills on either path

- **`FirstAsync`, `FirstOrDefaultAsync`, `SingleAsync` and `SingleOrDefaultAsync` on a query read
  one row, or two for Single.** They loaded every match with its children and took the first in
  memory. Which row First returns without an order is still whichever the database returns first.
- **`AnyAsync`, `CountAsync` and `LongCountAsync` on a query run in the database** and read no
  child collection. They loaded every match with its children. A name passed to `Exclude` or
  `Include` that is not on the type still throws.
- **`RowQuery<T>.IncludeDeleted()`** reads the rows marked deleted too. A row read saw live rows
  only.
- **A read of every row of a `[Cacheable]` type answers from the cache on every path.**
  `LoadAsync<T>()` with no predicate read the table each time; now it answers from the cache like
  `Load<T>()`, as do `Get<T>()` with no `Where` (`ToListAsync`, `await foreach` and a synchronous
  enumeration) and `LoadStreamAsync<T>()` with no predicate. A cold cache is filled on the
  asynchronous path by an asynchronous read (`LoadAsync`, `LoadByIDAsync`, `LoadAllAsync`, a
  stream), which filled it synchronously and blocked the thread.
- **A read of every row of a `[Cacheable]` type returns a list of its own.** `Load<T>()` and
  `LoadAllAsync` returned the cache's own list, so adding to it or removing from it changed it for
  every reader. The objects in the list are still shared. Code that compared two such lists by
  reference finds them different now.
- **Past `refreshInterval`, a read gets the cached rows at once while they are read again in the
  background.** The first read past the interval read the table in-line and waited for it.
- **A read by ID of a `[Cacheable]` type no longer misses a stored row when a save evicts the
  cache during the read.** It filled the cache and then looked the ID up in it separately, so an
  eviction in between returned null.

## A rollback ends the transaction at once

- **`Rollback` and `RollbackAsync` roll back immediately.** They only marked the
  transaction, which rolled back when the context was disposed; until then it kept the locks its
  writes took, and the objects its saves marked saved stayed marked. Now the transaction ends at
  once with the context still open: the locks are released, the objects are put back as they were
  before their first save in it, anything run on the context after it throws
  `TransactionCompleted`, and a commit throws `TransactionDoomed` as before. A rollback on a child
  context ends the whole tree's transaction, the root's work included; it only marked the tree,
  and the root's later commit threw. A commit of the child, or of any other context of the tree,
  after it throws `TransactionDoomed`.
- **A cancelled `CommitAsync` rolls back at once**, instead of marking the transaction for the
  dispose.
- **`TryRollback` and `TryRollbackAsync` never throw.** A rollback that fails is logged, so the
  failure a catch block is handling propagates.

## SQL Server: snapshot reads on a created database, and a locking members check

- **A database the SQL Server provider creates has `READ_COMMITTED_SNAPSHOT` on.** Its reads take
  the last committed version of a row instead of waiting behind a save, as on PostgreSQL, SQLite
  and Azure SQL. It was created with locking read committed, the SQL Server default. Set
  `ReadCommittedSnapshotOnCreate` false to keep that. An existing database is not changed.
- **Initialise warns when an existing SQL Server database has `READ_COMMITTED_SNAPSHOT` off**, and
  names the statement that turns it on.
- **A delete's check for linked collection members waits for a member being inserted on SQL
  Server, whatever the database's setting.** On a database with `READ_COMMITTED_SNAPSHOT` on, it
  read past a member another transaction had inserted and not yet committed, and the delete went
  ahead and left that member pointing at a deleted row. It now waits for that transaction, and
  refuses the delete once the member commits.

## A saved object's Timestamp moves in memory

- **Every row a save writes takes the client clock as its `Timestamp` when the save commits.**
  Only a new object did: a loaded object kept the value it was loaded with after any number of
  updates, though the server had stamped the row each time. Now the object the save was asked for,
  and each child, link or member the save wrote because it was new or changed, takes the clock; a
  row the save did not write keeps its value, as the stored row does. The rows of one save share
  one value. A rollback puts back the value each object had before its first save in the
  transaction. The stored value is still not read back, so the two differ by the clocks and the
  engine's precision.

## A cancelled commit rolls back, and every open is retried

- **A commit or rollback given a cancelled token rolls the transaction back.** `CommitAsync` threw
  `OperationCanceledException` and left the context unmarked, so without `RequireCommit` disposing
  it committed the work the caller had just cancelled. `RollbackAsync` threw before it rolled back,
  with the same result. Both now mark the transaction for rollback and then throw.
- **The schema check's connections and SQL Server's change subscriptions retry a transient failure
  to open**, `ConnectAttempts` times within the connect timeout, as a context's connection does.
  They made one attempt on SQL Server and SQLite; PostgreSQL already retried.
- **A SQLite schema statement waits `BusyTimeoutSeconds` for the write lock.** It was given
  `SchemaCommandTimeoutSeconds` as its command timeout, which on SQLite is the wait for another
  connection's write lock, so an index created at start waited up to 15 minutes behind a writer.
  SQLite now ignores `SchemaCommandTimeoutSeconds`, as it does `StatementTimeoutSeconds`.

## An opt-in retry policy for the manager's own operations

- **`RetryAttempts` on every provider's options, 1 by default.** Set above 1, a manager save,
  delete or load, a streamed load until its first row, a row read, or the read of a lazy
  collection or a linked object that fails with a transient `DatabaseException` having applied
  nothing is attempted again on a new connection, with the back-off opening a connection uses.
  At the default nothing changes. See [Retries](saving.md#retries).
- **With the policy on, the last attempt's failure is not transient.** It is the same failure with
  `IsTransient` false and `Attempts` set, so a caller's `catch (DatabaseException { IsTransient: true })`
  does not retry it again. Remove a retry of your own around manager calls when you turn the
  policy on; keep it around caller-owned contexts, which the policy never touches.
- **A batch save of mixed types checks its objects before it opens a connection.** The manager
  runs each provider's objects' rules and `PreSave` once, before the first attempt, instead of the
  context running them as it saved. A rule that fails now fails before a connection is taken.
- **`IDataProvider.RetryAttempts`** is a new member with a default of 1, so another implementation
  still compiles and is never retried.

## A save that fails keeps nothing the server returned

- **A value read back with `ReturnOutputToProperty` is taken when the save commits.** It used to
  replace the object's value, and drop its pending one, as soon as the write returned, before the
  transaction committed. A save that then failed left the object holding a value from a
  rolled-back write, and saving it again wrote that value instead of the object's own, so a server
  default or generated value was not produced again. Now the object keeps its own value until its
  changes are committed: after the manager's save commits, or as a caller-owned context's save
  runs, where a rollback puts it back with the rest of the object. Code that read the property
  between the write and its commit sees the old value; read it after the save returns.
- **`AcceptServerValue` holds the value until the commit.** Call `CommitChanges` after it to see
  the value, as a save does.

## A database failure is a DatabaseException, and statements have a time limit

- **A database failure is a `DatabaseException`**, on every engine and every path, with the
  driver's exception as its inner exception. It used to be a bare `System.Exception`, which
  `catch (NucleusException)` did not catch, with the driver's exception one level down on a read or
  a delete and two levels down on a manager save. Code that walked `InnerException.InnerException`
  to find the driver's exception now finds it one level down; code that caught `Exception` still
  catches it. See [Failures](saving.md#failures).
- **A failure says whether to retry.** `IsTransient` (worth another attempt, and nothing was
  applied), `MayHaveBeenApplied`, `Kind`, `Operation` and `Attempts`. A check of the driver's own
  `DbException.IsTransient` never retried on SQL Server or SQLite, whose drivers always say false.
- **The manager's `Save` and `SaveAsync` no longer wrap a failure in "Error saving objects".**
  Whatever the context threw reaches the caller as it was thrown. A failure that is not the
  database's (a defect, an `InvalidOperationException`) used to be wrapped in a bare `Exception`
  and is now thrown as it is.
- **A failed open, commit or rollback is translated too.** Opening a connection used to throw the
  driver's exception or a `TimeoutException`; it is a `DatabaseException` of operation `Open`, kind
  `ConnectionFailed` or `Timeout`. A failed commit used to throw the driver's exception; it is
  `CommitOutcomeUnknown`, which may have been applied and is not transient, unless the engine
  rejected the transaction (a deadlock, a serialization failure, a constraint checked at commit).
- **A failure reading a streamed row is translated** like a failed read; it used to reach the
  caller as the driver's exception.
- **A non-database failure of a read or a write propagates as it was thrown.** A defect in reading
  a row, or one of Nucleus's own exceptions such as `MissingResultSet`, used to be wrapped in a bare
  `Exception`.
- **Every statement has a time limit, `StatementTimeoutSeconds`**, 30 seconds by default, which is
  the drivers' own default, so nothing changes unless you set it. A context can take its own
  (`ContextOptions.StatementTimeoutSeconds`). SQLite ignores it; its only limit is still
  `BusyTimeoutSeconds`.
- **A bulk write's or a schema statement's timeout no longer carries over.** Each set its timeout on
  the context's shared command and left it there, so later statements on the same context ran with
  60 or 900 seconds. Each statement now gets its own limit.
- **`ConnectAttempts` is a shared option.** It moved from `PostgresConfiguration` to
  `RelationalProviderOptions`, so SQL Server and SQLite retry a transient failure to open too, 3
  attempts by default within the same connect timeout. They used to make one attempt. Code that set
  `PostgresConfiguration.ConnectAttempts` still compiles.
- **A change subscription that fails to start** is a `DatabaseException`, or a
  `DataProviderException` when the engine refused for a missing prerequisite, instead of a bare
  `Exception`. A cancelled start is an `OperationCanceledException`. On PostgreSQL, any failure now
  releases the subscription's connection slot; only some did.
- **A catalog read or schema statement that fails** is a `DatabaseException` of operation `Schema`
  instead of a bare `Exception`. A SQLite file that cannot be put in write-ahead log mode, or whose
  folder cannot be created, is a `SqliteDataProviderException`.

## MaxConnectionPoolSize is the one connection limit, and the defaults fail fast

- **A provider holds at most `MaxConnectionPoolSize` connections to its database.** On SQL Server
  and PostgreSQL its transactional connections and the rest had connection strings that differed
  by `Enlist`, and the drivers pool by connection string, so a provider kept two pools of that
  size and the server could see twice the setting. Every connection now has one connection string,
  so there is one pool. A server sized for twice the setting can be sized for the setting. One
  connection is outside it on SQL Server: the driver's query notification listener, while anything
  watches for changes (see the known limits).
- **A raw `ConnectionString` gets the limit too.** Its `Max Pool Size` (`Maximum Pool Size` on
  PostgreSQL) is replaced with `MaxConnectionPoolSize`, and its `Enlist` with `false`. It used to
  keep the driver's default of 100 whatever the setting said.
- **Change subscriptions count against the limit.** Each live subscription holds one connection
  and now one slot, beside the open contexts. With every slot held, a new context or subscription
  waits `PoolWaitTimeoutSeconds` (on the asynchronous path; the synchronous path never waits) and
  fails with `PoolExhausted`, instead of passing the check and
  timing out in the driver's pool. Raise `MaxConnectionPoolSize` by the number of subscriptions an
  application keeps open if it ran close to the limit.
- **Each provider has a budget of its own.** The count of open contexts was shared by every provider
  of one engine in a process, so two SQL Server providers took slots from each other. Each now
  counts its own, matching its own driver pool.
- **The startup connections are not pooled.** The `master` connection (SQL Server) and the
  maintenance database connection (PostgreSQL), which find and create the database at initialise,
  close when they are done instead of waiting idle in a pool of their own.
- **A transactional context inside a `TransactionScope` is refused**, on every engine, with a
  `DataContextException` of kind `AmbientTransaction`. That covers the manager's saves and deletes,
  which open one. No connection enlists in an ambient transaction any more, so such a context would
  have committed even if the scope rolled back. Before, it failed on PostgreSQL, joined the scope on
  SQL Server for one connection and failed on a second, and ignored the scope on SQLite. Reads, which
  are not transactional, run inside a scope as before.
- **No default waits more than 3 seconds.** The failure goes back to the caller, who decides
  whether to wait longer or retry. A busy application raises these:
  - `PoolWaitTimeoutSeconds`, on every provider: 10 to 3.
  - PostgreSQL `ConnectTimeoutSeconds`: 15 to 3, matching SQL Server.
  - PostgreSQL opens, retries included: every attempt now shares the connect timeout. A timeout
    used to be retried `ConnectAttempts` times, about 45 seconds in all at the old defaults. A quick
    failure is still retried within the timeout.
  - SQLite `BusyTimeoutSeconds`: 30 to 3. SQLite does not queue writers, so more writes fail under
    contention; raise it where several connections or processes write heavily.

## A context save follows its transaction in memory

- **An object saved through a caller-owned context is put back if the transaction does not
  commit.** `CoreObject.SaveAsync(IDataContext, ...)` used to mark the object, its children and its
  removals saved for good as soon as the writes ran, so after a rollback the object said it was
  saved and had forgotten its removals, and the caller had to reload it. Now a rollback, a failure
  that dooms the transaction, or a failed commit puts each object back as it was before its first
  save in that transaction: a new object is new again, a loaded one has its written changes
  pending again, and removed children and linked collection members are pending deletion again.
  Changes made after the save are kept as pending changes. Save the objects again, through the
  manager or a new context, without reloading them.
- **`IDataContext.Save` and `SaveAsync` mark the objects saved.** For one object or a batch, they
  used to leave the objects as they were, so after a commit a new object was still new and its
  next save inserted it again. They now behave as `CoreObject.SaveAsync(IDataContext, ...)` does.
  Remove any `CommitChanges()` call you made after a context save to work around it; it is now
  redundant.
- **While the transaction is open, the objects match it.** Saving the same object twice in one
  context updates it the second time instead of inserting it again. A context without a
  transaction marks the objects saved at once, because each write has already committed.
- **Unchanged.** The manager's own saves mark their objects saved only once their transaction has
  committed, and audit them as before. A save through a context is still not audited and still
  evicts no `[Cacheable]` cache.

## Unchanged children are not written

- **A save writes only the rows that changed below the object saved.** Saving a parent used to
  update every loaded child at every depth, and each update set `Timestamp`, so every child's
  `Timestamp` moved on every parent save. A child with no changes of its own is now not written:
  its `Timestamp`, any other `SetValueOverride` column and any `ReturnOutputToProperty` read-back
  are left as they were. Its own changed or new children are still written. The same holds for a
  `forceSave` link or a linked collection member that the save reaches only for what is below it.
- **The object you save is still always written.** Saving an unchanged object stamps its
  `Timestamp`, as before. Code that read a child's `Timestamp` as "when its parent was last saved"
  must read the parent's instead.

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
  15 seconds as before. (Since lowered to 3; see the connection limit section above.)
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
  failure is still wrapped. (No longer: see the `DatabaseException` section above.) Code that caught `Exception` still does; code that read
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
- **A unique violation is a `UniqueViolationException`.** It derives from `Exception` (since
  changed: it is now a `DatabaseException`, itself a `NucleusException`), keeps the old
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
