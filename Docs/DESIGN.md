# Updatr - Design

Portable Windows machine manager. One folder on a thumb drive keeps a mature machine current,
builds a freshly imaged one from a cart of templates, and strips the ads and junk Windows ships
and keeps adding. Runs on a stock or slim Windows install with nothing pre-installed: no .NET,
no PowerShell 7, no winget required.

See `TECHARCH.md` for how it is built and `workplan.md` for build state.

## Goals

- **Portable.** Copy the folder to a drive, plug into any Windows 10/11 machine, run. All user
  data (carts, templates, settings, logs, debloat history) lives in `Updatr.data\` beside the
  binaries. A cart made on machine A is there on machine B. Nothing is per-machine.
- **Self-sufficient.** If the machine is missing something Updatr needs (winget, App Installer,
  Node for an npm item, ...), Updatr says what is missing and why, and offers to install it.
- **Robust.** Every step is checked before it runs (network, elevation, disk, installer locks,
  pending reboot), failures are decoded into plain English, and one failure never sinks a batch.
- **Responsive.** The UI never blocks. Everything long-running is async, cancelable, and streams
  progress.
- **GUI and CLI parity.** Every action available in the GUI is available in `updatr-cli.exe`,
  backed by the same code.
- **Real application, not a script wrapper.** Native APIs throughout. No PowerShell anywhere in
  the product. Ecosystem tools (npm, pipx, dotnet, code) are invoked only because their CLI *is*
  their interface.
- **Source of truth stays upstream.** Installers come from winget, the vendor, or the package
  registry. The repo holds metadata only (catalog JSON). A mirror hook exists but is unused.

## Non-goals (for now)

- Scoop, Chocolatey, cargo/rustup.
- Remote alerting (toast, email, webhook). Alerting is in-app only.
- Per-machine profiles or settings.
- Hosting installer binaries.
- Linux / non-Windows hosts.

## Distribution

- GitHub Releases, one zip per architecture (`win-x64`, `win-arm64`), each containing
  `Updatr.exe` (GUI), `updatr-cli.exe` (CLI), and `Microsoft.Management.Deployment.winmd` (a 64 KB
  file WinGet's API needs beside the exes), plus a SHA256 file.
- Unsigned to start. SmartScreen / Defender may warn; the README documents unblocking.
- **Self-update:** checks GitHub Releases on launch (silent on network failure), offers the new
  version, downloads and verifies it, swaps the binaries on the drive, and relaunches.

## Startup: splash + preflight

A splash window opens immediately: app icon, name, version, a progress bar, and the current
check ("Checking internet connection...", "Checking winget..."). Each check lands as a row with
a pass / warn / fail mark. Passing runs close the splash and open the main window automatically.
Warnings let you continue. Failures stay on the splash with a reason and, where possible, a fix
button (Install winget, Retry, Continue offline).

Checks:

| Check | Fail means |
|---|---|
| OS version, build, edition (incl. LTSC / Server detection) | Informational; shapes later checks |
| Architecture | Picks the right installers |
| Data folder writable (drive not read-only, not full) | Warn: runs without saving history/carts |
| Internet: DNS + HTTPS to GitHub, raw.githubusercontent, winget CDN; proxy detection | Offline mode: cached catalog, no installs |
| Free disk space on the system drive | Warn below threshold |
| Elevation available (user is an admin) | Admin-only features disabled with explanation |
| Pending reboot | Warn: installs may fail until reboot |
| Installer busy (MSI mutex, running winget, Windows Update installing) | Warn: queue waits instead of colliding |
| winget present, version, sources healthy | Offer bootstrap (works on LTSC too) |
| Each ecosystem tool (npm, pipx/pip, dotnet, code, PSGallery path) | That source is hidden until installed |
| Updatr self-update available | Banner in main window |

The splash stays up for at least 8 seconds even when everything passes, so the results can be
read; a slower run is never held longer than it took.

The CLI equivalent is `updatr-cli preflight` (`--only <ids>`, `--verbose`, `--json`).

## Screens

### Updates

Everything installed, across all sources, in one grid: name, source, id, installed version,
available version, status. Filters: has-update, held, source, search.

- Select one, many, or all; **Upgrade**, **Reinstall**, **Pin/Hold**, **Unhold**.
- **Held** packages (version-sensitive, e.g. Node, databases, CUDA, WSL, VC redists) show a
  marker and are skipped by "Upgrade all" unless explicitly included. Hold rules ship in the
  catalog (ported from `Get-WingetUpdates.ps1`) and you can add or remove your own.
- Packages that cannot be upgraded from the CLI (App Installer) are labeled "updates via Store"
  instead of failing every run.
- **Launcher-managed apps** (Steam, Battle.net, Epic, Ubisoft, EA, GOG, Riot, Rockstar games) are
  recognized and labeled "Managed by Steam" etc. Updatr never changes them through winget - doing
  so risks a second copy outside the launcher or a broken install. Updating through the launcher
  ("open in Steam / validate") is a later addition.
- **Self-updating apps** (the launchers themselves, Discord) are skipped by "Upgrade all" and
  upgraded only when picked explicitly.
- **Needs a fresh install:** when the newer version switches installer technology (exe to MSI,
  MSI to MSIX), winget cannot upgrade in place; the row says so and why instead of failing.
- Outcomes per item: **Upgraded**, **Already current**, **Failed** (with decoded reason),
  **Skipped**. Reboot-required is surfaced.

### Install (cart)

Ninite-style picker over the catalog: categories, search, checkboxes. Already-installed items
are marked (and can be selected for reinstall).

- **Templates** are named sets (Base, Dev, Gaming, Media, ...). Applying one or several adds
  their items to the cart; duplicates merge.
- **Cart:** see everything queued, remove items, change source when an app has more than one,
  save as a named cart, load a saved cart, merge a saved cart into the current one.
- **Template editor:** create, copy, rename, and edit templates. Built-in templates (from the
  repo catalog) are read-only; "Copy to my templates" makes an editable one.
- **Dependencies:** if an item needs a tool that is not present (Node for an npm package,
  Python for pipx), the prerequisite is added to the cart as its own line, labeled with why
  ("Required by: typescript (npm)"). Removing it warns that the dependent items will fail.
- **Run:** downloads in parallel, installs one at a time, per-item progress and log, cancel at
  any point (current installer finishes; the rest are skipped).

### Debloat

Removes ads and junk, and re-runs cleanly after Windows feature updates.

- **Recommended (default):** ads and junk that should never have shipped. Sponsored and
  auto-installed Store apps, Start / Settings / lock screen / File Explorer ads and suggestions,
  "finish setting up" nags, consumer-feature auto-installs, plus baseline privacy and telemetry
  reduction.
- **Optional (opt-in, listed individually):** Edge, OneDrive, Copilot, Recall, Widgets, Xbox
  apps, Game Bar, Teams (consumer), new Outlook, Phone Link, Bing in Start search, stricter
  privacy settings, and more. Pick any.
- **Unrecognized:** any removable Store app that is neither on the keep list nor a known item
  shows up here for review. This is how new junk is caught without anyone naming it first.
- Each item shows its current state (applied / not applied) before you run anything, so a
  re-run only touches what has drifted. A "new since last run" filter highlights what Windows
  added or reverted since last time.
- Before applying: restore point (best effort) plus Updatr's own registry backup in
  `Updatr.data\debloat\backups\`. Each item can be reverted from the history list.
- Rules ship in the catalog, so new junk is handled by updating the repo, not the binary.

### Uninstall

- **v0.1:** list installed apps across sources, uninstall one or many through each app's own
  uninstaller (winget - which runs the registered quiet uninstall command for any app it lists -
  or the ecosystem tool). Game launchers' games and winget itself are refused.
- **v0.2 (fast follow) - leftover cleanup:** after the uninstaller runs, scan for leftovers
  (install folder, AppData/ProgramData folders, Software and Uninstall registry keys, services,
  scheduled tasks, shortcuts, firewall rules). Each is shown with a confidence level, **nothing
  is pre-checked**, and anything removed is moved to `Updatr.data\quarantine\` (files zipped,
  keys exported as `.reg`), not deleted. Quarantine entries can be restored or purged.
- **Install footprints:** for apps Updatr installed itself, it records a before/after snapshot
  so the leftover scan knows the exact footprint instead of guessing.

### Settings / Log

Data folder location, catalog source and refresh, hold rules, debloat keep list, log viewer
(DiagLog-style categories), run reports, about / version / check for updates.

## Alerting

In-app only:

- A banner strip at the top of the main window for app-level state (offline, update available,
  reboot pending, elevation declined).
- Per-item status and decoded error on every grid row and queue item.
- End-of-run summary dialog (n succeeded, n already current, n skipped, n failed, with reasons),
  also saved to `Updatr.data\logs\` as a report.

## CLI

`updatr-cli.exe <command>`, exit code non-zero on any failure, `--json` for machine-readable
output, `--yes` to skip confirmations.

```
updatr-cli preflight [--only <check>...] [--verbose]
updatr-cli admin test
updatr-cli list [--updates] [--source winget|npm|...]
updatr-cli upgrade [<id>...] [--all] [--include-held] [--include-unknown]
updatr-cli winget repair
updatr-cli reinstall <id>...
updatr-cli hold <id> | unhold <id>
updatr-cli install <id>... [--template <name>...] [--cart <name>...]
updatr-cli cart list | show <name> | save <name> | merge <name>...
updatr-cli template list | show <name>
updatr-cli debloat status [--changed] | apply [--recommended] [--item <id>...] [--app <name>...]
updatr-cli debloat revert <id>... | history | keep <name>... | unkeep <name>...
updatr-cli uninstall <id>... [--no-leftovers]
updatr-cli leftovers list | quarantine <scan> --item <n>... | --high | dismiss <scan>
updatr-cli quarantine list | restore <entry> | purge <entry>
updatr-cli self-update
```

## Starter templates (draft - to be tweaked)

Exact package ids are verified when the catalog is authored.

- **Base:** 7-Zip, Windows Terminal, PowerShell 7, Notepad++, a browser (Firefox / Chrome),
  Everything (search), PowerToys, ShareX.
- **Dev:** Git, GitHub CLI, VS Code, .NET SDK (current LTS), Node.js LTS, Python 3, pipx,
  JetBrains Toolbox, Docker Desktop, WSL, Postman / Bruno, a set of VS Code extensions
  (C#, Python, ESLint, GitLens), npm globals (typescript, pnpm).
- **Gaming:** Steam, Battle.net, Epic Games Launcher, GOG Galaxy, Discord, NVIDIA App,
  Overwolf / CurseForge.
- **Media:** VLC, Spotify, OBS Studio, HandBrake, Audacity, GIMP, IrfanView / ImageGlass.
- **Productivity:** Obsidian, LibreOffice, Adobe Acrobat Reader, Zoom, Slack.

## Known issues / limitations

- **ARM64** builds ship but are untested on real hardware.
- **Unsigned binaries** may trigger SmartScreen or Defender (debloat tools are often flagged).
- **LTSC:** winget is bootstrapped, but the `msstore` source stays unavailable unless the Store
  is installed (backlog).
- **Restore points** are throttled by Windows (one per 24 h by default) and disabled on many
  machines; Updatr's own backup is the primary undo.
- **Edge is not removed.** Microsoft allows it only in the EEA, and removal breaks WebView2 apps;
  the optional item turns off Edge's nags and background running by policy instead.
- **Store app removal is not perfectly reversible:** revert reinstalls from the Store.

## Roadmap / backlog

- Leftover cleanup + install footprints (v0.2).
- Opt-in scheduled task, e.g. "debloat after each feature update".
- Reinstall the Microsoft Store on LTSC.
- Mirror fallback for direct-URL installers (S3 / home server) - schema field exists now.
- Code signing (Azure Trusted Signing) if Defender becomes a nuisance.
- ARM64 hardware validation.
- Linux / Raspberry Pi host (separate product idea; no winget, no Appx, no registry).
