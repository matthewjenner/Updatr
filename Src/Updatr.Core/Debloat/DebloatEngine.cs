using System.Diagnostics;
using Updatr.Core.Abstractions;
using Updatr.Core.Catalog;
using Updatr.Core.Operations;
using Updatr.Core.Packages;
using Updatr.Core.Sources;

namespace Updatr.Core.Debloat;

/// <summary>
/// Detect, apply, and revert debloat items. Status always comes from the live machine, so a
/// re-run touches only what drifted; every change is backed up (and the backup saved) before it is
/// made.
/// </summary>
/// <param name="catalog">The catalog (rules and keep list).</param>
/// <param name="targets">One per action kind.</param>
/// <param name="inventory">Installed Store apps, for the unrecognized list.</param>
/// <param name="restorePoint">System Restore, before machine-wide changes.</param>
/// <param name="store">History, backups, keep list.</param>
/// <param name="osBuild">This Windows build, for <see cref="DebloatItem.MinBuild"/>.</param>
public sealed class DebloatEngine(
    Func<CancellationToken, Task<AppCatalog>> catalog,
    IReadOnlyList<IDebloatTarget> targets,
    IAppxInventory inventory,
    IRestorePoint restorePoint,
    DebloatStore store,
    int osBuild)
{
    /// <summary>Prefix of the ids given to unrecognized apps removed one-off.</summary>
    public const string AppItemPrefix = "app:";

    private const string UnrecognizedCategory = "Unrecognized apps";

    public DebloatStore Store => store;

    public async Task<DebloatReport> StatusAsync(CancellationToken cancellationToken)
    {
        AppCatalog rules = await catalog(cancellationToken).ConfigureAwait(false);
        DebloatSnapshot? snapshot = await TryAsync(store.LoadSnapshotAsync(cancellationToken)).ConfigureAwait(false);
        HashSet<string> appliedBefore = [.. snapshot?.Applied ?? []];

        List<DebloatItemStatus> items = [];

        foreach (DebloatItem item in rules.Debloat)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DebloatItemStatus status = await DetectAsync(item, cancellationToken).ConfigureAwait(false);
            items.Add(status with { Drifted = appliedBefore.Contains(item.Id) && status.State is ItemState.NotApplied or ItemState.Partial });
        }

        IReadOnlyList<UnrecognizedApp> unrecognized = await UnrecognizedAsync(rules, snapshot, cancellationToken).ConfigureAwait(false);
        return new DebloatReport(items, unrecognized, snapshot?.At);
    }

    public async Task<RunSummary> ApplyAsync(DebloatApplyRequest request, IOperationSink sink, CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        List<ItemFinished> finished = [];
        AppCatalog rules = await catalog(cancellationToken).ConfigureAwait(false);

        sink.Report(new StepStarted("Checking what is applied"));
        List<DebloatItem> selected = await SelectAsync(request, rules, cancellationToken).ConfigureAwait(false);
        List<DebloatItemStatus> statuses = [];

        foreach (DebloatItem item in selected)
            statuses.Add(await DetectAsync(item, cancellationToken).ConfigureAwait(false));

        foreach (DebloatItemStatus status in statuses.Where(s => !s.NeedsWork))
        {
            Finish(sink, finished, status.Item, status.State == ItemState.Unavailable
                ? PackageOutcome.Skipped("Not on this version of Windows")
                : new PackageOutcome(OutcomeKind.AlreadyCurrent, "Already applied"));
        }

        List<DebloatItemStatus> work = [.. statuses.Where(s => s.NeedsWork)];

        if (work.Count == 0)
            return new RunSummary("Debloat", finished, timer.Elapsed);

        string detail = string.Join("\n", work.Select(s => $"  {s.Item.Title}" + (s.Item.Warning is { } w ? $"\n    Note: {w}" : "")))
            + "\n\nUpdatr backs up every change first; each item can be reverted.";

        if (!await sink.ConfirmAsync(new Confirmation($"Apply {work.Count} debloat item(s)?", detail), cancellationToken).ConfigureAwait(false))
        {
            foreach (DebloatItemStatus status in work)
                Finish(sink, finished, status.Item, PackageOutcome.Skipped("Not confirmed"));

            return new RunSummary("Debloat", finished, timer.Elapsed);
        }

        if (work.Any(s => Pending(s).Any(a => TargetFor(a)?.NeedsAdmin(a) == true)))
        {
            sink.Report(new StepStarted("Creating a restore point"));
            string? why = await restorePoint.CreateAsync("Updatr debloat", cancellationToken).ConfigureAwait(false);
            sink.Report(new LogLine(why is null ? LogLevel.Info : LogLevel.Warning,
                why is null ? "Restore point created." : $"No restore point ({why}). Updatr's own backup still covers every change."));
        }

        DateTimeOffset at = DateTimeOffset.Now;
        string backupPath = DebloatStore.BackupPathFor(at);
        BackupFile backup = new() { At = at };
        List<HistoryEntry> history = [];
        string? backupProblem = null;

        for (int i = 0; i < work.Count; i++)
        {
            DebloatItem item = work[i].Item;

            if (cancellationToken.IsCancellationRequested || backupProblem is not null)
            {
                Finish(sink, finished, item, PackageOutcome.Skipped(backupProblem is null ? "Canceled before it started" : "No backup could be saved, so nothing else was changed"));
                continue;
            }

            sink.Report(new StepStarted($"[{i + 1}/{work.Count}] {item.Title}"));
            IReadOnlyList<DebloatAction> pending = Pending(work[i]);
            List<ActionBackup> captured = [];

            try
            {
                foreach (DebloatAction action in pending)
                    captured.Add(await (TargetFor(action) ?? throw new NotSupportedException(action.GetType().Name)).CaptureAsync(action, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                Finish(sink, finished, item, PackageOutcome.Failed($"Could not read the current state, so nothing was changed: {ex.Message}"));
                continue;
            }

            backup = backup with { Items = [.. backup.Items, new ItemBackup { ItemId = item.Id, Title = item.Title, Actions = captured }] };

            try
            {
                await store.SaveBackupAsync(backupPath, backup, cancellationToken).ConfigureAwait(false);
            }
            catch (DataStoreException ex)
            {
                // No backup, no change: an unrevertable debloat is worse than none.
                backupProblem = ex.Message;
                Finish(sink, finished, item, PackageOutcome.Failed($"Could not save the backup, so nothing was changed: {ex.Message}"));
                continue;
            }

            List<PackageOutcome> outcomes = [];

            foreach (DebloatAction action in pending)
            {
                sink.Report(new LogLine(LogLevel.Detail, action.Describe, item.Id));
                outcomes.Add(await TargetFor(action)!.ApplyAsync(action, item.Id, sink, CancellationToken.None).ConfigureAwait(false));
            }

            PackageOutcome outcome = Combine(outcomes);
            Finish(sink, finished, item, outcome);
            history.Add(new HistoryEntry(at, "apply", item.Id, item.Title, outcome.Kind, outcome.Detail, backupPath));
        }

        if (history.Any(h => h.Outcome == OutcomeKind.Ok && !h.ItemId.StartsWith(AppItemPrefix, StringComparison.Ordinal)))
            sink.Report(new LogLine(LogLevel.Info, "Some changes show only after you sign out or restart (Start menu, taskbar, Explorer)."));

        await RecordAsync(rules, history, cancellationToken).ConfigureAwait(false);
        return new RunSummary("Debloat", finished, timer.Elapsed);
    }

    public async Task<RunSummary> RevertAsync(IReadOnlyList<string> itemIds, IOperationSink sink, CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        List<ItemFinished> finished = [];
        AppCatalog rules = await catalog(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<HistoryEntry> history = await store.LoadHistoryAsync(cancellationToken).ConfigureAwait(false);
        List<(string Id, string Title, ItemBackup Backup)> work = [];

        foreach (string id in itemIds.Distinct(StringComparer.Ordinal))
        {
            HistoryEntry? last = history.LastOrDefault(h => string.Equals(h.ItemId, id, StringComparison.Ordinal));
            string title = last?.Title ?? rules.Debloat.FirstOrDefault(d => d.Id == id)?.Title ?? id;
            ItemBackup? backup = null;

            if (last is { Kind: "apply", Backup: { } path })
            {
                BackupFile? file = await store.LoadBackupAsync(path, cancellationToken).ConfigureAwait(false);
                backup = file?.Items.LastOrDefault(b => b.ItemId == id);

                if (backup is null)
                {
                    Finish(sink, finished, id, title, PackageOutcome.Failed($"The backup ({path}) is missing from the data folder"));
                    continue;
                }
            }

            if (backup is null)
            {
                Finish(sink, finished, id, title, new PackageOutcome(OutcomeKind.AlreadyCurrent, last is null ? "Updatr has not applied this" : "Already reverted"));
                continue;
            }

            work.Add((id, title, backup));
        }

        if (work.Count == 0)
            return new RunSummary("Revert", finished, timer.Elapsed);

        if (!await sink.ConfirmAsync(new Confirmation($"Revert {work.Count} debloat item(s)?", string.Join("\n", work.Select(w => "  " + w.Title))), cancellationToken).ConfigureAwait(false))
        {
            foreach ((string id, string title, _) in work)
                Finish(sink, finished, id, title, PackageOutcome.Skipped("Not confirmed"));

            return new RunSummary("Revert", finished, timer.Elapsed);
        }

        DateTimeOffset at = DateTimeOffset.Now;
        List<HistoryEntry> entries = [];

        for (int i = 0; i < work.Count; i++)
        {
            (string id, string title, ItemBackup backup) = work[i];

            if (cancellationToken.IsCancellationRequested)
            {
                Finish(sink, finished, id, title, PackageOutcome.Skipped("Canceled before it started"));
                continue;
            }

            sink.Report(new StepStarted($"[{i + 1}/{work.Count}] Revert {title}"));
            List<PackageOutcome> outcomes = [];

            foreach (ActionBackup action in backup.Actions.Reverse())
            {
                IDebloatTarget? target = TargetFor(action.Action);
                sink.Report(new LogLine(LogLevel.Detail, "undo " + action.Action.Describe, id));
                outcomes.Add(target is null
                    ? PackageOutcome.Failed($"Updatr cannot undo {action.Action.GetType().Name} changes")
                    : await target.RevertAsync(action, id, sink, CancellationToken.None).ConfigureAwait(false));
            }

            PackageOutcome outcome = Combine(outcomes);
            Finish(sink, finished, id, title, outcome);
            entries.Add(new HistoryEntry(at, "revert", id, title, outcome.Kind, outcome.Detail, null));
        }

        await RecordAsync(rules, entries, cancellationToken).ConfigureAwait(false);
        return new RunSummary("Revert", finished, timer.Elapsed);
    }

    /// <summary>Detects every action of an item and rolls them up.</summary>
    public async Task<DebloatItemStatus> DetectAsync(DebloatItem item, CancellationToken cancellationToken)
    {
        if (item.MinBuild is { } min && osBuild < min)
            return new DebloatItemStatus(item, ItemState.Unavailable, [.. item.Actions.Select(_ => new ActionStatus(ActionState.NotApplicable))], false);

        List<ActionStatus> actions = [];

        foreach (DebloatAction action in item.Actions)
        {
            IDebloatTarget? target = TargetFor(action);

            try
            {
                actions.Add(target is null
                    ? new ActionStatus(ActionState.Unknown, "Not supported by this build")
                    : await target.DetectAsync(action, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                actions.Add(new ActionStatus(ActionState.Unknown, ex.Message));
            }
        }

        return new DebloatItemStatus(item, RollUp(actions), actions, false);
    }

    /// <summary>Overall state from the per-action states. Not-applicable actions do not count.</summary>
    public static ItemState RollUp(IReadOnlyList<ActionStatus> actions)
    {
        ActionState[] relevant = [.. actions.Select(a => a.State).Where(s => s != ActionState.NotApplicable)];

        if (relevant.All(s => s == ActionState.Applied))
            return ItemState.Applied;
        if (relevant.Any(s => s == ActionState.NotApplied))
            return relevant.Any(s => s == ActionState.Applied) ? ItemState.Partial : ItemState.NotApplied;

        return ItemState.Unknown;
    }

    /// <summary>One outcome for an item from its actions' outcomes.</summary>
    public static PackageOutcome Combine(IReadOnlyList<PackageOutcome> outcomes)
    {
        string details = string.Join("; ", outcomes.Select(o => o.Detail).Where(d => d.Length > 0).Distinct(StringComparer.Ordinal));
        bool reboot = outcomes.Any(o => o.RebootRequired);

        if (outcomes.Any(o => o.Kind == OutcomeKind.Failed))
            return new PackageOutcome(OutcomeKind.Failed, details, reboot);
        if (outcomes.Count > 0 && outcomes.All(o => o.Kind == OutcomeKind.Skipped))
            return new PackageOutcome(OutcomeKind.Skipped, details, reboot);
        if (outcomes.Count > 0 && outcomes.All(o => o.Kind == OutcomeKind.AlreadyCurrent))
            return new PackageOutcome(OutcomeKind.AlreadyCurrent, details, reboot);

        bool partly = outcomes.Any(o => o.Kind == OutcomeKind.Skipped);
        return new PackageOutcome(OutcomeKind.Ok, partly ? "Partly applied - " + details : details, reboot);
    }

    private async Task<List<DebloatItem>> SelectAsync(DebloatApplyRequest request, AppCatalog rules, CancellationToken cancellationToken)
    {
        List<DebloatItem> selected = [.. rules.Debloat.Where(d => request.Recommended && d.Group == DebloatGroup.Recommended)];

        foreach (string id in request.ItemIds)
        {
            DebloatItem item = rules.Debloat.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No debloat item '{id}' (see: debloat status).");

            if (!selected.Contains(item))
                selected.Add(item);
        }

        if (request.RemoveApps.Count > 0)
        {
            IReadOnlyList<StoreApp> installed = await inventory.ListRemovableAsync(cancellationToken).ConfigureAwait(false);

            foreach (string name in request.RemoveApps)
            {
                StoreApp app = installed.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"No removable Store app named '{name}' is installed.");

                selected.Add(new DebloatItem
                {
                    Id = AppItemPrefix + app.Name,
                    Group = DebloatGroup.Optional,
                    Category = UnrecognizedCategory,
                    Title = $"Remove {app.DisplayName}",
                    Description = $"{app.Name} ({app.Publisher ?? "unknown publisher"})",
                    Actions = [new AppxAction { Name = app.Name }],
                });
            }
        }

        if (selected.Count == 0)
            throw new ArgumentException("Nothing selected: pass --recommended, --item, or --app.");

        return selected;
    }

    private async Task<IReadOnlyList<UnrecognizedApp>> UnrecognizedAsync(AppCatalog rules, DebloatSnapshot? snapshot, CancellationToken cancellationToken)
    {
        IReadOnlyList<StoreApp> installed = await inventory.ListRemovableAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> mine = await TryAsync(store.LoadKeepAsync(cancellationToken)).ConfigureAwait(false) ?? [];

        WildcardPattern[] known = [.. rules.Debloat.SelectMany(d => d.Actions).OfType<AppxAction>().Select(a => new WildcardPattern(a.Name))
            .Concat(rules.DebloatKeep.Select(k => new WildcardPattern(k)))];
        WildcardPattern[] kept = [.. mine.Select(k => new WildcardPattern(k))];
        HashSet<string> seen = new(snapshot?.Seen ?? [], StringComparer.OrdinalIgnoreCase);

        return [.. installed
            .Where(a => !known.Any(p => p.IsMatch(a.Name)))
            .Select(a => new UnrecognizedApp(a, kept.Any(p => p.IsMatch(a.Name)), snapshot is not null && !seen.Contains(a.Name)))
            .OrderBy(u => u.App.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>History and the last-run snapshot. A read-only drive loses them, not the run.</summary>
    private async Task RecordAsync(AppCatalog rules, IReadOnlyList<HistoryEntry> entries, CancellationToken cancellationToken)
    {
        try
        {
            if (entries.Count > 0)
                await store.AppendHistoryAsync(entries, CancellationToken.None).ConfigureAwait(false);

            List<string> applied = [];

            foreach (DebloatItem item in rules.Debloat)
            {
                if ((await DetectAsync(item, CancellationToken.None).ConfigureAwait(false)).State == ItemState.Applied)
                    applied.Add(item.Id);
            }

            IReadOnlyList<StoreApp> apps = await inventory.ListRemovableAsync(CancellationToken.None).ConfigureAwait(false);
            await store.SaveSnapshotAsync(new DebloatSnapshot { At = DateTimeOffset.Now, Applied = applied, Seen = [.. apps.Select(a => a.Name)] }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (DataStoreException)
        {
            // Recorded as a data-folder failure by the store; the changes themselves were made.
        }
    }

    private IReadOnlyList<DebloatAction> Pending(DebloatItemStatus status)
        => [.. status.Item.Actions.Where((_, i) => status.Actions[i].State is ActionState.NotApplied or ActionState.Unknown)];

    private IDebloatTarget? TargetFor(DebloatAction action) => targets.FirstOrDefault(t => t.Handles(action));

    private static async Task<T?> TryAsync<T>(Task<T> task) where T : class?
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (DataStoreException)
        {
            return null;
        }
    }

    private static void Finish(IOperationSink sink, List<ItemFinished> finished, DebloatItem item, PackageOutcome outcome)
        => Finish(sink, finished, item.Id, item.Title, outcome);

    private static void Finish(IOperationSink sink, List<ItemFinished> finished, string id, string title, PackageOutcome outcome)
    {
        ItemFinished done = new(id, title, outcome);
        finished.Add(done);
        sink.Report(done);
    }
}
