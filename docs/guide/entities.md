# Entities

An entity is a class that derives from a Nucleus base class and marks its stored properties with
attributes. This page covers the base classes, how a property becomes a column, indexes, the hooks
an entity can override, and soft delete, full-text search and caching. How one entity holds others
is on the [Relationships](relationships.md) page.

## Entity base classes

| Base class | Use for |
|---|---|
| `CoreObject<T>` | A normal root entity. `T` is the entity's own type, which gives the static API its generic constraint. |
| `CoreChildObject<T, TParent>` | A child entity that always belongs to a parent. Exposes `Parent` and participates in cascade saves and deletes. |
| `CoreChildObject<TParent>` | The non self-referencing form of the above. |

Every entity has two properties it does not declare:

- **`ID`**, a `Guid` primary key, assigned when the object is constructed.
- **`Timestamp`**, a `DateTimeOffset` the server stamps on every insert and update. A save
  updates the object saved and only those rows below it that are new or changed, so a child's
  `Timestamp` moves when the child changes, not whenever its parent is saved. The stamped
  value is not read back. In memory, every row a save wrote takes the client clock when the save
  is committed, and a row it did not write keeps the value it was loaded with, so the value is
  close to the stored one but not equal to it: the clocks differ, and the engines store it at
  their own precision. Reading it back on every write was measured at 10% to 65% slower for
  batched, split and bulk saves.

## Columns

`[DatabaseColumn]` marks a property as stored. A property without it lives in memory only. The
column takes the property's name, so `[DatabaseColumn]` on its own is the common case.

The attribute is written three ways, each with an optional trailing column name: `()`,
`(ColumnDataType)` and `(ColumnDataType, int dataLength)`. Two more constructors take a nullable
length, which attribute syntax does not accept, so a length is set together with its type.
Everything else is set by name:

| Field | Default | Meaning |
|---|---|---|
| `ColumnName` | property name | Physical column name, used by every statement, index name and predicate. Set it as `[DatabaseColumn("code")]` or `[DatabaseColumn(ColumnName = "code")]`. See renaming below. |
| `DataType` | `NotSpecified` | A `ColumnDataType`: one of `NotSpecified`, `Unicode`, `Date`, `Integer`, `Binary`, `LargeBinary`. `NotSpecified` infers the type from the CLR property type. |
| `DataLength` | inferred | Column length. `-1` maps to `max`. Nullable, so attribute syntax cannot set it by name; give it to the constructor: `[DatabaseColumn(ColumnDataType.Unicode, 200)]`. |
| `DecimalPlaces` | none | Scale for decimal columns. Nullable, so attribute syntax cannot set it; use `Scale`. |
| `Scale` | provider default (5) | Sets `DecimalPlaces` from attribute syntax: `[DatabaseColumn(Scale = 6)]`. Precision is the data length (default 20) minus the scale. |
| `PrimaryKey` | `false` | Reserved for the `ID` column `CoreObject` provides. Exactly one column must carry it, and marking any other property fails at provider initialisation. |
| `Unique` | `false` | Adds a unique index on the column. NULLs are not constrained. See below. |
| `Indexed` | `false` | Adds an index on the column. Neither this nor `Unique` is allowed on a `max` length or externally stored column. |
| `IndexDescending` | `false` | Sorts that index descending. |
| `FullTextSearch` | `false` | Includes the column in the table's full-text index, which `string.Contains` on the column searches. |
| `ExternalStorage` | `false` | Stores a `byte[]` column outside the row where the provider supports it (SQL Server FILESTREAM). |
| `SetValueOverride` | `None` | A `ServerFunctionKind`. Has the server compute the value on every insert and update, for example `ServerFunctionKind.CurrentDateTimeOffset` for a server timestamp; `CurrentDateTime`, `UtcNow` and `NewGuid` are the others. A value the client set for the property is not sent. |
| `ReturnOutputToProperty` | `false` | Reads the value the server stored back into the property, in the same statement as the write (`OUTPUT` on SQL Server, `RETURNING` on PostgreSQL and SQLite), on every write path including the bulk engine. The property takes it once the save commits (through a caller's context, as its save runs), so a save that fails leaves the object's own value in place for the next one. A write that reads back runs as a reader rather than a non-query, which costs batched and bulk saves measurably (see `Timestamp` above). On SQL Server a table with an enabled trigger refuses `OUTPUT` (error 334), and the write fails with a message saying so. |

### Renaming a column

To rename a property and keep its data, rename the property and set `ColumnName` to the old name.
The table does not change.

Changing `ColumnName` on a table that already has rows is different, and loses sight of the data:

- The schema sync sees a missing column and adds it empty, so **the property reads null on every
  existing row**. The old column and its values stay in the table, but nothing reads them.
- If the same deployment also changes a column's type, the table is rebuilt instead. A rebuild
  copies only the columns whose names the old and new tables share, so the old column's values are
  lost.

Rename the column in the database yourself before deploying the new name.

Some names are refused at provider initialisation: two properties mapped to one column (names are
compared without regard to case), a property renamed to `ID` or `Timestamp`, any property named
`ClusterID`, and `IsDeleted` or `DeletedAt` on a soft-deletable type.

### Column types

Each property maps to a column type by its CLR type, or by `ColumnDataType` when one is given:

| Property | SQL Server | PostgreSQL | SQLite (declared type) |
|---|---|---|---|
| `string` | `varchar(255)`, or the given length; `-1` is `varchar(max)` | `varchar(255)`, or the given length; `-1` is `text` | `VARCHAR(255)`, or the given length (not enforced); `-1` is `TEXT` |
| `string` with `ColumnDataType.Unicode` | `nvarchar(510)`, or the given length | `varchar(510)`, or the given length | `VARCHAR(510)`, or the given length |
| `int`, any enum (stored as its integer) | `int` | `integer` | `INTEGER` |
| `long` | `bigint` | `bigint` | `BIGINT` |
| `double` | `float` | `double precision` | `REAL` |
| `bool` | `bit` | `boolean` | `BOOLEAN` (0 or 1) |
| `decimal` | `decimal(15,5)`, or as `Scale` and `DataLength` give | `numeric(15,5)`, the same way | `DECIMAL(15,5)`, stored as a real |
| `DateTime` | `datetime2(7)` | `timestamp` | `DATETIME` (text) |
| `DateTime` with `ColumnDataType.Date` | `date` | `date` | `DATE` (text) |
| `DateTimeOffset` | `datetimeoffset(7)` | `timestamptz` (UTC, microseconds) | `DATETIMEOFFSET` (UTC text, ticks) |
| `Guid`, or an entity stored by its `ID` | `uniqueidentifier` | `uuid` | `UUID` (lower-case text) |
| `byte[]` | `varbinary(max)` | `bytea` | `BLOB` |
| a type named `TFMJsonString` | `json` | `jsonb` | `JSON_TEXT` |

The vocabulary is named for what it means, not for one engine's feature. `ColumnDataType.Unicode`
means the string holds Unicode text, and a provider whose strings are always Unicode treats it like
any string. The older SQL Server names (`NonClusteredIndex`, `IndexSortDESC`, `FullTextIndex`,
`Filestream`, `SYSDATETIME`) are gone; [Behaviour changes](behaviour-changes.md#behaviour-changes)
has the table of renames.

### Unique columns

`Unique` creates a unique index named `UQ_<table>_<column>`, filtered to rows where the column is
not null. Any number of rows may leave the column NULL; every non-null value must be distinct.

- Setting `Indexed` as well does not add a second index, and `IndexDescending` applies to whichever
  index is created.
- A write that breaks the index throws a [`UniqueViolationException`](saving.md#unique-violations).
- For a rule over several columns together, declare a unique
  [`[TableIndex]`](#indexes-over-several-columns) instead.

On SQL Server the index is nonclustered and filtered, and a filtered index needs
`SET QUOTED_IDENTIFIER ON` for every INSERT, UPDATE and DELETE against the table. SqlClient, and so
Nucleus, sets it on by default. SQL Agent T-SQL steps, `bcp` without `-q`, `sqlcmd` without `-I`
and some legacy ODBC connections do not, and fail with error 1934 until they do.

### How indexes are kept in step

At initialisation the provider compares the `NCI_*`, `UQ_*` and `NIX_*` indexes in the database
with the entity model and drops or creates them to match, without rebuilding the table. Indexes
with any other name are never touched.

A unique index cannot be created over rows that already hold duplicates. That happens when an
existing column is changed to `Unique`, or a `[TableIndex]` is made `Unique`. Initialisation then
fails with a logged error naming the index, the table and its columns. No rows are modified, and
the duplicates must be resolved by hand before initialising again. The sync drops the index being
replaced before it builds the new one, so until then the table has neither.

## Attributes on the class

| Attribute | Effect |
|---|---|
| `[Cacheable(refreshInterval, cacheLifetime)]` | Keeps the whole collection plus a Guid index in memory. Both values are in seconds and default to 120 and 900. |
| `[Deletable(DeleteBehavior.Hard)]` | Deletes physically remove rows. `[Deletable]` alone is hard. |
| `[Deletable(DeleteBehavior.Soft)]` | Deletes flag the row through `IsDeleted` and `DeletedAt` system columns, and reads filter them out unless the read asks for them: `LoadOptions.IncludeDeleted` on a manager load, or `.IncludeDeleted()` on a query from `Get<T>()`. |
| `[Deletable(false)]` | On a child type: its rows are never deleted, by any route. A declaration above it that would delete them fails the schema. |
| `[NotStored]` | Excludes the type from schema management entirely: it has no table. |
| `[InitializePriority(n)]` | Intended to order type initialisation, descending. Parsed but not currently applied. |
| `[TableIndex(name, keys..., Include = [...], Unique = true)]` | An index over several columns, named `NIX_<table>_<name>`, optionally unique over all of its keys together. Repeat it for more than one. See below. |
| `[DatabaseTable("tbl_Name")]` | The table name, used as written in place of the one the naming policy would give. Not inherited. See [Table names](schema.md#table-names). |
| `[TableNaming(style, levels)]` | The naming policy of this type's table, in place of the provider's. Inherited, so it can sit on a root. See [Table names](schema.md#table-names). |
| `[LegacyTableNames("tbl_Old", ...)]` | Earlier names of the type's table. A missing table is renamed from the one of them that exists. Not inherited. See [Table names](schema.md#table-names). |

Deletion is opt-in. Nothing is deleted unless a `[Deletable]` declaration says so: on the type, on
a child collection property, or on an owner above a child type. See [Deleting](relationships.md#deleting).

### Indexes over several columns

`[TableIndex]` declares an index over several columns, by property name. This entity has two, and
also shows the table naming attributes, which [Tables and schema](schema.md#table-names) explains:

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

**Keys and included columns.** The keys order the index, first to last, each ascending. A read
that filters on the first key, or on the first and a range of the second, finds its rows without
scanning the table. The included columns are carried in the index without ordering it, so a read
that needs only key and included columns (a [row read](querying.md#row-reads) of them, say) is
answered from the index alone. SQLite has no included columns and creates the index over its keys.

**What is refused.** Keys cannot be long text (`DataLength = -1`); included columns can. A name
used twice on the type, a property that is not a stored column, a column named twice or a long
text key fails when the schema is built.

**How it is maintained.** The index is compared and synced like a column index. Changing its keys
or included columns drops and creates it again, and removing the attribute drops it. Creating one
over a large table blocks writes to the table while it builds, and the time it took is logged.

**Unique indexes.** `Unique = true` makes the keys a unique rule taken together, the several-column
form of a column's `Unique`, as `SessionPath` is above. No two rows may share every key value.

- The index is filtered to rows whose keys are all non-null
  (`WHERE [SessionId] IS NOT NULL AND [Path] IS NOT NULL`), so a row with any key left null is not
  constrained. `Include` can be used with it.
- A write that breaks it throws a [`UniqueViolationException`](saving.md#unique-violations) naming
  the entity and the index.
- Turning `Unique` on or off drops and creates the index again, never the table. Existing
  duplicates fail initialisation, as described under
  [How indexes are kept in step](#how-indexes-are-kept-in-step).
- With `ReadOnlySchema`, a missing index, or one whose uniqueness differs, reports the schema as
  not current and nothing is created.
- On SQL Server the index is filtered, so the `QUOTED_IDENTIFIER` requirement described under
  [Unique columns](#unique-columns) applies to the table.

### Relationship attributes

The [Relationships](relationships.md) page explains these; this is the list.

| Attribute | Applies to | Effect |
|---|---|---|
| `[LinkedObject]` | a property holding another entity | Stores the referenced entity's `ID` in this table. A load reads only the ID; the first read of the property loads the entity, and `Include` loads it with the rows. See [Linked objects](relationships.md#linked-objects). Overloads take a `linkedIDProperty` name, which is accepted and does nothing (see the known limits), and `forceSave`: with `forceSave: true`, saving this entity also inserts or updates the referenced one, in the same transaction, when it is new, changed, or holds changed or removed children; its own forceSave links follow, and a cycle ends. A link that was never read since the load has nothing to write and is not loaded by the save. The referenced type must be stored by the same provider, or the save throws before anything runs. |
| `[Deletable]` | a `NucleusChildCollection` property | Removing a stored child from the collection and saving the parent deletes the child. See [Deleting](relationships.md#deleting). |
| `[RetrieveOnDemand]` | a `NucleusChildCollection` property | Makes the collection lazy: loads leave its rows out, and it loads them the first time it is read. See [Lazy collections](relationships.md#lazy-collections). |
| `[LinkedBy(keyProperty)]` | a `NucleusLinkedCollection` property | Names the many type's key, the property holding the one's ID. See [Linked collections](relationships.md#linked-collections). |
| `[ParentObject(parentIDProperty)]` | the `Parent` property of a child entity | Identifies the owning entity. `CoreChildObject` already applies it. |
| `[ForeignKey(typeof(T))]`, `[ForeignKey(false)]`, `[ForeignKey(OnDelete = ...)]` | a stored column | A foreign key from a plain `Guid` column to `T`'s table; or drops, or sets the delete action of, the key a reference gets anyway. See [Foreign keys](schema.md#foreign-keys). Its name is also used by `System.ComponentModel.DataAnnotations.Schema`; a file that imports both namespaces names it `TFM.Nucleus.Data.ForeignKey`. |

### Logging and auditing

| Attribute | Effect |
|---|---|
| `[SuppressLogging]` | Omits the type or property from audit summaries. Note it is inherited, so applying it to a base class silences every descendant. |
| `[AbbreviatedLogging]` | Records that the property changed without recording the values, for long or sensitive data. |
| `[LinkWhenLogging]` | Marks a linked object whose type and ID are to be attached to the log entry's linked objects. Not applied yet: the attribute is read into the schema and nothing acts on it. See [Known limits](known-limits.md#known-limits). |
| `[RetrieveOnDemand]` | On a child collection, makes it lazy (see [Relationship attributes](#relationship-attributes)). On any other property it has no effect. |

## The static API

These members live on `CoreObject`. They forward to the runtime that `AddNucleusData()` publishes
when the manager is first resolved, so dependency injection must be set up before they are used.
The generic argument of each load and delete is constrained to `where T : CoreObject<T>, new()`.

| Member | Returns | Notes |
|---|---|---|
| `LoadByID<T>(Guid id)` | `T?` | O(1) from cache for `[Cacheable]` types. |
| `TryLoadByID<T>(Guid id, out T? obj)` | `bool` | Non-throwing form of the above. |
| `LoadByIDAsync<T>(Guid id, CancellationToken)` | `Task<T?>` | The same on the asynchronous path. |
| `LoadFirstWhere<T>(Expression<Func<T, bool>>)`, `LoadFirstWhereAsync<T>(..., CancellationToken)` | `T?`, `Task<T?>` | First match, or null. One row is read, however many match. `LoadObjectWhere` is obsolete: it loaded every match to return the first. |
| `LoadAllAsync<T>(CancellationToken)` | `Task<NucleusCollection<T>>` | Whole collection on the asynchronous path. |
| `LoadAll<T>()` | `NucleusCollection<T>` | Whole collection. |
| `LoadWhere<T>(Expression<Func<T, bool>>)` | `NucleusCollection<T>` | Translated to a parameterised query. |
| `LoadWhereAsync<T>(Expression<Func<T, bool>>, CancellationToken)` | `Task<NucleusCollection<T>>` | Async form. |
| `LoadByID<T>(Guid, params Expression<Func<T, INucleusChildCollection>>[] exclude)` | `T?` | Leaves the selected child collections unloaded. Bypasses the cache when anything is excluded. |
| `LoadWhere<T>(predicate, params exclude)`, `LoadWhereAsync<T>(predicate, CancellationToken, params exclude)` | as above | The same, for a predicate. |
| `DeleteWhere<T>(Expression<Func<T, bool>>)`, `DeleteWhereAsync<T>(..., CancellationToken)` | `int`, `Task<int>` | Deletes every matching row without loading any, with its child collection rows, in one transaction. Returns the rows of `T` affected. See [Deleting](relationships.md#deleting). |
| `Save(IEnumerable<ICoreObject?>)`, `SaveAsync(IEnumerable<ICoreObject?>, CancellationToken)` | `void`, `Task` | Saves a batch of any types, each routed to the provider that stores it, in one transaction per provider. On the asynchronous path a batch too large for ordinary statements is written by the bulk engine. `BulkSave` is the obsolete name. |

Instance members on every entity:

| Member | Notes |
|---|---|
| `Save()`, `SaveAsync()`, `SaveAsync(CancellationToken)` | Validates as every save does (`Validate()`, which also validates the new and changed children and the forceSave links, then `PreSave()`), then writes only the changed columns, the child collections (deleting removed children) and the forceSave links, in one transaction. |
| `SaveAsync(IDataContext, CancellationToken)` | Saves inside a caller-owned context and transaction, validating first; the caller commits or rolls back. The object is marked saved as the writes run, and put back as it was if the transaction does not commit. |
| `Delete()`, `DeleteAsync(CancellationToken)` | Types with their own `[Deletable]` only. Removes the object's child collection rows at every depth in the same statement batch, whether or not they were loaded. Throws before anything executes if a child type is stored by another provider. |
| `IsNew`, `IsReadOnly`, `HasChanges`, `DataChanged` | Change-tracking state. |
| `ToJson()`, `DisplayName` | Serialisation and display helpers. |

Equality, `GetHashCode` and the explicit conversion to `Guid` are all based on `ID`.

## Hooks and validation

Override these on an entity to run logic at defined points:

| Hook | When it runs |
|---|---|
| `Validate()` | At the start of every save, on every path (entity, manager, context, bulk), once per object saved. Throw to reject the save: nothing is written and the exception reaches the caller as thrown. The root's also runs every new or changed child's, and each forceSave link's. Call `base.Validate()`: a save of an object whose override never does is refused. A value the override sets, before or after that call, is written. |
| `PreSave()` | After validation and before anything is written, on the object saved and on each new or changed child and forceSave link the save writes. A value set here is written but not validated. |
| `OnAfterLoadFromDB()` | Once per loaded object, at the end of every load: after its columns and parent are bound, its loaded children are attached and have run theirs, and an excluded collection reads as unloaded. A lazy collection's rows run theirs when it loads. |
| `OnAfterInitializeDB()` | Never. It is obsolete: schema initialisation has no instance to call it on. Run start-up work after `InitializeDataProviderAsync`. |

An entity overriding two of them:

<!-- sample: entity-weighin -->
```csharp
[Deletable]
public class WeighIn : CoreObject<WeighIn>
{
    [DatabaseColumn(Indexed = true)]
    public string? MonkeyName { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(Scale = 1)]
    public decimal WeightKg { get => GetValue<decimal>(); set => SetValue(value); }

    [DatabaseColumn]
    public string? Notes { get => GetValue<string>(); set => SetValue(value); }

    /// <summary>Worked out when the row is loaded; not stored.</summary>
    public bool IsHeavy { get; private set; }

    // Runs after validation and before anything is written. What it sets is saved.
    public override void PreSave()
    {
        Notes = Notes?.Trim();
        base.PreSave();
    }

    // Runs once per loaded object, after its columns and children are in place.
    protected override void OnAfterLoadFromDB()
    {
        IsHeavy = WeightKg > 20;
        base.OnAfterLoadFromDB();
    }
}
```

`Validate()` is where an entity's rules go. Throw to refuse the save: nothing is written, and the
exception reaches the caller as it was thrown. A parent's `Validate()` runs its new and changed
children's, so a child's rule holds without the parent restating it. The order line here refuses a
quantity of zero, and that stops a save of its order:

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

## Soft delete, full-text search and server-stamped values

One entity shows all three:

- `[Deletable(DeleteBehavior.Soft)]` keeps a deleted row and hides it from ordinary reads.
- `FullTextSearch` indexes a text column.
- `SetValueOverride` has the database server stamp a value on every write.

<!-- sample: entity-mischief -->
```csharp
public enum Severity { Cheeky, Brazen, Felonious }

// Soft delete: a deleted report keeps its row, flagged, and ordinary reads no longer see it.
[Deletable(DeleteBehavior.Soft)]
public class MischiefReport : CoreObject<MischiefReport>
{
    [DatabaseColumn(Indexed = true)]
    public string? Culprit { get => GetValue<string>(); set => SetValue(value); }

    // Contains() on this column searches the full-text index where the engine has one.
    [DatabaseColumn(FullTextSearch = true)]
    public string? Description { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn]
    public Severity Severity { get => GetValue<Severity>(); set => SetValue(value); }

    // A keeper's ID, not a [LinkedObject]: [ForeignKey] gives it the key a reference would get.
    [DatabaseColumn]
    [ForeignKey(typeof(Keeper))]
    public Guid? ReportedBy { get => GetValue<Guid?>(); set => SetValue(value); }

    // Stamped by the database server on every insert and update, whatever the client sets.
    [DatabaseColumn(SetValueOverride = ServerFunctionKind.CurrentDateTimeOffset)]
    public DateTimeOffset RecordedAt { get => GetValue<DateTimeOffset>(); set => SetValue(value); }
}
```

A soft-deleted row is read only when the read asks for it:

<!-- sample: deletes -->
```csharp
var report = new MischiefReport { Culprit = "Brenda", Description = "Stole the keeper's lunch. Again.", Severity = Severity.Brazen };
await data.SaveAsync(report, ct);

await data.DeleteAsync(report, ct);            // soft: the row stays, flagged

var live = await data.LoadAsync<MischiefReport>(r => r.Culprit == "Brenda", ct);                                          // empty
var all = await data.LoadAsync<MischiefReport>(r => r.Culprit == "Brenda", new LoadOptions { IncludeDeleted = true }, ct);
var viaQuery = await data.Get<MischiefReport>().IncludeDeleted().Where(r => r.Culprit == "Brenda").ToListAsync(ct);
var ever = await data.Rows<MischiefReport>(r => r.Culprit == "Brenda").IncludeDeleted().CountAsync(ct);    // a row read sees them when asked too

// Delete by predicate: no row is loaded, child rows go too, and the count comes back.
var removed = await data.DeleteWhereAsync<WeighIn>(w => w.MonkeyName == "Nobody", ct);
```

`Contains` on a `FullTextSearch` column uses the index on SQL Server and PostgreSQL. SQLite has no full-text index and matches the substring:

<!-- sample: full-text -->
```csharp
// Contains on a FullTextSearch column searches the index on an engine that has one.
var lunches = await data.Get<MischiefReport>().IncludeDeleted().Where(r => r.Description!.Contains("lunch")).ToListAsync(ct);
```

## Caching

`[Cacheable]` holds the whole table in memory. It suits a small table that is read far more often
than it changes:

<!-- sample: entity-variety -->
```csharp
// A small lookup table, read often and changed rarely: held whole in memory, refreshed after
// 120 seconds in the background and dropped after 900.
[Cacheable(refreshInterval: 120, cacheLifetime: 900)]
public class BananaVariety : CoreObject<BananaVariety>
{
    [DatabaseColumn(Unique = true)]
    public string? Name { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn]
    public int Curvature { get => GetValue<int>(); set => SetValue(value); }
}
```

A read of every row (`LoadAsync<T>()`, `Load<T>()`, `LoadAllAsync`, `LoadStreamAsync<T>()`, and
`Get<T>()` with no `Where`) and a read by ID come from
memory, and a save or a delete of the type evicts the cache. The first such read fills it, on the
asynchronous path when the read is asynchronous, and reads that miss at the same time share that
one fill. A read with a condition, or with `LoadOptions` that exclude, include or show deleted
rows, reads the table:

<!-- sample: cacheable -->
```csharp
// The first read of every row fills the cache; later reads of every row or by ID answer
// from memory until a save or a delete evicts it.
var varieties = await data.LoadAsync<BananaVariety>(cancellationToken: ct);   // a list of your own, over the shared objects
var cavendish = varieties.First(v => v.Name == "Cavendish");
var again = await data.LoadByIDAsync<BananaVariety>(cavendish.ID, ct);        // by ID: an index lookup in memory
```

Each read of every row returns a list of its own, so adding to it or removing from it changes
nothing for anyone else. The objects in it are shared by every caller, so a change to one is seen
by every reader of the cache before it is saved.
Past `refreshInterval` a read still gets the cached rows at once while they are read again in the
background; past `cacheLifetime` the read waits for them. A background read that fails is logged
as a warning, the cached rows stay, and the next background read starts one `refreshInterval`
later.

A `[Cacheable]` type cannot have a lazy (`[RetrieveOnDemand]`) child collection, and cannot be the
one side of a linked collection: its cached instances are shared by every caller.
