using System.Text.Json;
using System.Text.Json.Nodes;
using Updatr.Core.Operations;
using Updatr.Core.Preflight;

namespace Updatr.Cli;

/// <summary>Renders operation events as text rows for a person at a console.</summary>
public class ConsoleSink(bool assumeYes) : IOperationSink
{
    private readonly Lock _gate = new();

    /// <summary>Show every check's detail, not only warnings and failures.</summary>
    public bool Verbose { get; set; }

    protected bool Colors { get; } = !Console.IsOutputRedirected;

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

    public virtual void Error(string message) => Line(ConsoleColor.Red, message);

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
            Write(color, tag);
            Console.WriteLine($" {check.Title,-22} {check.Result.Summary}");

            bool showDetail = check.Result.Detail is not null && (Verbose || check.Result.Status is PreflightStatus.Warn or PreflightStatus.Fail);

            if (showDetail)
            {
                foreach (string line in check.Result.Detail!.Split('\n'))
                    Line(ConsoleColor.DarkGray, $"         {line}");
            }
        }
    }

    protected void Line(ConsoleColor color, string text)
    {
        lock (_gate)
        {
            Write(color, text);
            Console.WriteLine();
        }
    }

    private void Write(ConsoleColor color, string text)
    {
        if (!Colors)
        {
            Console.Write(text);
            return;
        }

        ConsoleColor previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = previous;
    }
}

/// <summary>
/// <c>--json</c>: one JSON object per line on stdout, so scripts can parse progress as it happens.
/// Field names are stable; every line has an <c>event</c>.
/// </summary>
public sealed class JsonLinesSink(bool assumeYes) : ConsoleSink(assumeYes)
{
    // Relaxed escaping: Windows paths and apostrophes stay readable ('C:\x', not \u0027C:\\x\u0027).
    // Still valid JSON, and stdout is never embedded in HTML.
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
                ["elapsedMs"] = (long)c.Elapsed.TotalMilliseconds,
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
        Emit(new JsonObject { ["event"] = "confirmation", ["title"] = confirmation.Title, ["answer"] = answer });
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

    public override void Error(string message) => Emit(new JsonObject { ["event"] = "error", ["message"] = message });

    public override void Result(bool succeeded, string summary)
        => Emit(new JsonObject { ["event"] = "result", ["succeeded"] = succeeded, ["summary"] = summary });

    private void Emit(JsonObject line)
    {
        lock (_gate)
            Console.WriteLine(line.ToJsonString(Compact));
    }
}
