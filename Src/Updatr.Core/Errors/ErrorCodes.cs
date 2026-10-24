namespace Updatr.Core.Errors;

/// <summary>
/// Decodes the numeric codes installers and winget return into plain English, and says whether a
/// code is really a failure.
/// </summary>
/// <remarks>
/// <para>
/// These are documented, stable values - Windows/MSI installer exit codes and winget's own
/// <c>0x8A15xxxx</c> range - not random, so the common ones can be named. Ported from
/// <c>Get-WingetUpdates.ps1</c> (see <c>Docs/legacy-script.md</c>) and extended. Every winget value
/// was checked against winget-cli <c>src/AppInstallerSharedLib/Public/AppInstallerErrors.h</c> at
/// tag v1.29.380; the constant name is kept beside each entry so a future re-check is a grep.
/// </para>
/// <para>
/// Unknown codes still decode - to their hex, with no hint - so a failure is never silent.
/// </para>
/// </remarks>
public static class ErrorCodes
{
    private static readonly Dictionary<uint, ErrorCodeInfo> Known = Build();

    /// <summary>Decodes any code: an exit code, an installer error code, or an HRESULT.</summary>
    /// <param name="code">The code; values outside 32 bits are masked.</param>
    public static ErrorCodeInfo Describe(long code)
    {
        uint value = unchecked((uint)(code & 0xFFFFFFFFL));

        if (Known.TryGetValue(value, out ErrorCodeInfo? info))
            return info;

        // HRESULTs 0x8019xxxx are FACILITY_HTTP: the low word is the HTTP status code.
        if (value is >= 0x80190000u and <= 0x8019FFFFu)
        {
            uint status = value & 0xFFFFu;
            return new ErrorCodeInfo(
                value,
                $"download failed - HTTP {status} (the server refused or could not serve the installer)",
                CodeOutcome.Failure,
                Transient: status is 408 or 429 or >= 500);
        }

        return new ErrorCodeInfo(value, null, CodeOutcome.Failure, Transient: false);
    }

    private static Dictionary<uint, ErrorCodeInfo> Build()
    {
        Dictionary<uint, ErrorCodeInfo> map = [];

        void Fail(uint code, string hint, bool transient = false)
            => map.Add(code, new ErrorCodeInfo(code, hint, CodeOutcome.Failure, transient));
        void Current(uint code, string hint)
            => map.Add(code, new ErrorCodeInfo(code, hint, CodeOutcome.AlreadyCurrent, Transient: false));
        void Reboot(uint code, string hint)
            => map.Add(code, new ErrorCodeInfo(code, hint, CodeOutcome.SuccessRebootRequired, Transient: false));

        // ---- Installer / Windows exit codes ------------------------------------------------
        Fail(0x00000642, "user canceled the install (1602)");
        Fail(0x00000643, "fatal install error (1603) - often the app is running, a reboot is pending, or permissions");
        Fail(0x00000652, "another install is already in progress (1618) - let it finish, then retry", transient: true);
        Fail(0x00000654, "the installer package could not be opened (1620)");
        Fail(0x00000666, "a different version is already installed (1638)");
        Reboot(0x00000669, "succeeded; the machine is rebooting (1641)");
        Reboot(0x00000BC2, "succeeded; a reboot is required to finish (3010)");
        Fail(0x80070005, "access denied - this step needs to run elevated");
        Fail(0xC0000005, "the installer crashed (access violation)");
        Fail(0xC0000409, "the installer aborted (fail-fast / stack buffer overrun)");
        Fail(0xC000041D, "the installer crashed - usually because the app is still running; close it and retry", transient: true);

        // ---- winget: general (0x8A1500xx) --------------------------------------------------
        Fail(0x8A150005, "canceled (Ctrl-C)");                                                         // CTRL_SIGNAL_RECEIVED
        Fail(0x8A150006, "the installer winget launched reported failure");                            // SHELLEXEC_INSTALL_FAILED
        Fail(0x8A150008, "downloading the installer failed", transient: true);                         // DOWNLOAD_FAILED
        Fail(0x8A150010, "no applicable installer for this system (architecture or scope)");           // NO_APPLICABLE_INSTALLER
        Fail(0x8A150011, "installer hash mismatch - the vendor changed the installer without updating winget; usually not your machine"); // INSTALLER_HASH_MISMATCH
        Fail(0x8A150014, "no package found matching the id");                                          // NO_APPLICATIONS_FOUND
        Fail(0x8A150019, "this step needs to run elevated");                                            // COMMAND_REQUIRES_ADMIN
        Current(0x8A15002B, "no applicable upgrade (already current)");                                // UPDATE_NOT_APPLICABLE
        Fail(0x8A15002C, "at least one package in the batch failed");                                  // UPDATE_ALL_HAS_FAILURE
        Fail(0x8A15002D, "the installer failed a security (SmartScreen / Defender) check");            // INSTALLER_SECURITY_CHECK_FAILED
        Fail(0x8A15002E, "downloaded size did not match the manifest");                                // DOWNLOAD_SIZE_MISMATCH
        Fail(0x8A150041, "package agreements were not accepted");                                      // PACKAGE_AGREEMENTS_NOT_ACCEPTED
        Fail(0x8A150045, "a winget source could not be opened", transient: true);                      // SOURCE_OPEN_FAILED
        Fail(0x8A150046, "source agreements were not accepted");                                       // SOURCE_AGREEMENTS_NOT_ACCEPTED
        Fail(0x8A150049, "the MSI install failed");                                                    // MSI_INSTALL_FAILED
        Fail(0x8A15004B, "none of the winget sources could be opened", transient: true);               // FAILED_TO_OPEN_ALL_SOURCES
        Current(0x8A15004F, "the available version is not newer than the installed one");              // UPGRADE_VERSION_NOT_NEWER
        Fail(0x8A150050, "the installed version is unknown, so winget will not upgrade it");           // UPGRADE_VERSION_UNKNOWN
        Fail(0x8A150056, "the installer refuses to run elevated - run it unelevated");                 // INSTALLER_PROHIBITS_ELEVATION
        Current(0x8A150061, "the package is already installed");                                       // PACKAGE_ALREADY_INSTALLED
        Fail(0x8A150068, "the package is pinned in winget - unpin it to upgrade");                     // PACKAGE_IS_PINNED
        Fail(0x8A15007D, "this action is not allowed from an elevated context");                       // ADMIN_CONTEXT_ACTION_PROHIBITED
        Fail(0x8A15008E, "the update uses a different install technology than what is installed - uninstall and reinstall"); // UPDATE_INSTALL_TECHNOLOGY_MISMATCH

        // ---- winget: installer results (0x8A1501xx) ----------------------------------------
        Fail(0x8A150101, "the application is in use - close it and retry", transient: true);           // INSTALL_PACKAGE_IN_USE
        Fail(0x8A150102, "another install is already in progress", transient: true);                   // INSTALL_INSTALL_IN_PROGRESS
        Fail(0x8A150103, "a file is in use - close the app and retry", transient: true);               // INSTALL_FILE_IN_USE
        Fail(0x8A150104, "a dependency is missing");                                                   // INSTALL_MISSING_DEPENDENCY
        Fail(0x8A150105, "the disk is full");                                                          // INSTALL_DISK_FULL
        Fail(0x8A150106, "not enough memory to install");                                              // INSTALL_INSUFFICIENT_MEMORY
        Fail(0x8A150107, "the installer needs a network connection", transient: true);                 // INSTALL_NO_NETWORK
        Fail(0x8A150108, "the installer failed - its vendor asks you to contact support");             // INSTALL_CONTACT_SUPPORT
        Reboot(0x8A150109, "succeeded; a reboot is required to finish");                               // INSTALL_REBOOT_REQUIRED_TO_FINISH
        Fail(0x8A15010A, "a reboot is required before this can install");                              // INSTALL_REBOOT_REQUIRED_FOR_INSTALL
        Reboot(0x8A15010B, "succeeded; the installer started a reboot");                               // INSTALL_REBOOT_INITIATED
        Fail(0x8A15010C, "the install was canceled in the installer");                                 // INSTALL_CANCELLED_BY_USER
        Current(0x8A15010D, "already installed (nothing to do)");                                      // INSTALL_ALREADY_INSTALLED
        Fail(0x8A15010E, "a newer version is already installed (downgrade blocked)");                  // INSTALL_DOWNGRADE
        Fail(0x8A15010F, "blocked by policy");                                                         // INSTALL_BLOCKED_BY_POLICY
        Fail(0x8A150110, "a dependency failed to install");                                            // INSTALL_DEPENDENCIES
        Fail(0x8A150111, "a running application is using the package - close it and retry", transient: true); // INSTALL_PACKAGE_IN_USE_BY_APPLICATION
        Fail(0x8A150112, "the installer rejected its parameters");                                     // INSTALL_INVALID_PARAMETER
        Fail(0x8A150113, "this package is not supported on this system");                              // INSTALL_SYSTEM_NOT_SUPPORTED
        Fail(0x8A150114, "winget upgrade is not supported for this package (it likely updates itself)"); // INSTALL_UPGRADE_NOT_SUPPORTED

        return map;
    }
}
