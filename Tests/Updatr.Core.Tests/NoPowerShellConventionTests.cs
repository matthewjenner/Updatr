using System.Globalization;
using System.Text.RegularExpressions;

namespace Updatr.Core.Tests;

/// <summary>
/// Updatr is a real application, not a PowerShell wrapper: nothing that ships starts PowerShell or
/// hosts a runspace.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why:</b> a fresh or slim Windows image may not have PowerShell 7, execution policy and
/// language mode vary per machine, and text parsing of script output is exactly the fragility
/// Updatr replaces. Every capability goes through a native API (WinGet COM, the WinRT
/// <c>PackageManager</c>, the registry, Task Scheduler COM, ...). Shelling out is allowed only to
/// an ecosystem's own CLI (npm, pipx, dotnet, code), because that CLI is the ecosystem's interface.
/// </para>
/// <para>
/// Mentioning PowerShell in prose is fine (PSGallery modules install into PowerShell's module
/// paths). The patterns below target ways of <i>running</i> it.
/// </para>
/// </remarks>
public sealed class NoPowerShellConventionTests
{
    private static readonly Regex Forbidden = new(
        @"powershell\.exe|pwsh\.exe|""powershell""|""pwsh""|System\.Management\.Automation|Microsoft\.PowerShell\.SDK",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void NothingThatShipsRunsPowerShell()
    {
        List<string> offenders = [];

        foreach (string file in RepositoryFiles.Source())
        {
            string[] lines = File.ReadAllLines(file);

            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match match in Forbidden.Matches(lines[i]))
                {
                    offenders.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}:{1}  {2}",
                        Path.GetRelativePath(RepositoryPaths.Root, file),
                        i + 1,
                        match.Value));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} PowerShell invocation(s) in Src/. Use the native API instead "
                + "(see Docs/TECHARCH.md, Hard rules):\n  "
                + string.Join("\n  ", offenders));
    }
}
