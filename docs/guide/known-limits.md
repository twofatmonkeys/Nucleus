# Known limits

What Nucleus does not do yet, or does in a way a consumer has to allow for. Each is an open
finding in [`docs/code-review-findings.md`](../code-review-findings.md), which has the detail.

## Registration and entities

- **The parameterless `AddNucleus()` makes the schema writable.** `ReadOnlySchema` is `true` by
  default, but that overload sets it `false`, so the shortest registration creates and updates
  tables. Use `AddNucleus(o => o.ReadOnlySchema = true)` where the schema must not change.
- **`Timestamp` in memory is the client's clock.** The server stamps the column on every write, and
  the stamped value is not read back. A save gives each row it wrote the client clock when it
  commits, so the value in memory is close to the stored one but not equal to it. Reload the
  object where the stored value matters. See [Entities](entities.md).
- **A column's length cannot be set without its type.** `[DatabaseColumn(ColumnDataType.Unicode, 200)]`
  is the one form that sets a length: the length-only constructor and the `DataLength` field are
  nullable, which attribute syntax does not accept.
- **Three declarations are accepted and do nothing.** The `linkedIDProperty` argument of
  `[LinkedObject]`; `[RetrieveOnDemand]` on a property that is not a child collection; and
  `[InitializePriority]`.

## Saving

- **A caller-owned context disposed without a commit still commits, until v3.** Commit it with
  `Commit` or `CommitAsync`. A failure of anything it runs (a statement, a validation rule, a
  cancellation) marks it for rollback, but an exception in your own code between two saves does
  not, so disposing it commits the first and logs a warning. Set `RequireCommit` on the provider's
  options and a context disposed without a commit rolls back instead; v3 makes that the only
  behaviour. Work done through a caller-owned context evicts no `[Cacheable]` cache. A single
  type's batch belongs in `SaveAsync(objs, token)` instead, which has neither problem.
- **A context opened without options has no transaction.** `CreateContextAsync<T>()` with no
  `ContextOptions` writes each statement on its own, and its `CommitAsync` does nothing. Pass
  `ContextOptions.Transactional` for work that must commit as one.
- **A child context shares its root's connection.** A context opened with `ContextOptions.Parent`
  runs on its root's connection and transaction, so the tree commits or rolls back as one. Use one
  context of a tree at a time; they cannot run commands concurrently.
- **An asynchronous execute skips a hand-built query without a table schema.**
  `ExecuteQueryAsync` runs nothing for a query whose table schema is not set, and does not say so;
  `ExecuteQuery` runs it. Every query Nucleus builds has one.
- **The synchronous save never uses the bulk engine.** A synchronous batch over the provider's
  parameter limit runs as several commands. `SaveAsync` is the path that hands a large batch to
  the bulk engine.

## Failures and retries

- **The retry policy is off by default, and never covers a caller-owned context.** Until a
  provider's `RetryAttempts` is set above 1, only opening a connection is retried, and a failed
  statement or commit is reported at once. Work on a context you opened is yours to retry, as a
  whole, when `IsTransient` is true. Each attempt, the policy's or yours, opens its own connection
  with its own `ConnectAttempts`, so the two multiply.
- **A write on a context with no transaction reports that it may have been applied.** Each statement
  commits as it runs, so Nucleus cannot tell how much of a failed write was kept, and such a failure
  is never transient, even a lock wait that ran out before anything ran. Write through a
  transactional context (every manager save and delete does) to get a failure known not to have
  been applied.
- **After `CommitOutcomeUnknown`, the objects are left unsaved.** The commit may have taken effect.
  Load the rows to find out before saving again: a new object that did commit fails a second insert
  with a `UniqueViolationException` on its key.
- **Not every failure of the database is a `DatabaseException`.** A full connection budget is a
  `DataContextException` of kind `PoolExhausted`, and a database that cannot be reached when the
  provider first initialises is a `DataProviderException` of kind `NotInitialized`, with the cause
  in the log only. Code that handles "the database is unavailable" catches all three for now.
- **SQLite has no statement time limit.** Its driver's command timeout is the wait for the write
  lock (`BusyTimeoutSeconds`), so `StatementTimeoutSeconds` is ignored, a long statement runs until
  it finishes, and a cancelled token does not stop one under way.

## Reading

- **A filter on a long list of values fails at the engine's parameter limit.**
  `ids.Contains(x.ID)` sends one parameter per value, so a list over about 2,000 values is refused
  on SQL Server (the limits are far higher on PostgreSQL and SQLite). Split the list and read each
  part; a read split that way is ordered, paged and counted in memory.
- **A projection throws `ArgumentException`.** `Get<T>().Select(...)` to anything but the entity
  fails with an `ArgumentException` about a generic constraint, not the `NotSupportedException`
  the other untranslated operators throw. Project in memory, or use a row read.
- **Text sorts by each engine's own rules.** An `OrderBy` on a text column is written as the
  engine's plain `ORDER BY`, so the order follows the column's collation: case-insensitive under
  SQL Server's usual collations, the database's locale on PostgreSQL, and by code point on SQLite,
  where `"Banana"` sorts before `"apple"`. NULL sorts below every value on every engine. Where
  the same order matters on several engines, order by a column that holds a normalised form.
- **A deferred read runs outside the caller's transaction.** The first read of a lazy collection,
  a linked collection or a `[LinkedObject]`, and a collection's `LoadAsync()`, run on a context of
  their own, even for an object a transactional context loaded. They do not see rows the caller's
  transaction has written and not committed, and each takes a connection of its own within
  `MaxConnectionPoolSize` while the caller holds one. On SQL Server without
  `READ_COMMITTED_SNAPSHOT`, a read of rows the caller's own transaction has written waits for that
  transaction, which cannot commit while the thread waits, so the read fails with a `Timeout` after
  `StatementTimeoutSeconds`. Read the collection before writing to it in the transaction, or after
  the commit.
- **A `[LinkedObject]` has no asynchronous load.** Its first read is synchronous, and unlike a
  collection there is no `LoadAsync()` for it. Use `Include` where that matters. Rows that read the
  same reference this way each load their own instance, unless the referenced type is `[Cacheable]`
  and the rows were loaded through the manager, which answers from the cache; included references
  share one.
- **On SQL Server, reads wait behind saves.** Under the default locking read committed, a load
  whose condition has no index scans its table and waits for any row a concurrent save holds,
  until that save commits. It no longer deadlocks, but it waits. Index the columns you filter by,
  or turn on `READ_COMMITTED_SNAPSHOT` for the database. The provider turns it on for a database it
  creates (`ReadCommittedSnapshotOnCreate`), and logs a warning at initialise when an existing
  database has it off; it never changes an existing database, because the setting needs every
  other connection to it closed and applies to every application that uses it.

## Foreign keys

- **Types whose new rows point at each other in a cycle cannot be saved together.** A save writes
  a type after the types its rows point at; when new rows of two types each point at the other's
  (A to B and B to A), no order serves, the save keeps the order it was given and the key refuses
  the second. Save one side without its reference first, then set it. SQL Server has no deferred
  keys, so no engine defers them.
- **A save that removes a stored row and clears a reference to it in the same save fails.** The
  removal's delete runs first, so a replacement with the same unique value does not collide, and
  the reference still points at the row then. Clear the reference in one save and remove the row
  in the next.
- **A bulk batch whose new rows point at each other and set different columns can be refused.**
  The bulk engine writes each set of columns as its own statement, so a row can be written before
  a row it points at that sets other columns. Give the batch's rows the same columns, or save the
  rows pointed at first.

## Linked collections

- **A connection opened outside Nucleus does not enforce SQLite's keys.** SQLite turns key
  checks on per connection; Nucleus turns them on for its own. A tool or script writing to the
  file runs `PRAGMA foreign_keys = ON` first, or its writes are not checked.
- **The one side of a linked collection is a root.** A child type cannot hold one, because its
  parent's cascade deletes it without loading it, where members could not be checked for.

## Connections and logging

- **SQL Server's notification listener is one connection outside `MaxConnectionPoolSize`.** A
  provider whose contexts or subscriptions watch for changes starts the driver's query
  notification listener, which opens one connection of its own, unpooled, for as long as anything
  watches. Every other connection the provider holds is within the limit.
- **A transactional context cannot join a `TransactionScope`.** No connection enlists in an
  ambient transaction, so opening a transactional context, or saving or deleting through the
  manager, inside a scope fails with `DataContextException` of kind `AmbientTransaction`. Make the
  context's transaction the unit of work: open it with `CreateContextAsync<T>` and commit it.
- **`[LinkWhenLogging]` does nothing yet.** It is meant to attach the linked object's type and ID
  to the log entry's linked objects. The implementation did not come across from the library
  Nucleus grew out of and is to be restored; until then the attribute is checked (on a property
  whose type is not an entity it fails the schema build) and otherwise ignored.
- **SQLite takes a file path only.** An in-memory database and any `file:` URI, one naming a file
  on disk included, are refused.
