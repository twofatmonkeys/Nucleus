# Known limits

What Nucleus does not do yet, or does in a way a consumer has to allow for. Each is an open
finding in [`docs/code-review-findings.md`](../code-review-findings.md), which has the detail.

## Saving

- **A caller-owned context disposed without a commit still commits, until v3.** Commit it with
  `Commit` or `CommitAsync`. A failure of anything it runs (a statement, a validation rule, a
  cancellation) marks it for rollback, but an exception in your own code between two saves does
  not, so disposing it commits the first and logs a warning. Set `RequireCommit` on the provider's
  options and a context disposed without a commit rolls back instead; v3 makes that the only
  behaviour. Work done through a caller-owned context evicts no `[Cacheable]` cache. A single
  type's batch belongs in `SaveAsync(objs, token)` instead, which has neither problem.
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
- **SQLite has no statement time limit.** Its driver's command timeout is the wait for the write
  lock (`BusyTimeoutSeconds`), so `StatementTimeoutSeconds` is ignored, a long statement runs until
  it finishes, and a cancelled token does not stop one under way.

## Reading

- **A lazy collection's first read is a synchronous round trip**, on a connection of its own. Use
  `Include` or `LoadAsync()` where that matters.
- **A linked collection's first read is a synchronous round trip**, like a lazy collection's. Use
  `Include` or `LoadAsync()` where that matters.
- **A `[LinkedObject]`'s first read is a synchronous round trip** too, with no asynchronous form.
  Use `Include` where that matters. Rows that read the same reference this way each load their own
  instance; included references share one.
- **A row read sees live rows only.** `Rows<T>()` has no counterpart to `IncludeDeleted`.
- **On SQL Server, reads wait behind saves.** Under the default locking read committed, a load
  whose condition has no index scans its table and waits for any row a concurrent save holds,
  until that save commits. It no longer deadlocks, but it waits. Index the columns you filter by,
  or turn on `READ_COMMITTED_SNAPSHOT` for the database; Nucleus does not set it.

## Linked collections

- **A refused delete of a one is a check, not a constraint.** Nucleus creates no foreign keys, so a
  member another transaction inserts while the one is being deleted is not seen, and is left
  pointing at a deleted row.
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
  Nucleus grew out of and is to be restored; until then the attribute is accepted and ignored.
