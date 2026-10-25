# Updatr - Work Plan

Living build plan. Update **Current state** and check boxes as work proceeds; append to the
**Decisions log** when something is decided. See `DESIGN.md` (product) and `TECHARCH.md`
(architecture).

---

## Current state

- **Phase:** 7 complete - uninstall (basic) and self-update. 0.1.0 released 2026-10-25. Still open (not blocking): winget repair on a clean / LTSC VM.
- **Last updated:** 2026-10-24
- **Build status:** `dotnet build` green (0 warnings); `dotnet test` green (303/303).
  Version 0.1.0 (released 2026-10-25).
- **Next:** Phase 8 (leftover cleanup -> 0.2.0).

## Versioning

- `0.0.0` until something is shippable - CI builds and tests it but never releases it.
- `0.0.x` preview releases as phases ship usable pieces (first one at the end of Phase 3).
- **`0.1.0`** = Phases 0-7: updates, install/carts, debloat, uninstall (uninstaller only),
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
- Moved: debloat rule model -> Phase 6 (designed with its targets); remote + cache origins ->
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
- [x] **Manual (2026-10-23):** `updatr-cli admin test` and Settings > Test administrator access
      both passed - UAC prompt, elevated broker answered, exited cleanly with its parent
- Moved: run summary dialog -> Phase 3 (built with the first batch operation that fills it)

## Phase 3 - winget + Updates screen

- [x] winget source via COM: parallel name-prefix partitions (8-way), failing partition split by
      two letters, residue listed as "could not check"; `winget.exe` text fallback
      (`WinGetCliSource` + `WinGetTableParser`) when the API cannot be activated
- [x] Embedded `.winmd` self-heal (done in Phase 2, verified on the published build)
- [x] winget repair operation: re-register App Installer by family name, else install the
      latest App Installer bundle + architecture dependencies from winget's GitHub release via the
      WinRT `PackageManager`; offered as a Fix button on the splash/Settings winget row
- [ ] **Manual:** winget repair on a clean / LTSC VM (spike 5)
- [x] Updates grid: streams rows as groups finish, sort + search + "updates only", check
      boxes, live per-row activity and progress, counts line
- [x] Upgrade / reinstall / hold / unhold, checked rows or "upgrade all", one installer at a
      time, confirmation listing every package
- [x] Collision waits (MSI mutex, winget.exe) + one retry on "another install in progress"
- [x] Machine-scope packages upgrade in the elevated broker (one UAC per session); declined or
      "refuses elevation" falls back to running unelevated
- [x] Run summary dialog (failures first, reboot note)
- [x] Broker writes its own run log (`logs/...-broker.log`) beside the host's
- [x] CLI: `list [--updates]`, `upgrade <ids> | --all [--include-held] [--include-unknown]`,
      `reinstall <ids>`, `hold`, `unhold`, `winget repair`
- [x] Launcher-managed apps (Steam, Battle.net, Epic, Ubisoft, EA, GOG, Riot, Rockstar) detected
      from uninstall command / install folder; listed, never changed through winget
- [x] Self-updating apps (launchers, Discord): new `explicit` hold behavior - skipped by
      "upgrade all", upgraded when named
- [x] "Newer version needs a fresh install" (installer technology changed) listed with the
      reason instead of a failing upgrade
- [x] **Manual (2026-10-23):** real upgrades via the CLI, verified with `winget list` -
      GitHub Desktop 3.6.2 -> 3.6.8 (user scope, unelevated) and PuTTY 0.84 -> 0.85 (machine MSI,
      through the elevated broker: one UAC prompt, broker log written to the drive)
- [x] Elevation routing uses the installer winget would run (machine scope, ElevationRequired,
      ElevatesSelf), not only the installed scope - which winget almost never reports
- [x] Version bumped to 0.0.1 - first preview release on push

## Phase 4 - Other sources

- [x] Shared `Downloader`: stall detection (no data for 30 s) rather than a total timeout,
      retries with backoff, then mirrors in order; `.part` file renamed only when complete
- [x] Direct URL installer: fixed URL or GitHub latest-release asset by pattern; sha256 when
      given; Authenticode trust + exact signer CN (`WinVerifyTrust`, since
      `CreateFromSignedFile` is obsolete in .NET 10); silent switches per installer type; elevated
      installs via broker handler `directurl-run`, which re-verifies before running. Used by
      Phase 5's carts (not listed on the Updates screen - those apps show up through winget)
- [x] npm global (`npm ls` / `npm outdated`), pipx (+ PyPI for latest), dotnet global tools
      (+ nuget.org; private-feed tools listed without update info), VS Code extensions
      (+ Marketplace, per platform, stable only)
- [x] PSGallery natively: gallery-installed modules found by `PSGetModuleInfo.xml`, latest from the
      gallery's OData feed, upgrade = download + extract side by side (zip-slip guarded, metadata
      carried over); all-users modules through broker handler `psgallery-install`, which only
      writes into the known all-users module folders
- [x] Argument safety for `.cmd` CLIs (npm, pipx, code): only names/versions that cannot carry
      shell syntax are passed (`EcosystemParsers.IsSafePackageName`)
- [x] All sources listed together on the Updates screen and by `updatr-cli list` (`--source`),
      source filter in the GUI; every source works with upgrade / reinstall / hold
- [x] **Manual (2026-10-24):** listed all five on the dev machine (1-3 s each); upgraded
      `Microsoft.WinGet.Client` 1.12.440 -> 1.29.380 via PSGallery - PowerShell lists and loads it
- [x] pip (global/user site-packages) - left out by decision (pipx is the supported path)

## Phase 5 - Install, carts, templates

- [x] Starter catalog: 46 apps in 7 categories, every winget id checked with `winget show`
      (NVIDIA App is not in winget - backlog as a direct-URL candidate); npm and VS Code entries
      checked against their registries
- [x] Templates: Base, Dev, Gaming, Media, Productivity (built in, read-only); copy to yours, edit
      (the cart becomes the template, Save / Cancel), save-as-template, delete yours
- [x] Cart: check apps in the catalog list, add templates, merge (no duplicates), remove, choose
      source per app, save / load / merge / delete saved carts (`Updatr.data\carts\`)
- [x] Dependency resolver: a source's required tool (npm, code, dotnet, pipx) is satisfied by the
      machine, another cart item, or the catalog app that provides it - added as "Required by
      ..."; explicit `requires` too; prerequisites ordered first; automatic items drop when nothing
      needs them
- [x] Install run: skip already-installed, prerequisites first, one installer at a time behind the
      installer gate, a failed prerequisite skips its dependents with that reason, PATH re-read
      from the registry after a provider installs, cancel between items, summary dialog
- [x] Installers per source: winget (named source, broker for elevated installers, CLI fallback),
      npm, pipx, dotnet tool, VS Code, PSGallery (user scope), direct URL
- [x] Catalog browser: categories, search, installed markers (checked in the background)
- [x] CLI: `install`, `catalog list`, `cart list|show|save|remove|delete`,
      `template list|show|save|copy|delete`; `--json` confirmation now carries the plan
- [x] `UPDATR_CATALOG_DIR` developer override: test catalog edits locally before pushing them
- [x] Real install verified: Everything via `updatr-cli install everything` (then "Already
      installed" on re-run); template copy / save / show / built-in guard / delete round trip

## Phase 6 - Debloat

- [x] Rule model in the catalog (`debloat.json` + generated schema): items (recommended /
      optional, category, warning, minBuild) of typed actions; plus a keep list of Store apps
- [x] Targets: registry value (set / remove), Store app (remove for the user + deprovision from
      the image), service start type (SCM API), scheduled task (Task Scheduler COM)
- [x] Detect / apply / revert: status from the live machine, only drifted actions applied; every
      change captured and the backup saved (JSON + `.reg`) before it is made - no backup, no
      change; history; revert from the latest backup; restore point (best effort) before
      machine-wide changes
- [x] Admin half through the broker (`debloat-run`, `restore-point`) with an allow-list: HKLM
      only under SOFTWARE, HKCU only under Software\Policies, tasks only under \Microsoft\,
      service names validated, revert never restores boot/system start types
- [x] Recommended set (13): Start / Settings / lock screen / Explorer / search ads and
      suggestions, sponsored auto-installs, tips and nags, junk Microsoft apps, sponsored games,
      advertising id, tailored experiences, required-only diagnostics, activity history
- [x] Optional set (14): Copilot, Recall, Widgets, Bing in search, Edge nags, OneDrive, Xbox app,
      Game Bar, Teams (personal), new Outlook, Phone Link, Feedback Hub, Weather, stricter
      telemetry (services + tasks)
- [x] Unrecognized Store apps (not a rule target, not kept by the catalog or the user) for
      review: remove one-off (revertible) or keep
- [x] "Changed since last run": items applied at the last run that are not applied now, and
      Store apps not seen then
- [x] Edge removal investigated: not offered (see decisions); nags and background running are
- [x] CLI: `debloat status [--all] [--changed] | apply [--recommended] [--item] [--app] |
      revert <id> | history | keep | unkeep`
- [x] GUI: Debloat screen (grouped items with live state, recommended pre-picked, unrecognized
      apps with Keep, history with Revert)
- [x] Real round trip verified (CLI, current-user item): apply, re-run is "already applied",
      drift detected after Windows-style reset, revert restores the original state
- [x] Admin path verified: `diagnostic-data` applied through one UAC prompt (restore point via
      the broker in 8 s, two HKLM policy writes) and reverted; restore point outcome now logged
- [ ] **Manual:** look over the Debloat screen in the GUI before pushing 0.0.4

## Phase 7 - Uninstall (basic) and self-update -> 0.1.0

- [x] Installed-apps list across sources: the existing `list` (winget's local catalog already
      covers every Uninstall-key and MSIX entry, catalog or not); the Uninstall-key reader moves
      to Phase 8, where leftover scanning needs install locations
- [x] Uninstall single / batch via each app's own uninstaller: winget COM `UninstallPackageAsync`
      (registered quiet uninstaller, machine-wide through the broker), `winget uninstall` on the
      CLI fallback, npm / pipx / dotnet tool / VS Code through their tools, PSGallery by removing
      the module folder (all-users through the broker, root-checked); launcher games and winget
      itself are refused
- [x] Self-update: latest release -> zip for this architecture -> SHA256SUMS check -> stage in
      temp -> rename the running files to `*.old`, copy the new ones (all or nothing) -> restart
      (GUI) / "run again" (CLI); `*.old` cleaned on a later start; dev builds refuse
- [x] CLI: `uninstall <id>...`, `self-update [--force]`
- [x] GUI: Uninstall screen; Settings > Check for an update, with restart
- [x] README: getting started, unblocking unsigned binaries, screens, CLI, safety, known issues
- [x] Verified: a published copy run as v0.0.3 updated itself to the real v0.0.4 release while
      running; uninstall resolution (unknown id, protected winget, confirmation) on this machine
- [x] First manual uninstall (Everything) failed with 1603: it ran unelevated (scope "System" not
      recognized). Fixed; detection confirmed True for it
- [x] Manual: Everything uninstalled from the Uninstall screen through one UAC prompt
- [x] Feedback: no checkboxes anywhere - rows are picked by clicking them (highlighted), filters
      are toggle switches (`Behaviors/RowSelection`); the selected count now updates live
- [x] Manual: selection change looked over (Uninstall count and highlight confirmed)
- [x] Released 0.1.0 (2026-10-25)

## Phase 8 - Leftover cleanup -> 0.2.0

- [ ] Leftover scanner (files, registry, services, tasks, shortcuts, firewall rules)
- [ ] Confidence levels, nothing pre-checked
- [ ] Quarantine (zip + .reg), restore, purge
- [ ] Install footprints for Updatr-installed apps

---

## Backlog

- **Uninstall / Updates list organization (polish):** 464 rows with nothing telling a Windows
  component from a game, a runtime, a driver panel, or a launcher's item. Add kind tags and
  filters (Windows / Microsoft, games + launcher, runtimes and redistributables, drivers and
  hardware tools, apps), group or filter by them, and say up front what cannot be removed from
  here. User feedback 2026-10-24.
- NVIDIA App in the catalog - not in winget and no stable "latest" download link; needs a
  direct-URL resolver for NVIDIA's versioned URLs.
- VS Code extensions show their id as the name on the Updates screen; use the Marketplace display
  name.

- Update launcher-managed games through their launcher (e.g. open Steam's update / validate for
  a game, Battle.net's update) instead of only labeling them "Managed by ...".

- Opt-in scheduled task (debloat after feature updates).
- Reinstall Microsoft Store on LTSC.
- Mirror implementation for direct-URL installers (S3 / home server).
- Code signing.
- ARM64 hardware validation.
- Linux / Raspberry Pi host (separate product idea).
- Revisit trimming (GUI 57 -> ~22 MB, CLI 45 -> ~11 MB) once WinRT.Runtime / the SDK projection
  are trim-safe, or with targeted trimmer roots and runtime tests.

## Known edges

- **2026-10-23 bugcheck 0x153 on the dev machine (investigated, not attributed to Updatr).**
  KERNEL_LOCK_ENTRY_LEAKED_ON_THREAD_TERMINATION: a user-mode thread exited while a file-system
  filter still held a Filter Manager stream-list lock (lock sits beside
  `FLTMGR!DeleteStreamListCtrlCallback`). The minidump cannot name the owning process. Loaded
  minifilters were Microsoft's (Defender, cloud files, bindflt, wcifs, Wof, UnionFS) plus Gaming
  Services' `gameflt`; anti-cheat drivers (BattlEye, EAC) were not loaded. The machine was also
  mid-servicing (reboot pending). User-mode code cannot leak a kernel lock entry, so this is a
  driver bug; Updatr's heavy winget listing (8 parallel queries) was the last thing it ran
  ~8 min earlier and could at most have been a trigger. If it recurs during an Updatr run:
  lower `WinGetComSource` parallelism and capture a kernel (not mini) dump.

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
- **2026-10-24** - winget reports a per-machine install's scope as "System", not "Machine" (found
  when Everything's MSI uninstall ran unelevated and failed with 1603). Both now mean machine. This
  likely explains Phase 3's "installed scope is rarely reported". Also: MSI product codes are
  checked against HKLM's Uninstall key, and an uninstaller failing with a permission code (1603,
  1625, 1730, access denied) is retried once through the broker.
- **2026-10-24** - Uninstall goes through winget's COM API for everything winget lists, including
  apps no catalog knows (its local catalog wraps every Uninstall-key entry and runs the registered
  quiet uninstaller). A separate Uninstall-key reader would duplicate that; it is deferred to the
  leftover scanner, which needs install locations anyway.
- **2026-10-24** - Self-update replaces files by renaming the running ones (allowed on NTFS and
  FAT) rather than a helper process that waits for exit: no extra exe, no race, and a failure part
  way puts everything back. Integrity is the release's SHA256SUMS.txt (corruption, not tampering -
  the binaries are unsigned); revisit with code signing.
- **2026-10-24** - Edge is not removed. Microsoft supports uninstalling it only in the EEA (via
  Settings); elsewhere removal needs unsupported setup.exe flags, breaks WebView2-based apps and
  Windows features, and Windows Update reinstalls it. Debloat offers Edge policies instead (no
  first-run, startup boost, background mode, shopping, sidebar, default-browser prompts).
- **2026-10-24** - Debloat backups are saved before each item's change, and a failed save stops the
  run: an unrevertable debloat is worse than none (a read-only drive debloats nothing).
- **2026-10-24** - Store apps: removed for the current user unelevated; deprovisioned (new users)
  through the broker. Provisioned packages cannot be listed unelevated, so an app already gone
  for the user counts as applied. Revert reinstalls from the Store when the rule has a
  `storeId` (verified with `winget show --source msstore`); otherwise it says to.
- **2026-10-24** - Dev Home's package (`Microsoft.Windows.DevHome`) now ships "Windows Advanced
  Settings" on 25H2: kept, not junk. The compatibility appraiser task is "... Appraiser Exp" on
  build 26200; both names are listed (the missing one is not applicable).
- **2026-10-24** - HKCU\Software\Policies is writable only by administrators, so those values go
  through the broker too (into the same user's hive in the normal UAC case).
- **2026-10-24** - Carts store only real picks; prerequisites are recomputed at install time from
  what is on the machine then, so a cart saved on a dev box still pulls in Node on a fresh one.
- **2026-10-24** - Template editing reuses the cart (load template -> edit cart -> Save template)
  instead of a separate editor; built-ins are read-only and edited via "Copy".
- **2026-10-24** - The remote catalog always wins over the embedded one, so catalog edits cannot be
  tried locally before pushing; added `UPDATR_CATALOG_DIR` (local folder ahead of GitHub, never
  cached, flagged by a preflight warning).
- **2026-10-24** - After installing a tool provider (Node, VS Code, .NET SDK), PATH is re-read from
  the registry: this process keeps its start-up PATH, so without it the npm / code / dotnet items
  right after would fail.
- **2026-10-24** - VS Code Marketplace traps (both seen live): "latest only" queries return one
  latest build per platform plus sometimes a stale universal one (Claude Code: universal 2.1.89,
  win32-x64 2.1.310), and the latest may be a pre-release (GitLens 2026.10.x pre-release vs stable
  19.4.0). Updatr takes the newest stable for this platform; pre-release-only results get a second,
  full-history query for just those extensions.
- **2026-10-24** - PSGallery upgrades are recognized by PowerShell and PSResourceGet
  (`Get-InstalledPSResource`); the legacy PowerShellGet 2.2.5 `Get-InstalledModule` lists only
  versions it installed itself. Accepted - the module loads and the current tooling agrees.
- **2026-10-24** - pip is not a source yet: upgrading global site-packages breaks the virtualenvs
  and apps that pin them, which is why pipx exists. Decided 2026-10-24: left out (the user
  does not use Python much; pipx covers Python apps).
- **2026-10-24** - Windows PowerShell's execution policy blocked an unsigned dev helper script on
  the dev machine - a live reminder of why Updatr itself never runs PowerShell.
- **2026-10-23** - winget reports an installed scope for almost no packages (all but 7 of ~50
  upgradable showed none), so broker routing also looks at the applicable installer's scope and
  elevation requirement. Without this, machine-wide installers ran unelevated and prompted one by one.
- **2026-10-23** - "Defluff" renamed to **Debloat** everywhere (user choice; the term people already
  search for). Nav, docs, log category, and the future `debloat.json` / `updatr-cli debloat ...`.
- **2026-10-23** - Phase 3 finding: winget's COM API and its CLI disagree on what is an update.
  COM `IsUpdateAvailable` is false when the new version switches installer technology (Audacity
  exe -> WiX, Wave Link MSI -> MSIX); the CLI lists those and the upgrade then fails
  (`0x8A15008E`). Updatr follows COM and lists them as "newer version needs a fresh install".
  Conversely the CLI hides `RequireExplicitUpgrade` packages (Discord) that COM reports - that
  flag is not in the COM API, so the catalog carries an `explicit` hold for known self-updaters.
- **2026-10-23** - Launcher-managed games are detected from the uninstall command and install
  folder (patterns taken from the dev machine: 38 Steam games, Battle.net's WoW / Diablo IV) and
  are never changed through winget, even when named. Launchers themselves are self-updating
  (`explicit`), not "managed by" themselves.
- **2026-10-23** - Adding a value to a catalog enum (`HoldBehavior.Explicit`) breaks older
  readers (the whole set is rejected and they fall back to cache/embedded). Fine before the first
  release; after it, a new enum value needs a `schemaVersion` bump.
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
- **2026-10-23** - Debloat default removes ads/junk only; everything else is opt-in; unknown
  Appx are surfaced for review, never auto-removed.
- **2026-10-23** - Alerting in-app only.
- **2026-10-23** - Unsigned to start.
- **2026-10-23** - Leftover cleanup stubbed for 0.1.0, fast follow in 0.2.0, quarantine not
  delete.
