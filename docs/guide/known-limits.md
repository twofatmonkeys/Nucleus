# Known limits

What Nucleus does not do yet, or does in a way a consumer has to allow for. Each is an open
finding in [`docs/code-review-findings.md`](../code-review-findings.md), which has the detail.

## Saving

- **A save through a caller-owned context commits in memory before the caller commits.**
  `SaveAsync(IDataContext, ...)` marks the object, its children and its removals saved as soon as
  the writes run. A caller who then rolls back must reload the objects.
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

- **A provider can hold twice `MaxConnectionPoolSize` connections** on SQL Server and PostgreSQL.
  Transactional and other connections differ by `Enlist`, so the driver keeps a pool of that size
  for each. Size the server's connection limit for twice the setting per process, or lower the
  setting.
- **`[LinkWhenLogging]` does nothing yet.** It is meant to attach the linked object's type and ID
  to the log entry's linked objects. The implementation did not come across from the library
  Nucleus grew out of and is to be restored; until then the attribute is accepted and ignored.
