# Updatr

A portable Windows machine manager you can run from a thumb drive.

- **Update** everything installed - winget, Microsoft Store, npm, pipx, dotnet tools, PowerShell
  Gallery modules, VS Code extensions - from one list. Upgrade, reinstall, or hold, one at a time
  or in batches.
- **Install** a new machine Ninite-style: pick apps or whole templates (Base, Dev, Gaming, Media,
  Productivity), merge them into a cart, save and reuse carts. Missing prerequisites (Node for npm
  packages, VS Code for extensions) are added automatically.
- **Debloat** Windows: remove ads, sponsored apps, and junk, and re-run safely after feature
  updates to catch whatever Windows put back. Every change is backed up and can be reverted.
- **Uninstall** apps through their own uninstallers.

Works on a fresh or slim install: no .NET, no PowerShell 7, and no winget required - Updatr
checks what is missing on startup and offers to fix it.

## Getting started

1. Download `Updatr-<version>-win-x64.zip` (or `win-arm64` for ARM PCs) from
   [Releases](https://github.com/matthewjenner/Updatr/releases).
2. **Unblock the zip before extracting:** right-click it > Properties > tick **Unblock** > OK.
   Updatr is not code-signed yet. Without unblocking, every file inherits the "downloaded from the
   internet" mark and Windows warns each time.
3. Extract the folder anywhere - a thumb drive is the intended home. Keep the three files
   together: `Updatr.exe`, `updatr-cli.exe`, `Microsoft.Management.Deployment.winmd`.
4. Run `Updatr.exe`. If SmartScreen says "Windows protected your PC", choose **More info > Run
   anyway** (once per machine).

Updatr runs as you and asks for administrator access (one UAC prompt per session) only when
something machine-wide needs it. Declining just skips those items.

Your carts, templates, holds, debloat history and backups, and logs live in `Updatr.data\`
beside the exe, so they travel with the drive. Nothing is stored on the machine itself.

## The screens

- **Startup checks** - Windows version, network, winget, the app catalog, tools, admin access,
  pending reboot, disk space, the data folder. Problems show as banners, with a fix button where
  one exists (for example "Install winget" on LTSC or a slim image).
- **Updates** - every installed package with its update, if any. Upgrade checked or all, reinstall
  to repair, or hold a package so "upgrade all" leaves it alone. Version-sensitive packages
  (databases, SDK majors, ...) are held by default; game launchers' games are never touched
  outside their launcher.
- **Install** - browse the catalog, check apps or add templates to the cart, then install.
  Already-installed apps are skipped. Save carts and your own templates for the next machine.
- **Debloat** - each item shows whether it is applied on this PC. Recommended items (ads, junk,
  baseline privacy) are pre-selected; optional ones (Copilot, OneDrive, Xbox, Edge nags, ...) are
  yours to pick. Store apps Updatr does not recognize are listed for review, never removed on
  their own. History lets you revert any item.
- **Uninstall** - everything installed, removable through its own uninstaller.
- **Settings** - where your data is, the run log, admin test, and **Check for an update**
  (downloads the latest release, verifies its checksum, and replaces the files on the drive).

## Command line

`updatr-cli.exe` does everything the GUI does. Add `--yes` to skip confirmations and `--json` for
one JSON object per line.

```
updatr-cli preflight                         check this machine
updatr-cli list [--updates]                  installed packages and updates
updatr-cli upgrade --all | <id>...           upgrade (holds respected by --all)
updatr-cli reinstall <id>...                 repair an install
updatr-cli hold <id> | unhold <id>           keep a package out of "upgrade all"
updatr-cli uninstall <id>...                 remove through the app's own uninstaller
updatr-cli catalog list                      apps Updatr can install
updatr-cli install <app>... [-t <template>] [-c <cart>]
updatr-cli cart list | show | save | remove | delete
updatr-cli template list | show | save | copy | delete
updatr-cli debloat status [--changed]        what is applied, what Windows put back
updatr-cli debloat apply --recommended | --item <id> | --app <name>
updatr-cli debloat revert <id> | history | keep <name> | unkeep <name>
updatr-cli winget repair                     install or fix winget (LTSC, slim images)
updatr-cli admin test                        prove elevation works (one UAC prompt)
updatr-cli self-update                       update Updatr on this drive
```

Exit codes: 0 success, 1 something failed, 2 usage error, 130 canceled.

## How it stays safe

- No PowerShell scripts: Updatr talks to Windows and winget through their APIs, and to npm, pipx,
  dotnet, and VS Code through their own command lines.
- One installer at a time, waiting for any other install to finish first.
- Debloat backs up every value, service, and task before changing it (JSON plus a `.reg` file in
  `Updatr.data\debloat\backups\`). If the backup cannot be saved - a write-protected drive - it
  changes nothing.
- Admin work happens in a separate elevated helper that accepts only a fixed set of requests.
- The app catalog (apps, templates, holds, debloat rules) is fetched from this repository on each
  start, so fixes reach every copy without a new release; a copy is kept on the drive and one is
  built in for offline use.

## Known issues

- **Unsigned binaries:** SmartScreen warns on first run, and some antivirus products flag debloat
  tools in general. Unblock the zip (above); the source is all here.
- **Restore points** are best effort: Windows makes at most one per 24 hours by default, and
  System Restore is off on many PCs. Updatr's own backup is the real undo.
- **Edge is not removed** - Microsoft allows that only in the EEA, and removal breaks apps built on
  its WebView. The Edge item turns off its nags and background running instead.
- **Removed Store apps** come back from the Microsoft Store on revert, where the Store still
  offers them.
- **Uninstall leaves leftovers** (settings folders, registry keys). Leftover cleanup, with
  quarantine instead of deletion, is planned for 0.2.
- **ARM64** builds are published but not yet tested on ARM hardware.
- **LTSC:** winget is installed for you, but Microsoft Store apps stay unavailable without the
  Store.

## Building

Requires the .NET 10 SDK (version pinned in `global.json`).

```bash
dotnet build
dotnet test
./Scripts/run.sh            # build + run the GUI
./Scripts/publish.sh        # portable zips in dist/, as CI builds them
```

See `Docs/DESIGN.md` for the product and `Docs/TECHARCH.md` for the architecture.

## License

MIT - see `LICENSE`.
