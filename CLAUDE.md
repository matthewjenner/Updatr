# Updatr

Portable Windows machine manager. Runs from a thumb drive on a mature or freshly imaged machine:
lists and applies updates across winget and other ecosystems, installs from a Ninite-style cart of
templates, removes Windows ads and junk (debloat), and uninstalls apps. GUI (`Updatr.exe`) and CLI
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
Tests/Updatr.Platform.Tests/ Real files, named pipes, processes (no network, no UAC).
Tests/Updatr.Cli.Tests/     Command tree + the GUI/CLI parity test.
catalog/                    apps.json, templates.json, holds.json + generated schema/. Fetched live
                            by every Updatr in the field, and embedded in Core as the fallback.
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
./Scripts/run.sh Debug Cli -- preflight
./Scripts/run.sh Debug Cli -- admin test   # one UAC prompt; proves the elevated broker works
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
- **No checkboxes for picking items.** Rows implement `ISelectableRow`; grids use
  `b:RowSelection.Enabled="True"` (click toggles, highlight mirrors `IsSelected`), other lists a
  `Border` with `b:RowSelection.ToggleOnClick="True"` and `Classes.selected`. On/off settings use
  `ToggleSwitch`.
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
  `Scripts/publish.sh` ships it in the zip.
- **Activate WinGet through the explicit handler,** not CsWinRT's default probing (which cannot see
  native DLLs inside a single-file bundle). The handler on `WinRT.ActivationFactory` loads the
  native factory via `NativeLibrary.Load` and calls `DllGetActivationFactory`; it is the path
  measured working both unelevated and elevated. See `Docs/TECHARCH.md`.
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
- **Optional JSON model properties coerce null in their setter**
  (`{ get; init => field = value ?? []; } = [];`). The source-generated deserializer assigns every
  init property and passes null for omitted fields, silently wiping `= []` defaults.
- **No `[JsonIgnore]` on abstract or virtual members of polymorphic models.** It does not carry
  over to overrides, which then leak into the JSON and the schema. Compute such members in the
  base class (see `PackageSourceRef.RequiredTool`).
- **Call `WinGetRuntime.EnsureReady()` (or `CreatePackageManager()`) before any
  `Microsoft.Management.Deployment` type.** It installs the activation handler and restores the
  `.winmd` beside the exe if it is missing.
- **Do not cancel cross-process work from a `CancellationToken.Register` callback in a method
  that also awaits `WaitAsync(token)`.** Callbacks run newest-first; `WaitAsync`'s resumes the
  method, which disposes the registration before yours runs. Catch the cancellation and act
  there (see `BrokerClient.SendAsync`).
- **`IDataStore` throws only `DataStoreException`** (and `ArgumentException` for a path that
  escapes the folder). The drive may be read-only or yanked; decide per call whether it matters.
- **Do not `taskkill` a running `Updatr.exe` the user started.** Only kill dev instances you
  launched yourself from `bin/`.
- **winget's COM API and CLI disagree on updates.** COM says "no update" when the new version
  changes installer technology (the CLI lists it and the upgrade fails with `0x8A15008E`); the CLI
  hides `RequireExplicitUpgrade` packages that COM reports, and COM does not expose that flag.
  Follow COM; surface the first as "needs a fresh install", cover the second with `explicit`
  holds in the catalog. Never "fix" a mismatch by trusting the CLI's list.
- **Launcher-managed apps are never changed through winget, even when named.** `PackageBatch`
  enforces it; `LauncherDetector` decides it. Launchers themselves are `explicit`, not managed.
- **Adding an enum value to a catalog model breaks older readers** (unknown value -> whole set
  rejected). Bump `CatalogFiles.SchemaVersion` for it once a release exists.
- **VS Code Marketplace "latest" is not "latest stable for this machine".** Pick the newest
  non-pre-release entry for this platform or universal (see `EcosystemParsers.VsCodeMarketplace`).
- **npm, pipx, and code are `.cmd` files** - cmd.exe re-parses their arguments. Validate every
  name/version with `EcosystemParsers.IsSafePackageName` / `IsSafeVersion` before passing it.
- **Signer names are the exact certificate CN.** Microsoft signs `dotnet.exe` as ".NET", not
  "Microsoft Corporation". Check a new direct-URL entry's signer with `Authenticode.Check`.
- **The live catalog comes from GitHub, so unpushed catalog edits are invisible** to a normal run.
  Set `UPDATR_CATALOG_DIR=<repo>/catalog` to test them (the splash says "Local catalog folder").
- **A process keeps its start-up PATH.** After installing something that adds a tool (Node, VS
  Code, .NET SDK), call `IToolEnvironment.Refresh()` before running that tool.
- **Late-bound COM throws mapped .NET exceptions,** not `COMException` (0x80070002 arrives as
  `FileNotFoundException`). Match `Exception.HResult` (see `ScheduledTaskTarget`).
- **A `= true` default on a JSON model bool is lost** when the field is omitted (the deserializer
  assigns false). Name such flags negatively, default false (`AppxAction.KeepProvisioned`). Other
  defaults the same way: `UserSettings` keeps `int?` fields (null = default) for that reason.
- **winget's installed scope says "System" for per-machine installs,** not "Machine". Parse it
  with `WinGetComSource.ParseScope`, never a string compare.
- **Index WinRT lists, never enumerate them.** `foreach` / LINQ over a CsWinRT `IReadOnlyList`
  from the WinGet API throws `InvalidCastException` ("No such interface supported"); use
  `for (int i = 0; i < list.Count; i++)`.
- **A running exe can be renamed, not overwritten.** Self-update relies on it (`SelfUpdater.Swap`);
  keep the release file list there in step with `release_files` in `Scripts/publish.sh`.
- **An installer's success does not mean the new version is active.** Open MSIX apps get their
  update staged (winget still says OK); some apps switch versions on next start. `PackageBatch`
  re-reads the version after an upgrade and reports `OutcomeKind.Pending`; any new front-end text
  for outcomes must handle Pending.
- **Uninstallers often finish in the background.** Never trust state right after one returns:
  the leftover scan waits for the Uninstall key to go, and the review list re-checks every item.
- **"In use" is answered by Restart Manager** (`InUseFinder`), over the app's folders plus
  `InUseFinder.ExtraFolders` for installers that check files elsewhere (OBS's capture hook loads
  into any Vulkan app). `RM_UNIQUE_PROCESS` holds a FILETIME: two uints, not a long - a long pads
  the struct and garbles every entry after the first.
- **Leftovers are never deleted, only quarantined,** and only within `LeftoverSafety`. A new place
  to look must also pass `LeftoverRules.IsOfferableFolder` / `LeftoverSafety.Problem` and get tests
  in `LeftoverTests`.
- **Keep winget COM load low.** Two listing workers, one connection each; lookups serialized
  (`WinGetComSource.DefaultParallelism`). Anything nobody is waiting on (the GUI's background
  check) lists with `ListRequest(Quietly: true)` - one worker. Heavier load preceded three 0x153 bugchecks on the dev
  machine (a driver bug, triggered by it). And never kill an Updatr process mid-listing in
  testing (`| head`): write output to a file instead.
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
- **Edit the catalog:** change `catalog/*.json` (your editor validates against `catalog/schema/`),
  then `dotnet test`. `RepositoryCatalogTests` parse it strictly, so typos fail. Verify a new
  winget id with `winget show --id <id> --exact` first. Once the remote origin ships (Phase 2), pushing
  to `main` updates every Updatr in the field on its next launch - no release needed for
  catalog-only changes.
- **Change a catalog model:** edit the record in `Src/Updatr.Core/Catalog/`, run
  `UPDATR_UPDATE_SCHEMAS=1 dotnet test`, commit the regenerated schema with it. Bump
  `CatalogFiles.SchemaVersion` only for breaking changes (renamed/retyped/new required fields).
- **Decode an installer or winget code:** `ErrorCodes.Describe(code)` / `PackageOutcome.FromCode`.
  New winget codes come from `AppInstallerErrors.h`; keep the constant name in the comment.
- **Add a preflight check:** implement `IPreflightCheck` in `Src/Updatr.Platform/Preflight/`
  (derive `IndependentCheck` if it needs no other check's result), return Warn/Fail rather than
  throwing for expected conditions, write shared facts to `PreflightContext`, and add it to
  `UpdatrHost.CreateChecks()` in display order. It appears on the splash, in Settings, and in
  `updatr-cli preflight` with no other changes.
- **Add something that needs admin:** add an `IBrokerHandler` to `BrokerHandlers.All` (typed
  payload in, result out, progress via `emit`), call it with
  `host.Elevation.GetBrokerAsync()` + `SendAsync`. Treat a null broker as "declined": skip
  with a reason.
- **Add a user-facing action:** a class implementing `IOperation<,>` with `[Operation("verb noun")]`,
  a CLI command at that path in `CliApp`, and a GUI entry point. `ParityTests` fails until the
  CLI command exists.
- **Add an ecosystem source:** derive `ToolSource` (or implement `IPackageSource` if it is native),
  put its output parsers in `EcosystemParsers` with tests on real captured output, add it to
  `UpdatrHost.Sources`, and its filter label to `AppKinds.SourceLabel` and `PackageFilters.Sources`.
- **Sort a package into the right kind:** adjust the patterns in `AppKinds` (Core) and add the real
  name to `AppKindsTests`. Kinds only drive filters and the Kind column, never what Updatr does.
- **Add a filter to a list:** a `FilterGroupViewModel` (title, options, key-of-row) in the page's
  `FilterBarViewModel`, a `<views:FilterBarView>` bound to it, and the same filter on the Core
  request so the CLI has it too.
- **Add an app or template to the catalog:** edit `catalog/apps.json` / `templates.json`, verify
  ids (`winget show --id <id> --exact`, `npm view`, Marketplace), set `provides` on apps that put a
  tool on PATH, run `dotnet test`, and try it with `UPDATR_CATALOG_DIR` before pushing.
- **Install a new kind of source:** implement `IAppInstaller` (Handles / IsInstalled / Install) and
  add it to `UpdatrHost`'s installer list; `InstallApps` picks it up.
- **Add a debloat item:** add it to `catalog/debloat.json` (machine registry keys must be under
  `SOFTWARE\`; give appx actions a `storeId` verified with `winget show --id <id> --source msstore`
  so revert can reinstall), check it with `UPDATR_CATALOG_DIR=<repo>/catalog updatr-cli debloat
  status --json`, run `dotnet test`. A new action kind needs an `IDebloatTarget` and an entry in the
  broker's allow-list (`DebloatBrokerHandler.Allowed`).
- **Recognize another launcher's games:** add its uninstall-command / install-folder markers to
  `LauncherDetector.Launchers` (or, when its games carry no such marker, its publisher to
  `PublisherLaunchers`, as Battlestate's), its own entries to `LauncherSelf` / `LauncherIds`, a test with a
  real uninstall string, and an `explicit` hold for the launcher's winget id in `holds.json`.
- **Test self-update without touching `bin/`:** publish with `-p:VersionPrefix=<older>` into a
  scratch folder (copy the exes there) and run `updatr-cli self-update --yes` from it. Dev builds
  refuse by design.
- **Add a convention test:** walk files with `RepositoryFiles.Authored()` (everything a person
  wrote) or `RepositoryFiles.Source()` (what ships), so new folders and extensions are covered by
  every convention at once.

## What NOT to do

- Don't run PowerShell, or wrap a script and call it a feature.
- Don't auto-remove anything debloat does not recognize; surface it for review.
- Don't delete leftover files/keys during uninstall cleanup; quarantine them.
- Don't host or mirror installers; pull from upstream (the mirror hook stays a stub).
- Don't add per-machine state; carts, templates, and settings travel with the drive.
