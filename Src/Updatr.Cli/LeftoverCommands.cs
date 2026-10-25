using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Updatr.Core.Leftovers;
using Updatr.Core.Sources;

namespace Updatr.Cli;

/// <summary>Commands for leftovers after uninstall, and the quarantine (Phase 8).</summary>
internal static class LeftoverCommands
{
    public static IEnumerable<Command> All() => [LeftoversCommand(), QuarantineCommand()];

    private static Command LeftoversCommand()
    {
        Command list = new("list", "Leftovers found after uninstalls on this PC, waiting for review.");
        list.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            IReadOnlyList<LeftoverScan> scans = await host.LeftoversList.RunAsync(true, sink, ct).ConfigureAwait(false);
            StringBuilder text = new();

            foreach (LeftoverScan scan in scans)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"{scan.Id}  {scan.Target.Name} (uninstalled {scan.At:yyyy-MM-dd HH:mm})");

                foreach (Leftover item in scan.Items)
                    text.AppendLine(CultureInfo.InvariantCulture, $"  {item.Id,3}  {item.Confidence,-6}  {item.Kind,-13} {item.Location}{Size(item.SizeBytes)}  - {item.Reason}");
            }

            sink.Output(scans.Count == 0 ? "No leftovers waiting for review." : text.ToString().TrimEnd(), new JsonArray([.. scans.Select(s => (JsonNode)new JsonObject
            {
                ["id"] = s.Id,
                ["app"] = s.Target.Name,
                ["at"] = s.At,
                ["items"] = new JsonArray([.. s.Items.Select(i => (JsonNode)new JsonObject
                {
                    ["id"] = i.Id,
                    ["kind"] = i.Kind.ToString(),
                    ["location"] = i.Location,
                    ["confidence"] = i.Confidence.ToString(),
                    ["reason"] = i.Reason,
                    ["sizeBytes"] = i.SizeBytes,
                })]),
            })]));
            return CliApp.ExitCodes.Ok;
        }));

        Argument<string> scanId = new("scan") { Description = "Scan id (see: leftovers list)." };
        Option<string[]> items = new("--item", "-i") { Description = "Item numbers within the scan (repeatable).", AllowMultipleArgumentsPerToken = true };
        Option<bool> high = new("--high") { Description = "Every high-confidence item." };
        Command quarantine = new("quarantine", "Move picked leftovers into quarantine on the drive (restorable). Nothing is picked unless you say so.") { scanId, items, high };
        quarantine.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            QuarantineEntry? entry = await host.LeftoversQuarantine.RunAsync(new QuarantineRequest(parse.GetValue(scanId)!, parse.GetValue(items) ?? [], parse.GetValue(high)), sink, ct).ConfigureAwait(false);

            if (entry is null)
            {
                sink.Result(true, "Nothing moved.");
                return CliApp.ExitCodes.Ok;
            }

            foreach (QuarantinedItem item in entry.Items.Where(i => !i.Stored || i.Problem is not null))
                sink.Error($"{item.Location}: {(item.Stored ? "moved, but " : "left in place - ")}{item.Problem}");

            bool failed = entry.Items.Any(i => !i.Stored);
            sink.Result(!failed, $"Quarantined {entry.Items.Count(i => i.Stored)} of {entry.Items.Count} as {entry.Id}{Size(entry.SizeBytes)}. Restore with: quarantine restore {entry.Id}");
            return failed ? CliApp.ExitCodes.Failed : CliApp.ExitCodes.Ok;
        }));

        Argument<string> dismissId = new("scan");
        Command dismiss = new("dismiss", "Drop a scan from review. Nothing on the PC is touched.") { dismissId };
        dismiss.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            await host.LeftoversDismiss.RunAsync(parse.GetValue(dismissId)!, sink, ct).ConfigureAwait(false);
            sink.Result(true, $"Dismissed {parse.GetValue(dismissId)}.");
            return CliApp.ExitCodes.Ok;
        }));

        Command leftovers = new("leftovers", "What uninstallers left behind: review, then quarantine (never deleted outright).");
        foreach (Command c in new[] { list, quarantine, dismiss })
            leftovers.Subcommands.Add(c);
        return leftovers;
    }

    private static Command QuarantineCommand()
    {
        Command list = new("list", "Quarantined leftovers from this PC.");
        list.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            IReadOnlyList<QuarantineEntry> entries = await host.QuarantineList.RunAsync(true, sink, ct).ConfigureAwait(false);
            string text = string.Join(Environment.NewLine, entries.Select(e => string.Create(CultureInfo.InvariantCulture,
                $"{e.Id}  {e.App}  {e.Items.Count(i => i.Stored)} item(s){Size(e.SizeBytes)}  {e.At:yyyy-MM-dd HH:mm}")));

            sink.Output(entries.Count == 0 ? "Quarantine is empty." : text, new JsonArray([.. entries.Select(e => (JsonNode)new JsonObject
            {
                ["id"] = e.Id,
                ["app"] = e.App,
                ["at"] = e.At,
                ["sizeBytes"] = e.SizeBytes,
                ["items"] = new JsonArray([.. e.Items.Select(i => (JsonNode)new JsonObject { ["kind"] = i.Kind.ToString(), ["location"] = i.Location, ["stored"] = i.Stored, ["problem"] = i.Problem })]),
            })]));
            return CliApp.ExitCodes.Ok;
        }));

        Argument<string> restoreId = new("entry");
        Command restore = new("restore", "Put a quarantined entry back where it came from (existing files are not overwritten).") { restoreId };
        restore.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            PackageOutcome outcome = await host.QuarantineRestore.RunAsync(parse.GetValue(restoreId)!, sink, ct).ConfigureAwait(false);
            sink.Result(outcome.Kind == OutcomeKind.Ok, outcome.Kind == OutcomeKind.Ok ? "Restored." : outcome.Detail);
            return outcome.Kind == OutcomeKind.Failed ? CliApp.ExitCodes.Failed : CliApp.ExitCodes.Ok;
        }));

        Argument<string> purgeId = new("entry");
        Command purge = new("purge", "Delete a quarantined entry for good.") { purgeId };
        purge.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            bool purged = await host.QuarantinePurge.RunAsync(parse.GetValue(purgeId)!, sink, ct).ConfigureAwait(false);
            sink.Result(true, purged ? "Purged." : "Not purged.");
            return CliApp.ExitCodes.Ok;
        }));

        Command quarantine = new("quarantine", "Leftovers moved out of the way, restorable until purged.");
        foreach (Command c in new[] { list, restore, purge })
            quarantine.Subcommands.Add(c);
        return quarantine;
    }

    private static string Size(long? bytes) => bytes is > 0 ? string.Create(CultureInfo.InvariantCulture, $" ({bytes / 1048576.0:0.#} MB)") : string.Empty;
}
