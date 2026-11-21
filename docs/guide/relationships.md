# Relationships

Three ways one entity holds others: rows it owns (a child collection), a reference to another root
(a linked object), and roots that belong to it (a linked collection).

## Child collections

Child collections are rows a parent owns: they are created with it, change with it and go with it
(a banana order's lines, a resident's naps). Model them as a
`NucleusChildCollection<TChild, TParent>` property on the parent, read through
`GetChildCollection`, with the child deriving from `CoreChildObject<TChild, TParent>`. A child can
own a collection of its own, so owned rows nest to any depth. A parent holds at most one collection
of each child type; two fail when the schema is built.

<!-- sample: entity-order -->
```csharp
[Deletable]
public class BananaOrder : CoreObject<BananaOrder>
{
    [DatabaseColumn(Indexed = true)]
    public DateTimeOffset PlacedUtc { get => GetValue<DateTimeOffset>(); set => SetValue(value); }

    [DatabaseColumn]
    public OrderStatus Status { get => GetValue<OrderStatus>(); set => SetValue(value); }

    // The order owns its lines: they are saved, loaded and deleted with it.
    public NucleusChildCollection<BananaOrderLine, BananaOrder> Lines => GetChildCollection<BananaOrderLine, BananaOrder>();
}

public class BananaOrderLine : CoreChildObject<BananaOrderLine, BananaOrder>
{
    [DatabaseColumn]
    public string? Variety { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn]
    public int Quantity { get => GetValue<int>(); set => SetValue(value); }

    // A grandchild collection: owned rows two levels down.
    public NucleusChildCollection<LineSubstitution, BananaOrderLine> Substitutions => GetChildCollection<LineSubstitution, BananaOrderLine>();

    public override void Validate()
    {
        if (Quantity <= 0)
            throw new InvalidOperationException("An order line needs at least one banana.");
        base.Validate();                               // marks the line valid; always call it
    }
}

public class LineSubstitution : CoreChildObject<LineSubstitution, BananaOrderLine>
{
    [DatabaseColumn]
    public string? Variety { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn]
    public string? Reason { get => GetValue<string>(); set => SetValue(value); }
}

public enum OrderStatus { Placed, Ripening, Delivered, Eaten }
```

Add children to the collection and save the root. The graph follows:

<!-- sample: children -->
```csharp
var order = new BananaOrder { PlacedUtc = DateTimeOffset.UtcNow, Status = OrderStatus.Placed };
var cavendish = new BananaOrderLine { Variety = "Cavendish", Quantity = 400 };
order.Lines.Add(cavendish);
order.Lines.Add(new BananaOrderLine { Variety = "Lady Finger", Quantity = 60 });
cavendish.Substitutions.Add(new LineSubstitution { Variety = "Plantain", Reason = "supplier ran out" });

// Save the root. The lines and their substitutions are validated and written with it, in one transaction.
await data.SaveAsync(order, ct);

// A load brings the whole graph back.
var loaded = await data.LoadByIDAsync<BananaOrder>(order.ID, ct);
var line = loaded!.Lines.First(l => l.Variety == "Cavendish");
line.Quantity = 360;                           // a changed child is written by its parent's save
await data.SaveAsync(loaded, ct);

// Deleting the root deletes its lines and their substitutions, loaded or not.
// await data.DeleteAsync(loaded, ct);
```

Nucleus then does the bookkeeping:

- **Loading.** Every load of the parent brings its children in the same round trip, bound to the
  parent and unchanged: `LoadWhere`, `LoadByID`, paged loads and the async LINQ operators. When a
  load has a `Take` or a page, the child query selects the children of the returned parents only.
  Lazy collections are the exception (below).
- **Saving.** Save the parent, never a child (a child's own `Save` throws
  `UnableToSaveFromChildObject`). New children are inserted, changed ones updated and removed ones
  deleted, in one transaction. The deletes run first, so a replacement with the same unique key
  does not collide with the row it replaces. Large batches go to the bulk engine on their own. A
  save that fails rolls back and keeps the removals, so saving again deletes them.
- **Validating.** The parent's `Validate()` runs `Validate()` on every new or changed child, at
  every depth, before anything is written, so a child's rules hold without the parent restating
  them. Unchanged loaded children are not checked again. Every save path validates: an entity's
  own `Save`, and the manager's and a context's `Save` and `SaveAsync`, for one object or a
  batch. Do not call `Validate()` before saving; the save does.
- **Indexing.** A child's `Parent` column always has an index (`NCI_<table>_Parent`), since every
  load of a collection and every cascaded delete filters on it. Declare no `[TableIndex]` on
  `Parent` alone, which would duplicate it; one that leads with `Parent` and adds a column the
  child is read by, such as `[TableIndex("ByParent", nameof(Parent), nameof(Sequence))]`, still
  helps an ordered read.
- **Deleting.** Declared, and cascaded at every depth (below).
- **Ownership.** A child belongs to one parent: adding it to a second throws
  `ParentObjectAlreadyBound`. A child removed and added back to the same collection before the
  save is kept.
- **Change notification.** A change to a child, or to the collection, raises `PropertyChanged` on
  the parent for the collection property's name.

Do not keep owned rows as a separate entity with a hand-written parent ID column, a private list,
or attach, remove and acknowledge methods. That rebuilds what the collection already does, and gets
the transaction, the cascade or the cache eviction wrong. Use a separate entity with a plain ID
reference only for rows that outlive the parent or that more than one parent shares.

### Deleting

Nothing is deleted unless a declaration says so. `[Deletable]` can go in three places, on any or
all of them:

A resident's naps are declared deletable on the collection (the entity is shown under [Lazy collections](#lazy-collections)), so removing one and saving deletes its row:

<!-- sample: remove-child -->
```csharp
// Naps is declared [Deletable], so a stored nap removed from it is deleted by the save.
var nap = resident.Naps.First();
resident.Naps.Remove(nap);
await data.SaveAsync(resident, ct);
```

| Declared on | What it enables |
|---|---|
| The entity type | Its rows can be deleted directly (`Delete()`, `DeleteWhere`), and everything it owns goes with them. On a child type, removing one from its collection deletes it too. |
| A child collection property | Removing a stored child from that collection and saving the parent deletes the child, with everything it owns. |
| The parent (or any owner above) | Its own rows can be deleted, and every child type below it, at any depth, is deleted with it or when removed from its collection. |

A child type made deletable only by its collection or an owner is deleted through its owner and
never on its own: `Delete()` on it throws. `[Deletable(false)]` on a child type forbids deleting its
rows by any route, and wins: when a declaration above it would delete them, the schema fails to
build, naming both declarations, rather than orphaning rows at delete time. `[Deletable(false)]` on
a parent stops only the parent's own deletion; `[Deletable]` on its collection still makes removals
delete. `[Deletable]` on a property that is not a child collection, and `[Deletable(false)]` on a
collection, fail when the schema is built.

**Hard or soft.** A type's own declaration decides. A child type without one takes the behaviour
of the nearest declaration above it that enables deleting it (the collection's when it names one,
then the parent's, and so on up), so the lines of a soft-deleted order can be restored with it. A
child type made soft this way gets the soft-delete columns, added in place like any new column.

**Removing without a declaration.** Removing a stored child from a collection whose children are
not deletable throws `InvalidOperationException` from `Remove`, `RemoveAt`, `Clear` or the indexer,
before anything changes, and says where to declare `[Deletable]`. `Clear` checks every child first.
A child that was never saved can always be removed; it is simply dropped.

**The cascade.** Deleting a parent, by object or by predicate, deletes its child collection rows at
every depth in one batch: deepest first, each level selecting by its parent's condition, and each
type keeping its own hard or soft behaviour. It does not depend on which children were loaded.

**Deleting by predicate.** `DeleteWhere` and `DeleteWhereAsync` exist on `CoreObject` (static), the
object data manager (each in a transaction of its own, with cache eviction), and `IDataContext`
(in a caller-owned context and transaction, without cache eviction). No row is loaded. The predicate
uses the same translation as `LoadWhere`, with two guards: a predicate that matches every row, such
as `x => true`, throws `ArgumentException`, and one that matches none, such as `x => false`,
returns 0 without a round trip. Soft-deletable types are flagged, and only rows not already
deleted count.

### Leaving a collection out

`Exclude(x => x.Items)` on a query, or the `exclude` selectors on `LoadByID` and `LoadWhere`, drop
that collection's query. Its rows are never queried or transferred, and the collection comes back
with `IsLoaded` false. Reading it (enumerating, `Count`, the indexer, `Contains`, `Remove`, `Clear`)
throws `InvalidOperationException` rather than answering as if the parent had no children. Adding
still works, and added children save with the parent. `collection.LoadAsync()` fetches the stored
children by the parent's ID and merges them by ID with any added since;
`IObjectDataManager.LoadChildCollectionAsync(collection)` does the same without the ambient
runtime. An excluded load never reads from or populates the `[Cacheable]` cache. Serialising a
parent whose collection is excluded throws for the same reason.

### Lazy collections

`[RetrieveOnDemand]` on a child collection makes it lazy, for a collection that can grow large.
Every load of the parent leaves its rows out, and the collection loads them the first time it is
read (enumerating, `Count`, the indexer, `Contains`, `IndexOf`, `Remove`, `Clear`, `Insert`), once,
however many threads read it together. The read goes through the manager or data context that
loaded the parent, on a connection of its own and outside any transaction the caller holds. Adding
to an unread lazy collection does not load it, and the added children save with the parent.

<!-- sample: entity-resident -->
```csharp
// A monkey in residence: it has a keeper, belongs to a troop and logs its naps.
[Deletable]
public class Resident : CoreObject<Resident>
{
    [DatabaseColumn(Indexed = true)]
    public string? Name { get => GetValue<string>(); set => SetValue(value); }

    // A reference to another root. It is stored as the keeper's ID; [DatabaseColumn] gives it a column.
    [LinkedObject]
    [DatabaseColumn]
    public Keeper? Keeper { get => GetValue<Keeper>(); set => SetValue(value); }

    // The key a troop's linked collection is kept by. It must be indexed.
    [DatabaseColumn(Indexed = true)]
    public Guid TroopId { get => GetValue<Guid>(); set => SetValue(value); }

    // Can grow large, so it is lazy: left out of loads and read on first use.
    // [Deletable] here lets a nap removed from the collection be deleted by the save.
    [RetrieveOnDemand]
    [Deletable]
    public NucleusChildCollection<NapLog, Resident> Naps => GetChildCollection<NapLog, Resident>();
}

public class NapLog : CoreChildObject<NapLog, Resident>
{
    [DatabaseColumn(Indexed = true)]
    public DateTimeOffset StartedUtc { get => GetValue<DateTimeOffset>(); set => SetValue(value); }

    [DatabaseColumn]
    public int Minutes { get => GetValue<int>(); set => SetValue(value); }

    [DatabaseColumn]
    public string? Location { get => GetValue<string>(); set => SetValue(value); }
}
```

Reading a lazy collection is a round trip that the call site does not show, so a loop that reads it
for each parent of a listing runs a query per parent. A listing that needs the children includes
them, and they come for every returned parent in the same round trip. The example also saves a
linked collection and includes a reference, both described further down:

<!-- sample: relationships -->
```csharp
var mavis = new Keeper { Name = "Mavis", Email = "mavis@portlyprimate.example" };
var loungers = new Troop { Name = "The Loungers", Motto = "Why stand" };
await data.SaveAsync(mavis, ct);
await data.SaveAsync(loungers, ct);

var brenda = new Resident { Name = "Brenda", Keeper = mavis };
brenda.Naps.Add(new NapLog { StartedUtc = DateTimeOffset.UtcNow, Minutes = 95, Location = "the good hammock" });

// Add a member to a linked collection and save the one: the member is saved with it, its key set.
loungers.Residents.Add(brenda);
await data.SaveAsync(loungers, ct);

// A listing that reads a lazy collection, a reference or a linked collection on every row includes it.
var residents = await data.Get<Resident>()
    .Include(r => r.Naps)
    .Include(r => r.Keeper)
    .Where(r => r.TroopId == loungers.ID)
    .ToListAsync(ct);
var troops = await data.Get<Troop>().Include(t => t.Residents).Where(t => t.Name == "The Loungers").ToListAsync(ct);

// Without Include, the first read of each is a round trip of its own.
var loaded = await data.LoadByIDAsync<Resident>(brenda.ID, ct);
var keeper = loaded!.Keeper;                   // loads the keeper
var naps = loaded.Naps.Count();                // loads the naps
```

`Include` and `Exclude` apply to every read through the adaptor, `FindAsync` and
`ToPagedResultAsync` included. `Exclude` wins over lazy: an excluded lazy collection throws
when read. A lazy collection always loads all of its rows; to read part of one, query the child
type by its parent, which reads only the rows asked for and leaves the parent's collection unloaded:

<!-- sample: child-query -->
```csharp
// Part of a large collection: query the child type by its parent. The parent's collection stays unloaded.
var longNaps = await data.Get<NapLog>()
    .Where(n => n.Parent.ID == resident.ID && n.Minutes > 60)
    .ToPagedResultAsync(skip: 0, take: 50, ct);
var total = await data.Rows<NapLog>(n => n.Parent == resident).CountAsync(ct);
```

Serialising a parent reads its lazy collections. A `[Cacheable]` type cannot have one, because its cached instances are shared and
loading the collection would change it for every caller; the schema fails to build.

## Linked objects

A `[LinkedObject]` property stores the referenced entity's `ID`, and a load reads only that ID.
The first read of the property loads the entity, once, however many threads read it together, and
keeps it, so the next read runs no query and the object is not made dirty. The read goes through
the manager or data context that loaded the object, as a lazy collection's does: a manager loads it
by ID from the provider that stores its type (from the cache for a `[Cacheable]` type), and a data
context from its own provider, on a connection of its own. A row that no longer exists reads as
`null`.

`Resident.Keeper` above is one. The property carries `[DatabaseColumn]` as well as `[LinkedObject]`:
without it the reference has no column and is not stored. The keeper is an ordinary root:

<!-- sample: entity-keeper -->
```csharp
[Deletable]
public class Keeper : CoreObject<Keeper>
{
    [DatabaseColumn]
    public string? Name { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(Unique = true)]
    public string? Email { get => GetValue<string>(); set => SetValue(value); }
}
```

Like a lazy collection, that read is a round trip the call site does not show, so a listing that
reads the reference on every row includes it. The referenced entities come in the same round trip,
selected by the reference column of exactly the rows returned (page and `TOP` included), and rows
that refer to the same entity share one instance:

`Include` of a reference works on every read the adaptor runs, on the manager's loads through
`LoadOptions.Include` by property name, and with child collection includes in the same read. The
referenced type must be stored by the same provider as the rows; including one stored elsewhere
throws rather than leaving every row to load its own. A referenced entity included this way is
loaded with its own child collections and runs its load hook.

A `[LinkedObject]` can name a child type. The child loads like any referenced entity, and its `Parent` holds the parent's ID
until it is first read, which loads the parent the same way. The same goes for a child loaded on
its own by `LoadByID` or a query of its type. The parent loads as `LoadByID` loads it, so its child
collection holds instances of its own, not the one that read it. A child is saved through its
parent, so `forceSave` on a link to one fails the schema; removing the child from its collection
deletes it, and the link then reads `null`.

## Linked collections

A `NucleusLinkedCollection<TMany, TOne>` is the many side of a one-to-many relationship between two
root types, held on the one: a troop's residents, where each resident is a root of its own,
loaded, saved and moved between troops without its troop. Rows a parent owns, with no life of their
own, are a [child collection](#child-collections) instead, and a reference from one entity to
another is a [linked object](#linked-objects).

The many type declares the key, the property holding the one's ID, and the collection names it:

<!-- sample: entity-troop -->
```csharp
// Residents are roots of their own, loaded and saved without their troop, so the troop holds
// them in a linked collection, kept by the key the resident declares.
[Deletable]
public class Troop : CoreObject<Troop>
{
    [DatabaseColumn]
    public string? Name { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn]
    public string? Motto { get => GetValue<string>(); set => SetValue(value); }

    [LinkedBy(nameof(Resident.TroopId))]
    public NucleusLinkedCollection<Resident, Troop> Residents => GetLinkedCollection<Resident, Troop>();
}
```

The key is a stored, indexed `Guid`, `Guid?`, or a `[LinkedObject]` typed as the one. With a
`[LinkedObject]` key, loaded members hold the one itself, so reading it runs no query. Because the
many type declares its key, its table, its queries (`r => r.TroopId == id`) and its rules do not
depend on the one. A many type can be the many side of several ones, with a key for each, and a one
can hold several collections, two of them over one many type when each has its own key.

**Loading.** Reading the property loads nothing. The whole collection loads, in one query, the first
time a member is read (enumerating, `Count`, the indexer, `Contains`, `IndexOf`, `Remove`, `Clear`,
`Insert`, `CopyTo`), once however many threads read it, on the synchronous path and through the
manager or data context that loaded the one. `await collection.LoadAsync()` loads it without
blocking a thread. Adding does not load it, and the collection of a new one starts loaded and
empty. A listing includes the collection, which loads every row's members in the same round trip,
selected by the key of exactly the rows returned (page and `TOP` included):

A linked collection is never part of a read unless it is included; there is no `[RetrieveOnDemand]`
and no `Exclude` for one.

**Saving.** Adding a member sets its key to the one. Saving the one writes the new and changed
members it holds, after the one, in the same transaction, and runs each one's `Validate()` first;
saving a member on its own still works as for any root. Removing a stored member deletes its row
with the one's save when the many type is `[Deletable]`, and throws at the call otherwise (point
its key at another one and save it instead). Removing a member that was never saved just drops it.

**Deleting the one** is refused while it has members, on every delete path: the delete throws
naming the collection and deletes nothing. Delete the members, or point them elsewhere, first.

**Refused when the schema is built:** a collection without `[LinkedBy]`; a key the many type does
not have, that is not a stored column, that is not indexed, or that is not a `Guid`, `Guid?` or
`[LinkedObject]` of the one; two collections on one type with the same key; a many type that is a
child type; a one that is a child type or `[Cacheable]`; and a many type stored by a different
provider from the one.
