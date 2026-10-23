# Updatr - Work Plan

Living build plan. Update **Current state** and check boxes as work proceeds; append to the
**Decisions log** when something is decided. See `DESIGN.md` (product) and `TECHARCH.md`
(architecture).

---

## Current state

- **Phase:** planning - design docs drafted, awaiting review.
- **Last updated:** 2026-10-23
- **Build status:** no code yet.
- **Next:** review DESIGN / TECHARCH, then Phase 0.

## Versioning

- `0.0.x` preview releases as phases ship usable pieces.
- **`0.1.0`** = Phases 0-7: updates, install/carts, defluff, uninstall (uninstaller only),
  self-update, CLI parity.
- **`0.2.0`** = Phase 8: leftover cleanup + install footprints.

---

## Phase 0 - Scaffold and spikes

- [ ] Solution `Updatr.slnx`: Core, Platform, App, Cli, Core.Tests, Platform.Tests
- [ ] `Directory.Build.props` (version 0.0.1, nullable, implicit usings, warnings as errors)
- [ ] `.gitignore`, `README.md`, `CLAUDE.md`
- [ ] `Scripts/bump-version.sh`, `Scripts/run.sh`
- [ ] `release.yml` with win-x64 + win-arm64 matrix, current action majors (verified)
- [ ] Convention tests: US English (ported list), ASCII-only, no PowerShell in `Src/`
- [ ] Spike 1: WinGet COM from .NET 10 (unelevated + elevated)
- [ ] Spike 2: Avalonia single-file publish, x64 + arm64, startup from USB
- [ ] Spike 3: CLI trim / AOT with WinGet interop
- [ ] Spike 4: rename running exe on FAT32/exFAT
- [ ] Spike 5: App Installer bootstrap via `PackageManager` on a clean VM
- [ ] Record spike outcomes in the decisions log

## Phase 1 - Core models and catalog

- [ ] Catalog models (apps, sources, templates, holds, defluff rules) + JSON schema
- [ ] Catalog loader: remote -> cache -> embedded, schemaVersion gate
- [ ] Port the error code table + HTTP-facility decoding (`legacy-script.md`) to
      `Errors/ErrorCodes` with tests
- [ ] Port the hold list (`legacy-script.md`) to `catalog/holds.json`, reasoning as descriptions
- [ ] `PackageOutcome` model (Ok / AlreadyCurrent / Failed / Skipped + reboot flag)
- [ ] Operation + sink abstractions; parity test scaffold

## Phase 2 - Host foundation

- [ ] Portable data store (`Updatr.data\`, atomic writes, fail-soft on drive removal)
- [ ] DiagLog + per-run log files
- [ ] Preflight checks (all rows in DESIGN)
- [ ] Splash window with icon, version, progress, per-check rows, fix buttons
- [ ] Main window shell: nav, banner strip, run summary dialog
- [ ] Elevation broker (pipe, ACL, typed requests, lifetime)
- [ ] CLI host: System.CommandLine, console sink, `--json`, `doctor`

## Phase 3 - winget + Updates screen

- [ ] winget source via COM, text fallback
- [ ] winget bootstrap (incl. LTSC path)
- [ ] Updates grid: incremental load, filters, multi-select
- [ ] Upgrade / reinstall / hold / unhold, batch + single
- [ ] Collision waits (MSI mutex, winget running)
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

## Known edges

- ARM64 untested on hardware.
- Unsigned binaries: SmartScreen / Defender warnings.
- Restore points throttled (24 h) or disabled; own backup is the primary undo.
- Edge removal restricted outside the EEA.

## Decisions log

- **2026-10-23** - `Get-WingetUpdates.ps1` documented in `legacy-script.md` and deleted.
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
