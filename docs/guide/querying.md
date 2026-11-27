# Querying

There are three ways to read, and each suits a different need:

| To read | Use |
|---|---|
| Entities by a filter, ordered and paged or all of them | [A query](#queries), `Get<T>()` |
| Entities with control over what comes with them | [A load with options](#loading-with-options) |
| Chosen columns, ordered, paged or grouped in the database | [A row read](#row-reads), `Rows<T>()` |

## Queries

`Get<T>()` on the manager is a LINQ query over an entity type. `Where` filters it, `OrderBy` and
`ThenBy` order it and `Skip` and `Take` cut it, all in the database, and an async operator runs it:

<!-- sample: queries -->
```csharp
var monkeys = data.Get<Monkey>();

var smug = await monkeys.Where(m => m.Mood == Mood.Smug && m.WeightKg > 10).ToListAsync(ct);
var gerald = await monkeys.FirstOrDefaultAsync(m => m.Name == "Gerald", ct);
var anyHangry = await monkeys.AnyAsync(m => m.Mood == Mood.Hangry, ct);
var heavy = await monkeys.CountAsync(m => m.WeightKg > 20, ct);

// Ordered and cut in the database.
var heaviest = await monkeys.OrderByDescending(m => m.WeightKg).ThenBy(m => m.Name).Take(5).ToListAsync(ct);

// One page and the unpaged total, in a single round trip. With no OrderBy, a page
// follows the order the rows were written in.
var page = await monkeys.Where(m => m.WeightKg > 10).OrderBy(m => m.Name).ToPagedResultAsync(skip: 0, take: 20, ct);
Console.WriteLine($"{page.Count} of {page.TotalCount}, more to come: {page.HasMore}");

// Select is not translated: project in memory, or use a row read.
var names = heaviest.Select(m => m.Name).ToList();
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

`Where`, `OrderBy`, `OrderByDescending`, `ThenBy`, `ThenByDescending`, `Skip` and `Take` are
translated to the database. Nothing is dropped: an operator is either translated or refused.

- Every `Where` in a chain applies, combined with AND, wherever it sits before a `Skip` or `Take`.
- An order key is a stored property of the entity (`m => m.Name`). Anything else, such as
  `m => m.Name.Length`, throws `NotSupportedException`.
- A later `OrderBy` sorts first, and the earlier keys still order the rows it ties, as LINQ's
  sorts do.
- `Skip` and `Take` combine as in LINQ: `Skip(20).Take(10)` is the third page of ten.
  `Take(0)` returns nothing without a round trip.
- A `Where` or an order after a `Skip` or `Take` would act on the page rather than the rows, so it
  throws `NotSupportedException`. Put it first. The predicate an async operator takes is a `Where`
  too, so `FirstAsync(m => ...)` on a query that skips or takes throws the same way.
- Any other operator (`Select`, `Distinct`, `GroupBy` and so on) throws `NotSupportedException`.
  Project in memory, or choose columns and group in the database with a [row read](#row-reads).

A skipped or limited query with no `OrderBy` is ordered by the order the rows were written in, so
the first page and the next never share or miss a row. An `OrderBy` on a skipped or limited query
is followed by the same order to break its ties. NULL sorts below every value on every engine:
first in ascending order, last in descending. Text sorts by each engine's own rules, which differ
(see [known limits](known-limits.md)).

A page costs the database in proportion to how far it skips, on every engine: to return rows
50,001 to 50,050 it walks the 50,000 before them. A listing that people page deep into, such as a
feed, pages by key instead: filter on the last key the previous page showed
(`Where(e => e.OccurredUtc < last)`), order by that key and `Take` a page, with no `Skip`. With an
index on the key, every page then costs the same.

The async operators run the query: `ToListAsync`, `ToArrayAsync`, `FirstAsync`,
`FirstOrDefaultAsync`, `SingleAsync`, `SingleOrDefaultAsync`, `AnyAsync`, `CountAsync` and
`LongCountAsync`. Each takes a cancellation token, and all but the list, the array and the long
count take an optional predicate. `ToPagedResultAsync(skip, take, cancellationToken)` returns one
page of the query's order and the total count in a single round trip; the query itself must not
`Skip` or `Take`.

The ones that return one row or a figure read only what they need, in the database:
`FirstAsync` and `FirstOrDefaultAsync` read one row, and `SingleAsync` and
`SingleOrDefaultAsync` read two at most (enough to tell one from several), each with the
query's child collections, `Exclude`, `Include` and `IncludeDeleted`. `AnyAsync` reads one ID,
and `CountAsync` and `LongCountAsync` count; neither reads a child collection or builds an entity.
With no `OrderBy`, "first" is the first matching row written. Each keeps the query's order, `Skip`
and `Take`: `CountAsync` of `Skip(20).Take(10)` counts at most ten rows.

`Where`, `OrderBy`, `OrderByDescending`, `Skip`, `Take` and the async operators are declared on the
query that `Get<T>()` returns, not only as extension methods. That query is both an `IQueryable<T>`
and an `IAsyncEnumerable<T>`, and from .NET 10 `System.Linq.AsyncEnumerable` declares the same
names, so without the members each call straight on `Get<T>()` would be ambiguous and not compile.
After the first operator the rest come from `Queryable` and the extensions in `TFM.Nucleus.Data`.

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

// The 25 most visited paths: ordered by a measure and cut in the database.
var busiest = await data.Rows<GiftShopVisit>(v => v.OccurredUtc >= since)
    .GroupBy(v => v.Path)
    .Count("Visits")
    .OrderByDescending("Visits")
    .Take(25)
    .ToListAsync(ct);

var total = await data.Rows<GiftShopVisit>(v => v.Kind == VisitKind.Purchase).CountAsync(ct);
```

What each part does:

- **Columns:** `Select(...)` names the properties each row returns. `OrderBy`, `OrderByDescending`, `ThenBy` and `ThenByDescending` order the rows in the database, and `Take(n)` reads only the first n of that order (the order the rows were written in when the read gives none).
- **Groups:** `GroupBy(...)` returns one row per distinct combination of the keys, each named by its property, with the measures asked for: `Count(alias)`, `CountDistinct(property, alias)`, `Min`, `Max` and `Sum`. Measures without `GroupBy` are one row over every match. A grouped read does not `Select` as well.
- **Ordered groups:** a grouped read is ordered by a group key (`OrderBy(v => v.Kind)`) or by a measure, named by its alias (`OrderByDescending("Visits")`), and `Take(n)` keeps the first n groups of that order, cut in the database. A limited grouped read needs an order, and groups that tie on it are then ordered by their keys, so the same groups come back every time. A grouped read is not paged: there is no `ToPageAsync` for it and no skip. A measure's alias is a plain identifier (letters, digits, underscores, starting with a letter or underscore) and must not repeat a key's name.
- **Running it:** `ToListAsync`, `StreamAsync` (one row at a time; each row can be kept), `ToPageAsync(skip, take)` (rows and the total count in one round trip; not for a grouped read or one limited by `Take`; a read with no order is paged in the order the rows were written in) and `CountAsync` (a `COUNT` in the database, as the query's `CountAsync` is too).
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
