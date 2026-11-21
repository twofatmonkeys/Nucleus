# Querying

Reading entities through LINQ, loading by options, and reading rows of values instead of entities.

## Queries

`Get<T>()` on the manager is a LINQ query over an entity type:

<!-- sample: queries -->
```csharp
var monkeys = data.Get<Monkey>();

var smug = await monkeys.Where(m => m.Mood == Mood.Smug && m.WeightKg > 10).ToListAsync(ct);
var gerald = await monkeys.FirstOrDefaultAsync(m => m.Name == "Gerald", ct);
var anyHangry = await monkeys.AnyAsync(m => m.Mood == Mood.Hangry, ct);
var heavy = await monkeys.CountAsync(m => m.WeightKg > 20, ct);

// One page and the unpaged total, in a single round trip.
var page = await monkeys.Where(m => m.WeightKg > 10).ToPagedResultAsync(skip: 0, take: 20, ct);
Console.WriteLine($"{page.Count} of {page.TotalCount}, more to come: {page.HasMore}");

// Only Where translates. Order and project in memory, or use a row read.
var names = smug.OrderBy(m => m.Name).Select(m => m.Name).ToList();
```

## Loading with options

The manager's loads take a `LoadOptions`: child collections to leave out, lazy collections and references to bring in, and whether soft-deleted rows are seen.

<!-- sample: load-options -->
```csharp
var options = new LoadOptions
{
    Include = [nameof(Resident.Naps), nameof(Resident.Keeper)],   // a lazy collection and a reference, in the same round trip
    IncludeDeleted = false,                                       // true also returns soft-deleted rows
};
var resident = await data.LoadByIDAsync<Resident>(residentId, options, ct);

var withoutLines = new LoadOptions { Exclude = [nameof(BananaOrder.Lines)] };   // a child collection left unloaded
var orders = await data.LoadAsync<BananaOrder>(o => o.Status == OrderStatus.Placed, withoutLines, ct);
var page = await data.LoadPagedAsync<BananaOrder>(skip: 0, take: 20, o => o.Status == OrderStatus.Placed, withoutLines, ct);

// An excluded collection throws when read, until it is loaded.
var firstOrder = orders.First();
await firstOrder.Lines.LoadAsync(ct);
```

## Async query operators

`ObjectDataAdaptor<T>` is a LINQ provider that translates `Where`: every `Where` in a chain applies, combined with AND, and any other operator before a `Where` (`OrderBy`, `Select` and so on) throws `NotSupportedException` rather than being ignored. To choose columns, order, page or group in the database, use a [row read](#row-reads); otherwise order, page and project in memory, or use `ToPagedResultAsync`. `Where` and the async operators are declared on the adaptor itself, because the adaptor is both an `IQueryable<T>` and an `IAsyncEnumerable<T>`, and from .NET 10 `System.Linq.AsyncEnumerable` declares the same names: called straight on `Get<T>()`, each would be ambiguous and not compile. The async operators are `ToListAsync`, `ToArrayAsync`, `FirstAsync`, `FirstOrDefaultAsync`, `SingleAsync`, `SingleOrDefaultAsync`, `AnyAsync`, `CountAsync` and `LongCountAsync`, each with an optional predicate (except the list, array and long count) and cancellation token; the same operators after a `Where` come from `TFM.Nucleus.Data`'s extensions on `IQueryable<T>`, plus `ToPagedResultAsync(skip, take, cancellationToken)` which returns rows and the total count in a single round trip. An enum column compares directly in a predicate, including a nullable one and an `IN` over a collection of enum values, and is bound as the underlying integer the column stores.

An entity reference (a child's `Parent`, a `[LinkedObject]`) is stored as the referenced entity's ID, and a predicate compares it by that ID. `x => x.Parent.ID == id`, `x => x.Parent == order`, `x => ids.Contains(x.Parent.ID)` and `x => orders.Contains(x.Parent)` all translate to the reference's column, in a load, a paged load, a [row read](#row-reads), a count or a `DeleteWhere`. Only the ID translates: `x => x.Parent.Name == "a"` would need a join, and throws `NotSupportedException` naming the term, as does any other member read through a column (`x.Name.Length`) and a comparison of two columns. A comparison may be written either way round: `3 < x.Quantity` is `x.Quantity > 3`.

## Row reads

`IObjectDataManager.Rows<T>(predicate)` reads rows of values rather than entities, over the rows the predicate matches (the same single `Where` a load translates; every row when it is null). Only what is asked for is read, so a long text column that is never selected is never transferred, and nothing is materialised as an entity.

<!-- sample: row-reads -->
```csharp
// The newest 100 visits, two columns each, and how many match in all: one round trip.
var page = await data.Rows<GiftShopVisit>(v => v.OccurredUtc >= since)
    .Select(v => v.OccurredUtc, v => v.Path)
    .OrderByDescending(v => v.OccurredUtc)
    .ToPageAsync(skip: 0, take: 100, ct);

// One row per kind, with figures worked out in the database.
var groups = await data.Rows<GiftShopVisit>(v => v.OccurredUtc >= since)
    .GroupBy(v => v.Kind)
    .Count("Visits")
    .CountDistinct(v => v.SessionId, "Sessions")
    .Max(v => v.OccurredUtc, "Last")
    .ToListAsync(ct);
foreach (var g in groups)
    Console.WriteLine($"{g.Get<VisitKind>("Kind")}: {g.Get<long>("Visits")} visits in {g.Get<long>("Sessions")} sessions");

var total = await data.Rows<GiftShopVisit>(v => v.Kind == VisitKind.Purchase).CountAsync(ct);
```

- **Columns:** `Select(...)` names the properties each row returns. `OrderBy`, `OrderByDescending`, `ThenBy` and `ThenByDescending` order the rows in the database.
- **Groups:** `GroupBy(...)` returns one row per distinct combination of the keys, each named by its property, with the measures asked for: `Count(alias)`, `CountDistinct(property, alias)`, `Min`, `Max` and `Sum`. Measures without `GroupBy` are one row over every match. A grouped read is not ordered or paged (order its groups in memory) and does not `Select` as well. A measure's alias is a plain identifier (letters, digits, underscores, starting with a letter or underscore) and must not repeat a key's name.
- **Running it:** `ToListAsync`, `StreamAsync` (one row at a time; each row can be kept), `ToPageAsync(skip, take)` (rows and the total count in one round trip; the read must be ordered and not grouped) and `CountAsync` (a real `COUNT` in the database, which the LINQ `CountAsync` is not: that one loads every row).
- **Values:** `ResultRow.Get<T>(name)` converts each value: a database null is the type's default, an integer becomes an enum, a number becomes the numeric type asked for. A count is a `long`; a minimum or maximum has the column's type (a date stays a `DateTimeOffset` on every engine); a sum is the number the engine returns. `row[name]` is the value as read.
- **Rules:** soft-deleted rows are left out as in any read. Child collections are never read. The rows cannot be saved: a partial entity would write nulls over the columns it never read. A predicate that matches nothing returns no rows without a round trip.

A covering [`[TableIndex]`](entities.md#class-level-behaviour) over the filter and the columns a row read needs lets the database answer it from the index alone.

## Streaming

<!-- sample: streaming -->
```csharp
// Rows are yielded as they arrive, so a large result is never held whole.
var heavy = 0;
await foreach (var weighIn in data.LoadStreamAsync<WeighIn>(w => w.WeightKg > 0, cancellationToken: ct))
{
    if (weighIn.IsHeavy)
        heavy++;
}
```
