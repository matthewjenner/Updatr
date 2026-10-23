# Updatr

Portable Windows machine manager. Runs from a thumb drive on a mature or freshly imaged machine:
lists and applies updates across winget and other ecosystems, installs from a Ninite-style cart of
templates, removes Windows ads and junk (defluff), and uninstalls apps. GUI (`Updatr.exe`) and CLI
(`updatr-cli.exe`) with full parity.

See `Docs/DESIGN.md` for product detail and `Docs/TECHARCH.md` for the architecture.
`Docs/workplan.md` is the living build tracker - current phase, what's done, what's next, and the
decisions log. `Docs/legacy-script.md` is the rationale carried over from the PowerShell script
Updatr replaces (hold list, error codes). This file is the quick orientation - read it first.

## Stack

- .NET 10 (SDK pinned in `global.json`), C#
- Avalonia (GUI) + CommunityToolkit.Mvvm source generators
- System.CommandLine (CLI)
- WinGet COM API via `Microsoft.WindowsPackageManager.ComInterop` (CsWinRT projection)
- xUnit v3 on Microsoft.Testing.Platform + AwesomeAssertions
- Package versions are central in `Directory.Packages.props`

## Layout

```
Src/Updatr.Core/            Pure logic: models, operations, abstractions. net10.0, no platform deps.
Src/Updatr.Platform/        Windows implementations shared by GUI and CLI. net10.0-windows.
Src/Updatr.App/             Avalonia GUI -> Updatr.exe
Src/Updatr.Cli/             Console -> updatr-cli.exe
Tests/Updatr.Core.Tests/    Unit + convention tests.
Docs/                       DESIGN, TECHARCH, workplan, legacy-script.
Scripts/                    run.sh, publish.sh, bump-version.sh.
.github/workflows/          release.yml - calls Scripts/publish.sh, releases on version bump.
Directory.Build.props       Version source of truth + shared Windows TFM / RID properties.
Directory.Build.targets     Keeps the WinGet .winmd beside the exe on publish.
```

## Build & run

```bash
dotnet build
dotnet test                         # MTP runner (global.json)
./Scripts/run.sh                    # clean + build + run GUI (Debug|Release, App|Cli)
./Scripts/run.sh Debug Cli -- --help
./Scripts/publish.sh                # dist/ zips for win-x64 + win-arm64, as CI builds them
```

## Hard rules

- **No PowerShell in `Src/`.** Native APIs only. Shelling out is allowed only to an ecosystem's own
  CLI (npm, pipx, dotnet, code) through `IProcessRunner`. Enforced by `NoPowerShellConventionTests`.
- **Every user action is a Core operation;** GUI ViewModels and CLI commands are thin adapters.
- **Core is pure.** No Avalonia, WinRT, P/Invoke, or file paths in `Updatr.Core`.
- **ASCII only, US English** in code, UI strings, comments, docs, JSON. Enforced by
  `AsciiConventionTests` and `UsEnglishConventionTests`. Write a non-ASCII data character as a
  C# unicode escape (backslash, `u`, four hex digits), never literally. Editing tools can turn a
  typed escape into the literal character - the test will catch it.
- **Portable:** all user data lives in `Updatr.data\` beside the exe. Never store a path that
  includes the drive letter. Downloads go to the host's temp folder, not the drive.

## Conventions

- **MVVM:** ViewModels never reference Views. `[ObservableProperty]` / `[RelayCommand]`.
- **Async everywhere, cancelable:** every operation takes a `CancellationToken`. No `.Result`,
  `.Wait()`, or `Thread.Sleep` on any path the UI waits on.
- **Threading:** operation events reach the GUI through its sink, which marshals with
  `Dispatcher.UIThread.Post`. ViewModels are touched only on the UI thread.
- **Diagnostics:** typed methods on `DiagLog` (Klakr pattern); no free-form log strings elsewhere.
- **Naming:** ViewModels end in `ViewModel`, Views in `Window` or `View`, operations are verb-noun
  (`UpgradePackages`), sources end in `Source`.

## Gotchas

- **The WinGet `.winmd` must sit beside the exe.** WinGet's COM server is out of process; an
  unpackaged app resolves the metadata to marshal its interfaces from `.winmd` files in the exe's
  own directory. Inside a single-file bundle it is invisible and the first call fails with
  `0x80073D54` "Interface not registered". `Directory.Build.targets` excludes it from the bundle;
  `Scripts/publish.sh` ships it in the zip. No custom activation code is needed once it is there.
- **WinGet COM needs a RuntimeIdentifier.** The interop package copies a per-architecture native
  factory and errors on AnyCPU. App and Cli set a RID (host arch by default) and reference the
  package directly; Platform opts out of the error because a library has no architecture.
- **One bad winget index entry fails the whole upgrade list** - `winget upgrade` and the COM API
  alike (`0x80070057` from `VersionRange::operator<` when two manifests' ranges overlap). Never
  issue one "list everything" query. Query the composite catalog in parallel partitions by name
  prefix; split a failing partition one level further and report only its packages as
  "could not check". See `Docs/workplan.md` decisions log (Phase 0).
- **Composite searches need non-explicit catalogs only.** Skip catalogs whose `Info.Explicit` is
  true (`winget-font`). Wrap the read in try/catch - older servers lack the property.
- **Do not trim.** WinRT.Runtime and the Windows SDK projection are not trim-safe (IL2104). Publish
  is single-file + compressed, untrimmed.
- **Version 0.0.0 never releases.** CI builds, tests, and packages it, but the release step is
  skipped. The first `bump-version.sh` produces the first release.

## Common tasks

- **Add a package version:** check nuget.org for the latest stable first, then add a
  `PackageVersion` to `Directory.Packages.props` and a version-less `PackageReference`.
- **Cut a release:** `./Scripts/bump-version.sh` (default Patch; `Minor` / `Major`), commit, push
  to `main`. CI skips if that release exists. Check what has shipped first:
  `gh release list --limit 5`. Bump only for user-visible changes, not docs/refactors.
- **Add a convention test:** walk files with `RepositoryFiles.Authored()` (everything a person
  wrote) or `RepositoryFiles.Source()` (what ships), so new folders and extensions are covered by
  every convention at once.

## What NOT to do

- Don't run PowerShell, or wrap a script and call it a feature.
- Don't auto-remove anything defluff does not recognize; surface it for review.
- Don't delete leftover files/keys during uninstall cleanup; quarantine them.
- Don't host or mirror installers; pull from upstream (the mirror hook stays a stub).
- Don't add per-machine state; carts, templates, and settings travel with the drive.
