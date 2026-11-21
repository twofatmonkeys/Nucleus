# Versioning

## The schema

```
MAJOR . YY . DDD . N
  1   . 26 . 241 . 3
```

| Part | Source | Notes |
| --- | --- | --- |
| `MAJOR` | `<VersionMajor>` in `Directory.Build.props` | The only part a human edits, and only for a breaking release. |
| `YY` | Two digit year, `Australia/Sydney` | |
| `DDD` | Day of year, 1 to 366, no leading zeros | 29 August 2026 is 241. |
| `N` | Revision within that day, from 1 | Highest existing tag for that day plus one. |

`1.26.241.3` is the third release cut on 29 August 2026. All four parts are numeric and within the
16 bit limit MSBuild puts on `AssemblyVersion` components, so the same string serves as
`AssemblyVersion`, `FileVersion` and the package version.

This is not semver. It is a build stamp: the number says when something shipped, not what changed in
it. Breaking changes are communicated by bumping `VersionMajor` and by the release notes, not by the
number alone.

## How CI derives it

`.github/workflows/tag-and-release.yml`, steps "Read repo settings" and "Compute version", on every
push to `master`:

1. Read `VersionMajor` from `Directory.Build.props` with `dotnet msbuild -getProperty:VersionMajor`. This
   is an evaluation, not a build, so it runs before restore. A missing or non numeric value fails
   the run rather than silently producing a nonsense version.
2. Compute `YY` and `DDD` from the runner clock with `TZ=Australia/Sydney` set at job level.
3. List the existing tags matching `v<MAJOR>.<YY>.<DDD>.*`, strip the prefix, keep the purely
   numeric remainders, take the highest, add one. No tags for today gives `N=1`.
4. Pass the result to build, test and pack as `-p:VersionPrefix=<version>`.

`Directory.Build.props` derives `AssemblyVersion` and `FileVersion` from `VersionPrefix`, and the
SDK derives the package version from it, so one command line property stamps everything
consistently. Passing the version only to `pack` would produce a package labelled one version with
an assembly inside it labelled another.

## Why nothing is committed back

The tag history is the record of what shipped. Rewriting a version into `Directory.Build.props` and
pushing that commit to `master` would mean a bot commit per release, a push that re-triggers the
release workflow unless guarded, and a merge conflict on that line for anyone with a branch open.

The cost is that a local build does not know the version it would be released as. That is deliberate:
`VersionPrefix` falls back to `<VersionMajor>.0.0.0`, so a dev build is visibly a dev build and can
never be mistaken for a released one.

## Concurrency

The revision counter reads the tag list and writes a new tag, which is a race if two runs overlap.
The workflow declares:

```yaml
concurrency:
  group: release-${{ github.ref }}
  cancel-in-progress: false
```

so two pushes landing seconds apart are serialised and the second sees the first one's tag.

The checkout uses `fetch-depth: 0`. A shallow clone carries no tags, and the counter would restart
at 1 on every run and collide with the existing release.

## Multi-package repos

Every package in a repo shares one version, taken from the one `VersionMajor` and the one run. That
is a change from the older multi-project repos, where each project carried its own `AssemblyVersion`
maintained by a Visual Studio extension and they drifted apart. One version per repo means the
release notes, the tag and the packages all agree, and a consumer pinning
`TFM.Widgets.Server 1.26.241.3` knows exactly which `TFM.Widgets.Core` it was built against.

The cost is that a package with no changes still gets republished on a new version. `--skip-duplicate`
makes that harmless, and the alternative was four versions nobody could reason about.

## Bumping the major

Edit `<VersionMajor>` in `Directory.Build.props` and push. The next release is
`<newmajor>.YY.DDD.1`, because no tag exists yet for that major on that day. Do it for a breaking
change to the public surface, and say what broke in the release notes.

## Tags and branches

Each release creates:

- a tag `v<version>`, on the released commit, carrying the GitHub release and the `.nupkg` files;
- a branch `release/v<version>`, pointing at the same commit.

The branch is prefixed rather than named `v<version>` because a branch and a tag sharing a name make
every later `git checkout v<version>` ambiguous. Pushing the ref directly is also idempotent, so a
re-run cannot fail the way `git checkout -b` would.
