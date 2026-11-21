# Getting started

How to register Nucleus, define an entity and read and write it. The examples are set in the
[Portly Primate Sanctuary](../sample-domain.md), and every one of them is compiled and run by the
tests in `tests/TFM.Nucleus.DocSamples`.

## Quick start

Reference `TFM.Nucleus` and a provider package (see [Install](../../README.md#install)). A console
application also needs `Microsoft.Extensions.DependencyInjection` and `Microsoft.Extensions.Logging`;
a web application has them already.

Register Nucleus, the data services and a provider, then initialise. This one uses SQLite, so it
runs anywhere without a server:

<!-- sample: quick-start -->
```csharp
var services = new ServiceCollection();
services.AddLogging();
services.AddNucleus();                         // options and the schema provider; the schema is writable
services.AddNucleusData();                     // the provider registry and IObjectDataManager
services.AddNucleusSqliteDataProvider(options =>
{
    options.Database = databaseFile;           // created, with its folder, when missing
});

var provider = services.BuildServiceProvider();
var data = provider.GetRequiredService<IObjectDataManager>();

// Connects, then creates or updates a table for every entity class it finds.
var status = await data.InitializeDataProviderAsync();
if (status != DataProviderStatus.Ok)
    throw new InvalidOperationException($"The database is not ready: {status}.");
```

On SQL Server or PostgreSQL only the provider line changes:

<!-- sample: register-sqlserver -->
```csharp
services.AddNucleusSqlServerDataProvider(options =>
{
    options.Server = "localhost";              // host, host:port or host\instance
    options.Database = "PortlyPrimate";
    options.Username = "sanctuary";            // or UseIntegratedSecurity, or ConnectionString
    options.Password = password;
});
```

<!-- sample: register-postgres -->
```csharp
services.AddNucleusPostgresDataProvider(options =>
{
    options.Server = "localhost";              // host or host:port
    options.Database = "portlyprimate";
    options.Username = "sanctuary";            // or ConnectionString
    options.Password = password;
});
```

`InitializeDataProviderAsync` connects, then creates or migrates the database, tables, indexes and full-text indexes (and, on PostgreSQL, the change notification triggers) to match your entity classes. Set `NucleusOptions.ReadOnlySchema` to `true` to make it verify without writing.

How a table is migrated depends on what changed. A table that only lacks columns (a new property, or the soft-delete columns of a type newly made soft-deletable) has them added in place with `ALTER TABLE`. A new property's column is created NULL, so the change is metadata only and takes no time at any table size, and existing rows read the new column as null. The soft-delete flag is the exception: it is created `NOT NULL` with a default of 0 (see [Behaviour changes](behaviour-changes.md#behaviour-changes) for what that costs on SQL Server). A column whose type or size changed means a rebuild: the table is renamed, created again and its rows copied across in one transaction, which takes time in proportion to the table. A column the entity no longer has is left in the table either way. Each schema statement runs under `SchemaCommandTimeoutSeconds` (15 minutes by default).

Define entities. Properties are backed by the framework's change-tracking dictionaries through `GetValue`/`SetValue` rather than by fields:

<!-- sample: entity-monkey -->
```csharp
public enum Mood { Content, Smug, Plotting, Hangry, Asleep }

[Deletable]                                            // deleting is opt-in
public class Monkey : CoreObject<Monkey>
{
    [DatabaseColumn(Unique = true)]                    // no two monkeys share a name
    public string? Name { get => GetValue<string>(); set => SetValue(value); }

    [DatabaseColumn(Scale = 1)]
    public decimal WeightKg { get => GetValue<decimal>(); set => SetValue(value); }

    [DatabaseColumn]                                   // an enum is stored as its integer
    public Mood Mood { get => GetValue<Mood>(); set => SetValue(value); }
}
```

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

There are two ways in, and both run the same code. The static API on `CoreObject` is the short one:

<!-- sample: static-api -->
```csharp
// The static API routes through the runtime AddNucleusData publishes when the manager is first resolved.
var gerald = new Monkey { Name = "Gerald", WeightKg = 14.2m, Mood = Mood.Smug };
gerald.Save();

var found = CoreObject.LoadByID<Monkey>(gerald.ID);
var smug = CoreObject.LoadWhere<Monkey>(m => m.Mood == Mood.Smug);
var first = await CoreObject.LoadFirstWhereAsync<Monkey>(m => m.Name!.StartsWith("Ger"));

gerald.WeightKg = 14.6m;                       // only the changed column is written
await gerald.SaveAsync();
```

The manager, through its LINQ adaptor, is the one an application service uses:

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

### The manager interface

`IObjectDataManager` is the data surface DI registers, and it is what an application service or a
store takes. Every call routes the entity type to the provider of its nearest registered root,
runs in a transaction of its own, evicts the `[Cacheable]` cache when it commits and marks the
objects saved. Do not cast it to `ObjectDataManager`.

| Member | Does |
|---|---|
| `Get<T>()` | The LINQ adaptor: `Where`, `Exclude`, `Include`, `FindAsync(id, token)`, `ToListAsync`, `ToPagedResultAsync(skip, take, token)` and the other async operators. |
| `Rows<T>(predicate)` | Rows of values rather than entities. See [Row reads](querying.md#row-reads). |
| `SaveAsync(obj, token)` | Saves the object with its child collections and forceSave links. |
| `SaveAsync(objs, token)` | Saves a batch of one type in one transaction. A batch of mixed types (`IEnumerable<ICoreObject?>`) is saved in one transaction per provider. A batch too large for ordinary statements goes to the provider's bulk engine by itself. |
| `LoadByIDAsync<T>(id, token)`, `LoadByIDAsync<T>(id, options, token)` | One row by primary key, or null. A `[Cacheable]` type answers from its cache unless the options ask for something. |
| `LoadAsync<T>(predicate, token)`, `LoadAsync<T>(predicate, options, token)` | The matching rows (every row for a null predicate) with their child collections. Never null. |
| `LoadPagedAsync<T>(skip, take, predicate, options, token)` | One page and the unpaged total in a single round trip. |
| `LoadStreamAsync<T>(predicate, options, token)` | The matching rows as an `IAsyncEnumerable<T>`. |
| `DeleteAsync(obj, token)`, `DeleteAsync(objs, token)` | Deletes, cascading to child rows. The type must be `[Deletable]`. |
| `DeleteWhereAsync<T>(predicate, token)` | Deletes by predicate without loading. |
| `LoadChildCollectionAsync(collection, token)` | Loads an excluded or lazy collection. |
| `SubscribeToChangesAsync<T>(token)` | Change notifications from the provider that owns `T`. |
| `CreateContextAsync<T>(options, token)` | A caller-owned context on the provider that owns `T`, for one transaction across several types. See the known limits: it commits on dispose unless rolled back, and the manager's eviction does not apply. |
| `InitializeDataProviderAsync()` | Creates or migrates every registered provider's schema. |
| `Status` | The worst status among the registered providers; `Ok` only when every one is. |

A load takes its extras as a `LoadOptions`, so the names cannot be passed in each other's place:

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

<!-- sample: store -->
```csharp
// What an application service or a store looks like: it takes the interface DI registers.
public sealed class KeeperDesk(IObjectDataManager data)
{
    public Task<Monkey?> FindAsync(Guid id, CancellationToken ct) =>
        data.LoadByIDAsync<Monkey>(id, ct);

    public Task SaveAllAsync(IReadOnlyList<Monkey> monkeys, CancellationToken ct) =>
        data.SaveAsync(monkeys, ct);                   // one transaction

    // Two types in one transaction. Disposing the context commits, so roll back on failure.
    public async Task WeighAsync(Monkey monkey, WeighIn weighIn, CancellationToken ct)
    {
        await using var context = await data.CreateContextAsync<Monkey>(ContextOptions.Transactional, ct);
        try
        {
            await context.SaveAsync(monkey, ct);       // validates, then writes
            await context.SaveAsync(weighIn, ct);
        }
        catch
        {
            await context.RollbackAsync(ct);
            throw;
        }
    }
}
```

`DataProvider` and `DataProviderStatus` are obsolete: they are the unrooted default provider and
its status, so a context opened or a subscription made through `DataProvider` goes to the wrong
database for a type under a rooted provider. Use the routed members and `Status`. The synchronous
writes and loads stay on the concrete class for the static `CoreObject` API. `BulkSave` is
obsolete everywhere: there is one save, and it picks the bulk engine by itself. On the concrete
class the overloads that took two lists of names are obsolete in favour of `LoadOptions`, and a
load that takes an untyped `Expression` accepts a predicate whole or as its `Body`.

## Registration reference

| Method | Package | Purpose |
|---|---|---|
| `AddNucleus()` | `TFM.Nucleus` (package) | Registers the schema provider and derives `AppName` and `AppVersion` from the entry assembly. Note this overload sets `ReadOnlySchema` to `false`, which enables automatic schema creation and migration. |
| `AddNucleus(Action<NucleusOptions>)` | `TFM.Nucleus` (package) | The same, with explicit options. `ReadOnlySchema` defaults to `true` here. An `AppName` or `AppVersion` the options leave unset is read from the entry assembly. |
| `AddNucleusLogging()` | `TFM.Nucleus` (package) | Opt-in. Persists host log entries into the `NucleusLogEntry` table. |
| `AddNucleusData()` | `TFM.Nucleus.Data` | Registers the provider registry and the object data manager, and publishes the ambient runtime that the static API uses. |
| `AddNucleusSqlServerDataProvider()` | `TFM.Nucleus.Data.SQL` | Configures from the `SQL_SERVER`, `SQL_DATABASE`, `SQL_USERNAME` and `SQL_PASSWORD` environment variables. |
| `AddNucleusSqlServerDataProvider(Action<SQLConfiguration>)` | `TFM.Nucleus.Data.SQL` | Configures explicitly. |
| `AddNucleusSqlServerDataProvider<TRoot>(Action<SQLConfiguration>)` | `TFM.Nucleus.Data.SQL` | Registers a provider rooted at `TRoot`, for spanning several databases. |
| `AddNucleusSqlServerDataProvider(Type rootType, Action<SQLConfiguration>)` | `TFM.Nucleus.Data.SQL` | The same for open-generic roots, for example `typeof(AppRoot<>)`. |
| `AddNucleusPostgresDataProvider()` | `TFM.Nucleus.Data.PostgreSQL` | Configures from the `PG_SERVER`, `PG_DATABASE`, `PG_USERNAME` and `PG_PASSWORD` environment variables, and `PG_PORT` when set. |
| `AddNucleusPostgresDataProvider(Action<PostgresConfiguration>)` | `TFM.Nucleus.Data.PostgreSQL` | Configures explicitly. |
| `AddNucleusPostgresDataProvider<TRoot>(Action<PostgresConfiguration>)` | `TFM.Nucleus.Data.PostgreSQL` | Registers a provider rooted at `TRoot`. Roots on SQL Server and on PostgreSQL can live in one application. |
| `AddNucleusPostgresDataProvider(Type rootType, Action<PostgresConfiguration>)` | `TFM.Nucleus.Data.PostgreSQL` | The same for open-generic roots. |
| `AddNucleusSqliteDataProvider()` | `TFM.Nucleus.Data.SQLite` | Configures the database file from the `SQLITE_DATABASE` environment variable. |
| `AddNucleusSqliteDataProvider(Action<SqliteConfiguration>)` | `TFM.Nucleus.Data.SQLite` | Configures explicitly. |
| `AddNucleusSqliteDataProvider<TRoot>(Action<SqliteConfiguration>)` | `TFM.Nucleus.Data.SQLite` | Registers a provider rooted at `TRoot`, with its own database file. |
| `AddNucleusSqliteDataProvider(Type rootType, Action<SqliteConfiguration>)` | `TFM.Nucleus.Data.SQLite` | The same for open-generic roots. |

### NucleusOptions

| Option | Default | Meaning |
|---|---|---|
| `ReadOnlySchema` | `true` | When true the schema is verified but never written. The parameterless `AddNucleus()` sets it false. |
| `AppName` | entry assembly name | Reported to the database server as the application name, and written on each entry `AddNucleusLogging` persists. Resolved at registration: the value set, or else the entry assembly's name. |
| `AppVersion` | entry assembly version | Written on each entry `AddNucleusLogging` persists. Resolved at registration: the value set, or else the entry assembly's informational version (without build metadata), or else its assembly version. |
| `EnableAuditLogging` | `true` | Writes change summaries such as "Updated Name from A to B" on save and delete. |
| `AuditLogLevel` | `Information` | The level those summaries are written at. The category is the entity type's full name. |
| `LogEntityMinimumLevel` | `Warning` | Minimum level for host log entries persisted by `AddNucleusLogging()`. |

### Configuration from appsettings

A provider's options and `NucleusOptions` bind from configuration like any other options class:

<!-- sample: register-configuration -->
```csharp
// appsettings.json:
//   "Nucleus": { "ReadOnlySchema": false, "AppName": "PortlyPrimate.Web" },
//   "Database": { "Server": "db01", "Database": "PortlyPrimate", "Username": "sanctuary" }
// The password comes from a secret store, not from the file.
services.AddNucleus(options => configuration.GetSection("Nucleus").Bind(options));
services.AddNucleusData();
services.AddNucleusSqlServerDataProvider(options =>
{
    configuration.GetSection("Database").Bind(options);
    options.Password = configuration["Database:Password"];
});
```

### More than one database

Register a provider for a root type, and every entity under that root is stored by it. Anything
under no registered root goes to the provider registered without one:

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
