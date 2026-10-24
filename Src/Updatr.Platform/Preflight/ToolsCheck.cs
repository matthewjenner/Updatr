using Updatr.Core.Abstractions;
using Updatr.Core.Preflight;

namespace Updatr.Platform.Preflight;

/// <summary>
/// Which ecosystem CLIs are installed. Informational: a missing tool hides its source until
/// something in a cart installs it.
/// </summary>
public sealed class ToolsCheck(IProcessRunner processes) : IndependentCheck
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Tool name (as used by catalog <c>provides</c>) and the program to look for.</summary>
    private static readonly (string Tool, string Program)[] Tools =
    [
        ("npm", "npm"),
        ("pipx", "pipx"),
        ("pip", "pip"),
        ("dotnet", "dotnet"),
        ("code", "code"),
    ];

    public override string Id => "tools";

    public override string Title => "Package tools";

    public override async Task<PreflightResult> RunAsync(PreflightContext context, CancellationToken cancellationToken)
    {
        (string Tool, string? Path, string? Version)[] found = await Task.WhenAll(Tools.Select(async t =>
        {
            string? path = processes.FindOnPath(t.Program);
            string? version = path is null ? null : await VersionOfAsync(path, cancellationToken).ConfigureAwait(false);
            return (t.Tool, path, version);
        })).ConfigureAwait(false);

        context.Tools = found.Where(f => f.Path is not null).ToDictionary(f => f.Tool, f => f.Path!, StringComparer.Ordinal);

        string[] present = [.. found.Where(f => f.Path is not null).Select(f => f.Version is null ? f.Tool : $"{f.Tool} {f.Version}")];
        string[] missing = [.. found.Where(f => f.Path is null).Select(f => f.Tool)];

        string summary = present.Length == 0 ? "None installed" : string.Join(", ", present);
        string? detail = missing.Length == 0
            ? null
            : $"Not installed: {string.Join(", ", missing)}. Their packages stay hidden until a cart installs the tool (added automatically as a prerequisite).";

        return PreflightResult.Info(summary, detail);
    }

    private async Task<string?> VersionOfAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult result = await processes.RunAsync(path, ["--version"], VersionTimeout, cancellationToken).ConfigureAwait(false);
            string line = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

            // "pip 25.2 from C:\..." / "pipx 1.7.1" / "10.9.2" -> the first token that starts with a digit.
            return line.Split(' ').FirstOrDefault(token => token.Length > 0 && char.IsAsciiDigit(token[0]));
        }
        catch (Exception ex) when (ex is TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
