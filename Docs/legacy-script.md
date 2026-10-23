# Legacy script reference (Get-WingetUpdates.ps1)

Updatr replaces a PowerShell 7 script, `Get-WingetUpdates.ps1`, that listed winget upgrades and
optionally applied them. The script itself is gone; this file keeps everything worth carrying
forward. Phase 1 ports the hold list and error table into `catalog/holds.json` and
`Updatr.Core/Errors/ErrorCodes`; this doc stays as the rationale.

## Behavior worth keeping

- **Structured data first.** Preferred the `Microsoft.WinGet.Client` module (structured objects)
  over parsing `winget upgrade` text. Updatr's equivalent is the WinGet COM API, which that
  module wraps.
- **Text fallback parsing.** Columns located by header text positions (`Name`, `Id`, `Version`,
  `Available`, `Source`), not whitespace splitting, so names and ids with spaces survive. Data
  starts two lines below the header (skip the dashes row). Footer lines matching
  `upgrades? available` or `package\(s\) have` are skipped.
- **Truncated ids.** winget truncates long ids with a trailing unicode ellipsis (U+2026). A
  truncated id breaks `--exact`; the script warned. Updatr should treat such a row as
  unreliable and prefer COM data.
- **Encoding.** Forced UTF-8 console encoding so winget's ellipsis / dash characters decode, and
  set `WINGET_DISABLE_PROGRESS=1` to keep progress bars out of captured output.
- **Unknown versions excluded by default.** Packages whose installed version is empty or
  `Unknown` were hidden unless `-IncludeUnknown`, because upgrades on them are unreliable.
- **Flags used on every CLI call:** `--accept-source-agreements`, `--accept-package-agreements`
  (install/upgrade), `--disable-interactivity`, `--id <id> --exact`.
- **Collision guard.** Refused to start if a `winget` process was already running (an install
  prompt in progress or a previous run). It deliberately did **not** check for `msiexec`
  processes: msiexec runs for many unrelated reasons and false-positives. Updatr's MSI check
  should use the `Global\_MSIExecute` mutex (held only during an actual install transaction),
  not the process list.
- **Three outcomes.** `Ok`, `AlreadyCurrent` (winget had nothing to apply - common when a
  previous run already upgraded it but winget still lists it), `Failed`. Detail always carried
  the decoded status / exit code so a no-op is never mistaken for a failure.
- **Module result decoding.**
  - `Status` string: `Ok` -> Ok. Matches of
    `NoApplicable|NotApplicable|AlreadyInstalled|NoAvailableUpgrade|NoUpgrade|UpToDate` ->
    AlreadyCurrent. Anything else -> Failed. Status names vary by module version, so this match
    should be extendable.
  - `InstallerErrorCode`: the installer's own exit code.
  - `ExtendedErrorCode`: usually a `COMException`; its `HResult` holds the `0x8A15xxxx` code.
  - `RebootRequired`: surfaced as "reboot required".
- **Text-path result:** exit code 0 -> Ok, otherwise Failed with the decoded code.
- **Modes:** list only (default); batch upgrade with one confirmation (`-Update`, `-Force` to
  skip it); interactive per-package `[y]es [n]o [a]ll remaining [q]uit` (`-Interactive`).
  Version-sensitive packages skipped unless `-IncludeRisky`. Final summary: n upgraded, n
  already current, n skipped, n failed, plus failed names.
- **Spinner while scanning** ran the query as a background job and always cleaned the job up,
  even on Ctrl+C. Updatr's equivalent is the async, cancelable operation model.

## Hold list (formerly "risky patterns")

Guiding rule: hold a package only when an in-place upgrade can cross a breaking boundary - the
major version is **not** pinned in the id - or when it is stateful / pinned by other tools, or
best left to the installers that bundle it. Runtimes whose id pins the major
(`Python.Python.3.12`, `Microsoft.DotNet.SDK.8`, `EclipseAdoptium.Temurin.21.JDK`, ...) are
deliberately **not** held: upgrading stays within that major and is drop-in safe.

Patterns are winget id wildcards (`-like` semantics: `*` any run, case-insensitive).

| Group | Patterns | Why |
|---|---|---|
| Runtimes without a pinned major | `OpenJS.NodeJS*`, `GoLang.Go*`, `Rustlang.*`, `Ruby.Ruby*`, `PHP.PHP*` | Upgrade can jump majors in place (e.g. Node 20 -> 22) |
| Databases and client drivers | `PostgreSQL.PostgreSQL*`, `Oracle.MySQL*`, `MariaDB.*`, `Redis.*`, `MongoDB.*`, `Microsoft.msodbcsql*`, `Microsoft.SQLServer.*` | Stateful; upgrade on your own terms even within a major |
| Build tools | `Gradle.Gradle`, `Apache.Maven`, `Kitware.CMake`, `Git.Git` | Single id that can cross a major (CMake 3 -> 4) |
| Containers / Kubernetes | `Kubernetes.*`, `Helm.*` | Version skew with clusters |
| GPU / compute | `Nvidia.CUDA*` | ML frameworks pin CUDA versions |
| Media library | `Gyan.FFmpeg` | Scripts and apps depend on it; major API changes |
| Platform | `Microsoft.WSL` | Your Linux distros run on it |
| Redistributables | `Microsoft.VCRedist.*` | Installers bundle the exact version they need |
| Self-servicing | `Microsoft.AppInstaller` | Listed as upgradable but the CLI cannot advance it (it updates through the Store); the CLI "upgrade" reports OK and the version never moves. Show as "updates via Store". |

## Error code hints

Keyed by 8-digit uppercase hex of the code masked to 32 bits. Unknown codes fall through to the
raw hex. winget values were verified against winget-cli `AppInstallerErrors.h` when the script
was written; re-verify when porting.

Installer / Windows exit codes:

| Code | Meaning |
|---|---|
| `0x00000642` | User canceled the install (1602) |
| `0x00000643` | Fatal install error (1603) - often the app is running, a reboot is pending, or permissions |
| `0x00000652` | Another install is already in progress (1618) - let it finish, then retry |
| `0x00000654` | The installer package could not be opened (1620) |
| `0x00000666` | A different version is already installed (1638) |
| `0x00000669` | Succeeded; the machine is rebooting (1641) |
| `0x00000BC2` | Succeeded; a reboot is required to finish (3010) |
| `0x80070005` | Access denied - try elevated |
| `0xC0000005` | The installer crashed (access violation) |
| `0xC0000409` | The installer aborted (fail-fast / stack buffer overrun) |
| `0xC000041D` | The installer crashed - usually because the app is still running; close it and retry |

winget codes:

| Code | Meaning |
|---|---|
| `0x8A150005` | Canceled (Ctrl-C) |
| `0x8A150006` | The installer winget launched reported failure |
| `0x8A150008` | Downloading the installer failed |
| `0x8A150010` | No applicable installer for this system (architecture / scope) |
| `0x8A150011` | Installer hash mismatch - vendor changed the installer without updating winget; usually not your machine |
| `0x8A150019` | Requires an elevated (admin) process |
| `0x8A15002B` | No applicable upgrade (already current) |
| `0x8A15002C` | At least one package in the batch failed |
| `0x8A15002D` | The installer failed a security (SmartScreen / Defender) check |
| `0x8A15002E` | Downloaded size did not match the manifest |
| `0x8A150041` | Package agreements were not accepted |
| `0x8A150049` | The MSI install failed |
| `0x8A150056` | The installer refuses to run elevated - re-run unelevated |
| `0x8A150102` | An install is already in progress |
| `0x8A150103` | A file is in use - close the app and retry |
| `0x8A150109` | Succeeded; a reboot is required to finish |
| `0x8A15010A` | A reboot is required before this can install |
| `0x8A15010D` | Already installed (nothing to do) |
| `0x8A15010F` | Blocked by policy |
| `0x8A150111` | A running application is using the package - close it and retry |
| `0x8A150113` | This package is not supported on your system |
| `0x8A150114` | winget upgrade is not supported for this package (it likely self-updates) |

Range rule: HRESULTs `0x80190000`-`0x8019FFFF` are FACILITY_HTTP; the low word is the HTTP
status code -> "download failed - HTTP <status>".

Outcome mapping to keep in mind when porting: `0x8A15002B` and `0x8A15010D` are
AlreadyCurrent, not Failed; `0x00000BC2`, `0x00000669`, and `0x8A150109` are Ok with the
reboot flag set.
