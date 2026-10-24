using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Updatr.App.Services;
using Updatr.Core.Catalog;
using Updatr.Core.Debloat;
using Updatr.Core.Operations;
using Updatr.Core.Packages;
using Updatr.Core.Sources;
using Updatr.Platform;

namespace Updatr.App.ViewModels;

/// <summary>A heading in the debloat list ("Recommended - Ads and suggestions").</summary>
public sealed record DebloatHeaderViewModel(string Title, string? Subtitle);

/// <summary>One debloat item: its live state, whether it is picked, and the last run's result.</summary>
public sealed partial class DebloatItemRowViewModel(DebloatItemStatus status) : ObservableObject
{
    public DebloatItemStatus Status { get; } = status;

    public string Id => Status.Item.Id;

    public string Title => Status.Item.Title;

    public string Description => Status.Item.Description;

    public string? Warning => Status.Item.Warning;

    public bool CanSelect => Status.State != ItemState.Unavailable;

    public string StateText => Status.Drifted ? "Put back since last run" : Status.State switch
    {
        ItemState.Applied => "Applied",
        ItemState.Partial => "Partly applied",
        ItemState.NotApplied => "Not applied",
        ItemState.Unavailable => "Not on this Windows",
        _ => "Unknown",
    };

    /// <summary>Reuses the check colors: applied is green, pending blue, partial or put back amber.</summary>
    public CheckRowState Tone => Status.Drifted ? CheckRowState.Warn : Status.State switch
    {
        ItemState.Applied => CheckRowState.Pass,
        ItemState.Partial => CheckRowState.Warn,
        ItemState.NotApplied => CheckRowState.Running,
        _ => CheckRowState.Pending,
    };

    /// <summary>Every action and what detection found, for the tooltip.</summary>
    public string Details => string.Join(Environment.NewLine, Status.Item.Actions.Select((a, i) =>
        $"{(Status.Actions[i].State == ActionState.Applied ? "done" : Status.Actions[i].State == ActionState.NotApplicable ? "n/a " : "todo")}  {a.Describe}"
        + (Status.Actions[i].Detail is { } d ? $"  ({d})" : "")));

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial string? Activity { get; set; }

    [ObservableProperty]
    public partial OutcomeKind? Outcome { get; set; }
}

/// <summary>A Store app no rule knows: remove it, or keep it so it stops being flagged.</summary>
public sealed partial class UnrecognizedRowViewModel(UnrecognizedApp app, DebloatPageViewModel page) : ObservableObject
{
    public UnrecognizedApp App { get; } = app;

    public string Name => App.App.DisplayName;

    public string Detail => $"{App.App.Name}{(App.App.Publisher is { } p ? " - " + p : "")}{(App.IsNew ? " - new since last run" : "")}";

    public bool IsKept => App.Kept;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private Task Keep() => page.KeepAsync(App.App.Name, keep: !App.Kept);
}

/// <summary>A line of debloat history, with revert for the latest apply of each item.</summary>
public sealed partial class DebloatHistoryRowViewModel(HistoryEntry entry, bool canRevert, DebloatPageViewModel page) : ObservableObject
{
    public string When => entry.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public string Text => $"{(entry.Kind == "apply" ? "Applied" : "Reverted")} {entry.Title}";

    public string? Detail => entry.Detail.Length == 0 ? null : entry.Detail;

    public OutcomeKind Outcome => entry.Outcome;

    public bool CanRevert => canRevert;

    [RelayCommand]
    private Task Revert() => page.RevertAsync(entry.ItemId);
}

/// <summary>
/// The Debloat screen: every item with its live state, recommended items pre-picked when they
/// need work, unrecognized Store apps for review, and the history with per-item revert.
/// </summary>
public sealed partial class DebloatPageViewModel(UpdatrHost host, IDialogService dialogs) : PageViewModel
{
    private DebloatReport? _report;
    private CancellationTokenSource? _run;
    private bool _loaded;
    private bool _picked;

    public override string Title => "Debloat";

    /// <summary>Headers, items, and unrecognized apps in display order.</summary>
    public ObservableCollection<object> Rows { get; } = [];

    public ObservableCollection<DebloatHistoryRowViewModel> History { get; } = [];

    [ObservableProperty]
    public partial bool ShowOnlyChanged { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "Checking this PC...";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(RefreshCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string BusyText { get; set; } = string.Empty;

    private bool IsIdle => !IsBusy;

    partial void OnShowOnlyChangedChanged(bool value) => Rebuild();

    public void EnsureLoaded()
    {
        if (_loaded)
            return;

        _loaded = true;
        _ = RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        BusyText = "Checking what is applied on this PC...";

        try
        {
            _report = await Task.Run(() => host.DebloatStatus.RunAsync(true, NullSink, CancellationToken.None));
            IReadOnlyList<HistoryEntry> history = await Task.Run(() => host.DebloatHistory.RunAsync(true, NullSink, CancellationToken.None));
            Rebuild();
            RebuildHistory(history);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Core.Diagnostics.DiagLog.UnhandledException("Debloat page", ex);
            Summary = $"Could not check this PC: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SelectRecommended()
    {
        foreach (DebloatItemRowViewModel row in Rows.OfType<DebloatItemRowViewModel>())
            row.IsSelected = row.Status.Item.Group == DebloatGroup.Recommended && row.Status.NeedsWork;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (DebloatItemRowViewModel row in Rows.OfType<DebloatItemRowViewModel>())
            row.IsSelected = false;

        foreach (UnrecognizedRowViewModel row in Rows.OfType<UnrecognizedRowViewModel>())
            row.IsSelected = false;
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ApplyAsync()
    {
        DebloatApplyRequest request = new()
        {
            ItemIds = [.. Rows.OfType<DebloatItemRowViewModel>().Where(r => r.IsSelected).Select(r => r.Id)],
            RemoveApps = [.. Rows.OfType<UnrecognizedRowViewModel>().Where(r => r.IsSelected).Select(r => r.App.App.Name)],
        };

        if (request.ItemIds.Count + request.RemoveApps.Count == 0)
        {
            BusyText = "Pick something to apply first.";
            return;
        }

        await RunAsync("Applying...", (sink, ct) => host.DebloatApply.RunAsync(request, sink, ct));
    }

    public Task RevertAsync(string itemId)
        => RunAsync("Reverting...", (sink, ct) => host.DebloatRevert.RunAsync([itemId], sink, ct));

    public async Task KeepAsync(string name, bool keep)
    {
        try
        {
            if (keep)
                await host.DebloatKeep.RunAsync([name], NullSink, CancellationToken.None);
            else
                await host.DebloatUnkeep.RunAsync([name], NullSink, CancellationToken.None);
        }
        catch (Core.Abstractions.DataStoreException ex)
        {
            BusyText = ex.Message;
            return;
        }

        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        _run?.Cancel();
        BusyText = "Canceling - the item in progress will finish first...";
    }

    private async Task RunAsync(string text, Func<IOperationSink, CancellationToken, Task<RunSummary>> operation)
    {
        if (IsBusy)
            return;

        _run = new CancellationTokenSource();
        IsBusy = true;
        BusyText = text;
        RunSummary? summary = null;

        try
        {
            GuiSink sink = new(OnEvent, dialogs.ConfirmAsync);
            CancellationToken token = _run.Token;
            summary = await Task.Run(() => operation(sink, token));
        }
        catch (ArgumentException ex)
        {
            BusyText = ex.Message;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Core.Diagnostics.DiagLog.UnhandledException("Debloat page", ex);
            BusyText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _run.Dispose();
            _run = null;
        }

        if (summary is { Items.Count: > 0 })
        {
            await dialogs.ShowSummaryAsync(summary);
            await RefreshAsync();
        }
    }

    private void OnEvent(OperationEvent operationEvent)
    {
        DebloatItemRowViewModel? Row(string? id) => Rows.OfType<DebloatItemRowViewModel>().FirstOrDefault(r => r.Id == id);

        switch (operationEvent)
        {
            case StepStarted step:
                BusyText = step.Step;
                break;
            case ProgressReported { Message: { } message } progress when Row(progress.ItemId) is { } row:
                row.Activity = message;
                break;
            case ItemFinished done when Row(done.ItemId) is { } row:
                row.Outcome = done.Outcome.Kind;
                row.Activity = done.Outcome.Detail.Length > 0 ? done.Outcome.Detail : done.Outcome.Kind.ToString();
                break;
        }
    }

    private void Rebuild()
    {
        if (_report is null)
            return;

        HashSet<string> selected = [.. Rows.OfType<DebloatItemRowViewModel>().Where(r => r.IsSelected).Select(r => r.Id)];
        bool first = !_picked;
        _picked = true;
        Rows.Clear();

        foreach (IGrouping<DebloatGroup, DebloatItemStatus> group in _report.Items.Where(i => !ShowOnlyChanged || i.Drifted).GroupBy(i => i.Item.Group))
        {
            foreach (IGrouping<string, DebloatItemStatus> category in group.GroupBy(i => i.Item.Category))
            {
                Rows.Add(new DebloatHeaderViewModel(category.Key, group.Key == DebloatGroup.Recommended ? "Recommended" : "Optional - pick what you want"));

                foreach (DebloatItemStatus status in category)
                {
                    bool pick = first ? status.Item.Group == DebloatGroup.Recommended && status.NeedsWork : selected.Contains(status.Item.Id);
                    Rows.Add(new DebloatItemRowViewModel(status) { IsSelected = pick && status.State != ItemState.Unavailable });
                }
            }
        }

        UnrecognizedApp[] apps = [.. _report.Unrecognized.Where(u => !ShowOnlyChanged || u.IsNew)];

        if (apps.Length > 0)
        {
            Rows.Add(new DebloatHeaderViewModel("Unrecognized Store apps", "Not known to Updatr - review, remove, or keep. Nothing here is removed unless you pick it."));

            foreach (UnrecognizedApp app in apps.OrderBy(a => a.Kept))
                Rows.Add(new UnrecognizedRowViewModel(app, this));
        }

        int recommended = _report.Items.Count(i => i.Item.Group == DebloatGroup.Recommended);
        int applied = _report.Items.Count(i => i.Item.Group == DebloatGroup.Recommended && i.State == ItemState.Applied);
        int drifted = _report.Items.Count(i => i.Drifted);
        int newApps = _report.Unrecognized.Count(u => u.IsNew && !u.Kept);

        Summary = $"{applied} of {recommended} recommended items applied."
            + (drifted > 0 ? $" {drifted} put back since the last run." : "")
            + (newApps > 0 ? $" {newApps} new unrecognized app(s)." : "")
            + (_report.LastRun is { } last ? $" Last run {last.ToLocalTime():yyyy-MM-dd HH:mm}." : " Not run on this PC yet.");
    }

    private void RebuildHistory(IReadOnlyList<HistoryEntry> history)
    {
        History.Clear();
        HashSet<string> latestSeen = new(StringComparer.Ordinal);

        foreach (HistoryEntry entry in history.Reverse().Take(200))
        {
            // Only the newest entry per item can be reverted, and only if it is an apply.
            bool newest = latestSeen.Add(entry.ItemId);
            History.Add(new DebloatHistoryRowViewModel(entry, newest && entry.Kind == "apply" && entry.Outcome is OutcomeKind.Ok or OutcomeKind.Failed, this));
        }
    }

    private static readonly IOperationSink NullSink = new GuiSink(_ => { });
}
