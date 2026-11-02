# Development

[Back to README](../README.md)

## Requirements

- Windows 10/11 x64 for the WPF app and Windows integration checks.
- .NET 8 SDK or a newer SDK capable of targeting .NET 8.
- PowerShell for the build/helper scripts.
- Git for repository checks and GitHub CLI only if publishing releases yourself.

The repository intentionally includes the audited upstream core executable, public bootstrap, seed list, and license. You do not need an installed Psiphon GUI to build or run this version. The shipped application is unsigned; code signing and a Windows installer are not currently configured.

## Layout

```text
src/
  PsiSplit.App/            WPF app, dialogs, tray integration and assets
  PsiSplit.Core/
    Models/               App configuration, site rules and app profiles
    Services/             Core lifecycle, proxies, PAC and persistence
    Resources/Psiphon/    Unmodified upstream binary and public bootstrap
  PsiSplit.SelfTest/       Local and isolated integration checks
docs/
  images/                 Sanitized captures of the offline preview
  examples/               A generic, portable example profile
tools/
  check-privacy.ps1        Audit tracked/staged files before publication
  import-psiphon-core.ps1  Read-only import from an official distribution
  generate-icon.ps1       Regenerate the existing application assets
publish.ps1               Publish a standalone build to a separate output
```

`bin/`, `obj/`, `artifacts/`, `dist/`, IDE settings, logs, runtime profiles, and proxy snapshots are ignored. `Directory.Build.props` maps checkout paths to a neutral source prefix so build metadata does not embed a developer's local checkout path.

## Build and run

Run all commands from the repository root:

```powershell
dotnet restore PsiSplit.sln
dotnet build PsiSplit.sln -c Release --no-restore
dotnet run --project src/PsiSplit.App -c Release --no-build
```

For UI inspection without touching a running session:

```powershell
dotnet run --project src/PsiSplit.App -c Release -- --preview
```

Preview uses dummy app profiles with environment-variable paths and no live traffic. Close the preview window directly when finished. It does not minimize to a tray icon.

## Testing

| Command suffix | Scope | System proxy changes |
| --- | --- | --- |
| `--local` | Domain matching, PAC JavaScript, defaults, notice parsing, bundle integrity, startup guards, and a fake HTTP upstream. | None. |
| `--isolated` or no suffix | Local core checks plus real HTTP/SOCKS, Full/Filtered routing, detach/reattach, simultaneous cores, cancellation, and preservation checks. | None. |
| `--system-proxy` | Explicit opt-in to the older system PAC, shell, and browser integration path. Refuses an active PsiSplit/Psiphon session. | Temporary PAC changes with restoration. |

```powershell
dotnet run --project src/PsiSplit.SelfTest -c Release -- --local
dotnet run --project src/PsiSplit.SelfTest -c Release -- --isolated
```

Use a normal Windows environment that permits loopback sockets and Windows Script Host. A restricted runner can block those facilities independently of application correctness. Isolated network tests also require working access to the Psiphon network and the test destination.

Each test run creates its own temporary profile. The isolated suite only stops its own engine handles. Test logs can contain local paths, process identifiers, or network details; do not commit them. The `artifacts/` directory is reserved for local verification material.

## Publish

```powershell
./publish.ps1

# Use an independent destination for a release/review while another build runs.
./publish.ps1 -OutputDirectory dist/review
```

The script creates a self-contained, compressed single-file Windows executable and copies the third-party notices/license alongside it. The application itself includes the engine resources. Keep the notices in distributed archives.

Publishing does not stop or restart any process. It refuses a running output executable and uses a unique intermediate publish directory. The default output is `dist/standalone`; existing builds elsewhere are left alone.

To skip restore, first restore for the runtime being published:

```powershell
dotnet restore PsiSplit.sln -r win-x64
./publish.ps1 -NoRestore
```

## Refresh the upstream engine

Use an official Psiphon Windows distribution you already trust. The helper reads its extracted core, generated core configuration, and distribution seed list. If the default locations are unavailable, pass the three file paths explicitly:

```powershell
./tools/import-psiphon-core.ps1

# Explicit paths can be assembled without hardcoding a local user name.
./tools/import-psiphon-core.ps1 `
  -CorePath (Join-Path $env:LOCALAPPDATA 'Temp/psiphon-tunnel-core.exe') `
  -ConfigPath (Join-Path $env:LOCALAPPDATA 'Psiphon3/psiphon.config') `
  -ServerListPath (Join-Path $env:LOCALAPPDATA 'Psiphon3/server_list.dat')
```

The import does not launch the GUI, stop it, change its registry, or copy its live datastore. It runs only the imported binary's immediate-exit `--version` command. The configuration is built from a field allowlist: public channel/sponsor identifiers, public signature keys, list download locations, the client version, TLS compatibility option, and selected exit region. Account authorizations, opaque overrides, migration paths, upstream credentials, and network identifiers are omitted.

Before importing a new revision, obtain its license text from the corresponding upstream source. After import, update `THIRD-PARTY-NOTICES.md`, verify the manifest hashes and source links, and rerun both test suites. The manifest includes exact file hashes; resource files are stored byte-for-byte in Git to preserve those hashes.

## CI

The Windows workflow uses pinned revisions of the official checkout, .NET setup, and artifact upload actions. It checks tracked content for privacy issues, builds the solution, runs `--local`, and publishes a portable build artifact. It does not apply a PAC or depend on a live public tunnel connection.

Releases contain a zip with the app and third-party notices. GitHub also provides the repository source at each release tag; the corresponding Psiphon source is linked next to the engine's version and license information.

## Before committing

```powershell
git add .
./tools/check-privacy.ps1
git diff --cached --stat
git diff --cached --check
```

The privacy check reports file names and rule categories without printing matched secrets. Review images visually as well: a text scan cannot reliably find information rendered inside a screenshot. Use a GitHub no-reply address for commits if you do not want to publish a personal email address.
