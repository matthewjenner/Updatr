# Updatr - Technical Architecture

## Stack

- **Language:** C# on .NET 10
- **GUI:** Avalonia (Fluent theme) + CommunityToolkit.Mvvm source generators
- **CLI:** System.CommandLine
- **winget:** WinGet COM API (`Microsoft.Management.Deployment`), CLI text parsing only as a
  fallback when COM activation fails
- **Store apps / App Installer bootstrap:** WinRT `Windows.Management.Deployment.PackageManager`
- **Registry / services / tasks / restore points:** `Microsoft.Win32.Registry`,
  `System.ServiceProcess`, Task Scheduler COM, `SRSetRestorePoint` P/Invoke
- **Signature checks:** `WinVerifyTrust` P/Invoke + signer subject inspection
- **Serialization:** System.Text.Json (source-generated contexts, stable lowercase `$type`
  discriminators for polymorphic rules)
- **Tests:** xUnit v3 on Microsoft.Testing.Platform + AwesomeAssertions (Apache-2.0 fork of
  FluentAssertions 7; FluentAssertions 8 is commercially licensed)

All package versions are verified against their registries when added (see the global rules);
the default is the latest stable.

## Hard rules

- **No PowerShell.** Nothing in `Src/` starts `powershell.exe` or `pwsh.exe` or hosts a
  PowerShell runspace. Enforced by a convention test.
- **Shell out only to an ecosystem's own CLI** (`npm`, `pipx`, `pip`, `dotnet`, `code`) and to
  installers. All of it goes through one `IProcessRunner`.
- **Every user action is a Core operation.** GUI ViewModels and CLI commands are adapters.
  Enforced by a parity test (every operation has a CLI command).
- **Core is pure.** No Avalonia, no WinRT, no P/Invoke, no file paths in `Updatr.Core`.
- **ASCII only, US English.** Enforced by convention tests (US-English test ported from Klakr).

## Project structure

```
Updatr/
+-- Src/
|   +-- Updatr.Core/              Pure logic, net10.0, no platform deps
|   |   +-- Catalog/              CatalogApp, Template, HoldRule, DefluffRule models, loader
|   |   +-- Carts/                Cart, CartMerger, DependencyResolver
|   |   +-- Operations/           One class per user action + OperationRunner, events, results
|   |   +-- Sources/              IPackageSource, PackageRef, InstalledPackage, Outcome
|   |   +-- Defluff/              IDefluffTarget, rule evaluation, drift detection, history
|   |   +-- Preflight/            IPreflightCheck, check results, ordering
|   |   +-- Errors/               ErrorCodes (decoded hint table), ErrorInfo
|   |   +-- Diagnostics/          DiagLog (typed emit surface, no UI dispatch)
|   |   +-- Abstractions/         IProcessRunner, IDataStore, IDownloader, IElevation, IClock
|   +-- Updatr.Platform/          Windows implementations, net10.0-windows10.0.x
|   |   +-- WinGet/               COM source + text fallback
|   |   +-- Ecosystems/           Npm, Pipx, Pip, DotnetTool, PsGallery, VsCode sources
|   |   +-- DirectUrl/            Downloader, signature verification, mirror hook
|   |   +-- Appx/                 PackageManager wrapper, App Installer bootstrap
|   |   +-- Defluff/              Registry / Appx / service / task targets, backup, restore point
|   |   +-- Uninstall/            Uninstall-key reader, (v0.2) leftover scanner, quarantine
|   |   +-- Elevation/            Broker launcher, named-pipe client and server
|   |   +-- Preflight/            Concrete checks
|   |   +-- Data/                 Portable data folder (IDataStore), SelfUpdate
|   +-- Updatr.App/               Avalonia GUI (Updatr.exe)
|   |   +-- ViewModels/  Views/  Converters/  Assets/
|   +-- Updatr.Cli/               Console (updatr-cli.exe), no UI deps
+-- Tests/
|   +-- Updatr.Core.Tests/        Unit tests, fakes for every abstraction
|   +-- Updatr.Platform.Tests/    Integration tests, opt-in (added with the first Platform code)
+-- catalog/                      apps.json, templates.json, holds.json, defluff.json, schema/ (Phase 1)
+-- Docs/  Scripts/  .github/workflows/
+-- Directory.Build.props         Version source of truth + shared Windows TFM / RID properties
+-- Directory.Build.targets       Keeps the WinGet .winmd beside the exe on publish
+-- Directory.Packages.props      Central package versions
+-- global.json                   SDK pin + test runner
```

The split from Klakr's two-project layout exists because the CLI and the GUI both need the
Windows implementations. `Updatr.Platform` is that shared layer.

## Operations: the parity mechanism

```csharp
public interface IOperation<TRequest, TResult>
{
    Task<TResult> RunAsync(TRequest request, IOperationSink sink, CancellationToken ct);
}
```

`IOperationSink` receives typed events: step started, progress (percent / bytes / throughput),
log line, item outcome, needs-confirmation, needs-elevation. The GUI's sink marshals to the UI
thread with `Dispatcher.UIThread.Post` and updates ViewModels; the CLI's sink renders to the
console or emits JSON lines. Operations never know which one they are talking to.

Examples: `ListPackages`, `UpgradePackages`, `ReinstallPackages`, `SetHold`, `InstallCart`,
`MergeCarts`, `SaveCart`, `DefluffStatus`, `DefluffApply`, `DefluffRevert`, `Uninstall`,
`RunPreflight`, `SelfUpdate`.

## Package sources

```csharp
public interface IPackageSource
{
    string Id { get; }                         // "winget", "npm", "pipx", ...
    Task<SourceAvailability> DetectAsync(CancellationToken ct);
    IAsyncEnumerable<InstalledPackage> ListInstalledAsync(CancellationToken ct);
    Task<PackageOutcome> InstallAsync(PackageRef p, InstallOptions o, IOperationSink s, CancellationToken ct);
    Task<PackageOutcome> UpgradeAsync(...);
    Task<PackageOutcome> UninstallAsync(...);
    // Reinstall = Uninstall + Install by default; sources may override (winget --force).
}
```

`PackageOutcome` is `Ok | AlreadyCurrent | Failed | Skipped`, plus decoded detail and a
reboot-required flag (the model from `Get-WingetUpdates.ps1`).

Sources in v0.1: winget (incl. `msstore`), direct URL, npm global, pipx (pip as fallback),
dotnet global tools, PSGallery modules, VS Code extensions.

- **PSGallery without PowerShell:** PSGallery is a NuGet feed. List = scan `PSModulePath`
  directories for module manifests; install/upgrade = fetch the `.nupkg` from the feed and
  extract into the per-user modules folder, honoring both Windows PowerShell and PowerShell 7
  paths.
- **Direct URL:** catalog entry gives URL (or a "latest release" resolver such as a GitHub
  repo), installer type, silent switches, expected Authenticode signer, and optional
  `mirrors[]`. Verification is HTTPS + signer match, not a pinned hash, because vendors replace
  binaries in place. Optional `sha256` is honored when present.

## Queue and concurrency

- `OperationRunner` plans a batch: resolves dependencies, orders prerequisites first, groups by
  elevation need.
- **Downloads parallel** (bounded, default 3), **installs serial** (MSI allows one at a time).
- Before each install: wait (with visible status) if the MSI mutex or another winget is busy,
  instead of failing with 1618.
- **Slow / stalled downloads:** stall detection (no bytes for N seconds) rather than a total
  timeout; retry with backoff; then the next mirror; then fail that item only. Throughput is
  shown so "slow" is visible, and any item can be skipped mid-download.
- Downloads go to a temp folder **on the host disk**, not the thumb drive (USB is slow, and
  some installers misbehave when run from removable media). Cleaned up after the run.
- Cancellation: the running installer is allowed to finish (killing installers corrupts
  state); everything not yet started becomes Skipped.

## Elevation broker

- The GUI and CLI run unelevated. When a step needs admin, the host launches its own exe with
  `--broker <pipe-name>` via `runas` (one UAC prompt per session).
- The pipe ACL allows only the current user's SID. The broker accepts a closed set of typed
  requests (run operation X with request Y); never arbitrary command lines.
- The broker exits when the pipe closes or the parent process dies.
- Scope routing: machine-scope installs and defluff go to the broker; user-scope installs run
  in the unelevated process (correct profile). An installer returning "refuses to run
  elevated" (`0x8A150056`) is retried unelevated automatically.
- If UAC is declined, admin-only items are Skipped with a clear reason; the rest still run.

## winget bootstrap (fresh / slim / LTSC)

1. Detect: `winget.exe` alias resolvable and COM server activatable.
2. If missing: download the latest App Installer `.msixbundle`, its dependencies zip, and the
   license file from the winget-cli GitHub release; install dependencies then App Installer
   via `PackageManager` (provisioned when elevated, so other users get it too).
3. Re-detect; reset sources if they are unhealthy.

No PowerShell module (`Repair-WinGetPackageManager`) involved.

## Defluff engine

```csharp
public interface IDefluffTarget   // one per kind: registry value, appx, provisioned appx, service, task, ...
{
    Task<TargetState> DetectAsync(DefluffItem item, CancellationToken ct);
    Task<ApplyResult> ApplyAsync(DefluffItem item, IBackupWriter backup, CancellationToken ct);
    Task<ApplyResult> RevertAsync(DefluffItem item, BackupEntry entry, CancellationToken ct);
}
```

- **Rules are data** (`catalog/defluff.json`): id, group (`recommended`, `optional`,
  `privacy`), title, description, and a list of typed actions (`registryValue`,
  `appxRemove`, `appxDeprovision`, `serviceStartType`, `scheduledTaskDisable`, ...).
- **Detect before apply, every time.** Status is computed from live machine state, so re-runs
  touch only what drifted (idempotent by construction).
- **Catching new junk:** (1) policy keys stop sponsored auto-installs at the source; (2) known
  junk patterns are maintained in the catalog; (3) any removable Appx not in the keep list,
  not protected (frameworks, Store, system), and not a known rule is listed as Unrecognized
  for review. Never auto-removed.
- **Backups:** every registry change records the prior value (or absence) to
  `Updatr.data\defluff\backups\<timestamp>.json` plus a `.reg` export. History records what was
  applied and when, which drives the "new since last run" view.
- **Restore point:** attempted before apply; failure (throttled, disabled) is a warning, not a
  blocker.

## Catalog

Files in `catalog/`, validated by JSON schema in CI. Loaded at runtime in this order:

1. Remote: `raw.githubusercontent.com/matthewjenner/Updatr/main/catalog/...` (short timeout).
2. Cache: last good remote copy in `Updatr.data\catalog-cache\`.
3. Embedded: copy compiled into the binary at build time.

The loaded catalog's origin and date are shown in Settings. A catalog with a newer
`schemaVersion` than the binary understands is ignored in favor of cache/embedded, with a
banner suggesting a self-update.

App entry sketch:

```json
{
  "id": "git",
  "name": "Git",
  "category": "dev",
  "sources": [
    { "$type": "winget", "packageId": "Git.Git" },
    { "$type": "directUrl", "githubRelease": "git-for-windows/git", "assetPattern": "Git-*-64-bit.exe",
      "installerType": "inno", "silentArgs": "/VERYSILENT /NORESTART",
      "signer": "Johannes Schindelin", "mirrors": [] }
  ],
  "requires": [],
  "hold": true
}
```

Template entry: `{ "id": "dev", "name": "Dev", "items": ["git", "vscode", "node-lts", ...] }`.
User templates and carts use the same shape and live in `Updatr.data\`.

## Portable data folder

```
Updatr.data\
  settings.json
  carts\<name>.json
  templates\<name>.json         user templates (built-ins come from the catalog)
  holds.json                    user hold rules (merged over catalog holds)
  catalog-cache\
  defluff\keep-list.json  history.json  backups\
  quarantine\                   (v0.2)
  footprints\                   (v0.2)
  logs\<timestamp>-<op>.log  reports\
```

- Located beside the exe; `--data-dir` overrides (dev, tests).
- Never stores absolute paths that include the drive letter (it changes between machines).
- Writes are atomic (temp file + replace). Drive pulled mid-run: data writes fail soft, the
  run continues, and the GUI shows a banner.
- FAT32/exFAT safe: no ACLs, no alternate data streams, no long-path assumptions.

## Diagnostics

`Updatr.Core/Diagnostics/DiagLog.cs` follows Klakr's pattern: one typed method per event, the
format string lives only there, categories (`System`, `Preflight`, `Source`, `Queue`,
`Defluff`, `Elevation`, `Update`). Unlike Klakr it is not tied to a sidecar window: every run
also writes its log to `Updatr.data\logs\`. The GUI log viewer subscribes and marshals.

## Threading (GUI)

- UI thread owns Views and ViewModels. All operation events arrive through the GUI sink, which
  posts to the UI thread.
- No synchronous waits on the UI thread, ever (no `.Result`, no `.Wait()`).
- Long lists (installed packages) are populated incrementally from `IAsyncEnumerable` so the
  grid fills as sources answer, each source independently.

## Self-update

1. Query GitHub Releases latest; compare with the running version.
2. Download the zip for the running architecture to host temp; verify SHA256 from the release.
3. Rename running `Updatr.exe` / `updatr-cli.exe` to `*.old`, extract the new ones in place,
   relaunch. `*.old` is deleted on next start.
4. Renaming a running exe works on NTFS and FAT32 (Phase 0 spike), so the swap happens in
   place while the app runs.

## Build and release

- Publish: self-contained, single-file, compressed, **not trimmed**, `win-x64` and `win-arm64`.
  `IncludeNativeLibrariesForSelfExtract` bundles native libs (Skia, HarfBuzz, the WinGet factory),
  which extract to the host's temp folder on first run.
- **Release folder = three files:** `Updatr.exe` (~57 MB), `updatr-cli.exe` (~45 MB), and
  `Microsoft.Management.Deployment.winmd` (64 KB). The `.winmd` must sit beside the exes - see
  "WinGet COM activation" below. `Directory.Build.targets` keeps it out of the bundle.
- `Scripts/publish.sh` produces `dist/Updatr-<version>-<rid>.zip` plus `SHA256SUMS.txt`.
  `.github/workflows/release.yml` calls the same script, so local and CI output match.
- Release workflow: same idempotent pattern as Klakr (read version, skip if the release exists,
  test, publish, `gh release create`), plus: pull requests and version `0.0.0` build and test
  but never release. Actions pinned to Node 24 majors (`checkout@v7`, `setup-dotnet@v6`).
- SDK pinned in `global.json` (`latestFeature` roll-forward); tests run on Microsoft.Testing.Platform.
- Package versions are centrally managed in `Directory.Packages.props`.

## WinGet COM activation (Phase 0 findings)

- The projection is `Microsoft.WindowsPackageManager.ComInterop` (targets
  `net8.0-windows10.0.26100.0`, works from net10). It needs a RuntimeIdentifier: App and Cli
  reference it directly so the per-architecture native factory is copied next to them.
- **`.winmd` beside the exe is required.** The server is out of process; an unpackaged caller
  resolves the interface metadata for marshaling from `.winmd` files in the exe's directory.
  Bundled, the first call fails with `0x80073D54`.
- **Explicit activation handler.** Updatr sets CsWinRT's `WinRT.ActivationFactory.ActivationHandler`
  for the `Microsoft.Management.Deployment.*` types: it loads the native factory with
  `NativeLibrary.Load` (which finds it in the single-file extraction folder) and calls its
  `DllGetActivationFactory` export. CsWinRT's own probing cannot see bundled native DLLs. Measured
  working unelevated and elevated in a compressed single-file build; plain `new PackageManager()`
  was only measured unelevated, so the handler is the supported path.
- **Self-heal:** the `.winmd` is also embedded as a resource. If it is missing beside the exe and
  the folder is writable, Updatr writes it back on startup; if it cannot, winget falls back to
  CLI text parsing and the splash shows a warning.
- **Searching:** `FindPackagesOptions` with no selector/filter is invalid on a composite catalog.
  Skip catalogs with `Info.Explicit` (e.g. `winget-font`).
- **Partitioned listing is the primary path.** A single bad manifest in the community index makes
  the whole composite "list everything" query fail (`0x80070057`, `VersionRange::operator<` on
  overlapping ranges - reproduced on the dev machine with the CLI too). Updatr queries in
  parallel partitions (installed-name prefix, built from the local catalog's actual first
  characters), streams each partition's results into the grid as it lands, splits a failing
  partition one level deeper, and reports the residue as "could not check (winget index error)".
  Measured: ~23 s for ~400 installed packages at 8-way parallelism; correlation cost scales with
  package count, so results arriving incrementally matters more than total time.

## Spikes (Phase 0)

1. **WinGet COM from .NET 10** - done, unelevated and elevated (see above).
2. **Single-file publish, x64 + arm64** - done. Sizes above; CLI starts in ~90 ms warm.
3. **CLI trimming / AOT** - done: trimming works at runtime but WinRT.Runtime and the SDK
   projection raise trim warnings (IL2104), so neither binary is trimmed. Revisit later.
4. **Rename a running exe** - passes on NTFS and on a FAT32 removable drive. Self-update uses the
   simple rename-and-replace swap; no relaunch helper needed.
5. **App Installer bootstrap on a clean VM** - deferred to Phase 3, where the bootstrap code lives.
