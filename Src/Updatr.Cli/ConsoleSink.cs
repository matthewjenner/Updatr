using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Updatr.Core.Operations;
using Updatr.Core.Packages;
using Updatr.Core.Preflight;
using Updatr.Core.Sources;

namespace Updatr.Cli;

/// <summary>Renders operation events as text for a person at a console.</summary>
/// <remarks>
/// Progress goes on a single line that is rewritten in place (only when stdout is a console); any
/// other output clears it first, so progress never leaves fragments behind.
/// </remarks>
public class ConsoleSink(bool assumeYes) : IOperationSink
{
    private readonly Lock _gate = new();
    private int _progressWidth;

    /// <summary>Show every check's detail, not only warnings and failures.</summary>
    public bool Verbose { get; set; }

    protected bool Interactive { get; } = !Console.IsOutputRedirected;

    /// <summary>The user passed <c>--yes</c>.</summary>
    protected bool AssumeYes => assumeYes;

    public virtual void Report(OperationEvent operationEvent)
    {
        switch (operationEvent)
        {
            case CheckFinished check:
                WriteCheck(check);
                break;
            case StepStarted step:
                Line(ConsoleColor.Cyan, step.Step);
                break;
            case ProgressReported progress:
                Progress(Describe(progress));
                break;
            case ItemFinished item:
                WriteOutcome(item);
                break;
            case LogLine { Level: LogLevel.Warning or LogLevel.Error } log:
                Line(log.Level == LogLevel.Error ? ConsoleColor.Red : ConsoleColor.Yellow, log.Text);
                break;
        }
    }

    public virtual async Task<bool> ConfirmAsync(Confirmation confirmation, CancellationToken cancellationToken)
    {
        if (assumeYes)
            return true;

        if (Console.IsInputRedirected)
            return confirmation.DefaultAnswer;

        Line(ConsoleColor.White, confirmation.Title);

        if (confirmation.Items.Count > 0)
        {
            Line(ConsoleColor.Gray, ConfirmationColumns.Table(confirmation));
            Console.WriteLine();
        }

        Line(ConsoleColor.Gray, confirmation.Detail);
        Console.Write(confirmation.DefaultAnswer ? "[Y/n] " : "[y/N] ");
        string? answer = await Task.Run(Console.ReadLine, cancellationToken).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(answer)
            ? confirmation.DefaultAnswer
            : answer.Trim().StartsWith('y') || answer.Trim().StartsWith('Y');
    }

    public virtual void Summary(PreflightReport report)
    {
        int warn = report.Results.Count(r => r.Result.Status == PreflightStatus.Warn);
        int fail = report.Results.Count(r => r.Result.Status == PreflightStatus.Fail);

        Console.WriteLine();
        Line(fail > 0 ? ConsoleColor.Red : warn > 0 ? ConsoleColor.Yellow : ConsoleColor.Green,
            fail > 0 ? $"{fail} check(s) failed, {warn} warning(s)."
            : warn > 0 ? $"Ready, with {warn} warning(s)."
            : "Ready.");
    }

    public virtual void Summary(RunSummary summary)
    {
        Console.WriteLine();
        string text = string.Create(CultureInfo.InvariantCulture,
            $"{summary.Action}: {summary.Count(OutcomeKind.Ok)} done, {summary.Count(OutcomeKind.AlreadyCurrent)} already current, "
            + $"{(summary.Count(OutcomeKind.Pending) > 0 ? $"{summary.Count(OutcomeKind.Pending)} pending an app restart, " : "")}"
            + $"{summary.Count(OutcomeKind.Skipped)} skipped, {summary.Count(OutcomeKind.Failed)} failed ({summary.Duration.TotalMinutes:0.0} min).");
        Line(summary.HasFailures ? ConsoleColor.Yellow : ConsoleColor.Green, text);

        if (summary.RebootRequired)
            Line(ConsoleColor.Yellow, "A reboot is needed to finish some of these.");
    }

    /// <summary>The package list as a table.</summary>
    public virtual void Table(IReadOnlyList<ListedPackage> packages)
    {
        ClearProgress();

        if (packages.Count == 0)
        {
            Line(ConsoleColor.Green, "Nothing to show.");
            return;
        }

        int name = Math.Clamp(packages.Max(p => p.Package.Name.Length), 4, 40);
        int id = Math.Clamp(packages.Max(p => p.Package.Id.Length), 2, 44);

        Console.WriteLine($"   {"Name".PadRight(name)}  {"Id".PadRight(id)}  {"Installed",-16}  {"Available",-16}  Source");

        foreach (ListedPackage p in packages)
        {
            string flag = p.IsStoreManaged ? "S" : p.IsHeld ? "H" : p.Package.CheckProblem is not null ? "?" : p.Package.HasUpdate ? "*" : " ";
            ConsoleColor color = flag switch { "*" => ConsoleColor.White, "H" or "S" => ConsoleColor.DarkYellow, "?" => ConsoleColor.Red, _ => ConsoleColor.Gray };

            Line(color, $" {flag} {Fit(p.Package.Name, name)}  {Fit(p.Package.Id, id)}  {Fit(p.Package.InstalledVersion, 16)}  {Fit(p.Package.AvailableVersion ?? "", 16)}  {p.Package.Catalog}");
        }

        Console.WriteLine();
        Line(ConsoleColor.DarkGray, "* update available   H held (skipped by upgrade --all)   S updates via the Microsoft Store   ? could not be checked");
        Line(ConsoleColor.Gray, $"{packages.Count} package(s), {packages.Count(p => p.Package.HasUpdate)} with updates, {packages.Count(p => p.IsHeld)} held.");
    }

    public virtual void Error(string message) => Line(ConsoleColor.Red, message);

    /// <summary>Command output: <paramref name="text"/> for people, <paramref name="json"/> under --json.</summary>
    public virtual void Output(string text, JsonNode json)
    {
        lock (_gate)
        {
            ClearProgress();
            Console.WriteLine(text);
        }
    }

    /// <summary>The one-line outcome of a command that is not a list of items.</summary>
    public virtual void Result(bool succeeded, string summary) => Line(succeeded ? ConsoleColor.Green : ConsoleColor.Red, summary);

    private void WriteCheck(CheckFinished check)
    {
        (string tag, ConsoleColor color) = check.Result.Status switch
        {
            PreflightStatus.Pass => ("[ OK ]", ConsoleColor.Green),
            PreflightStatus.Info => ("[INFO]", ConsoleColor.Gray),
            PreflightStatus.Warn => ("[WARN]", ConsoleColor.Yellow),
            _ => ("[FAIL]", ConsoleColor.Red),
        };

        lock (_gate)
        {
            ClearProgress();
            Write(color, tag);
            Console.WriteLine($" {check.Title,-22} {check.Result.Summary}");

            bool problem = check.Result.Status is PreflightStatus.Warn or PreflightStatus.Fail;

            if (check.Result.Detail is not null && (Verbose || problem))
            {
                foreach (string line in check.Result.Detail.Split('\n'))
                    WriteLine(ConsoleColor.DarkGray, $"         {line}");
            }

            if (check.Result.Fix is { } fix)
                WriteLine(ConsoleColor.Cyan, $"         Fix: {fix.CliCommand}");
        }
    }

    private void WriteOutcome(ItemFinished item)
    {
        (string tag, ConsoleColor color) = item.Outcome.Kind switch
        {
            OutcomeKind.Ok => ("[ OK ]", ConsoleColor.Green),
            OutcomeKind.AlreadyCurrent => ("[SAME]", ConsoleColor.Gray),
            OutcomeKind.Skipped => ("[SKIP]", ConsoleColor.DarkYellow),
            OutcomeKind.Pending => ("[WAIT]", ConsoleColor.Yellow),
            _ => ("[FAIL]", ConsoleColor.Red),
        };

        lock (_gate)
        {
            ClearProgress();
            Write(color, tag);
            Console.WriteLine($" {item.DisplayName}{(item.Outcome.Detail.Length > 0 ? "  - " + item.Outcome.Detail : "")}");
        }
    }

    private static string Describe(ProgressReported p)
    {
        string text = p.Message ?? "Working";

        if (p.Bytes is { } bytes)
            text += p.TotalBytes is { } total ? $" {Mb(bytes)} / {Mb(total)}" : $" {Mb(bytes)}";

        if (p.Fraction is { } fraction && p.Bytes is null)
            text += $" {fraction:P0}";

        return text;

        static string Mb(long b) => string.Create(CultureInfo.InvariantCulture, $"{b / 1048576.0:0.0} MB");
    }

    private void Progress(string text)
    {
        if (!Interactive)
            return;

        lock (_gate)
        {
            string line = "    " + text;
            int width = Math.Max(1, Console.WindowWidth - 1);

            if (line.Length > width)
                line = line[..width];

            Console.Write("\r" + line.PadRight(_progressWidth));
            _progressWidth = line.Length;
        }
    }

    private void ClearProgress()
    {
        if (_progressWidth == 0)
            return;

        Console.Write("\r" + new string(' ', _progressWidth) + "\r");
        _progressWidth = 0;
    }

    protected void Line(ConsoleColor color, string text)
    {
        lock (_gate)
        {
            ClearProgress();
            WriteLine(color, text);
        }
    }

    private void WriteLine(ConsoleColor color, string text)
    {
        Write(color, text);
        Console.WriteLine();
    }

    private void Write(ConsoleColor color, string text)
    {
        if (!Interactive)
        {
            Console.Write(text);
            return;
        }

        ConsoleColor previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = previous;
    }

    private static string Fit(string text, int width)
        => text.Length <= width ? text.PadRight(width) : text[..(width - 3)] + "...";
}

/// <summary>
/// <c>--json</c>: one JSON object per line on stdout, so scripts can follow progress as it happens.
/// Field names are stable; every line has an <c>event</c>.
/// </summary>
public sealed class JsonLinesSink(bool assumeYes) : ConsoleSink(assumeYes)
{
    // Relaxed escaping: Windows paths and apostrophes stay readable. Still valid JSON, and stdout
    // is never embedded in HTML.
    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Lock _gate = new();

    public override void Report(OperationEvent operationEvent)
    {
        JsonObject? line = operationEvent switch
        {
            CheckStarted c => new JsonObject { ["event"] = "checkStarted", ["id"] = c.CheckId },
            CheckFinished c => new JsonObject
            {
                ["event"] = "checkFinished",
                ["id"] = c.CheckId,
                ["title"] = c.Title,
                ["status"] = c.Result.Status.ToString().ToLowerInvariant(),
                ["summary"] = c.Result.Summary,
                ["detail"] = c.Result.Detail,
                ["fix"] = c.Result.Fix?.CliCommand,
                ["elapsedMs"] = (long)c.Elapsed.TotalMilliseconds,
            },
            PackageListed p => Package("package", p.Listed),
            ProgressReported p => new JsonObject
            {
                ["event"] = "progress",
                ["item"] = p.ItemId,
                ["fraction"] = p.Fraction,
                ["bytes"] = p.Bytes,
                ["totalBytes"] = p.TotalBytes,
                ["message"] = p.Message,
            },
            ItemFinished i => new JsonObject
            {
                ["event"] = "itemFinished",
                ["item"] = i.ItemId,
                ["name"] = i.DisplayName,
                ["outcome"] = i.Outcome.Kind.ToString().ToLowerInvariant(),
                ["detail"] = i.Outcome.Detail,
                ["code"] = i.Outcome.Code?.Hex,
                ["rebootRequired"] = i.Outcome.RebootRequired,
            },
            StepStarted s => new JsonObject { ["event"] = "step", ["step"] = s.Step },
            LogLine l => new JsonObject { ["event"] = "log", ["level"] = l.Level.ToString().ToLowerInvariant(), ["text"] = l.Text, ["item"] = l.ItemId },
            _ => null,
        };

        if (line is not null)
            Emit(line);
    }

    public override Task<bool> ConfirmAsync(Confirmation confirmation, CancellationToken cancellationToken)
    {
        // A script cannot answer a prompt; it says --yes up front or gets the safe default.
        bool answer = AssumeYes || confirmation.DefaultAnswer;
        JsonArray items = [];

        foreach (ConfirmationItem item in confirmation.Items)
            items.Add(new JsonObject { ["name"] = item.Name, ["id"] = item.Id, ["current"] = item.Current, ["next"] = item.Next, ["note"] = item.Note });

        Emit(new JsonObject { ["event"] = "confirmation", ["title"] = confirmation.Title, ["detail"] = confirmation.Detail, ["items"] = items, ["answer"] = answer });
        return Task.FromResult(answer);
    }

    public override void Summary(PreflightReport report)
        => Emit(new JsonObject
        {
            ["event"] = "result",
            ["worst"] = report.Worst.ToString().ToLowerInvariant(),
            ["failed"] = report.Results.Count(r => r.Result.Status == PreflightStatus.Fail),
            ["warnings"] = report.Results.Count(r => r.Result.Status == PreflightStatus.Warn),
        });

    public override void Summary(RunSummary summary)
        => Emit(new JsonObject
        {
            ["event"] = "result",
            ["action"] = summary.Action.ToLowerInvariant(),
            ["ok"] = summary.Count(OutcomeKind.Ok),
            ["alreadyCurrent"] = summary.Count(OutcomeKind.AlreadyCurrent),
            ["skipped"] = summary.Count(OutcomeKind.Skipped),
            ["pending"] = summary.Count(OutcomeKind.Pending),
            ["failed"] = summary.Count(OutcomeKind.Failed),
            ["rebootRequired"] = summary.RebootRequired,
        });

    /// <summary>Packages were already streamed as "package" events; the table adds a closing count.</summary>
    public override void Table(IReadOnlyList<ListedPackage> packages)
        => Emit(new JsonObject { ["event"] = "result", ["packages"] = packages.Count, ["updates"] = packages.Count(p => p.Package.HasUpdate) });

    public override void Error(string message) => Emit(new JsonObject { ["event"] = "error", ["message"] = message });

    public override void Output(string text, JsonNode json) => Emit(new JsonObject { ["event"] = "data", ["data"] = json.DeepClone() });

    public override void Result(bool succeeded, string summary)
        => Emit(new JsonObject { ["event"] = "result", ["succeeded"] = succeeded, ["summary"] = summary });

    private static JsonObject Package(string kind, ListedPackage listed)
    {
        InstalledPackage p = listed.Package;

        return new JsonObject
        {
            ["event"] = kind,
            ["source"] = p.Source,
            ["id"] = p.Id,
            ["name"] = p.Name,
            ["installed"] = p.InstalledVersion,
            ["available"] = p.AvailableVersion,
            ["catalog"] = p.Catalog,
            ["kind"] = AppKinds.CliName(listed.Kind),
            ["publisher"] = p.Publisher,
            ["managedBy"] = p.ManagedBy,
            ["scope"] = p.Scope.ToString().ToLowerInvariant(),
            ["managed"] = p.IsManaged,
            ["held"] = listed.IsHeld,
            ["storeManaged"] = listed.IsStoreManaged,
            ["holdReason"] = listed.Hold?.Rule.Reason,
            ["problem"] = p.CheckProblem,
        };
    }

    private void Emit(JsonObject line)
    {
        lock (_gate)
            Console.WriteLine(line.ToJsonString(Compact));
    }
}
