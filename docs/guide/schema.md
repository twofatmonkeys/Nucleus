# Tables and schema

Nucleus creates and updates tables from the entity classes. This page covers how a table gets its
name, how to change a name without losing the rows, the foreign keys it keeps, and what does and
does not get a table.
[Getting started](getting-started.md#2-what-initialising-does) describes what initialising does to
an existing table's columns.

## Table names

A table's name comes from the first of these that applies:

1. `[DatabaseTable]` on the type.
2. `[TableNaming]` on the type, or inherited from a base class.
3. The `TableNaming` option of the provider that stores it.

There are two policies:

- **`TableNamingPolicy.FullName`**, the default, gives every table the name it always had: `tbl_`
  and the type's full name with each `.` and `+` replaced by `_`. So
  `PortlyPrimate.Shop.GiftShopVisit` is `tbl_PortlyPrimate_Shop_GiftShopVisit`.
- **`TableNamingPolicy.ObjectName(levels)`** keeps only the last `levels` parts of the full name,
  a nested type's outer type counting as a part. `ObjectName(1)` names it `tbl_GiftShopVisit` and
  `ObjectName(2)` `tbl_Shop_GiftShopVisit`. More levels than the name has gives the full name.

A provider's policy is set where it is registered:

<!-- sample: register-rooted -->
```csharp
// Everything under ShopRoot<T> lives in the shop's database; everything else in the main one.
services.AddNucleusSqlServerDataProvider(options =>
{
    options.Server = "localhost";
    options.Database = "PortlyPrimate";
    options.Username = "sanctuary";
    options.Password = password;
});
services.AddNucleusSqlServerDataProvider(typeof(ShopRoot<>), options =>
{
    options.Server = "localhost";
    options.Database = "PortlyPrimateShop";
    options.Username = "sanctuary";
    options.Password = password;
    options.TableNaming = TableNamingPolicy.ObjectName(2);   // tables named by the last two parts of the type name
    options.RenameFullNameTables = true;                     // adopt each existing full-name table
});
```

A policy can also be declared on a base class, and a single table named outright, with the names
it had before:

<!-- sample: entity-naming -->
```csharp
// Every entity under this root is named by its class alone: tbl_GiftShopVisit, not
// tbl_PortlyPrimate_GiftShopVisit.
[TableNaming(TableNamingStyle.ObjectName, 1)]
public abstract class ShopRoot<T> : CoreObject<T> where T : CoreObject<T> { }

public enum VisitKind { Browse, Purchase, Refund }

[DatabaseTable("tbl_ShopVisits")]                      // this name, whatever the policy
[LegacyTableNames("tbl_GiftShopVisit")]                // and the name it had before: adopted, not recreated
[TableIndex("KindTime", nameof(Kind), nameof(OccurredUtc), Include = [nameof(Path)])]
[TableIndex("SessionPath", nameof(SessionId), nameof(Path), Unique = true)]
public class GiftShopVisit : ShopRoot<GiftShopVisit>
{
    [DatabaseColumn]
    public Guid SessionId { get => GetValue<Guid>(); set => SetValue(value); }

    [DatabaseColumn]
    public DateTimeOffset OccurredUtc { get => GetValue<DateTimeOffset>(); set => SetValue(value); }

    [DatabaseColumn(ColumnDataType.Unicode, 200)]
    public string? Path { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn]
    public VisitKind Kind { get => GetValue<VisitKind>(); set => SetValue(value); }
}
```

The rules for these attributes:

- **`[DatabaseTable]`** gives the whole table name, prefix included: letters, digits and
  underscores, starting with a letter or underscore, at most 128 characters. It is not inherited,
  because a root and the entities under it each have a table, and an inherited name would give
  them one between them.
- **`[TableNaming]`** is inherited, since a policy still gives each type a name of its own. A
  type's own attribute wins over its root's.
- **Index and constraint names** follow the table name (`NCI_<table>_<column>` and so on). On
  PostgreSQL a name over 63 bytes is shortened.
- **Clashes fail at provider initialisation,** naming the types: two types that end up with one
  table name, a `[LegacyTableNames]` entry that is another type's table, and an old name two types
  both claim.

## Changing a table's name

**Changing a name does not move the data by itself.** On a database that already has tables, a new
name makes the schema sync look for a table that is not there. By default it creates an empty one
and leaves the old table, rows and all, where it was.

Two things make it rename the old table instead. The rename happens inside the schema transaction
and keeps the table's rows and its object id; nothing is copied.

- `[LegacyTableNames]` on the entity lists the names its table had. It applies under any policy.
- `RenameFullNameTables` on the provider adopts the table under the entity's full-name form, the name every table had before policies existed, whenever the configured name differs from it. Set it when turning on a shorter policy, or when adding `[DatabaseTable]` to an existing entity.

What the rename does:

- It covers the table's primary key, constraints and Nucleus-named indexes, which take the new
  table's names, so no index is rebuilt. SQLite cannot rename an index, so there the index sync
  drops the old-named ones and creates them again under the new names.
- On PostgreSQL the old change notification trigger is dropped and the new one created.
- On SQL Server a full-text index cannot change inside a transaction, so it moves with the table
  and is recreated in the new table's catalog by the full-text sync.
- The table is then brought up to date like any existing table: columns it lacks are added in
  place, and a changed column type rebuilds it.

When it does not rename:

- **Several old tables exist.** Initialisation fails, nothing is changed, and the error names the
  tables. Nucleus does not merge tables: their rows can share IDs, their children point at parents
  by ID, and their columns can differ, so which rows to keep is a decision for a person. Move the
  rows you want into one of them, drop the others, and initialise again.
- **The entity's table exists and an old one does too.** The old table is left alone and a warning
  is logged.
- **The schema is read-only.** Nothing is renamed; the check logs what an update would rename.

Two processes starting together against a database that still needs a rename can both try it. The
second fails its initialisation and succeeds on its next start, as a racing table create does.

## Foreign keys

Nucleus keeps a foreign key for each column that holds another row's ID, so the database refuses a
row that points at nothing, whoever writes it. On every engine a key is kept, without being asked
for, for:

- a child's `Parent` column, to its parent's table;
- a stored `[LinkedObject]` column, to the table of the type it holds;
- the key a `[LinkedBy]` collection names, to the one's table;
- a `Guid` column marked `[ForeignKey(typeof(Keeper))]`, to that type's table.

`[ForeignKey(false)]` leaves a reference without its key, and `[ForeignKey(OnDelete = ForeignKeyAction.SetNull)]`
has the database set the column to null when the row it points at is deleted (not on a child's
`Parent`). Every key is `ON DELETE NO ACTION` otherwise: there is no cascade, because Nucleus
deletes owned rows itself, keeping each type's hard or soft rule. Every key column is indexed, so
a delete of the row it points at does not scan the table; an existing link column gains its index
at the next start.

A key is kept only where it holds for every row the column can point at. There is none when:

- the referenced type is stored by another provider, or another provider keeps the rows of either
  type (a provider can hold tables for types another provider stores);
- the reference is typed as an abstract type, or as an entity that another stored entity derives
  from, because the row can be in more than one table.

A `[ForeignKey(typeof(...))]` that cannot hold fails the schema build instead, and so does a
`[ForeignKey]` on a column that is not a stored `Guid` or that names a different type than the
property holds.

**What a key changes.**

- Deleting a row that a live row still points at fails with a `DatabaseException` of kind
  `ForeignKeyViolation`. Delete or change the rows that point at it first, or give the reference
  `[ForeignKey(false)]` or `SetNull`.
- A soft-deleted row keeps its references. A hard delete first sets to null the references that
  soft-deleted rows hold to the rows it deletes, so only live rows stop it; a reference to a row
  that no longer exists reads as null, as before.
- Saving a reference to an entity that was never saved fails, unless the link is `forceSave`, which
  writes it first. A save writes a type after the types its rows point at, whatever order it was
  given them in.

A broken key is a `ForeignKeyViolationException` (see [Saving](saving.md#unique-violations)),
which names the reference on the side that points: on a refused delete, the entity still pointing
at the row, not the one being deleted.

<!-- sample: foreign-key-violation -->
```csharp
var colin = new Keeper { Name = "Colin", Email = "colin@portlyprimate.example" };
await data.SaveAsync(colin, ct);
var report = new MischiefReport { Culprit = "Brenda", Description = "Hid the keeper's keys.", Severity = Severity.Cheeky, ReportedBy = colin.ID };
await data.SaveAsync(report, ct);

var refused = false;
try
{
    await data.DeleteAsync(colin, ct);
}
// SQLite does not say which key broke, so the names are null there.
catch (ForeignKeyViolationException ex) when (ex.Is<MischiefReport>(nameof(MischiefReport.ReportedBy)) || ex.EntityType is null)
{
    refused = true;                            // a live report still names Colin
}

await data.DeleteAsync(report, ct);            // soft: the row stays, flagged
await data.DeleteAsync(colin, ct);             // the flagged report lets go of Colin first
```

**Keeping them in step.** Keys are named `FK_`, the table and the column, and only keys with that
prefix are ever read, dropped or created, as with indexes. A key that is missing or has changed
makes the schema not current, and the update creates it after the tables and indexes. A rebuilt
table loses the keys to and from it inside the rebuild's transaction, and they are created again
after it commits. A key over a table that already holds rows pointing at missing rows is created
without checking them (`WITH NOCHECK` on SQL Server, `NOT VALID` on PostgreSQL), with a warning
giving the count; rows written after it are checked. Nucleus never changes rows to make a key fit.

With `ReadOnlySchema`, a missing or changed key makes initialisation fail, as a missing index
does; one writable start creates them. `RelationalProviderOptions.ForeignKeys` (on by default)
turns all of it off; the next start drops the keys Nucleus created.

**On SQLite** a key is part of its column's definition, so a table is created with its keys, a
column is added with its key, and a table whose keys differ from what the entities need is rebuilt
(copied into a new table, as a changed column type is) rather than altered. The first start with
keys therefore rebuilds every table that has a reference, which takes time in proportion to its
rows. A rebuild copies the rows without checking them, then counts, for each key, the rows that
point at missing rows and reports them as a warning, as the other engines do for a key created
over them; rows written after it are checked. Keys are enforced on every connection a context
opens (`PRAGMA foreign_keys`), and turned off on the schema update's own, so a rebuild can replace a
table other tables point at.

## What gets a table

- An entity gets a table only when it has at least one column of its own beside `ID` and
  `Timestamp`. A class with child collections and no `[DatabaseColumn]` property is not stored.
- `[NotStored]` keeps a type out of the schema altogether.
- `NucleusOptions.ReadOnlySchema` makes initialisation verify the schema without changing it. The
  parameterless `AddNucleus()` turns it off; `AddNucleus(options => ...)` leaves it on unless the
  options turn it off.
