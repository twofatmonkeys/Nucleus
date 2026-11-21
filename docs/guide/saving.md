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

The message is the one an ordinary storage failure carries, and the provider's own exception is
the inner exception, so code that caught `Exception` before still does. The manager's `Save` and
`SaveAsync` throw it as it is, not wrapped, so the filters below work on them as they do on a
context's save.

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

## Exceptions

Every exception Nucleus defines derives from `NucleusException` (`TFM.Nucleus`), so
`catch (NucleusException)` catches the family: `UniqueViolationException`, `DataProviderException`
and each provider's own, `DataContextException`, `InvalidQueryException`, `CoreObjectException` and
the schema exceptions.

Nucleus also throws the framework's own exceptions where they are the right ones
(`ArgumentException`, `NotSupportedException`, `OperationCanceledException`). Those are not wrapped.

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
