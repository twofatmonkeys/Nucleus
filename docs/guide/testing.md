# Testing

`TFM.Nucleus.Testing` runs a test against a real database. It is what this repository's own
integration tests run on, packaged so a store in another repository can be tested against the same
databases the same way.

## Testing against a live database

The package references no test framework. A test asks whether a database is configured, and skips
in its own framework's way when it is not:

<!-- sample: testing -->
```csharp
var database = NucleusTestDatabase.Sqlite;             // or .SqlServer, or .Postgres
if (!database.IsConfigured)
    return;                                            // skip with database.SkipReason, your framework's way

using var schemaLock = await database.SchemaLockAsync(ct);   // another run may share the database
using var stack = await database.StartStackAsync();    // AddNucleus plus the provider, schema initialised
var desk = new KeeperDesk(stack.Manager);
```

The types in the package:

| Type | What it gives a test |
|---|---|
| `NucleusTestDatabaseSettings` | Where the database is: the `Nucleus:*` (SQL Server), `Nucleus:Postgres:*` and `Nucleus:Sqlite:Database` (a file path, which each .NET runtime's run suffixes before the extension, `.net8` or `.net10`, so two runs never write one file; `SqliteTestDatabase.FilePath` is the file in use) user secrets under the id `tfm-nucleus-integrationtests`, or the `NUCLEUS_TEST_*`, `NUCLEUS_PG_TEST_*` and `NUCLEUS_SQLITE_TEST_DATABASE` environment variables. One local configuration serves every repo. `ToString()` leaves the password out. |
| `NucleusTestDatabase` | `SqlServer`, `Postgres` and `Sqlite` (a `SqlServerTestDatabase`, a `PostgresTestDatabase` and a `SqliteTestDatabase`, whose `StartStackAsync` takes that provider's own options): `IsConfigured`, `SkipReason`, `StartSharedStackAsync` (a fixture's shared stack, booted under the schema lock), `StartStackAsync` (optionally with the provider's options changed), `StartRootedStackAsync` (a provider rooted at one entity type), `LockAsync(suite)` and `SchemaLockAsync()`, and raw SQL for a test's own setup: `ExecuteAsync`, `ScalarAsync`, `TableIdAsync`, `RowCountAsync` and the statements behind them (`RenameTableSql`, `DropTableIfExistsSql`, `CopyTableSql`, `DeleteAllSql`, `DropColumnsSql`). |
| `NucleusEntityTable` | `NucleusEntityTable.For(typeof(MyEntity))`: the table an entity's rows live in and its `[LegacyTableNames]`, computed without a database, for a test that pins them. `NucleusTestDatabase.MoveToLegacyNamesAsync(entities)` puts live tables back under their old names, so a test can prove the next start adopts them with their rows. |
| `NucleusTestStack` | A booted stack: `Provider`, `Manager` (an `IObjectDataManager`, what a store under test is given), `Log` (a `CapturingLoggerProvider`: the warnings and errors it logged) and `Services`. Dispose it. |
| `NucleusTestLock` | A cross-process lock held as an exclusively opened temp file, so the two target framework runs of a suite never reset each other's rows. Waits up to five minutes. |

Three things to know when using it:

- **A stack finds entities the way an application does,** from every assembly beside the entry
  assembly. A store's test project initialises the store's tables just by referencing the store.
- **The schema lock is not reentrant.** Take it once per test, around the whole change.
- **A stack does not publish the ambient runtime** that the static `CoreObject` API uses. Give the
  code under test the stack's `Manager`.

A PostgreSQL stack sets `MaxConnectionPoolSize` to 20 before the test's own options run, so a test
can still change it. At the provider default of 100, one process could take every connection the
server allows. The net8.0 and net10.0 runs of a suite share a server whose default
`max_connections` is 100, so overlapping runs failed with 53300 "too many clients".

## An xunit fixture

A fixture boots one stack for a collection of tests and turns "not configured" into a skip, so a
machine with no test database passes cleanly:

<!-- sample: testing-fixture -->
```csharp
// One stack for every test in a collection. xunit v3 shown; any framework's fixture has the same shape.
public sealed class DatabaseFixture : IAsyncLifetime
{
    private static NucleusTestDatabase Database => NucleusTestDatabase.Sqlite;   // or .SqlServer, or .Postgres
    private NucleusTestStack? _stack;

    public async ValueTask InitializeAsync()
    {
        if (Database.IsConfigured)
            _stack = await Database.StartSharedStackAsync();   // holds the schema lock while it starts
    }

    /// <summary>The manager, or a skipped test when no test database is configured.</summary>
    public IObjectDataManager RequireData()
    {
        Assert.SkipWhen(_stack is null, Database.SkipReason ?? "No test database is configured.");
        return _stack!.Manager;
    }

    public ValueTask DisposeAsync()
    {
        _stack?.Dispose();
        return ValueTask.CompletedTask;
    }
}
```
