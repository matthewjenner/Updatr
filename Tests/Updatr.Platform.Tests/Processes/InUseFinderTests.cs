using System.Diagnostics;
using Updatr.Platform.Windows;

namespace Updatr.Platform.Tests.Processes;

public sealed class InUseFinderTests
{
    [Fact]
    public void ARunningProgramFromTheFolderIsNamed()
    {
        using TempFolder folder = new();
        string exe = Path.Combine(folder.Path, "updatr-blocker.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe);

        using Process blocker = Process.Start(new ProcessStartInfo(exe, "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;

        try
        {
            InUseFinder.Find([folder.Path]).Should().Contain(p => p.Name == "updatr-blocker.exe" && p.ProcessId == blocker.Id);
        }
        finally
        {
            blocker.Kill();
            blocker.WaitForExit();
        }
    }

    [Fact]
    public void ARegistrationPointingAtAWholeProgramFilesFolderIsIgnored()
    {
        // A broken install location must not register half the disk with Restart Manager.
        InUseFinder.Find([Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)]).Should().BeEmpty();
    }
}
