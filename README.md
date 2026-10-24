# Updatr

A portable Windows machine manager you can run from a thumb drive.

- **Update** everything installed - winget, Microsoft Store, npm, pipx, dotnet tools, PowerShell
  Gallery modules, VS Code extensions - from one list. Upgrade, reinstall, or hold, one at a time
  or in batches.
- **Install** a new machine Ninite-style: pick apps or whole templates (Base, Dev, Gaming, ...),
  merge them into a cart, save and reuse carts.
- **Debloat** Windows: remove ads, sponsored apps, and junk, and re-run safely after feature
  updates to catch whatever Windows added back.
- **Uninstall** apps through their own uninstallers, with leftover cleanup to follow.

Works on a fresh install with nothing on it: no .NET, no PowerShell 7, no winget required -
Updatr checks what is missing and offers to install it.

> **Status:** early development. Nothing is released yet; see `Docs/workplan.md`.

## Using it

1. Download `Updatr-<version>-win-x64.zip` (or `win-arm64`) from
   [Releases](https://github.com/matthewjenner/Updatr/releases).
2. **Unblock the zip before extracting** (right-click > Properties > Unblock). The binaries are
   not code-signed yet, so SmartScreen may warn on first run.
3. Extract the folder anywhere - a thumb drive is the intended home. Keep the three files
   together: `Updatr.exe`, `updatr-cli.exe`, `Microsoft.Management.Deployment.winmd`.
4. Run `Updatr.exe` for the GUI, or `updatr-cli.exe --help` for the command line.

Your carts, templates, settings, and logs are kept in `Updatr.data\` beside the binaries, so they
travel with the drive.

## Building

Requires the .NET 10 SDK (version pinned in `global.json`).

```bash
dotnet build
dotnet test
./Scripts/run.sh            # build + run the GUI
./Scripts/publish.sh        # portable zips in dist/
```

## License

MIT - see `LICENSE`.
