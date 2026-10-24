# Updatr - Work Plan

Living build plan. Update **Current state** and check boxes as work proceeds; append to the
**Decisions log** when something is decided. See `DESIGN.md` (product) and `TECHARCH.md`
(architecture).

---

## Current state

- **Phase:** 2 complete - host foundation. Awaiting review (and the manual admin test) before
  Phase 3.
- **Last updated:** 2026-10-23
- **Build status:** `dotnet build` green (0 warnings); `dotnet test` green (133/133 across Core,
  Platform, Cli test projects). Version 0.0.0 (never released).
- **Next:** Phase 3 (winget source + Updates screen; first preview release).

## Versioning

- `0.0.0` until something is shippable - CI builds and tests it but never releases it.
- `0.0.x` preview releases as phases ship usable pieces (first one at the end of Phase 3).
- **`0.1.0`** = Phases 0-7: updates, install/carts, defluff, uninstall (uninstaller only),
  self-update, CLI parity.
- **`0.2.0`** = Phase 8: leftover cleanup + install footprints.

---

## Phase 0 - Scaffold and spikes

- [x] Solution `Updatr.slnx`: Core, Platform, App, Cli, Core.Tests (Platform.Tests arrives with
      the first Platform code - an empty MTP test project fails `dotnet test`)
- [x] `Directory.Build.props` (version, nullable, implicit usings, warnings as errors, shared
      Windows TFM / RID), `Directory.Packages.props`, `Directory.Build.targets`, `global.json`
- [x] `.gitignore`, `README.md`, `CLAUDE.md`
- [x] `Scripts/bump-version.sh`, `Scripts/run.sh`, `Scripts/publish.sh`
- [x] `release.yml`: win-x64 + win-arm64 via `publish.sh`, Node 24 action majors (verified)
- [x] Convention tests: US English (ported list), ASCII-only, no PowerShell in `Src/`
- [x] Placeholder icon + window showing the version (replaced by the splash in Phase 2)
- [x] Spike 1: WinGet COM from .NET 10 - unelevated and elevated, via the explicit activation handler
- [x] Spike 2: single-file publish, x64 + arm64 (sizes and startup in TECHARCH)
- [x] Spike 3: CLI trim / AOT - trimming works but is not trim-safe; not trimming
- [x] Spike 4: rename running exe - passes on NTFS and a FAT32 removable drive
- [x] MIT `LICENSE`
- [ ] Spike 5: App Installer bootstrap on a clean VM - moved to Phase 3 (needs the bootstrap code)
- [x] Record spike outcomes in the decisions log

## Phase 1 - Core models and catalog

- [x] Catalog models (apps, polymorphic sources, templates, holds) + JSON schemas generated from
      the models and snapshot-tested (`UPDATR_UPDATE_SCHEMAS=1 dotnet test` regenerates)
- [x] Catalog loader: origins tried in order, whole-set-or-nothing, schemaVersion gate, cache
      written only after validation, tolerant at runtime / strict for the repo catalog
- [x] Embedded origin (catalog compiled into Core) + `CatalogValidator`
- [x] Port the error code table + HTTP-facility decoding to `Errors/ErrorCodes` - every winget
      code re-verified against `AppInstallerErrors.h` v1.29.380; extended with in-use, disk,
      network, reboot-initiated, pinned, already-installed, source-open codes; transient flag
- [x] Port the hold list to `catalog/holds.json` (reasons kept) + `HoldPolicy` + `WildcardPattern`
- [x] `PackageOutcome` model (Ok / AlreadyCurrent / Failed / Skipped + reboot flag + decoded code)
- [x] Operation + sink abstractions (`IOperation`, `IOperationSink`, events, `[Operation]` name)
- [x] Seed `apps.json` (9 apps, winget ids verified with `winget show`); templates come in Phase 5
- Moved: defluff rule model -> Phase 6 (designed with its targets); remote + cache origins ->
  Phase 2 (they need the data store and network checks); parity test -> Phase 2 (arrives with
  the first operation, `preflight`, so it is not vacuous)

## Phase 2 - Host foundation

- [x] Portable data store (`Updatr.data\`, atomic write-through writes, path-escape guard,
      fail-soft `DataStoreException` + `Failed` event for the banner)
- [x] Catalog origins: remote (HTTPS, 10 s whole-set deadline) and cache (`catalog-cache\`,
      marker written last so an interrupted write reads as absent)
- [x] DiagLog (typed, always on) + per-run log files in `logs\` (batched, pruned to 30)
- [x] Preflight: 12 checks (os, architecture, network, winget, catalog, tools, elevation, reboot,
      busy, disk, data, self-update), run in dependency waves in parallel; a crashing check is a
      Fail row, never a crash
- [x] WinGet runtime: explicit activation handler + `.winmd` self-heal from an embedded copy
- [x] Splash: icon, version, progress, live rows, inline detail for problems, Retry / Continue
      anyway / Quit on failure, minimum 8 s on screen
- [x] Main window shell: navigation, banner strip (preflight warnings, data-folder failures,
      newer version), placeholder pages, live Log page, Settings (data folder, log file, catalog
      origin, machine checks with re-run, administrator test)
- [x] Elevation broker: random pipe, FirstPipeInstance, current-user ACL + network deny, PID
      checks both ways, closed handler set, exits with its parent; `ElevationService` launches
      once per session and remembers a declined prompt
- [x] CLI host: System.CommandLine, text and `--json` sinks, `--data-dir`, `--yes`, exit codes;
      `preflight` (named so instead of `doctor`, matching the splash) and `admin test`
- [x] `Updatr.Platform.Tests` (data store, cache, broker over a real pipe, process runner) and
      `Updatr.Cli.Tests` (parity test over `[Operation]` names, command-line parsing)
- [ ] **Manual:** `updatr-cli admin test` (or Settings > Test administrator access) - approve the
      UAC prompt; expect "Working: elevated helper answered"
- Moved: run summary dialog -> Phase 3 (built with the first batch operation that fills it)

## Phase 3 - winget + Updates screen

- [ ] winget source via COM: parallel name-prefix partitions, bad-partition split, residue
      reported as "could not check"; text fallback
- [ ] Embedded `.winmd` self-heal (write beside exe if missing; else fall back + warn)
- [ ] winget bootstrap (incl. LTSC path) + clean-VM test (spike 5)
- [ ] Updates grid: incremental load, filters, multi-select
- [ ] Upgrade / reinstall / hold / unhold, batch + single
- [ ] Collision waits (MSI mutex, winget running)
- [ ] Run summary dialog (moved from Phase 2)
- [ ] CLI: `list`, `upgrade`, `reinstall`, `hold`, `unhold`
- [ ] Release 0.0.x preview (replaces the script)

## Phase 4 - Other sources

- [ ] Direct URL: resolver, downloader (stall detection, retry, mirror hook), signer check
- [ ] npm global, pipx (+ pip fallback), dotnet tool, VS Code extensions
- [ ] PSGallery via NuGet feed (no PowerShell)
- [ ] Each source wired into Updates + CLI

## Phase 5 - Install, carts, templates

- [ ] Catalog browser: categories, search, installed markers
- [ ] Cart: add / remove / change source, save, load, merge
- [ ] Templates: apply multiple, built-in read-only, copy-to-mine, editor
- [ ] Dependency resolver with "Required by" lines
- [ ] Queue runner: parallel downloads, serial installs, per-item progress, cancel
- [ ] Author starter catalog + templates (Base, Dev, Gaming, Media, Productivity)
- [ ] CLI: `install`, `cart *`, `template *`

## Phase 6 - Defluff

- [ ] Defluff rule model in the catalog (`defluff.json` + schema), designed with the targets
- [ ] Targets: registry value, appx, provisioned appx, service, scheduled task
- [ ] Detect / apply / revert, backups, history, restore point
- [ ] Recommended rule set (ads + junk + baseline privacy)
- [ ] Optional rule set (Edge, OneDrive, Copilot, Recall, Widgets, Xbox, Game Bar, ...)
- [ ] Unrecognized Appx detection + keep list
- [ ] "New since last run" view
- [ ] Investigate Edge removal constraints
- [ ] CLI: `defluff status | apply | revert`

## Phase 7 - Uninstall (basic) and self-update -> 0.1.0

- [ ] Installed-apps list across sources + Uninstall-key reader
- [ ] Uninstall single / batch via each app's own uninstaller
- [ ] Self-update (download, verify, swap, relaunch)
- [ ] CLI: `uninstall`, `self-update`
- [ ] README: usage, unblocking unsigned binaries, known issues
- [ ] Release 0.1.0

## Phase 8 - Leftover cleanup -> 0.2.0

- [ ] Leftover scanner (files, registry, services, tasks, shortcuts, firewall rules)
- [ ] Confidence levels, nothing pre-checked
- [ ] Quarantine (zip + .reg), restore, purge
- [ ] Install footprints for Updatr-installed apps

---

## Backlog

- Opt-in scheduled task (defluff after feature updates).
- Reinstall Microsoft Store on LTSC.
- Mirror implementation for direct-URL installers (S3 / home server).
- Code signing.
- ARM64 hardware validation.
- Linux / Raspberry Pi host (separate product idea).
- Revisit trimming (GUI 57 -> ~22 MB, CLI 45 -> ~11 MB) once WinRT.Runtime / the SDK projection
  are trim-safe, or with targeted trimmer roots and runtime tests.

## Known edges

- ARM64 untested on hardware.
- Unsigned binaries: SmartScreen / Defender warnings.
- Restore points throttled (24 h) or disabled; own backup is the primary undo.
- Edge removal restricted outside the EEA.

## Decisions log

- **2026-10-23** - `Get-WingetUpdates.ps1` documented in `legacy-script.md` and deleted.
- **2026-10-23** - Phase 0 spike: WinGet COM works from .NET 10 via
  `Microsoft.WindowsPackageManager.ComInterop` 1.29.380. Requires a RID and the `.winmd` beside the
  exe (single-file bundling it fails with `0x80073D54`).
- **2026-10-23** - Phase 0 spike: on the dev machine `winget upgrade` itself fails with
  `0x80070057` - one community manifest's overlapping version ranges throw during correlation and
  sink the whole list. Updatr lists in parallel name-prefix partitions (primary path, not a
  fallback) so one bad entry costs a handful of packages, not the screen.
- **2026-10-23** - Publish single-file + compressed, not trimmed (WinRT projections not trim-safe).
- **2026-10-23** - Release = zip of 3 files per arch (`Updatr.exe`, `updatr-cli.exe`, `.winmd`) +
  `SHA256SUMS.txt`, built by `Scripts/publish.sh` locally and in CI.
- **2026-10-23** - xUnit v3 on Microsoft.Testing.Platform + AwesomeAssertions; central package
  management; SDK pinned to 10.0.402 in `global.json`.
- **2026-10-23** - Version 0.0.0 = never released; first release on first bump.
- **2026-10-23** - Manual spike runs: WinGet COM works elevated (through the explicit activation
  handler, which becomes the production path); rename-and-replace of a running exe works on a
  FAT32 thumb drive, so self-update swaps in place. License: MIT.
- **2026-10-23** - Catalog = three files (apps, templates, holds), loaded as one set or not at
  all. Unknown properties ignored at runtime, disallowed for the repo's own catalog in tests.
  `schemaVersion` bumps only for breaking changes.
- **2026-10-23** - JSON schemas are generated from the models with `JsonSchemaExporter` and
  snapshot-tested, so editor validation cannot drift from what Updatr reads.
- **2026-10-23** - Optional model properties coerce null in their init setter (C# 14 `field`):
  the source-generated deserializer assigns omitted init properties null, wiping defaults.
- **2026-10-23** - Computed members on polymorphic source refs live in the base as a switch:
  `[JsonIgnore]` on an abstract member does not apply to its overrides, which leaked into JSON.
- **2026-10-23** - CLI health command is `preflight`, not `doctor` (user preference; matches the
  splash's wording).
- **2026-10-23** - Splash stays up at least 8 s even when everything passes (user feedback: it
  vanished before it could be read).
- **2026-10-23** - Preflight checks run in parallel waves by declared dependency; results are
  reported as they land, returned in registration order.
- **2026-10-23** - Broker cancel is sent explicitly when `SendAsync` observes cancellation, not
  from a token callback: callbacks run newest-first, so `WaitAsync`'s resumed the method and
  disposed the registration before it ran (caught by `BrokerTests`).
- **2026-10-23** - Windows ignores a PID's low two bits (`ProcessId + 1` opens the same
  process). Tests that need "some other process" start a real one; never fake a PID by offset.
- **2026-10-23** - Verified on the published single-file build: winget via the activation
  handler, and `.winmd` self-heal when the file is deleted from beside the exe.
- **2026-10-23** - `admin test` operation added so real UAC elevation can be verified by hand on
  any machine (GUI button + CLI command, per the parity rule).
- **2026-10-23** - Apps declare `provides` (tools they install); source types imply a required
  tool. A repo test guarantees every required tool has a provider, for the cart resolver.
- **2026-10-23** - .NET 10 + Avalonia + CommunityToolkit.Mvvm, matching Klakr.
- **2026-10-23** - Self-contained single-file binaries; no Velopack (portable, not installed).
- **2026-10-23** - Two binaries (`Updatr.exe` GUI, `updatr-cli.exe` CLI) over one shared Core +
  Platform; a WinExe cannot behave correctly as a console app.
- **2026-10-23** - No PowerShell in the product; native APIs only. Ecosystem CLIs (npm, pipx,
  dotnet, code) are allowed because they are those ecosystems' interfaces.
- **2026-10-23** - All user data beside the binary in `Updatr.data\`; nothing per-machine.
- **2026-10-23** - Installers come from upstream; catalog is metadata only; mirror hook stubbed.
- **2026-10-23** - Direct-URL verification by HTTPS + Authenticode signer, optional sha256.
- **2026-10-23** - Unelevated host + on-demand elevated broker over a named pipe.
- **2026-10-23** - Defluff default removes ads/junk only; everything else is opt-in; unknown
  Appx are surfaced for review, never auto-removed.
- **2026-10-23** - Alerting in-app only.
- **2026-10-23** - Unsigned to start.
- **2026-10-23** - Leftover cleanup stubbed for 0.1.0, fast follow in 0.2.0, quarantine not
  delete.
