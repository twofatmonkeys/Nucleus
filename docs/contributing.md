# Contributing

How the repository is laid out, built and tested. `AGENTS.md` holds the rules; this is the how-to.

## Repository layout

```
src\TFM.Nucleus.Abstractions\    contracts, no SQL
src\TFM.Nucleus\                 entity core
src\TFM.Nucleus.Data\            provider-neutral data layer
src\TFM.Nucleus.Data.SQL\        SQL Server provider
src\TFM.Nucleus.Data.PostgreSQL\ PostgreSQL provider
src\TFM.Nucleus.Data.SQLite\     SQLite provider
src\TFM.Nucleus.Testing\         live-database test support
tests\Shared\                    test support linked into every test project: source paths and the benchmark harness
tools\Run-Benchmarks.ps1         runs every benchmark suite in its own process, and gates or accepts the baselines
tests\TFM.Nucleus.Tests\         unit tests, no database
tests\TFM.Nucleus.IntegrationTests\  live database tests: every suite runs once per provider (SqlServer\, Postgres\ and Sqlite\ hold the subclasses and the engine-only tests)
tests\TFM.Nucleus.DocSamples\    the documentation's examples as code, and the tests that run them and hold the pages to them
docs\guide\                     the consumer documentation, one page per topic
```

## Build and test

```bash
dotnet build TFM.Nucleus.slnx --configuration Release
dotnet test  TFM.Nucleus.slnx --configuration Release
```

The unit tests need no database and cover golden-SQL snapshots (raw goldens pin every space of the SQL Server output), the public API snapshot of every package, expression translation, materialiser equivalence, the cache, multi-provider routing and the provider-seam tripwires. The two snapshots regenerate only when `NUCLEUS_ACCEPT_GOLDENS=1` is set, so a change to either is a reviewed diff. The integration tests run every suite once per provider. The SQL Server suites activate when `NUCLEUS_TEST_SERVER`, `NUCLEUS_TEST_DATABASE`, `NUCLEUS_TEST_USERNAME` and `NUCLEUS_TEST_PASSWORD` (or the matching `Nucleus:*` user secrets) point at a server and name a base database name (each .NET runtime uses its own, `<name>_net8` and `<name>_net10`, created at compatibility level 100 when missing, so the login needs to be able to create a database); the PostgreSQL suites when `NUCLEUS_PG_TEST_SERVER`, `NUCLEUS_PG_TEST_DATABASE`, `NUCLEUS_PG_TEST_USERNAME` and `NUCLEUS_PG_TEST_PASSWORD` (or `Nucleus:Postgres:*`) do; the SQLite suites when `NUCLEUS_SQLITE_TEST_DATABASE` (or `Nucleus:Sqlite:Database`) names a file path, which the provider creates; each .NET runtime gets its own file beside it (`nucleus.net8.db`, `nucleus.net10.db`), so the two framework runs never wait on each other's write lock. A provider without settings skips cleanly. A disposable PostgreSQL is one command away:

```bash
docker run -d --name nucleus-pg -e POSTGRES_USER=nucleus -e POSTGRES_PASSWORD=<password> -p 15432:5432 postgres:16
```

with `Nucleus:Postgres:Server` set to `localhost:15432`; the provider creates the database on the first run.

### Benchmarks

The benchmarks catch a regression on the save and read paths. They are opt-in: every benchmark skips unless `NUCLEUS_BENCHMARK=1` is set, so a normal `dotnet test` and CI never run them. A full run takes about 20 minutes, so run them for an architectural change (the save or load pipeline, the query builders, the schema model, the provider SDK, the entity base), not for each feature or bug fix. Each scenario's allocations and median time are compared with a committed baseline. Allocations are gated: they repeat to within about 1% from run to run, so a scenario fails when it allocates more than its baseline by more than the tolerance and the floor, and still does when it is measured once more. Time is reported, with its change against the baseline, but never fails: on a development database the same code measured 7 ms and 13 ms in back-to-back runs, so a stored baseline cannot hold it to 20%. Compare times by running the two builds one after the other in one session. A scenario with no baseline fails. Getting better never fails; the table says so and suggests accepting.

| Kind | Where | What it covers | Gated |
|---|---|---|---|
| Database | `tests/TFM.Nucleus.IntegrationTests/Benchmarks`: `SaveBenchmarks` and `ReadBenchmarks`, per provider | Saves (one at a time, a batch, the split sync path, the bulk engine, parent and child graphs inserted, updated, pruned and deleted, three levels, the save builder alone) and reads (flat, sync, streamed, paged, by ID, graphs with and without children, lazy collections listed, included and read, children read and counted by parent) | Allocations, against `Baselines/<machine>/`; time reported |
| Database-free | `tests/TFM.Nucleus.Tests/Benchmarks/InProcessBenchmarks.cs` | The save graph walk, child validation, the write and delete builders, binding rows to a graph, materialising, translating a predicate | Allocations, against `Baselines/any/`; time reported, against `Baselines/<machine>/` |

A baseline file is named `<suite>.<provider>.<framework>.<configuration>.json` (`inprocess.<framework>.<configuration>.json` for the database-free kind). The database baselines are filed per machine, because a driver's allocations and every time depend on the machine and its database; the database-free allocations are deterministic and are filed once for every machine. Allocations fail above 10% and 4 KB; a time is flagged in the table above 20% and 2 ms (`NUCLEUS_BENCHMARK_TOLERANCE` changes the percentage). Allocations are counted across the whole process, so only one benchmark measures at a time on a machine, whatever the process or provider, and a test stack boots only between measurements. Before measuring, a database suite deletes any rows a stopped run left and compacts the tables (`VACUUM` on PostgreSQL and SQLite), so a scan reads the same pages each time.

A database suite allocates 15-20% more for the same work after another suite's heavy work in the same process, so each database suite runs in a test process of its own. `tools/Run-Benchmarks.ps1` does that: it builds Release, then runs the database-free suite and every provider's Save and Read suite, each in its own process, for each framework, and exits non-zero if any fails. A second database suite in one process skips, naming the script, rather than measuring wrong.

```powershell
# Run and gate every benchmark.
./tools/Run-Benchmarks.ps1

# One provider, one suite, one framework.
./tools/Run-Benchmarks.ps1 -Provider Sqlite -Suite Save -Framework net10.0

# Write the baselines instead, after a change whose cost is deliberate. Review the diff and give
# the reason in the commit message.
./tools/Run-Benchmarks.ps1 -Accept
```

A new machine needs its own database baselines before its runs can pass: accept once on it. Benchmarks measure Release builds only, because Debug code allocates differently.

## Documentation examples

Every C# block in `README.md` and `docs/guide` is a copy of a `#region` in
`tests/TFM.Nucleus.DocSamples`, named by the `<!-- sample: name -->` line above the block. To add or
change an example:

1. Write it in the samples project, in the [sample domain](sample-domain.md), inside a
   `#region name`. Have `SampleTests` call it, so it runs and not only compiles.
2. Put `<!-- sample: name -->` and an empty C# block where the page should show it.
3. Copy the samples into the pages, then run the checks:

```powershell
$env:NUCLEUS_ACCEPT_GOLDENS = '1'
dotnet test tests/TFM.Nucleus.DocSamples --configuration Release --filter "FullyQualifiedName~DocSampleTests.Accept"
Remove-Item Env:NUCLEUS_ACCEPT_GOLDENS
dotnet test tests/TFM.Nucleus.DocSamples --configuration Release
```

`DocSampleTests` fails on a block with no sample above it, a block that differs from its sample
and a sample no page shows. The samples run against a SQLite file made in the temp folder, so
they need no configuration.
