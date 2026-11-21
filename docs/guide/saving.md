# Saving and deleting

There is one save and one delete, for one object or a batch. Save the root, and its graph follows:
its child collections at every depth, the children removed from them, and the linked objects and
linked collection members it holds. [Relationships](relationships.md) covers what a graph is; this
page covers the save itself.

## Saving through a store

An application service takes `IObjectDataManager`. A batch of one type is one call and one
transaction. Several types in one transaction use a caller-owned context, which the caller commits
with `CommitAsync` when the work is done:

<!-- sample: store -->
```csharp
// What an application service or a store looks like: it takes the interface DI registers.
public sealed class KeeperDesk(IObjectDataManager data)
{
    public Task<Monkey?> FindAsync(Guid id, CancellationToken ct) =>
        data.LoadByIDAsync<Monkey>(id, ct);

    public Task SaveAllAsync(IReadOnlyList<Monkey> monkeys, CancellationToken ct) =>
        data.SaveAsync(monkeys, ct);                   // one transaction

    // Two types in one transaction, committed when the work is done. A failed save rolls it all back.
    public async Task WeighAsync(Monkey monkey, WeighIn weighIn, CancellationToken ct)
    {
        await using var context = await data.CreateContextAsync<Monkey>(ContextOptions.Transactional, ct);
        await context.SaveAsync(monkey, ct);           // validates, then writes
        await context.SaveAsync(weighIn, ct);
        await context.CommitAsync(ct);
    }
}
```

An object saved through a context is marked saved as its writes run, so saving it twice in the
same context updates it the second time. If the transaction rolls back, a failure dooms it, or the
commit fails, every object saved in it is put back as it was before its first save there: a new
object is new again, a loaded one has its changes pending again, and its removed children are
pending deletion again. Changes made since the save are kept. Save the objects again without
reloading them.

## Saving a batch

A batch can hold objects of different types. Each is routed to the provider that stores it, and
each provider's objects are saved in one transaction:

<!-- sample: mixed-save -->
```csharp
// Objects of different types in one call: one transaction per provider that stores them.
// A batch too large for ordinary statements goes to the provider's bulk engine by itself.
await data.SaveAsync(new ICoreObject?[]
{
    new BananaVariety { Name = "Cavendish", Curvature = 7 },
    new BananaVariety { Name = "Plantain", Curvature = 3 },
    new WeighIn { MonkeyName = "Sir Reginald", WeightKg = 31.0m, Notes = "  no change  " },
}, ct);
```

There is no separate bulk save to call. On the asynchronous path a batch too large for ordinary
statements is written by the provider's bulk engine by itself. The synchronous `Save` never uses
the bulk engine: over the provider's parameter limit it runs as several commands.

## Unique violations

A write that breaks a unique index throws `UniqueViolationException` (namespace `TFM.Nucleus.Data`,
in the Abstractions package), whichever provider stores the data. It says which rule was broken:

- **`EntityType`** names the entity, and `IndexName` the Nucleus index name.
- **`PropertyNames` and `ColumnNames`** list the index's key properties and columns, in key order.
- **For a `[DatabaseColumn(Unique = true)]` index,** `PropertyName` and `ColumnName` name its one
  property and column, and `Is<T>(name)` tests for it by either.
- **For a unique `[TableIndex]`,** `DeclaredIndexName` is the name it was declared with, and
  `IsIndex<T>(name)` tests for it. `PropertyName` and `ColumnName` are then null, even for one key.
- **For an index Nucleus does not manage,** such as a primary key, the names are null and the lists
  empty.

It is a [`DatabaseException`](#failures) of kind `UniqueViolation`, never transient, with the
provider's own exception as its inner exception. The manager's `Save` and `SaveAsync` throw it as
it is, not wrapped, so the filters below work on them as they do on a context's save.

<!-- sample: unique-violation -->
```csharp
try
{
    await data.SaveAsync(new Keeper { Name = "Colin", Email = "mavis@portlyprimate.example" }, ct);
}
catch (UniqueViolationException ex) when (ex.Is<Keeper>(nameof(Keeper.Email)))
{
    return true;                               // another keeper has that address
}
catch (UniqueViolationException ex) when (ex.IsIndex<GiftShopVisit>("SessionPath"))
{
    return true;                               // a unique [TableIndex], by its declared name
}
```

## Failures

When the database fails an operation, Nucleus throws a `DatabaseException` (namespace
`TFM.Nucleus.Data`, in the Abstractions package), on every engine, with the driver's own exception
as its inner exception, one level down. It says what happened in terms that do not depend on the
engine:

- **`Kind`**: `ConnectionFailed`, `Timeout`, `Deadlock`, `SerializationFailure`, `UniqueViolation`,
  `ForeignKeyViolation`, `NotNullViolation`, `ValueTooLong`, `CommitOutcomeUnknown` or `Other`.
- **`IsTransient`**: another attempt of the whole operation may succeed and is safe. It is true
  for a deadlock, a dropped connection, a timeout and the like, and only when nothing was applied.
- **`MayHaveBeenApplied`**: the database may have kept some or all of what was written. It is true
  for a commit that failed in a way that does not say whether it took effect
  (`CommitOutcomeUnknown`), and for a write on a context with no transaction, where each statement
  commits as it runs. It is false for a read, and for a write whose transaction did not commit,
  which covers every manager save and delete.
- **`Operation`**: what Nucleus was doing (`Open`, `Read`, `Write`, `Delete`, `Commit`, ...).
- **`Attempts`**: how many times Nucleus tried the step that failed (see below).

A cancellation you asked for is an `OperationCanceledException`, never a `DatabaseException`, even
when the engine reports the cancelled command as its own error.

### Retries

Nucleus retries one thing by itself: opening a connection, when the failure is transient, up to
the provider's `ConnectAttempts` (3 by default), all within the connection's connect timeout. A
statement, a commit or a rollback that fails is reported at once, because only the caller knows
whether the operation is worth repeating. A retry is the caller's, of the whole operation, when
the failure says it is safe:

<!-- sample: transient-retry -->
```csharp
// Nucleus retries only opening a connection. Another attempt of the whole operation is
// the caller's, when the failure says it is worth one and nothing was applied.
for (var attempt = 1; ; attempt++)
{
    try
    {
        return await data.LoadByIDAsync<Monkey>(id, ct);
    }
    catch (DatabaseException ex) when (ex.IsTransient && attempt < 3)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);   // a deadlock, a dropped connection, a timeout
    }
}
```

Each attempt of the operation opens its own connection, with its own `ConnectAttempts`, so three
attempts at the default make at most nine opens. A save that failed leaves its objects unsaved, with
their changes and removals pending, so saving them again is the retry. After `CommitOutcomeUnknown`,
do not save again blindly: load the rows and see whether the commit took effect. An insert that
did commit fails a second time with a `UniqueViolationException` on its key.

The drivers do not say which of their errors are transient for SQL Server and SQLite
(`DbException.IsTransient` is always false for both), so a check of the driver's flag never
retries on those engines. Each provider classifies its engine's errors itself, by error number,
SQLSTATE or result code, which is what `IsTransient` reports.

### Time limits

Every statement a context runs has a time limit, the provider's `StatementTimeoutSeconds`, 30 by
default, the SQL Server and PostgreSQL drivers' own. A statement over it fails with a
`DatabaseException` of kind `Timeout`. A context for a long job takes a limit of its own:

<!-- sample: statement-timeout -->
```csharp
// A long job's own statements get five minutes; every other context keeps the provider's limit.
await using var context = await data.CreateContextAsync<WeighIn>(
    new ContextOptions { UseTransaction = true, StatementTimeoutSeconds = 300 }, ct);
await context.SaveAsync(new WeighIn { MonkeyName = "Gerald", WeightKg = 14.7m, Notes = "after lunch" }, ct);
await context.CommitAsync(ct);
```

A limit on the whole operation, retries included, is a `CancellationToken` with `CancelAfter`.
Schema statements have their own limit (`SchemaCommandTimeoutSeconds`), and so do bulk writes
(`BulkTimeoutSeconds`). SQLite has no limit on a running statement: its driver's command timeout is
the wait for another connection's write lock, which stays `BusyTimeoutSeconds`, so
`StatementTimeoutSeconds` is ignored there.

### Other exceptions

Every exception Nucleus defines derives from `NucleusException` (`TFM.Nucleus`), so
`catch (NucleusException)` catches the family: `DatabaseException` and `UniqueViolationException`,
`DataProviderException` and each provider's own, `DataContextException` (a full connection budget is
its kind `PoolExhausted`), `InvalidQueryException`, `CoreObjectException` and the schema exceptions.

Nucleus also throws the framework's own exceptions where they are the right ones
(`ArgumentException`, `NotSupportedException`, `InvalidOperationException`,
`OperationCanceledException`). Those, and a rule your `Validate()` throws, reach the caller as they
were thrown, not wrapped.

## Change notifications

A subscription tells a process that rows of a type were changed by something else, so it can
reload what it holds. SQL Server and PostgreSQL offer it; SQLite does not.

<!-- sample: change-subscription -->
```csharp
// SQL Server and PostgreSQL only: SQLite throws NotSupportedException.
await using var subscription = await data.SubscribeToChangesAsync<BananaVariety>(ct);
subscription.Changed += (_, e) => Console.WriteLine($"{e.EntityType.Name} changed outside this process: reload it.");
subscription.Faulted += (_, e) => Console.WriteLine("The subscription died; subscribe again.");
```

SQL Server uses SqlDependency and Service Broker, which needs `ALTER DATABASE ... SET ENABLE_BROKER`
and the `SUBSCRIBE QUERY NOTIFICATIONS` permission. PostgreSQL listens on a channel that each
table's trigger notifies, which needs nothing beyond the schema Nucleus creates.

## How it works underneath

- **Change tracking.** Every entity keeps committed and pending property dictionaries. Saves write only the columns that actually changed.
- **Schema management.** Tables, indexes and full-text catalogs are created and migrated from the entity attributes. Soft-deletable types gain `IsDeleted` and `DeletedAt` columns.
- **Several databases in one application.** Register one provider per abstract entity root. Each entity routes to the provider of its nearest registered root, and each provider owns its own connection, configuration and schema partition. Graphs that span providers save in separate transactions, so there is no distributed atomicity.
- **Layered cache.** `[Cacheable]` types keep the whole collection plus a Guid index in memory, with single-flight population, a hard TTL, serve-stale-while-revalidate, and eviction on save or delete. Hand-rolled, with no external caching package.
- **Paging.** `PagedResult<T>` batches the rows and the unpaged count into one round trip. SQL Server pages with a `ROW_NUMBER()` window, which works at every compatibility level; PostgreSQL and SQLite with `LIMIT` and `OFFSET`.
- **Bulk writes.** Large batches route through a temp table, loaded with `SqlBulkCopy` on SQL Server and COPY BINARY on PostgreSQL, then one insert or one changed-column bitmask update. SQLite, which writes in-process, runs them as commands one after another on one transaction.
- **Minimal dependencies.** The Microsoft.Extensions DI, Options and Logging abstractions, plus the provider's ADO.NET driver. JSON uses the in-box `System.Text.Json`.
