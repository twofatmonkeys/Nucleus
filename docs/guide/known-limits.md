# Known limits

These are open findings ([`docs/code-review-findings.md`](../code-review-findings.md)) that change what a consumer must do.

- **Saving a parent rewrites every loaded child.** An update links an update of every child in each
  loaded collection, and each sets `Timestamp`, so a parent with many loaded children writes them
  all on every save. Exclude, or make lazy, a large collection the save does not need.
- **A save through a caller-owned context commits in memory before the caller commits.**
  `SaveAsync(IDataContext, ...)` marks the object, its children and its removals saved as soon as
  the writes run. A caller who then rolls back must reload the objects.
- **A lazy collection's first read is a synchronous round trip**, on a connection of its own. Use
  `Include` or `LoadAsync()` where that matters.
- **A linked collection's first read is a synchronous round trip**, like a lazy collection's. Use
  `Include` or `LoadAsync()` where that matters.
- **A refused delete of a one is a check, not a constraint.** Nucleus creates no foreign keys, so a
  member another transaction inserts while the one is being deleted is not seen, and is left
  pointing at a deleted row.
- **The one side of a linked collection is a root.** A child type cannot hold one, because its
  parent's cascade deletes it without loading it, where members could not be checked for.
- **A `[LinkedObject]`'s first read is a synchronous round trip** too, with no asynchronous form.
  Use `Include` where that matters. Rows that read the same reference this way each load their own
  instance; included references share one.
- **A provider can hold twice `MaxConnectionPoolSize` connections** on SQL Server and PostgreSQL.
  Transactional and other connections differ by `Enlist`, so the driver keeps a pool of that size
  for each. Size the server's connection limit for twice the setting per process, or lower the
  setting.
- **On SQL Server, reads wait behind saves.** Under the default locking read committed, a load
  whose condition has no index scans its table and waits for any row a concurrent save holds,
  until that save commits. It no longer deadlocks, but it waits. Index the columns you filter by,
  or turn on `READ_COMMITTED_SNAPSHOT` for the database; Nucleus does not set it.
- **A caller-owned context commits when it is disposed** unless `Rollback()` or `TryRollback()`
  was called. A failed statement does not roll it back by itself, so wrap the work in a try/catch
  that rolls back and rethrows. Work done through it evicts no `[Cacheable]` cache. A single type's
  batch belongs in `SaveAsync(objs, token)` instead, which has neither problem.
- **`[LinkWhenLogging]` does nothing yet.** It is meant to attach the linked object's type and ID
  to the log entry's linked objects. The implementation did not come across from the library
  Nucleus grew out of and is to be restored; until then the attribute is accepted and ignored.
- **The synchronous save never uses the bulk engine.** A synchronous batch over the provider's
  parameter limit runs as several commands. `SaveAsync` is the path that hands a large batch to
  the bulk engine.
- **A row read sees live rows only.** `Rows<T>()` has no counterpart to `IncludeDeleted`.
