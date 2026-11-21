# Publishing

## Default: GitHub Packages

Every package publishes to `https://nuget.pkg.github.com/twofatmonkeys/index.json`. The release job
authenticates with the automatic `secrets.GITHUB_TOKEN`, so there is nothing to configure in a new
repo beyond the `packages: write` permission the workflow already declares.

`--skip-duplicate` is passed on every push: an already published version is skipped on the 409
rather than failing the run.

## Optional: nuget.org

Set `<PublishToNuGetOrg>true</PublishToNuGetOrg>` in `Directory.Build.props`. The release workflow
reads it at run time and enables two extra steps, both gated on it.

It lives in the props file rather than the workflow so the workflow files stay byte identical in
every package repo. Anything absent or not exactly `true` (case insensitive) means GitHub Packages
only, so a repo that predates the setting fails in the safe direction.

Publishing uses NuGet trusted publishing: the job's OIDC token is exchanged for a short lived API
key, so there is no long lived nuget.org secret sitting in the repo. It needs:

1. The `id-token: write` permission, which the workflow already declares.
2. A trusted publishing policy on nuget.org, created under **Account, then Trusted Publishing**,
   naming the repository owner (`twofatmonkeys`), the repository, and the workflow file name
   `tag-and-release.yml` on its own with no path.

Without step 2 the `NuGet/login` step fails with an authorisation error, which is the intended
behaviour: nothing reaches the public feed until someone has explicitly allowed it.

Note that a new policy on a private repository starts out **temporarily active for 7 days**. It
becomes permanent on the first successful publish, and lapses if none happens in that window,
because nuget.org needs the repository and owner IDs from a real token to pin the policy against a
delete-and-recreate attack. The window can be restarted from the nuget.org UI.

### The nuget.org username

The workflow passes `user: ${{ secrets.NUGET_USER || github.repository_owner }}`.

Our nuget.org profile name and our GitHub organisation name are both `twofatmonkeys`, so the
repository owner is the right value and no secret is needed. A nuget.org username is not
confidential in any case: it is printed under **Owners** on every package page.

The `NUGET_USER` secret is kept only as an override. Set it if a repo ever sits under a different
GitHub owner than the nuget.org account that publishes it, or if the two names stop matching. If it
is not set it is an empty string, which is falsy, so the expression falls through to the repository
owner.

Microsoft's own sample for `NuGet/login` recommends a secret here. That advice is about not
hardcoding an identity into a template rather than about confidentiality, and deriving the value
achieves the same thing with no per-repo setup. The failure mode if the two names ever diverge is a
failed login at release time, not a package published to the wrong account, because nuget.org
validates the policy against the repository owner, repository and workflow file independently of
this input.

A package on nuget.org is public and effectively permanent. Unlisting hides a version; it does not
remove it. Turn this on deliberately, not by default.

### Prereleases on nuget.org

`PublishToNuGetOrg` covers stable releases from `master` only. Alpha and beta packages need a
second switch:

```xml
<PublishToNuGetOrg>true</PublishToNuGetOrg>
<PublishPrereleaseToNuGetOrg>true</PublishPrereleaseToNuGetOrg>
```

Both must be `true`. `PublishPrereleaseToNuGetOrg` on its own does nothing, so a package that is
internal can never reach the public feed through its beta branch. The "Read repo settings" step
prints both values and the feeds the run will actually push to.

Leave it `false` unless consumers outside GitHub Packages really need early builds. Every push to
`alpha` or `beta` would add another public, permanent version.

The existing trusted publishing policy already covers prereleases, because it names the workflow
file and not a branch. No second policy is needed.

## Restoring our packages

`nuget.config` declares both feeds and reads the GitHub Packages credential from environment
variables, because GitHub Packages requires authentication even for reads.

**Locally**, set the two variables once, for example in a PowerShell profile:

```powershell
$env:GITHUB_USER  = 'your-github-username'
$env:GITHUB_TOKEN = 'ghp_...'   # classic PAT, read:packages scope only
```

A classic personal access token is required: GitHub Packages for NuGet does not accept fine grained
tokens. Give it `read:packages` and nothing else. Never commit it, and never paste it into
`nuget.config`.

**In CI**, the workflows set both from the job context, so no secret is needed.

This is also why the workflows do not pass `source-url` to `actions/setup-dotnet`. That input
rewrites `nuget.config` on the runner and would take the package source mapping with it.

## Package source mapping

```xml
<packageSource key="github">
  <package pattern="TFM.*" />
</packageSource>
<packageSource key="nuget.org">
  <package pattern="*" />
</packageSource>
```

`TFM.*` resolves from our own feed only. Without the mapping, NuGet queries every source and takes
the best match, so anyone who registers a `TFM.Something` on nuget.org could get their code into our
builds. That is dependency confusion, and the mapping closes it.

If a TFM package genuinely lives on nuget.org and not on GitHub Packages, add that one package as an
explicit pattern under `nuget.org`:

```xml
<package pattern="TFM.Widgets" />
```

Do not widen the `TFM.*` rule to cover it.

## What ends up in the package

- The assembly for each target framework, with embedded pdb and SourceLink metadata.
- The XML documentation file.
- `README.md`, `LICENSE.txt`, `icon.png` (at the package root).

No `.snupkg`. Symbols are embedded, so a separate symbols package would be empty and GitHub Packages
would reject it anyway.

To check before pushing:

```powershell
dotnet pack --configuration Release -p:VersionPrefix=1.26.241.1 --output .\artifacts
unzip -l .\artifacts\TFM.Widgets.1.26.241.1.nupkg
```

## Releases

Each successful run from `master` creates a `v<version>` tag, a `release/v<version>` branch and a
GitHub release. The release body lists the packages published and the short commit, followed by
GitHub's generated change list, and the `.nupkg` files are attached as assets. Attaching them means
a package can be retrieved from the release even if a feed is unavailable.

A run from `beta` or `alpha` creates the tag and a GitHub release marked as a prerelease, with the
same body and assets, but no `release/` branch. See `docs/versioning.md`.
