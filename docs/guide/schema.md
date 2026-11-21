# Tables and schema

How tables are named, and how the schema is created and updated.

## Table names

A table is named, in order of precedence, by the type's `[DatabaseTable]`, then by its own (or an inherited) `[TableNaming]` policy, then by its provider's `TableNaming` option. The default policy, `TableNamingPolicy.FullName`, gives every table the name it always had: `tbl_` and the type's full name with each `.` and `+` replaced by `_`, so `PortlyPrimate.Shop.GiftShopVisit` is `tbl_PortlyPrimate_Shop_GiftShopVisit`. `TableNamingPolicy.ObjectName(levels)` keeps only the last `levels` parts of the full name, a nested type's outer type counting as a part: `ObjectName(1)` names it `tbl_GiftShopVisit` and `ObjectName(2)` `tbl_Shop_GiftShopVisit`. More levels than the name has gives the full name.

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

The policy can also be declared on a base class, and a single table named outright, with the names it had before:

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

A `[DatabaseTable]` name is the whole table name, prefix included: letters, digits and underscores, starting with a letter or underscore, at most 128 characters. It is not inherited, because a root and the entities under it each have a table and an inherited name would give them one. `[TableNaming]` is inherited, since a policy still gives each type a name of its own, and a type's own attribute wins over its root's. Index and constraint names follow the table name (`NCI_<table>_<column>` and so on), and on PostgreSQL a name over 63 bytes is shortened as usual. Two types that end up with one table name, a `[LegacyTableNames]` entry that is another type's table, and an old name two types both claim each fail at provider initialisation, naming the types.

**Changing a name does not move the data by itself.** On a database that already has tables, a new name makes the schema sync look for a table that is not there, and by default it creates an empty one and leaves the old table, rows and all, where it was. Two things make it rename the old table instead, inside the schema transaction, keeping its rows and its object id (nothing is copied):

- `[LegacyTableNames]` on the entity lists the names its table had. It applies under any policy.
- `RenameFullNameTables` on the provider adopts the table under the entity's full-name form, the name every table had before policies existed, whenever the configured name differs from it. Set it when turning on a shorter policy, or when adding `[DatabaseTable]` to an existing entity.

The rename covers the table's primary key, constraints and Nucleus-named indexes, which take the new table's names, so no index is rebuilt (SQLite cannot rename an index, so there the index sync drops the old-named ones and creates them again under the new names); on PostgreSQL the old change notification trigger is dropped and the new one created. The table is then brought up to date like any existing table: columns it lacks are added in place, and a changed column type rebuilds it. On SQL Server a full-text index cannot change inside a transaction, so it moves with the table and is recreated in the new table's catalog by the full-text sync.

If more than one of a missing table's old tables exists, nothing is renamed: initialisation fails, nothing is changed, and the error names the tables. Nucleus does not merge tables, because their rows can share IDs, their children point at parents by ID, and their columns can differ, so which rows to keep is a decision for a person. Move the rows you want into one of them, drop the others, and initialise again. If the entity's table exists and an old one does too, the old table is left alone and a warning is logged. Nothing is renamed while the schema is read-only; the check logs what an update would rename. Two processes starting together against a database that still needs a rename can both try it, and the second fails its initialisation and succeeds on its next start, as a racing table create does.

## Rules for a table

- An entity gets a table only when it has at least one column of its own beside `ID` and
  `Timestamp`. A class with child collections and no `[DatabaseColumn]` property is not stored.
- `[NotStored]` keeps a type out of the schema altogether.
- `NucleusOptions.ReadOnlySchema` makes initialisation verify the schema without changing it. The
  parameterless `AddNucleus()` turns it off; `AddNucleus(options => ...)` leaves it on unless the
  options turn it off.
