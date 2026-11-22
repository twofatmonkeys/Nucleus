# Querying

There are three ways to read, and each suits a different need:

| To read | Use |
|---|---|
| Entities by a filter, a few at a time or all of them | [A query](#queries), `Get<T>()` |
| Entities with control over what comes with them | [A load with options](#loading-with-options) |
| Chosen columns, ordered, paged or grouped in the database | [A row read](#row-reads), `Rows<T>()` |

## Queries

`Get<T>()` on the manager is a LINQ query over an entity type. `Where` filters it in the database,
and an async operator runs it:

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

The manager's loads take a `LoadOptions`, which says what comes with the rows: child collections
to leave out, lazy collections and references to bring in, and whether soft-deleted rows are seen.

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

## What a query translates

`Where` is the only operator that is translated to the database.

- Every `Where` in a chain applies, combined with AND.
- Any other operator before a `Where` (`OrderBy`, `Select` and so on) throws
  `NotSupportedException` rather than being ignored.
- To choose columns, order, page or group in the database, use a [row read](#row-reads).
  Otherwise order, page and project in memory, or use `ToPagedResultAsync`.

The async operators run the query: `ToListAsync`, `ToArrayAsync`, `FirstAsync`,
`FirstOrDefaultAsync`, `SingleAsync`, `SingleOrDefaultAsync`, `AnyAsync`, `CountAsync` and
`LongCountAsync`. Each takes a cancellation token, and all but the list, the array and the long
count take an optional predicate. `ToPagedResultAsync(skip, take, cancellationToken)` returns one
page and the total count in a single round trip.

The ones that return one row or a figure read only what they need, in the database:
`FirstAsync` and `FirstOrDefaultAsync` read one row, and `SingleAsync` and
`SingleOrDefaultAsync` read two at most (enough to tell one from several), each with the
query's child collections, `Exclude`, `Include` and `IncludeDeleted`. `AnyAsync` reads one ID,
and `CountAsync` and `LongCountAsync` count; neither reads a child collection or builds an entity.
With no `OrderBy`, "first" is whichever matching row the database returns first.

`Where` and the async operators are declared on the query that `Get<T>()` returns, not only as
extension methods. That query is both an `IQueryable<T>` and an `IAsyncEnumerable<T>`, and from
.NET 10 `System.Linq.AsyncEnumerable` declares the same names, so without the members each call
straight on `Get<T>()` would be ambiguous and not compile. After a `Where` the same operators come
from the extensions in `TFM.Nucleus.Data`.

### What a predicate can say

An enum column compares directly, including a nullable one and an `IN` over a collection of enum
values. It is bound as the integer the column stores.

An entity reference (a child's `Parent`, a `[LinkedObject]`) is stored as the referenced entity's
ID, and a predicate compares it by that ID. These all translate to the reference's column, in a
load, a paged load, a [row read](#row-reads), a count or a `DeleteWhere`:

- `x => x.Parent.ID == id`
- `x => x.Parent == order`
- `x => ids.Contains(x.Parent.ID)`
- `x => orders.Contains(x.Parent)`

Only the ID translates. `x => x.Parent.Name == "a"` would need a join, and throws
`NotSupportedException` naming the term. So does any other member read through a column
(`x.Name.Length`), and a comparison of two columns. A comparison may be written either way round:
`3 < x.Quantity` is `x.Quantity > 3`.

## Row reads

`IObjectDataManager.Rows<T>(predicate)` reads rows of values rather than entities. The predicate is
the same single `Where` a load translates, and null means every row. Only what is asked for is
read: a long text column that is never selected is never transferred, and nothing is built as an
entity. Use it for a listing, a report or a count over a large table.

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

What each part does:

- **Columns:** `Select(...)` names the properties each row returns. `OrderBy`, `OrderByDescending`, `ThenBy` and `ThenByDescending` order the rows in the database.
- **Groups:** `GroupBy(...)` returns one row per distinct combination of the keys, each named by its property, with the measures asked for: `Count(alias)`, `CountDistinct(property, alias)`, `Min`, `Max` and `Sum`. Measures without `GroupBy` are one row over every match. A grouped read is not ordered or paged (order its groups in memory) and does not `Select` as well. A measure's alias is a plain identifier (letters, digits, underscores, starting with a letter or underscore) and must not repeat a key's name.
- **Running it:** `ToListAsync`, `StreamAsync` (one row at a time; each row can be kept), `ToPageAsync(skip, take)` (rows and the total count in one round trip; the read must be ordered and not grouped) and `CountAsync` (a `COUNT` in the database, as the query's `CountAsync` is too).
- **Values:** `ResultRow.Get<T>(name)` converts each value: a database null is the type's default, an integer becomes an enum, a number becomes the numeric type asked for. A count is a `long`; a minimum or maximum has the column's type (a date stays a `DateTimeOffset` on every engine); a sum is the number the engine returns. `row[name]` is the value as read.
- **Rules:** soft-deleted rows are left out as in any read, unless the read asks for them with `IncludeDeleted()`. Child collections are never read. The rows cannot be saved: a partial entity would write nulls over the columns it never read. A predicate that matches nothing returns no rows without a round trip.

A covering [`[TableIndex]`](entities.md#indexes-over-several-columns) over the filter and the columns a row read needs lets the database answer it from the index alone.

## Streaming

A stream yields each entity as its row arrives, so a large result is never held in memory whole.
The child graph is fetched first, and each entity arrives with its children attached:

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
