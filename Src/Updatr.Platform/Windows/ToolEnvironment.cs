using Microsoft.Win32;
using Updatr.Core.Abstractions;
using Updatr.Core.Carts;

namespace Updatr.Platform.Windows;

/// <summary>
/// Which ecosystem tools are on PATH, and picking up tools a run just installed.
/// </summary>
/// <remarks>
/// An installer (Node, VS Code, the .NET SDK) adds its folder to the PATH stored in the registry,
/// which new processes inherit - but this process keeps the PATH it started with. <see cref="Refresh"/>
/// rebuilds it from the registry exactly as Windows does for a new process: machine PATH, then
/// user PATH, with environment variables expanded.
/// </remarks>
public sealed class ToolEnvironment(IProcessRunner processes) : IToolEnvironment
{
    /// <summary>Tool names catalog apps provide and sources require.</summary>
    public static readonly IReadOnlyList<string> KnownTools = ["npm", "node", "pipx", "pip", "python", "dotnet", "code", "git"];

    public IReadOnlySet<string> ToolsPresent()
        => KnownTools.Where(t => processes.FindOnPath(t) is not null).ToHashSet(StringComparer.Ordinal);

    public void Refresh()
    {
        string? machine = ReadPath(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
        string? user = ReadPath(Registry.CurrentUser, "Environment");

        if (machine is null && user is null)
            return;

        Environment.SetEnvironmentVariable("PATH", string.Join(';', new[] { machine, user }.Where(p => !string.IsNullOrEmpty(p))));
    }

    private static string? ReadPath(RegistryKey hive, string subKey)
    {
        using RegistryKey? key = hive.OpenSubKey(subKey);

        // DoNotExpand, then expand ourselves: %USERPROFILE% etc. resolve against this process.
        return key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string raw
            ? Environment.ExpandEnvironmentVariables(raw)
            : null;
    }
}
