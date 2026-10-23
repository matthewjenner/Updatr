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
- **Tests:** xUnit + a free-license assertion library (AwesomeAssertions or Shouldly; chosen
  at scaffold time - FluentAssertions 8 is commercially licensed)

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
|   +-- Updatr.Platform.Tests/    Integration tests, [Trait("Category","Integration")], opt-in
+-- catalog/                      apps.json, templates.json, holds.json, defluff.json, schema/
+-- Docs/  Scripts/  .github/workflows/
+-- Directory.Build.props         Version source of truth (starts at 0.0.1)
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
4. Renaming a running exe on FAT32/exFAT is a spike item; fallback is a tiny relaunch step that
   waits for the old process to exit before swapping.

## Build and release

- Publish: self-contained, single-file, `win-x64` and `win-arm64`.
  `IncludeNativeLibrariesForSelfExtract` for Avalonia's native libs. The CLI is trimmed (Native
  AOT if the WinRT/COM interop allows - spike item).
- `.github/workflows/release.yml`: same idempotent pattern as Klakr (read version, skip if the
  release exists, test, publish both archs, zip, SHA256, `gh release create`). Action majors are
  verified current at scaffold time (Klakr's `@v4` pins are retired).
- `Scripts/bump-version.sh`, `Scripts/run.sh` as in Klakr.

## Spikes (Phase 0 - answer before building on them)

1. WinGet COM API from .NET 10, unelevated and elevated: activation, interop package, list /
   upgrade / install round trip.
2. Avalonia single-file self-contained publish on x64 and arm64: size, startup time from a USB
   drive.
3. CLI trimming / AOT with the WinGet interop.
4. Renaming a running exe on FAT32/exFAT.
5. `PackageManager` App Installer bootstrap on a clean VM (and LTSC if available).
