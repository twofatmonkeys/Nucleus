# Providers

The same entities and queries run on SQL Server, PostgreSQL and SQLite. An application picks its
database by picking a provider package and registering it; nothing else in its code names the
engine. This page lists what an application can see differ, then each provider's options.

| | SQL Server | PostgreSQL | SQLite |
|---|---|---|---|
| Package | `TFM.Nucleus.Data.SQL` | `TFM.Nucleus.Data.PostgreSQL` | `TFM.Nucleus.Data.SQLite` |
| Suits | A server database | A server database, version 16 or later | One file on a local disk |
| Equality on text | Follows the collation, which by default ignores case | Exact | Exact |
| Full-text search | `CONTAINS()` | A GIN index | None: a substring match |
| Change notifications | Yes, with Service Broker enabled | Yes, with no setup | No |
| Bulk engine | `SqlBulkCopy` | COPY BINARY | None: commands in turn |
| `[TableIndex]` included columns | Yes | Yes | Keys only |

## SQL Server

SQL Server is the engine Nucleus was first written for, so the rest of this guide describes its
behaviour unless it says otherwise.

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

## PostgreSQL

`TFM.Nucleus.Data.PostgreSQL` runs the same entities and queries on PostgreSQL 16 or later.

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

Everything in this guide applies to it, except where a section says otherwise. These are the
differences an application can see:

- **Equality is case-sensitive.** `x.Code == "abc"` and unique indexes compare exactly, as
  PostgreSQL always does; on SQL Server's default collation they ignore case. The pattern
  predicates `StartsWith`, `EndsWith`, `Contains` and `Like` use `ILIKE`, so they ignore case on
  both. `string.Equals(x, y, StringComparison.OrdinalIgnoreCase)` ignores case on both.
- **`DateTimeOffset` is stored as UTC, to the microsecond.** A `timestamptz` keeps the instant but
  not the offset, and no ticks below a microsecond, so a value saved at +10:00 reads back as the
  same instant at +00:00. `DateTimeOffset` equality compares instants, so comparisons hold.
- **Full-text search uses each engine's own rules.** `string.Contains` on a `FullTextSearch`
  column is a full-text search on both providers: `CONTAINS()` on SQL Server, and
  `to_tsvector('english', column) @@ plainto_tsquery(...)` over a GIN index on PostgreSQL, so which
  words match can differ. On any other column it is a substring match on both.
- **Long names are shortened.** PostgreSQL keeps 63 bytes of an identifier. A table, index or
  trigger name longer than that keeps its first 30 and last 23 bytes around eight hex characters
  of its SHA-256, the same way every time, so the schema check finds what it created.
- **Change notifications need no server setup.** Initialisation creates a statement-level trigger
  per table that notifies the table's channel, and `SubscribeToChangesAsync<T>()` listens on it
  with a connection of its own, reconnecting after a dropped connection (and reporting one change,
  since a notification sent while it was down is lost). `ContextOptions.WatchForChanges` is for an
  engine whose notifications are registered by the reading connection, and has no effect here.
- **Bulk writes use COPY.** An oversized insert or update batch is copied into a temp table with
  COPY BINARY and written with one statement; an update applies each row's own changed columns.
- **Values the server sets use `clock_timestamp()`**, so each statement stamps its own time rather
  than the transaction's start.
- **Paging is `LIMIT`/`OFFSET`**, ordered by `ClusterID` when the query asks for no order and
  after the query's own order to break its ties. NULL is placed explicitly (`NULLS FIRST`
  ascending, `NULLS LAST` descending), so it sorts below every value as on the other engines.

The schema is `public` unless `PostgresConfiguration.Schema` says otherwise, and is created when
missing. Each table carries the Guid `ID` primary key, an identity `ClusterID` that keeps a
rebuild's copy in insertion order and orders an unordered page, with a unique index
(`CIX_<table>_ClusterID`) that the first start adds to a table without one, and a column per `[DatabaseColumn]` property. A table that only
lacks columns has them added in place, and one whose column types change is rebuilt, as on SQL
Server, in one transaction: PostgreSQL's DDL is transactional, so a failed update leaves nothing
behind. Adding a column, `IsDeleted` with its constant default included, is a catalog-only change.

## SQLite

`TFM.Nucleus.Data.SQLite` runs the same entities and queries on a SQLite database file, for
desktop, edge and other single-machine applications. It uses the SQLite bundled with
Microsoft.Data.Sqlite and refuses to start on one older than 3.35.

<!-- sample: register-sqlite -->
```csharp
services.AddNucleusSqliteDataProvider(options =>
{
    options.Database = @"C:\ProgramData\PortlyPrimate\sanctuary.db";   // created with its folder when missing
});
```

Everything in this guide applies to it, except where a section says otherwise. These are the
differences an application can see:

- **The database is one file on a local disk.** `Database` is its path, created with its folder on
  initialise when it is missing and the schema is writable. An in-memory database is refused: it
  ends with its connection, and Nucleus keeps no connection between operations. So is any `file:`
  URI, one that names a file on disk included. Keep the file off
  network shares, where SQLite's locking, and write-ahead logging, are not safe.
- **One connection writes at a time.** Initialise puts the file in write-ahead log mode
  (`UseWriteAheadLog`), so reads run while another connection writes. A save begins its transaction
  IMMEDIATE, taking the write lock at once, and any other writer, in this process or another,
  waits up to `BusyTimeoutSeconds` (3 by default) for it. SQLite does not queue its waiters, so a
  writer in one process can wait out its timeout while another process writes back to back; give
  each process its own file, or one process the writing, when several write heavily. A write too
  large for one command runs its commands one after another on its own transaction, so it still
  commits or rolls back whole. There is no bulk engine; none is needed in-process.
- **No change notifications.** SQLite tells no other connection about a write, so
  `SubscribeToChangesAsync<T>()` throws `NotSupportedException` and `Capabilities` says so.
- **No full-text index.** `string.Contains` on a `FullTextSearch` column is a substring match
  without regard to case, not a word search, and no full-text index is created.
- **Case folding matches the other engines.** SQLite's own `LIKE` and `lower()` fold ASCII only, so
  Nucleus registers small functions on every connection it opens and its pattern terms
  (`StartsWith`, `EndsWith`, `Contains`, `Like`) and case-insensitive equality fold case across
  Unicode, as they do on SQL Server and PostgreSQL. Equality is exact, as on PostgreSQL. The SQL
  Nucleus writes therefore runs only on its own connections; the file itself is plain SQLite, which
  any tool reads and writes.
- **Values are stored in fixed text forms.** SQLite keeps only integers, reals, text and blobs. A
  `Guid` is 36 lower-case characters; a `DateTime` is `yyyy-MM-dd HH:mm:ss.fffffff`; a
  `DateTimeOffset` is UTC in the same form with `+00:00` (the instant is kept, the offset is not, as
  on PostgreSQL); a date is `yyyy-MM-dd`; a `bool` is 0 or 1. The forms are fixed width, so ordering
  and comparing them orders and compares the values. Values the server sets (`Timestamp`,
  `DeletedAt`) are written in the same forms, to the tick.
- **A `decimal` is stored as a real.** SQLite has no exact decimal type, so a value keeps about 15
  significant digits and is read back rounded to the column's scale. A value that needs more digits,
  or exact sums, belongs on another provider or in a `long` of minor units.
- **A string's length is not enforced.** `varchar(255)` is kept as the declared type, so the schema
  check sees a changed length, but SQLite stores a longer string as it is.
- **A `[TableIndex]` has no included columns.** SQLite indexes carry keys only, so `Include` is
  ignored and the index is created over its keys. A unique `[TableIndex]` enforces exactly its keys,
  as on the other engines.
- **Unique violations name columns, not indexes.** SQLite reports the columns a write broke, and the
  provider maps them back to the column's `UQ_` index or the unique `[TableIndex]` with those keys,
  so `UniqueViolationException` reads the same as on the other engines.
- **Paging is `LIMIT`/`OFFSET`** (`LIMIT -1` when only a skip is given), ordered by `rowid` when the
  query asks for no order and after the query's own order to break its ties.

Each table carries the Guid `ID` primary key and a column per `[DatabaseColumn]` property, with no
`ClusterID`: the table's rowid keeps its insertion order, which a rebuild's copy orders by. SQLite
cannot change a column's type, so a table that only lacks columns has them added in place, one
`ALTER TABLE ADD COLUMN` each, and any other change rebuilds it, in one transaction: SQLite's DDL is
transactional, so a failed update leaves nothing behind.

## Provider options

Most options are shared: every provider's configuration derives from `RelationalProviderOptions`.
Each table below lists what its provider accepts.

The defaults fail fast and alike on every provider: nothing waits more than 3 seconds (for a free
connection, a connection to open, or SQLite's write lock) before the failure goes back to the
caller, who decides whether to wait longer or retry. They suit an average application; a busy one
raises them.

No connection enlists in an ambient transaction. A transactional context opened inside a
`TransactionScope` fails with a `DataContextException` of kind `AmbientTransaction`, because its own
transaction would commit even if the scope rolled back. Reads, which are not transactional, run
inside a scope as before.

### SQLConfiguration

| Option | Default | Meaning |
|---|---|---|
| `Server` | none | `host`, `host:port` or `host\instance`. |
| `Database` | none | Initial catalog. |
| `Username`, `Password` | none | SQL authentication credentials. |
| `UseIntegratedSecurity` | `false` | Use Windows authentication and ignore the credentials above. |
| `ConnectionString` | none | A raw connection string that bypasses the builder, which is how Azure AD and managed identity are configured. `Database` is still used for schema introspection. Its `Max Pool Size` is replaced with `MaxConnectionPoolSize` and its `Enlist` with `false`. |
| `MaxConnectionPoolSize` | `100` | The most connections the provider holds to its database. Its open contexts and its change subscriptions share it, and the driver's pool is sized to it, also when `ConnectionString` is set. When all are held, a new context or subscription waits `PoolWaitTimeoutSeconds` and then fails with `PoolExhausted`. |
| `PacketSize` | `1500` | Network packet size. |
| `MaxThreadsForLargeQuery` | `12` | Obsolete and ignored. An oversized write the bulk engine cannot take runs its commands one after another on its own transaction. |
| `MaxParametersPerCommand` | `2000` | The most parameters one command carries: a batch over it is split, and a larger insert or update batch goes to the bulk engine. |
| `PoolWaitTimeoutSeconds` | `3` | How long opening a context or a change subscription on the asynchronous path waits for a free connection under `MaxConnectionPoolSize` before it fails with a `DataContextException` of kind `PoolExhausted`. `0` fails at once; the synchronous path never waits. It is not a statement timeout; that is `StatementTimeoutSeconds`. `CommandTimeoutSeconds` is its obsolete name. |
| `StatementTimeoutSeconds` | `30` | The time limit of each read, write and delete a context runs, the driver's own default. A statement over it fails with a `DatabaseException` of kind `Timeout`. A context can take its own (`ContextOptions.StatementTimeoutSeconds`). `0` means no limit; a negative value is refused. See [Time limits](saving.md#time-limits). |
| `ConnectAttempts` | `3` | Attempts at opening a connection (a context's, a change subscription's, or one the schema check reads the catalog on) when the failure is transient, the first included, with an exponential back-off. Every attempt shares the connect timeout, so retries never make an open wait longer than it: a quick failure, such as a refused connection while the server restarts, is retried within it. |
| `RetryAttempts` | `1` | Attempts at an operation the manager runs on a context of its own (a save, a delete, a load, a row read) when it fails transiently having applied nothing, the first included, with the same back-off. `1` attempts each once, so the policy is off until set. Never applied on a caller-owned context. See [Retries](saving.md#retries). |
| `SchemaCommandTimeoutSeconds` | `900` | The command timeout of each schema statement at initialise: a rebuild's row copy, an added column, an index created or dropped. `0` means no limit; a negative value is refused. |
| `BulkTimeoutSeconds` | `60` | Timeout for bulk copies and bulk statements. |
| `BulkBatchSize` | `50000` | The most staged rows one bulk INSERT or UPDATE statement writes; a larger batch runs as several statements in the same transaction. Must be 1 or more. |
| `ConnectTimeoutSeconds` | `3` | How long opening a connection waits before it fails. A busy server can take longer than 3 seconds to finish a login, and the open then fails with a connection timeout that is not retried; raise it for a server under load. `0` waits indefinitely; a negative value is refused. Ignored when `ConnectionString` is set. |
| `ReadCommittedSnapshotOnCreate` | `true` | Turn on `READ_COMMITTED_SNAPSHOT` for a database the provider creates, right after creating it, so its reads take the last committed version of a row instead of waiting behind a save. An existing database is never changed; initialise logs a warning when it has the setting off. If the setting cannot be turned on, the database is left as created, with a warning. |
| `TableNaming` | `TableNamingPolicy.FullName` | How the tables of entities that name neither their table nor their policy are named: `FullName` or `ObjectName(levels)`. See [Table names](schema.md#table-names). |
| `RenameFullNameTables` | `false` | Rename a missing table's full-name form to its configured name instead of creating it empty. See [Table names](schema.md#table-names). |
| `RequireCommit` | `false` | Roll back a transactional context disposed without `Commit` or `CommitAsync`. When off, such a context commits and logs a warning. v3 makes rolling back the only behaviour. See the known limits. |

Every option except `UseIntegratedSecurity`, `PacketSize`, `BulkBatchSize`,
`ConnectTimeoutSeconds` and `ReadCommittedSnapshotOnCreate` is one of the shared ones.

Connections are built with `TrustServerCertificate`, MARS enabled and 4 connect retries. A raw
`ConnectionString` keeps its own settings for those, for `PacketSize` and for the connect timeout;
only its pool size and `Enlist` are replaced. The
driver's connect retries reconnect a connection that broke while idle; they do not retry an open.
`ConnectAttempts` does, for an open whose error number the provider classifies as transient (a
network error, a server failing over or paused); SqlClient itself never says an error is transient.

`AppName` on a provider's options is not a setting: it is filled from `NucleusOptions.AppName` at
registration, replacing any value set on the provider, and is sent to SQL Server and PostgreSQL as
the application name when the provider builds the connection string.

A provider's options are ordinary .NET options, so `services.Configure<...>` after the registration
layers more settings on, here over the registration that reads the environment:

<!-- sample: register-layered -->
```csharp
// Server, database, user name and password from SQL_SERVER, SQL_DATABASE, SQL_USERNAME and SQL_PASSWORD.
services.AddNucleusSqlServerDataProvider();
// The other options are layered on with the options pattern. A later Configure wins.
services.Configure<SQLConfiguration>(options =>
{
    options.RetryAttempts = 3;
    options.RequireCommit = true;
});
```

This reaches the provider registered without a root type. A provider registered for a root type
(`AddNucleusSqlServerDataProvider<TRoot>(...)` or the `Type` overload) keeps its options under a
name of its own, which a plain `Configure` does not reach: put its settings in its registration.
Do not set `AppName` in a later `Configure`; it runs after Nucleus fills the name and would
replace it.

### PostgresConfiguration

| Option | Default | Meaning |
|---|---|---|
| `Server` | none | `host` or `host:port`. |
| `Port` | `5432` | The port, when `Server` does not give one. |
| `Database` | none | The database. Created on initialise when missing and the schema is writable, through the `postgres` maintenance database. |
| `Username`, `Password` | none | Credentials. |
| `Schema` | `public` | The schema the tables live in. Created when missing. |
| `SslMode` | driver default | `Disable`, `Allow`, `Prefer`, `Require`, `VerifyCA` or `VerifyFull`. |
| `ConnectionString` | none | A raw Npgsql connection string that bypasses the builder. `Database` still names the database to create and read. Its `Maximum Pool Size` is replaced with `MaxConnectionPoolSize` and its `Enlist` with `false`. |
| `ConnectAttempts` | `3` | As for SQL Server, for a failure the driver or the server's SQLSTATE marks transient. `ConnectRetryCount` is its obsolete name. |
| `RetryAttempts` | `1` | As for SQL Server. |
| `ConnectTimeoutSeconds` | `3` | How long opening a connection waits before it fails, retries included. `0` waits indefinitely; a negative value is refused. Ignored when `ConnectionString` is set, whose own `Timeout` is used the same way. |
| `MaxConnectionPoolSize` | `100` | The most connections the provider holds to its database. Its open contexts and its change subscriptions share it, and the driver's pool is sized to it, also when `ConnectionString` is set. When all are held, a new context or subscription waits `PoolWaitTimeoutSeconds` and then fails with `PoolExhausted`. |
| `MaxThreadsForLargeQuery` | `12` | As for SQL Server: obsolete and ignored. |
| `MaxParametersPerCommand` | `30000` | The most parameters one command carries (PostgreSQL allows 65,535): a batch over it is split, and a larger insert or update batch goes to the bulk engine. |
| `PoolWaitTimeoutSeconds` | `3` | As for SQL Server. |
| `StatementTimeoutSeconds` | `30` | As for SQL Server. |
| `SchemaCommandTimeoutSeconds` | `900` | As for SQL Server. |
| `BulkTimeoutSeconds` | `60` | Timeout for each COPY and bulk statement. |
| `TableNaming` | `TableNamingPolicy.FullName` | As for SQL Server. |
| `RenameFullNameTables` | `false` | As for SQL Server. |
| `RequireCommit` | `false` | As for SQL Server. |

### SqliteConfiguration

| Option | Default | Meaning |
|---|---|---|
| `Database` | none | The path of the database file. A relative path is resolved against the current directory. Created with its folder on initialise when missing and the schema is writable. |
| `ConnectionString` | none | A raw Microsoft.Data.Sqlite connection string; its data source is the file when `Database` is not set. The provider still sets the file, the open mode and the busy timeout. |
| `Password` | none | Refused: the bundled SQLite does not encrypt, and ignoring a password would leave the file readable. |
| `Server`, `Username` | none | Ignored. |
| `UseWriteAheadLog` | `true` | Puts the file in write-ahead log mode on initialise, so reads run while one connection writes. The mode is kept in the file. |
| `BusyTimeoutSeconds` | `3` | How long a command waits for another connection's write lock before it fails with a transient `DatabaseException` of kind `Timeout`. Raise it when several connections or processes write heavily. It is SQLite's only time limit. |
| `StatementTimeoutSeconds` | `30` | Ignored. The driver's command timeout is the wait for the write lock, which stays `BusyTimeoutSeconds`; nothing limits a running statement, and a cancelled token does not stop one under way. |
| `ConnectAttempts` | `3` | As for SQL Server, for an open that finds the file locked (SQLITE_BUSY or SQLITE_LOCKED). A missing or unreadable file is not retried. |
| `RetryAttempts` | `1` | As for SQL Server. A write that waited out `BusyTimeoutSeconds` is transient, so under the policy it waits again. |
| `MaxConnectionPoolSize` | `100` | The most contexts open at once. |
| `MaxParametersPerCommand` | `2000` | The most parameters one command carries; a larger write is split into commands run one after another on one transaction. SQLite allows 32,766 a statement, but a command of many statements binds slower as it grows, and holds the write lock while it does. |
| `PoolWaitTimeoutSeconds` | `3` | How long an async context waits for a free slot under `MaxConnectionPoolSize`, as for SQL Server. |
| `SchemaCommandTimeoutSeconds` | `900` | Ignored, like `StatementTimeoutSeconds`: a schema statement waits `BusyTimeoutSeconds` for the write lock and then runs until it finishes. |
| `BulkTimeoutSeconds` | `60` | Ignored: there is no bulk engine. |
| `TableNaming` | `TableNamingPolicy.FullName` | As for SQL Server. |
| `RenameFullNameTables` | `false` | As for SQL Server. |
| `RequireCommit` | `false` | As for SQL Server. |

## Stores for other libraries

The stores that keep other libraries' data in Nucleus tables live with the library whose contract
they implement, so a contract change ships with its store instead of forcing a Nucleus release:

| Package | Repo | Store |
|---|---|---|
| `TFM.Bindery.Nucleus` | [TFM.Bindery](https://github.com/twofatmonkeys/TFM.Bindery) | `NucleusBinderyStore`, the CMS's page store |
| `TFM.S3Library.Nucleus` | [TFM.S3Library](https://github.com/twofatmonkeys/TFM.S3Library) | `NucleusS3AssetStore`, the media library's asset metadata |
| `TFM.Stencil.Nucleus` | [TFM.Stencil](https://github.com/twofatmonkeys/TFM.Stencil) | `NucleusTemplateStore`, Mailroom and Paperwork templates |
| `TFM.Attribution.Nucleus` | [TFM.Attribution](https://github.com/twofatmonkeys/TFM.Attribution) | `NucleusAttributionStore`, marketing touches and sales |

Until November 2026 they shipped from this repo as `TFM.Nucleus.Bindery`, `TFM.Nucleus.S3Library`,
`TFM.Nucleus.Stencil` and `TFM.Nucleus.Attribution`. Their entities now pin their tables with
`[TableNaming(TableNamingStyle.ObjectName, 1)]` and list the old full-name tables in
`[LegacyTableNames]`, so a site moving to the new packages keeps its rows. Each repo tests its
store against the same databases through `TFM.Nucleus.Testing`.
