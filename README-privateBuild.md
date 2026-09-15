# Private/snapshot build notes (cyclonedx-cli)

This repo is a long-lived fork of [CycloneDX/cyclonedx-cli](https://github.com/CycloneDX/cyclonedx-cli)
that consumes a sibling fork of the library, `../cyclonedx-dotnet-library`.
As of 2026-08-27, branch `privateBuild/20260827-rebase` in **both** repos
was reset to `upstream/main` (CLI: `v0.33.1`; library: `v12.1.2` — six
major versions ahead of the `v0.25.0`/`v6.0.0` fork point this project was
previously built against) and the fork's custom capability was re-built on
top of current upstream: the `rename-entity` command, and (library-side) a
from-scratch, idiomatic re-implementation of the configurable multi-BOM
merge engine the fork's `BomEntity`+`Merge.cs` provided. See
`../cyclonedx-dotnet-library/README-privateBuild.md` for what changed
there and why it's not a literal port.

The two repos remain separate git checkouts connected only through NuGet
packages — this repo's `src/cyclonedx/cyclonedx.csproj` pulls
`CycloneDX.Utils`/`CycloneDX.Spdx.Interop` as `<PackageReference>`s, not
project references. Building this repo alone with plain `dotnet build`
restores the real, unmodified `12.1.2` packages from `nuget.org` and
produces a CLI **without any of this fork's changes** — it builds and runs,
just not with `rename-entity` available or the new merge behavior active.
Producing a real snapshot requires building the library first, packing it
under a version that only exists locally, and pointing this repo at that
version — same mechanism as before, detailed below.

Verified end-to-end on 2026-08-27 (branch `privateBuild/20260827-rebase` in
both repos).

## 0. Prerequisites: .NET 8/10 SDKs, not just 6/7

This repo now targets `net10.0` only (no more `net6.0`). This machine had
only the .NET 6/7 SDKs installed system-wide. `winget install
Microsoft.DotNet.SDK.8`/`.10` hung indefinitely on a UAC prompt nothing
could answer non-interactively; worked around with a per-user install (no
admin needed) via Microsoft's install script:

```powershell
Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile "$env:TEMP\dotnet-install.ps1" -UseBasicParsing
& "$env:TEMP\dotnet-install.ps1" -Channel 8.0 -InstallDir "$env:LOCALAPPDATA\dotnet-custom" -NoPath
& "$env:TEMP\dotnet-install.ps1" -Channel 10.0 -InstallDir "$env:LOCALAPPDATA\dotnet-custom" -NoPath
```

`setx`/persisting the PATH change via `[Environment]::SetEnvironmentVariable`
does **not** reach already-running shells (only new logon sessions), so
every command in this session (and every example below) needs:

```sh
export PATH="$LOCALAPPDATA/dotnet-custom:$PATH"
export DOTNET_ROOT="$LOCALAPPDATA/dotnet-custom"
```

## 1. Build & pack the library snapshot

Full detail is in `../cyclonedx-dotnet-library/README-privateBuild.md`.
Short version:

```sh
cd ../cyclonedx-dotnet-library
LIBVER=12.1.2.3-privateBuild.20260902
dotnet build CycloneDXLibrary.sln -c Debug
dotnet pack CycloneDXLibrary.sln -c Debug -p:Version="$LIBVER"
for P in src/CycloneDX.Core/bin/Debug/CycloneDX.Core.$LIBVER.nupkg \
         src/CycloneDX.Utils/bin/Debug/CycloneDX.Utils.$LIBVER.nupkg \
         src/CycloneDX.Spdx/bin/Debug/CycloneDX.Spdx.$LIBVER.nupkg \
         src/CycloneDX.Spdx.Interop/bin/Debug/CycloneDX.Spdx.Interop.$LIBVER.nupkg ; do
  dotnet nuget push "$P" -s userhome
done
```

`userhome` is a folder-based NuGet feed (`C:\Users\klimov\.nuget`)
registered and enabled in `%APPDATA%\NuGet\NuGet.Config` on this machine.
On a fresh machine, register it first: `dotnet nuget add source
"C:\Users\<you>\.nuget" --name userhome`.

## 2. Build the CLI against the snapshot

`src/cyclonedx/cyclonedx.csproj` routes the library package version through
one overridable MSBuild property (re-added this session after the branch
reset wiped the version committed in the prior one):

```xml
<CycloneDXLibraryVersion Condition="'$(CycloneDXLibraryVersion)' == ''">12.1.2</CycloneDXLibraryVersion>
...
<PackageReference Include="CycloneDX.Utils" Version="$(CycloneDXLibraryVersion)" />
<PackageReference Include="CycloneDX.Spdx.Interop" Version="$(CycloneDXLibraryVersion)" />
```

Plain `dotnet build` still defaults to the published `12.1.2`. To build
against the snapshot from step 1:

```sh
dotnet build cyclonedx-cli.sln -c Debug -p:CycloneDXLibraryVersion=12.1.2.1-privateBuild.20260827
```

## 3. Verify you actually got the snapshot, and smoke-test it

```sh
grep "CycloneDX.Utils/" src/cyclonedx/obj/project.assets.json   # should show the private version, not 12.1.2
```

Then the real thing — merge and rename-entity against fixture BOMs (both
confirmed working end-to-end this session):

```sh
dotnet src/cyclonedx/bin/Debug/net10.0/cyclonedx.dll merge \
  --input-files tests/cyclonedx.tests/Resources/Merge/sbom1.json tests/cyclonedx.tests/Resources/Merge/sbom2.json \
  --output-file /tmp/merged.json

dotnet src/cyclonedx/bin/Debug/net10.0/cyclonedx.dll rename-entity \
  --input-file <a bom with bom-ref "lib-old" and a dependsOn referencing it> \
  --old-ref lib-old --new-ref lib-new --output-file /tmp/renamed.json
# -> lib-old's bom-ref AND the dependency's back-reference both become
#    lib-new; SerialNumber/Timestamp/Tools refreshed.
```

## 4. Known test baseline

`dotnet test cyclonedx-cli.sln -p:CycloneDXLibraryVersion=$LIBVER`:
**132/132 passed** (129 pre-existing + 3 `RenameEntityTests`). Library
side: `CycloneDX.Utils.Tests` 46/46 passed; `CycloneDX.Core.Tests` has ~300
pre-existing Protobuf-only failures unrelated to this work (see the
library's `README-privateBuild.md` §5) — likely this machine missing
`protoc`, not a regression.

## 5. What's actually different, and what's still not covered

See `../cyclonedx-dotnet-library/README-privateBuild.md` §3 for the full
design rationale (interfaces + default methods instead of a base class)
and its "Fixed after initial review" section for real behavior
differences a second look caught (not just missing features) — most
notably, the default scope-conflict resolution initially landed backwards
and has since been corrected. CLI-visible summary of `merge`'s surface:

- `--component-conflict-resolution
  <KeepSeparate|Squash_UpgradeScope|Squash_DowngradeScope|
  Squash_RenameByScope>` (default `Squash_UpgradeScope`) selects how two
  equivalent-but-not-identical components (e.g. differing only by `Scope`)
  are reconciled. `Squash_RenameByScope` keeps both as distinct entries,
  suffixed `:scope=<value>`, with every back-reference rewritten to match,
  instead of squashing them together or leaving an ambiguous duplicate
  bom-ref — verified end-to-end with two BOMs where the same component is
  `required` in one and `excluded` in the other.
- `--input-files-list <file>...` / `--input-files-nul-list <file>...` add
  filenames (one per line, or 0x00-separated) from one or more list files
  to `--input-files`, to exceed OS/shell command-line length or
  argument-count limits when merging many BOMs.
- `--validate-output` / `--validate-output-relaxed` validate the merged
  document against its own spec version before writing; strict mode
  refuses to write on failure, relaxed mode writes anyway (for
  troubleshooting) but still reports failure via the exit code.
- `--strip-empty-lists` (also on `convert`) omits empty list properties
  (`"licenses": []`, `"dependsOn": []`, `"provides": []`, a `Pedigree`'s
  `"variants": []`, etc.) anywhere in the document instead of writing them
  out — schema-valid either way (none of these are `required` or carry
  `minItems` in the 1.4–1.7 schemas), purely to cut clutter. Matters most
  when the output spec version equals the library's current version:
  `BomUtils.GetBomForSerialization` serializes that case without a copy,
  so empty lists survive as-is; for every older target version the
  Protobuf-based deep copy `CopyBomAndDowngrade` already collapses them to
  `null` on its own (proto3 can't tell "empty repeated field" from
  "absent"), so the flag is a no-op there. Implemented as
  `CycloneDXUtils.CleanupEmptyListsDeep` in the library (a recursive
  counterpart to the top-level-only `CleanupEmptyLists` merge already
  applies unconditionally) — see
  `../cyclonedx-dotnet-library/README-privateBuild.md`. For `merge`, it
  runs before `--validate-output` so the validated content matches what
  actually gets written.
- Passing a BOM subject (`--group`/`--name`/`--version`) into a flat merge
  now links it into the dependency graph the same way a hierarchical merge
  already did (a synthetic `<dependency ref="subject"><dependsOn>` entry
  linking it to each source BOM's own component) — previously this only
  set `Metadata.Component`, silently dropping that linkage for flat merges.
- The merged document now always carries `Metadata.Tools` (via
  `Bom.BomMetadataUpdate`/`BomMetadataReferThisToolkit`) recording this
  library and the running program, matching what `rename-entity` already
  did; previously `merge` only stamped `Version`/`SerialNumber`/`Timestamp`
  and left `Tools` untouched.
- A `Metadata.Component` that's auto-selected from an input BOM (no
  explicit subject given) and also happens to duplicate an entry already
  in the merged `Components` list is now detected and merged/evicted
  (`CleanupMetadataComponent`), and now-empty top-level lists (e.g. an
  empty `vulnerabilities: []` from BOMs that had none) are dropped
  (`CleanupEmptyLists`) rather than serialized as noise.
- Not covered: CLI-level exposure of the *other* `MergeStrategy` toggles
  (`UseEntityMerge`, `RenameConflictingComponents`,
  `MergeSubsetDependencies`, `TreatDependencyAsExtraProperty`, the
  `DoBomMetadataUpdate*` group — all still hardcoded via `Default()`).

## 6. Bumping this repo's own version

Unchanged from before: `semver.txt` isn't wired into the assembly at
build time (only `release.yml`'s `dotnet publish .../p:Version=$(cat
semver.txt)` uses it). Pass `-p:Version=` explicitly for a private
build/publish, e.g. `-p:Version=0.33.1.1-privateBuild.20260827`, mirroring
the library's `-privateBuild.<date>` suffix convention.
