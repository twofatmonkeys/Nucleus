# Nucleus

Nucleus is a custom entity framework (ORM) for .NET by Two Fat Monkeys. Domain objects derive from a common `CoreObject` base; the framework discovers their schema from reflection and attributes, tracks property changes, translates LINQ expressions into parameterised queries, materialises objects from result sets, and creates and migrates the physical database schema automatically.

This repository holds the whole family. Seven packages ship from it, all on one version.

## Packages

| Package | Role |
|---|---|
| `TFM.Nucleus.Abstractions` | Contracts: `ICoreObject`, `IDataProvider` and `IDataContext`, the neutral query model (`IQuery`, `IQueryParameter`, `Operator`), schema interfaces, the `DataSet` row buffer, `NucleusCollection` and `NucleusChildCollection`, `PagedResult<T>`, `IChangeSubscription`, `INucleusRuntime`, `NucleusOptions`. Zero SQL. |
| `TFM.Nucleus` | The core: `CoreObject` entity base classes, attribute-driven schema reflection (`ObjectSchema` and `PropertySchema` with IL-emitted materialisers), change tracking, the static convenience API, `NucleusLoggerProvider`, and DI registration. |
| `TFM.Nucleus.Data` | Provider-neutral data layer: `ObjectDataManager` (save, load and delete orchestration, cascade, child-graph hydration), `ObjectDataAdaptor<T>` (a LINQ provider with async operators), `ExpressionParser`, `DataProviderRegistry` for multi-provider routing, `NucleusCache`. It also holds the query model and the schema model every provider shares, and the provider SDK a provider package builds on (see [Provider seam](https://github.com/twofatmonkeys/Nucleus/blob/master/docs/guide/provider-authoring.md#the-provider-seam)). Contains no SQL, proven by a tripwire test. |
| `TFM.Nucleus.Data.SQL` | SQL Server provider: T-SQL rendering, the SQL Server schema dialect and catalog, the SqlBulkCopy engine and SqlDependency change notifications. Its public surface is its registration (`AddNucleusSqlServerDataProvider`), `SQLConfiguration` and the exception its registration throws. |
| `TFM.Nucleus.Data.PostgreSQL` | PostgreSQL provider (PostgreSQL 16 or later, over Npgsql): query rendering, the PostgreSQL schema dialect and catalog, the COPY bulk engine and LISTEN/NOTIFY change notifications. Its public surface is its registration (`AddNucleusPostgresDataProvider`), `PostgresConfiguration` and the exception its registration throws. See [PostgreSQL](https://github.com/twofatmonkeys/Nucleus/blob/master/docs/guide/providers.md#postgresql). |
| `TFM.Nucleus.Data.SQLite` | SQLite provider for file databases in embedded and local applications (SQLite 3.35 or later, bundled, over Microsoft.Data.Sqlite): query rendering, the SQLite schema dialect and catalog, and write-ahead logging. No change notifications or full-text indexes. Its public surface is its registration (`AddNucleusSqliteDataProvider`), `SqliteConfiguration` and the exception its registration throws. See [SQLite](https://github.com/twofatmonkeys/Nucleus/blob/master/docs/guide/providers.md#sqlite). |
| `TFM.Nucleus.Testing` | Live-database test support for anything built on Nucleus, framework neutral: the SQL Server, PostgreSQL and SQLite test database settings, a booted stack per provider, cross-process locks and the raw statements a test sets a table up with. See [Testing against a live database](https://github.com/twofatmonkeys/Nucleus/blob/master/docs/guide/testing.md#testing-against-a-live-database). |

Dependency graph: `Abstractions` <- (`Nucleus`, `Data`) <- (`Data.SQL`, `Data.PostgreSQL`, `Data.SQLite`). The core and neutral layers never reference a concrete provider, and no provider references another, so an application picks its database by picking a provider package, and a store written against the neutral layers works over any of them. `Testing` sits above the providers, because a test picks its database too. See [Stores for other libraries](https://github.com/twofatmonkeys/Nucleus/blob/master/docs/guide/providers.md#stores-for-other-libraries).

## Install

The packages are published to GitHub Packages. Add the feed once, then reference `TFM.Nucleus` (the entity base classes and `AddNucleus`) and the provider package for your database. The provider package pulls `TFM.Nucleus.Abstractions` and `TFM.Nucleus.Data` transitively; it does not pull `TFM.Nucleus`.

```bash
dotnet add package TFM.Nucleus                   # always
dotnet add package TFM.Nucleus.Data.SQL          # SQL Server
dotnet add package TFM.Nucleus.Data.PostgreSQL   # PostgreSQL 16 or later
dotnet add package TFM.Nucleus.Data.SQLite       # a SQLite database file
```

The feed is `https://nuget.pkg.github.com/twofatmonkeys/index.json` and requires authentication even for reads. Set `GITHUB_USER` and `GITHUB_TOKEN` (a classic PAT with `read:packages`) in your environment. See [docs/publishing.md](https://github.com/twofatmonkeys/Nucleus/blob/master/docs/publishing.md).

## Target frameworks

`net8.0` and `net10.0`.

## Quick start

Register Nucleus, the data services and a provider, then initialise. This one uses SQLite, so it
runs anywhere without a server; SQL Server and PostgreSQL differ only in the provider line.

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

Define an entity. Properties are backed by the framework's change tracking through `GetValue` and
`SetValue`, not by fields:

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

Save it and read it back:

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

An application service takes `IObjectDataManager`, the interface dependency injection registers:

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

Every example in the documentation is set in the [Portly Primate Sanctuary](https://github.com/twofatmonkeys/Nucleus/blob/master/docs/sample-domain.md),
and is compiled and run by the tests in `tests/TFM.Nucleus.DocSamples`.

## Documentation

| Page | What it covers |
|---|---|
| [Getting started](https://github.com/twofatmonkeys/Nucleus/blob/master/docs/guide/getting-started.md) | Registration, the manager interface, configuration, more than one database |
| [Entities](https://github.com/twofatmonkeys/Nucleus/blob/master/docs/guide/entities.md) | Base classes, column attributes and type mapping, indexes, hooks, validation, soft delete, caching |
| [Relationships](https://github.com/twofatmonkeys/Nucleus/blob/master/docs/guide/relationships.md) | Child collections, linked objects, linked collections |
| [Querying](https://github.com/twofatmonkeys/Nucleus/blob/master/docs/guide/querying.md) | LINQ, the async operators, paging, row reads, streaming |
| [Saving and deleting](https://github.com/twofatmonkeys/Nucleus/blob/master/docs/guide/saving.md) | Batches, unique violations, change notifications |
| [Tables and schema](https://github.com/twofatmonkeys/Nucleus/blob/master/docs/guide/schema.md) | Table names, renames, what gets a table |
| [Providers](https://github.com/twofatmonkeys/Nucleus/blob/master/docs/guide/providers.md) | SQL Server, PostgreSQL and SQLite, their options, the store packages |
| [Testing](https://github.com/twofatmonkeys/Nucleus/blob/master/docs/guide/testing.md) | Testing against a live database with `TFM.Nucleus.Testing` |
| [Known limits](https://github.com/twofatmonkeys/Nucleus/blob/master/docs/guide/known-limits.md) | What a consumer must work with today |
| [Behaviour changes](https://github.com/twofatmonkeys/Nucleus/blob/master/docs/guide/behaviour-changes.md) | What changed, newest first, with the migration tables |
| [Writing a provider](https://github.com/twofatmonkeys/Nucleus/blob/master/docs/guide/provider-authoring.md) | The provider seam and SDK |

For contributors: [contributing](https://github.com/twofatmonkeys/Nucleus/blob/master/docs/contributing.md), [conventions](https://github.com/twofatmonkeys/Nucleus/blob/master/docs/conventions.md),
[versioning](https://github.com/twofatmonkeys/Nucleus/blob/master/docs/versioning.md), [publishing](https://github.com/twofatmonkeys/Nucleus/blob/master/docs/publishing.md) and the
[review findings](https://github.com/twofatmonkeys/Nucleus/blob/master/docs/code-review-findings.md).

## Licence

MIT. See [LICENSE.txt](https://github.com/twofatmonkeys/Nucleus/blob/master/LICENSE.txt).
