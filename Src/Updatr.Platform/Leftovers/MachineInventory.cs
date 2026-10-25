using Microsoft.Win32;

namespace Updatr.Platform.Leftovers;

/// <summary>One app registered in an Uninstall key.</summary>
/// <param name="Key">Full key path, e.g. <c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{GUID}</c>.</param>
/// <param name="KeyName">The subkey name (the product code for MSI installs).</param>
public sealed record UninstallEntry(string Key, string KeyName, string? DisplayName, string? Publisher, string? InstallLocation, string? UninstallString, string? DisplayIcon)
{
    /// <summary>Folders this app clearly owns: its install location and the folder of its uninstaller and icon.</summary>
    public IEnumerable<string> OwnedFolders()
    {
        if (!string.IsNullOrWhiteSpace(InstallLocation))
            yield return InstallLocation;

        foreach (string? command in new[] { UninstallString, DisplayIcon })
        {
            string? exe = LeftoverRules.ExecutableOf(command?.Split(',')[0]);

            if (exe is not null && !exe.Contains("msiexec", StringComparison.OrdinalIgnoreCase) && !exe.Contains("rundll32", StringComparison.OrdinalIgnoreCase)
                && Path.GetDirectoryName(exe) is { Length: > 3 } folder)
                yield return folder;
        }
    }
}

/// <summary>Reads the machine's (and this user's) Uninstall registrations - what Programs and Features lists.</summary>
public static class MachineInventory
{
    public const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static IReadOnlyList<UninstallEntry> Read()
    {
        List<UninstallEntry> entries = [];

        foreach ((RegistryHive hive, RegistryView view, string prefix) in new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM32"),
            (RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU"),
        })
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? uninstall = root.OpenSubKey(UninstallPath);

            if (uninstall is null)
                continue;

            foreach (string name in uninstall.GetSubKeyNames())
            {
                using RegistryKey? app = uninstall.OpenSubKey(name);

                if (app is null)
                    continue;

                string keyPath = prefix == "HKLM32"
                    ? $@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{name}"
                    : $@"{prefix}\{UninstallPath}\{name}";

                entries.Add(new UninstallEntry(keyPath, name,
                    app.GetValue("DisplayName") as string,
                    app.GetValue("Publisher") as string,
                    app.GetValue("InstallLocation") as string,
                    app.GetValue("UninstallString") as string,
                    app.GetValue("DisplayIcon") as string));
            }
        }

        return entries;
    }
}
