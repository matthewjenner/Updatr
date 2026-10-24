using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Updatr.Core.Catalog;
using Updatr.Core.Debloat;
using Updatr.Core.Packages;

namespace Updatr.Cli;

/// <summary>Commands for debloat (Phase 6).</summary>
internal static class DebloatCommands
{
    public static Command Command()
    {
        Command debloat = new("debloat", "Remove Windows ads and junk. Re-runnable: only what has drifted is changed, and every change can be reverted.");

        foreach (Command c in new[] { Status(), Apply(), Revert(), History(), Keep("keep"), Keep("unkeep") })
            debloat.Subcommands.Add(c);

        return debloat;
    }

    private static Command Status()
    {
        Option<bool> all = new("--all") { Description = "Also list kept Store apps and items for other Windows versions." };
        Option<bool> changed = new("--changed") { Description = "Only what is new or was put back since the last run." };
        Command status = new("status", "Show every debloat item's state on this machine, and Store apps no rule knows.") { all, changed };

        status.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            DebloatReport report = await host.DebloatStatus.RunAsync(true, sink, ct).ConfigureAwait(false);
            bool showAll = parse.GetValue(all);
            bool onlyChanged = parse.GetValue(changed);
            StringBuilder text = new();

            if (report.LastRun is { } last)
                text.AppendLine(CultureInfo.InvariantCulture, $"Last run {last:yyyy-MM-dd HH:mm}.");

            DebloatItemStatus[] shown = [.. report.Items.Where(i => (showAll || i.State != ItemState.Unavailable) && (!onlyChanged || i.Drifted))];
            UnrecognizedApp[] apps = [.. report.Unrecognized.Where(u => (showAll || !u.Kept) && (!onlyChanged || u.IsNew))];

            if (onlyChanged && shown.Length == 0 && apps.Length == 0)
                text.AppendLine("Nothing changed since the last run.");

            foreach (IGrouping<DebloatGroup, DebloatItemStatus> group in shown.GroupBy(i => i.Item.Group))
            {
                text.AppendLine().AppendLine(group.Key == DebloatGroup.Recommended ? "Recommended (applied by: debloat apply --recommended)" : "Optional (pick with --item)");

                foreach (DebloatItemStatus item in group)
                    text.AppendLine(CultureInfo.InvariantCulture, $"  {Tag(item),-14} {item.Item.Id,-24} {item.Item.Title}");
            }

            if (apps.Length > 0)
            {
                text.AppendLine().AppendLine("Unrecognized Store apps (remove with --app, or: debloat keep <name>)");

                foreach (UnrecognizedApp app in apps)
                    text.AppendLine(CultureInfo.InvariantCulture, $"  {(app.Kept ? "[kept]" : app.IsNew ? "[new]" : ""),-12} {app.App.Name,-48} {app.App.DisplayName}");
            }

            sink.Output(text.ToString().TrimEnd(), Json(report));
            return CliApp.ExitCodes.Ok;
        }));

        return status;
    }

    private static Command Apply()
    {
        Option<bool> recommended = new("--recommended", "-r") { Description = "Every recommended item (ads, junk, baseline privacy)." };
        Option<string[]> items = new("--item", "-i") { Description = "Item ids (see: debloat status). Repeatable.", AllowMultipleArgumentsPerToken = true };
        Option<string[]> apps = new("--app") { Description = "Unrecognized Store app names to remove. Repeatable.", AllowMultipleArgumentsPerToken = true };
        Command apply = new("apply", "Apply debloat items. Backs everything up first; a restore point is made when machine-wide settings change.") { recommended, items, apps };

        apply.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            DebloatApplyRequest request = new()
            {
                Recommended = parse.GetValue(recommended),
                ItemIds = parse.GetValue(items) ?? [],
                RemoveApps = parse.GetValue(apps) ?? [],
            };

            RunSummary summary = await host.DebloatApply.RunAsync(request, sink, ct).ConfigureAwait(false);
            sink.Summary(summary);
            return summary.HasFailures ? CliApp.ExitCodes.Failed : CliApp.ExitCodes.Ok;
        }));

        return apply;
    }

    private static Command Revert()
    {
        Argument<string[]> ids = new("ids") { Description = "Item ids to put back the way they were (see: debloat history).", Arity = ArgumentArity.OneOrMore };
        Command revert = new("revert", "Undo debloat items from Updatr's backup. Removed Store apps are reinstalled from the Store.") { ids };

        revert.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            RunSummary summary = await host.DebloatRevert.RunAsync(parse.GetValue(ids)!, sink, ct).ConfigureAwait(false);
            sink.Summary(summary);
            return summary.HasFailures ? CliApp.ExitCodes.Failed : CliApp.ExitCodes.Ok;
        }));

        return revert;
    }

    private static Command History()
    {
        Command history = new("history", "What debloat has applied and reverted on machines this drive has been used on.");

        history.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            IReadOnlyList<HistoryEntry> entries = await host.DebloatHistory.RunAsync(true, sink, ct).ConfigureAwait(false);
            string text = entries.Count == 0
                ? "Nothing applied yet."
                : string.Join(Environment.NewLine, entries.Select(e => string.Create(CultureInfo.InvariantCulture,
                    $"{e.At:yyyy-MM-dd HH:mm}  {e.Kind,-6}  {e.Outcome,-14} {e.ItemId,-28} {e.Detail}").TrimEnd()));

            sink.Output(text, new JsonArray([.. entries.Select(e => (JsonNode)new JsonObject
            {
                ["at"] = e.At,
                ["kind"] = e.Kind,
                ["item"] = e.ItemId,
                ["title"] = e.Title,
                ["outcome"] = e.Outcome.ToString(),
                ["detail"] = e.Detail,
            })]));
            return CliApp.ExitCodes.Ok;
        }));

        return history;
    }

    private static Command Keep(string name)
    {
        Argument<string[]> apps = new("names") { Description = "Store app package names (wildcards allowed).", Arity = ArgumentArity.OneOrMore };
        Command command = new(name, name == "keep"
            ? "Keep Store apps: they stop showing as unrecognized."
            : "Stop keeping Store apps.") { apps };

        command.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            string[] names = parse.GetValue(apps)!;
            IReadOnlyList<string> keep = name == "keep"
                ? await host.DebloatKeep.RunAsync(names, sink, ct).ConfigureAwait(false)
                : await host.DebloatUnkeep.RunAsync(names, sink, ct).ConfigureAwait(false);

            sink.Result(true, $"Keep list: {(keep.Count == 0 ? "empty" : string.Join(", ", keep))}");
            return CliApp.ExitCodes.Ok;
        }));

        return command;
    }

    private static string Tag(DebloatItemStatus item) => item.State switch
    {
        _ when item.Drifted => "[put back]",
        ItemState.Applied => "[applied]",
        ItemState.Partial => "[partial]",
        ItemState.NotApplied => "[not applied]",
        ItemState.Unavailable => "[n/a]",
        _ => "[unknown]",
    };

    private static JsonObject Json(DebloatReport report) => new()
    {
        ["lastRun"] = report.LastRun,
        ["items"] = new JsonArray([.. report.Items.Select(i => (JsonNode)new JsonObject
        {
            ["id"] = i.Item.Id,
            ["group"] = i.Item.Group.ToString().ToLowerInvariant(),
            ["category"] = i.Item.Category,
            ["title"] = i.Item.Title,
            ["state"] = i.State.ToString(),
            ["drifted"] = i.Drifted,
            ["actions"] = new JsonArray([.. i.Item.Actions.Select((a, n) => (JsonNode)new JsonObject
            {
                ["action"] = a.Describe,
                ["state"] = i.Actions[n].State.ToString(),
                ["detail"] = i.Actions[n].Detail,
            })]),
        })]),
        ["unrecognized"] = new JsonArray([.. report.Unrecognized.Select(u => (JsonNode)new JsonObject
        {
            ["name"] = u.App.Name,
            ["displayName"] = u.App.DisplayName,
            ["publisher"] = u.App.Publisher,
            ["kept"] = u.Kept,
            ["new"] = u.IsNew,
        })]),
    };
}
