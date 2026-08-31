# AGENTS.md

Repo level notes for anyone, human or agent, working in this repo.

## What this repo is

The Nucleus entity framework: a custom ORM for .NET, shipped as four packages that version and
release together.

| Package | Source | Role |
|---|---|---|
| `TFM.Nucleus.Abstractions` | `src\TFM.Nucleus.Abstractions` | Contracts and the neutral query model. Zero SQL. |
| `TFM.Nucleus` | `src\TFM.Nucleus` | Entity core: `CoreObject`, schema reflection, change tracking. |
| `TFM.Nucleus.Data` | `src\TFM.Nucleus.Data` | Provider-neutral data layer. Zero SQL. |
| `TFM.Nucleus.Data.SQL` | `src\TFM.Nucleus.Data.SQL` | SQL Server provider. |

Until August 2026 these lived in four separate repositories, with the shared tests and the only
real documentation in a fifth unversioned folder (`Nucleus.v2`). They were always released in
lockstep on a single version, so they are now one repo. The old folders are superseded; do not
edit them, and do not reintroduce the conditional package-or-project reference pattern they used.

Built on the TFM NuGet package scaffold. The layout, the MSBuild property split, the version
schema and the workflows are the shared TFM standard and are documented in `docs/`.

## Architecture

Dependency graph: `Abstractions` <- (`Nucleus`, `Data`) <- `Data.SQL`. Direction is enforced by
project references, and the "no SQL in the neutral layers" rule is enforced by
`ProviderSeamTests`, which scans the compiled string literals of the neutral assemblies.

`Abstractions` holds every interface the other layers speak: `ICoreObject`, `IDataProvider`,
`IDataContext`, `IObjectSchema`, `IQuery` and the neutral query tokens, plus `NucleusOptions`, the
`DataSet` row buffer and the collection types. Nothing in it knows what a database is.

`TFM.Nucleus` owns the entity model. `CoreObject` keeps committed and pending property
dictionaries rather than fields, which is what makes change tracking and partial updates possible.
`ObjectSchema` and `PropertySchema` reflect an entity type once and cache it, emitting IL
materialisers that construct entities without running constructors. The static API on `CoreObject`
is a thin forwarder over `NucleusRuntime.Current`, which `AddNucleusData()` publishes.

`TFM.Nucleus.Data` orchestrates. `ObjectDataManager` implements both `IObjectDataManager` and
`INucleusRuntime`, so the static and instance surfaces share one code path. `ObjectDataAdaptor<T>`
is a real LINQ provider; `ExpressionParser` lowers expression trees to the neutral query model;
`DataProviderRegistry` routes each entity type to the provider of its nearest registered root,
which is how one application spans several databases.

`TFM.Nucleus.Data.SQL` renders the neutral model as T-SQL and owns every SQL string in the
solution, along with schema create, compare and migrate, the bulk engine, and SqlDependency
notifications.

## Build and test

```powershell
dotnet build TFM.Nucleus.slnx --configuration Release
dotnet test  TFM.Nucleus.slnx --configuration Release --no-build
```

Restore needs no credentials: every `TFM.*` dependency is now an in-repo project reference and the
rest come from nuget.org. `nuget.config` still reads `GITHUB_USER` and `GITHUB_TOKEN` for
consistency with the other TFM repos.

Warnings become errors only when `ContinuousIntegrationBuild=true`. To reproduce what CI enforces:

```powershell
dotnet build TFM.Nucleus.slnx --configuration Release -p:ContinuousIntegrationBuild=true
```

The integration tests need a disposable SQL Server database, configured through the
`NUCLEUS_TEST_SERVER`, `NUCLEUS_TEST_DATABASE`, `NUCLEUS_TEST_USERNAME` and
`NUCLEUS_TEST_PASSWORD` environment variables or the matching `Nucleus:*` user secrets. They skip
cleanly when none is configured, which is what happens in CI.

## Deviations from the scaffold

Both are deliberate and both are recorded in `tests\Directory.Build.props`:

- The tests use **xunit.v3 2.0.0**, not the scaffold's xunit 2.9.3. They were written against v3.
- The test projects build **net8.0 only** and at **LangVersion 12.0**, while the shipping packages
  multi-target `net8.0;net10.0` at `latest`. Both pins exist because of real library limitations,
  not test defects, and both are described in `docs\code-review-findings.md`. Remove them when the
  underlying issues are fixed.

The repo also carries a `NoWarn` list in `Directory.Build.props` covering roughly 500 pre-existing
warnings, so that CI has a green baseline to protect. That list is a backlog, not an exemption:
shrink it as the code is cleaned up.

## Rules

- **Do not put SQL in `Abstractions`, `TFM.Nucleus` or `Data`.** The provider seam is the whole
  reason a second database engine is possible. `ProviderSeamTests` will fail if you do.
- **Do not move a shared property into a csproj.** The three level props split (repo, `src`,
  `tests`) is the standard. A csproj carries package identity, description, tags, dependencies and
  nothing else.
- **Do not hardcode a version anywhere.** `VersionMajor` in `Directory.Build.props` is the only
  version value in the repo. CI supplies the rest. See `docs/versioning.md`. The four packages
  share one version by design.
- **Pin package references per target framework**, 8.0.x for `net8.0` and 10.0.x for `net10.0`, so
  consumers get assemblies built for their runtime rather than a lowest common denominator.
- **Keep the workflow files free of configuration.** They are byte identical in every TFM package
  repo and read what they need from `Directory.Build.props` at run time (`VersionMajor`,
  `PublishToNuGetOrg`, `TargetFrameworks`). If something seems to need a knob in the workflow, add
  the property to `Directory.Build.props` and read it instead.
- **Evolve the public surface additively.** Two production sites already consume these packages,
  and `CoreObject` is the base class of every entity in them. A breaking change to the entity base,
  an attribute, or a registration method is a major version and a coordinated release.
- **Do not add a `NoWarn` entry to hide new code.** The existing list covers code that predates the
  scaffold. New code compiles clean.
- **Update `README.md` in the same commit** as any change to the public surface. It is packed into
  all four packages and is the reference documentation consumers see on the feed.
- **No em dashes in authored documentation.** Use hyphens.
