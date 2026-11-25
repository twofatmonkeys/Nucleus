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

The same context deletes too, so a save, a delete and a delete by predicate are one transaction:

<!-- sample: save-and-delete -->
```csharp
// A save, a delete and a delete by predicate in one transaction: all of it is committed, or none of it.
await using var context = await data.CreateContextAsync<WeighIn>(ContextOptions.Transactional, ct);
await context.SaveAsync(new WeighIn { MonkeyName = "Gerald", WeightKg = 15.1m, Notes = "corrected" }, ct);
await context.DeleteAsync(superseded, ct);
var removed = await context.DeleteWhereAsync<WeighIn>(w => w.MonkeyName == "Nobody", ct);   // the rows it deleted
await context.CommitAsync(ct);
```

Ask for a transaction (`ContextOptions.Transactional`): a context opened without one runs each
statement on its own, and its `CommitAsync` does nothing.

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
- **For an index Nucleus does not manage,** such as a primary key, `IndexName` is the engine's own
  name for it, the other names are null and the lists empty.

It is a [`DatabaseException`](#failures) of kind `UniqueViolation`, never transient, with the
provider's own exception as its inner exception. The manager's `Save` and `SaveAsync` throw it as
it is, not wrapped, so the filters below work on them as they do on a context's save.

A broken foreign key is a `ForeignKeyViolationException`, of kind `ForeignKeyViolation`, never
transient, thrown as it is in the same way (see [Foreign keys](schema.md#foreign-keys)). It names the
reference whose key broke, which is the side that points: `EntityType`, `PropertyName` and
`ColumnName` are the entity and the property holding the reference, `ReferencedType` the entity it
points at, and `Is<T>(name)` tests for it. On a refused delete they name the entity still pointing
at the row. SQLite does not say which key broke, so there the names are null and `Is<T>` is false.

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
engine. Two cases differ. An open that ran out its connect timeout on the asynchronous path has a
`TimeoutException` as its inner exception, with the driver's inside that. And on SQL Server a write
that reads a value back from a table with an enabled trigger is refused by the engine (error 334)
and reported as the provider's own exception, a `DataProviderException`, whose message says what
to change.

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

Opening a connection is always retried when the failure is transient, up to the provider's
`ConnectAttempts` (3 by default), all within the connection's connect timeout. That holds for a
context's connection, a change subscription's and the connections the schema check reads the
catalog on.

Everything else is retried only when you switch it on. With the provider's `RetryAttempts` above
1, an operation the manager runs on a context of its own (a save, a delete by object or by
predicate, a load, a paged load, a streamed load until its first row, a row read, and the read of a
lazy collection, a linked collection or a linked object) that
fails with `IsTransient` true is attempted again from the start, on a new connection, 200 ms, then
400 ms, then 800 ms later. A save's rules and `PreSave` run once, before the first attempt.

<!-- sample: retry-policy -->
```csharp
services.AddNucleusSqliteDataProvider(options =>
{
    options.Database = @"C:\ProgramData\PortlyPrimate\sanctuary.db";
    // A manager save, delete or load that fails transiently, having applied nothing, is
    // attempted again on a new connection: up to 3 attempts in all, 200 ms then 400 ms apart.
    options.RetryAttempts = 3;
});
```

The policy never repeats what may already have happened: a failure that may have been applied is
not transient, so a commit whose outcome is unknown is reported, not retried. Nor does it repeat a
stream that has handed out a row, or anything on a caller-owned context, whose transaction is the
caller's. Each retry is logged as a warning. When the last attempt fails transiently, the failure
is reported with `IsTransient` false and `Attempts` set to the attempts made, so a caller's own
filter does not retry it on top of the policy. With the policy on, do not wrap manager calls in a
retry of your own.

With it off, and always around a caller-owned context, the retry is the caller's, of the whole
unit of work, when the failure says it is safe:

<!-- sample: transient-retry -->
```csharp
// The policy never repeats work on a caller-owned context: the unit of work is the
// caller's to repeat, from the start, when the failure says nothing was applied.
for (var attempt = 1; ; attempt++)
{
    try
    {
        await using var context = await data.CreateContextAsync<Monkey>(ContextOptions.Transactional, ct);
        await context.SaveAsync(monkey, ct);
        await context.SaveAsync(weighIn, ct);
        await context.CommitAsync(ct);
        return;
    }
    catch (DatabaseException ex) when (ex.IsTransient && attempt < 3)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);   // a deadlock, a dropped connection, a timeout
    }
}
```

Each attempt opens its own connection, with its own `ConnectAttempts`, so three attempts at the
default make at most nine opens. A save that failed leaves its objects unsaved, with their changes
and removals pending, and with none of the values the server returned to the failed write, so
saving them again is the retry. After `CommitOutcomeUnknown`, do not save again blindly: load the
rows and see whether the commit took effect. An insert that did commit fails a second time with a
`UniqueViolationException` on its key.

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
the wait for another connection's write lock, which stays `BusyTimeoutSeconds` for every statement,
schema statements included, so `StatementTimeoutSeconds` and `SchemaCommandTimeoutSeconds` are
ignored there.

### Other exceptions

Every exception Nucleus defines derives from `NucleusException` (`TFM.Nucleus`), so
`catch (NucleusException)` catches the family: `DatabaseException` and `UniqueViolationException`,
`DataProviderException` and each provider's own, `DataContextException` (a full connection budget is
its kind `PoolExhausted`), `InvalidQueryException`, `CoreObjectException` and the schema exceptions.

Nucleus also throws the framework's own exceptions where they are the right ones
(`ArgumentException`, `NotSupportedException`, `InvalidOperationException`,
`OperationCanceledException`, and `ReadOnlyException` for a save of a read-only object). Those, and a rule your `Validate()` throws, reach the caller as they
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
- **Minimal dependencies.** The Microsoft.Extensions DI, Options and Logging abstractions, plus the provider's ADO.NET driver (the SQL Server package also pins a patched `System.Security.Cryptography.Pkcs`, which its driver brings in). JSON uses the in-box `System.Text.Json`.
