# Conventions

## Naming

| Thing | Rule | Sample |
| --- | --- | --- |
| Repo folder | Exactly the package id. No spaces, no `.NET` suffix. | `TFM.Widgets` |
| GitHub repo | Same as the folder. | `twofatmonkeys/TFM.Widgets` |
| Package id | `TFM.` plus the area. Add a segment for a role. | `TFM.Widgets`, `TFM.Widgets.Abstractions` |
| Project folder | Exactly the package id. | `src/TFM.Widgets/` |
| Assembly and root namespace | Exactly the package id. | `TFM.Widgets` |
| Test project | Shipping project plus `.Tests`. | `tests/TFM.Widgets.Tests/` |
| Solution | Primary package id plus `.slnx`. | `TFM.Widgets.slnx` |
| Registration method | `Add` plus the area, no `TFM` prefix. | `services.AddWidgets(...)` |
| Options class | Area plus `Options`. | `WidgetOptions` |
| Options section | `TFM:<Area>`. | `"TFM:Widgets"` |
| Exception type | Area plus `Exception`. | `WidgetException` |

Older repos are named inconsistently (spaces, a `.NET` suffix). New ones use the
package id and nothing else, so the folder, the repo, the solution, the assembly and the package all
read the same.

### Spellings that are fixed

Two abbreviations are spelled one way in member names, and new members follow them:

- Identity is `ID`: `LoadByID`, `ParentIDPropertyName`.
- Database is `DB`: `IsDBColumn`, `OnAfterLoadFromDB`.

A few older members spell them otherwise (`TryGetById`, `DbDataContext`, `DatabaseColumnAttribute`).
They stay as they are, and nothing is given a second name to match.

### Folders inside a project

`Abstractions`, `Configuration`, `Extensions`, `Models`, `Services`. Add more when a real grouping
appears, not in anticipation of one. Interfaces live in `Abstractions`, next to nothing else.

## The public surface

- One `public interface` per capability, in `Abstractions`.
- The implementation is `internal sealed` and registered by the `Add` extension. A consumer never
  news it up, so the transport, the retry policy and the caching can all change without a major
  version bump.
- `InternalsVisibleTo` lists real test projects only. Every entry is a hole in the assembly boundary.
- Transport exceptions do not leak. Wrap them in the package's own exception type and keep the
  original on `InnerException`.
- Every public member that is not self explanatory gets an XML doc comment. `GenerateDocumentationFile`
  is on, so the docs ship in the package and show up in the consumer's IntelliSense.

## Where a property belongs

| Level | File | Holds |
| --- | --- | --- |
| Repo | `Directory.Build.props` | Target frameworks, language settings, version schema, ownership, repository URL, SourceLink, pack output path. |
| Shipping projects | `src/Directory.Build.props` | `IsPackable`, doc generation, README, licence and icon packing, symbol policy. |
| Test projects | `tests/Directory.Build.props` | `IsPackable` false, `IsTestProject`, the xunit and test SDK references. |
| One package | `src/<Package>/<Package>.csproj` | `PackageId`, `Title`, `RootNamespace`, `Description`, `PackageTags`, dependencies, `InternalsVisibleTo`, embedded resources. |

`tests/Directory.Build.props` imports the repo root file directly, not `src/Directory.Build.props`,
so no packaging property can reach a test project by accident.

If you find yourself copying a property between two csproj files, it belongs one level up.

## Build settings that matter

- **`TreatWarningsAsErrors` is conditional on `ContinuousIntegrationBuild`.** A local build shows
  warnings but stays runnable, so an SDK bump that adds a new analyzer cannot block someone mid
  change. CI still refuses to publish anything that warns.
- **`DebugType` is `embedded`, and there is no `.snupkg`.** Symbols travel inside the assembly, so
  a consumer gets a usable stack trace with no symbol server involved. GitHub Packages does not
  accept snupkg pushes anyway, and a symbols package built from an embedded pdb build is empty
  (`NU5017`).
- **SourceLink is on** via `PublishRepositoryUrl` and `EmbedUntrackedSources`. The .NET SDK provides
  the GitHub provider, so no package reference is needed. Combined with
  `ContinuousIntegrationBuild=true` in CI, "go to definition" on our types lands on the real source
  at the exact commit that was published.
- **`Nullable` and `ImplicitUsings` are enabled everywhere.** A package that ships without nullable
  annotations pushes the warnings onto its consumers.

## Multi-targeting

`net8.0;net10.0`. Package references are pinned per target framework:

```xml
<PackageReference Include="Microsoft.Extensions.Http" Version="8.0.1" Condition="'$(TargetFramework)' == 'net8.0'" />
<PackageReference Include="Microsoft.Extensions.Http" Version="10.0.0" Condition="'$(TargetFramework)' == 'net10.0'" />
```

A single 8.0 reference used for both would work, but it forces a net10.0 consumer to carry the older
assemblies for no reason. Test projects multi-target too, so both builds are actually exercised
rather than only compiled. The release workflow reads `TargetFrameworks` from every project under `src` and `tests` and installs
the matching runtimes, so changing this list needs no workflow edit.

For an ASP.NET Core only package, drop the `Microsoft.Extensions.*` references and use
`<FrameworkReference Include="Microsoft.AspNetCore.App" />` instead.

## Testing

xunit, one test project per shipping project, multi-targeted. Hand written test doubles over a
mocking framework where the behaviour under test is about call counts and exception types, because
a real handler states the scenario more plainly than mock configuration does.

Cover the failure paths, not only the happy one: timeouts, retries and their exhaustion,
cancellation, malformed responses, and configuration that should be rejected at startup.

## Documentation

- `README.md` is the package README. It is packed into the `.nupkg` and is what the feed shows, so
  it is written for a consumer: install, register, use, and what happens when things go wrong.
- `docs/` holds the deeper material, one file per area.
- `AGENTS.md` records the architecture and the decisions behind it, and is updated in the same
  commit as any change to the module layout or the public interfaces. It is the one place repo
  guidance is written.
- `CLAUDE.md` is a pointer to `AGENTS.md` and nothing else. Claude Code reads `CLAUDE.md` by
  convention, so the file has to exist, but duplicating the content in it would guarantee the two
  drift apart. Other agents read `AGENTS.md` directly.
