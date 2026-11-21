# Providers

What differs between SQL Server, PostgreSQL and SQLite, and each provider's options.

## SQL Server

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

`TFM.Nucleus.Data.PostgreSQL` runs the same entities and queries on PostgreSQL 16
or later. Everything in this document applies on both providers, except where a section says
otherwise. These are the differences an application can see:

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
- **Paging is `LIMIT`/`OFFSET`**, ordered by ID when the query asks for no order.

The schema is `public` unless `PostgresConfiguration.Schema` says otherwise, and is created when
missing. Each table carries the Guid `ID` primary key, an identity `ClusterID` that keeps a
rebuild's copy in insertion order, and a column per `[DatabaseColumn]` property. A table that only
lacks columns has them added in place, and one whose column types change is rebuilt, as on SQL
Server, in one transaction: PostgreSQL's DDL is transactional, so a failed update leaves nothing
behind. Adding a column, `IsDeleted` with its constant default included, is a catalog-only change.

## SQLite

`TFM.Nucleus.Data.SQLite` runs the same entities and queries on a SQLite database file, for
desktop, edge and other single-machine applications. It uses the SQLite bundled with
Microsoft.Data.Sqlite and refuses to start on one older than 3.35. Everything in this document
applies to it, except where a section says otherwise. These are the differences an application can
see:

<!-- sample: register-sqlite -->
```csharp
services.AddNucleusSqliteDataProvider(options =>
{
    options.Database = @"C:\ProgramData\PortlyPrimate\sanctuary.db";   // created with its folder when missing
});
```

- **The database is one file on a local disk.** `Database` is its path, created with its folder on
  initialise when it is missing and the schema is writable. An in-memory database is refused: it
  ends with its connection, and Nucleus closes a connection after each read. Keep the file off
  network shares, where SQLite's locking, and write-ahead logging, are not safe.
- **One connection writes at a time.** Initialise puts the file in write-ahead log mode
  (`UseWriteAheadLog`), so reads run while another connection writes. A save begins its transaction
  IMMEDIATE, taking the write lock at once, and any other writer, in this process or another,
  waits up to `BusyTimeoutSeconds` (30 by default) for it. SQLite does not queue its waiters, so a
  writer in one process can wait out its timeout while another process writes back to back; give
  each process its own file, or one process the writing, when several write heavily. A write too large for one command runs
  its commands one after another on its own transaction, so it still commits or rolls back whole.
  There is no bulk engine; none is needed in-process.
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
- **Paging is `LIMIT`/`OFFSET`** (`LIMIT -1` when only a skip is given), ordered by ID when the query
  asks for no order.

Each table carries the Guid `ID` primary key and a column per `[DatabaseColumn]` property, with no
`ClusterID`: the table's rowid keeps its insertion order, which a rebuild's copy orders by. SQLite
cannot change a column's type, so a table that only lacks columns has them added in place, one
`ALTER TABLE ADD COLUMN` each, and any other change rebuilds it, in one transaction: SQLite's DDL is
transactional, so a failed update leaves nothing behind.

## Provider options

### SQLConfiguration

| Option | Default | Meaning |
|---|---|---|
| `Server` | none | `host`, `host:port` or `host\instance`. |
| `Database` | none | Initial catalog. |
| `Username`, `Password` | none | SQL authentication credentials. |
| `UseIntegratedSecurity` | `false` | Use Windows authentication and ignore the credentials above. |
| `ConnectionString` | none | A raw connection string that bypasses the builder, which is how Azure AD and managed identity are configured. `Database` is still used for schema introspection. |
| `MaxConnectionPoolSize` | `100` | The most contexts open at once, and the size of each of the driver's two connection pools (transactional and not), so the server can see up to twice this many connections from one process. See the known limits. |
| `PacketSize` | `1500` | Network packet size. |
| `MaxThreadsForLargeQuery` | `12` | The most parallel contexts an oversized write without a table schema fans out to. |
| `MaxParametersPerCommand` | `2000` | The most parameters one command carries: a batch over it is split, and a larger insert or update batch goes to the bulk engine. |
| `PoolWaitTimeoutSeconds` | `10` | How long opening a context on the asynchronous path waits for a free slot under `MaxConnectionPoolSize` before it fails with a `DataContextException` of kind `PoolExhausted`. `0` fails at once; the synchronous path never waits. It is not a command timeout: queries and saves use the driver's default of 30 seconds. `CommandTimeoutSeconds` is its obsolete name. |
| `SchemaCommandTimeoutSeconds` | `900` | The command timeout of each schema statement at initialise: a rebuild's row copy, an added column, an index created or dropped. `0` means no limit; a negative value is refused. |
| `BulkTimeoutSeconds` | `60` | Timeout for bulk copies and bulk statements. |
| `BulkBatchSize` | `50000` | The most staged rows one bulk INSERT or UPDATE statement writes; a larger batch runs as several statements in the same transaction. Must be 1 or more. |
| `ConnectTimeoutSeconds` | `3` | How long opening a connection waits before it fails. A busy server can take longer than 3 seconds to finish a login, and the open then fails with a connection timeout that is not retried; raise it for a server under load. `0` waits indefinitely; a negative value is refused. Ignored when `ConnectionString` is set. |
| `TableNaming` | `TableNamingPolicy.FullName` | How the tables of entities that name neither their table nor their policy are named: `FullName` or `ObjectName(levels)`. See [Table names](schema.md#table-names). |
| `RenameFullNameTables` | `false` | Rename a missing table's full-name form to its configured name instead of creating it empty. See [Table names](schema.md#table-names). |

Every option except `UseIntegratedSecurity`, `PacketSize`, `BulkBatchSize` and `ConnectTimeoutSeconds` comes from `RelationalProviderOptions`, which every relational provider's configuration shares. Connections are built with `TrustServerCertificate`, MARS enabled and 4 connect retries. The driver's connect retries reconnect a connection that broke while idle; they do not retry an open that times out.

### PostgresConfiguration

| Option | Default | Meaning |
|---|---|---|
| `Server` | none | `host` or `host:port`. |
| `Port` | `5432` | The port, when `Server` does not give one. |
| `Database` | none | The database. Created on initialise when missing and the schema is writable, through the `postgres` maintenance database. |
| `Username`, `Password` | none | Credentials. |
| `Schema` | `public` | The schema the tables live in. Created when missing. |
| `SslMode` | driver default | `Disable`, `Allow`, `Prefer`, `Require`, `VerifyCA` or `VerifyFull`. |
| `ConnectionString` | none | A raw Npgsql connection string that bypasses the builder. `Database` still names the database to create and read. |
| `ConnectAttempts` | `3` | Attempts at opening a connection when the failure is transient, the first included, with an exponential back-off. `ConnectRetryCount` is its obsolete name. |
| `ConnectTimeoutSeconds` | `15` | How long opening a connection waits before it fails. `0` waits indefinitely; a negative value is refused. Ignored when `ConnectionString` is set. |
| `MaxConnectionPoolSize` | `100` | The most contexts open at once, and the size of each of the driver's two connection pools (transactional and not), so the server can see up to twice this many connections from one process. See the known limits. |
| `MaxThreadsForLargeQuery` | `12` | As for SQL Server. |
| `MaxParametersPerCommand` | `30000` | The most parameters one command carries (PostgreSQL allows 65,535): a batch over it is split, and a larger insert or update batch goes to the bulk engine. |
| `PoolWaitTimeoutSeconds` | `10` | How long an async context waits for a free slot under `MaxConnectionPoolSize`, as for SQL Server. |
| `SchemaCommandTimeoutSeconds` | `900` | As for SQL Server. |
| `BulkTimeoutSeconds` | `60` | Timeout for each COPY and bulk statement. |
| `TableNaming` | `TableNamingPolicy.FullName` | As for SQL Server. |
| `RenameFullNameTables` | `false` | As for SQL Server. |

### SqliteConfiguration

| Option | Default | Meaning |
|---|---|---|
| `Database` | none | The path of the database file. A relative path is resolved against the current directory. Created with its folder on initialise when missing and the schema is writable. |
| `ConnectionString` | none | A raw Microsoft.Data.Sqlite connection string; its data source is the file when `Database` is not set. The provider still sets the file, the open mode and the busy timeout. |
| `Password` | none | Refused: the bundled SQLite does not encrypt, and ignoring a password would leave the file readable. |
| `Server`, `Username` | none | Ignored. |
| `UseWriteAheadLog` | `true` | Puts the file in write-ahead log mode on initialise, so reads run while one connection writes. The mode is kept in the file. |
| `BusyTimeoutSeconds` | `30` | How long a command waits for another connection's write lock before it fails. |
| `MaxConnectionPoolSize` | `100` | The most contexts open at once. |
| `MaxParametersPerCommand` | `2000` | The most parameters one command carries; a larger write is split into commands run one after another on one transaction. SQLite allows 32,766 a statement, but a command of many statements binds slower as it grows, and holds the write lock while it does. |
| `PoolWaitTimeoutSeconds` | `10` | How long an async context waits for a free slot under `MaxConnectionPoolSize`, as for SQL Server. |
| `SchemaCommandTimeoutSeconds` | `900` | As for SQL Server. |
| `TableNaming` | `TableNamingPolicy.FullName` | As for SQL Server. |
| `RenameFullNameTables` | `false` | As for SQL Server. |

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
