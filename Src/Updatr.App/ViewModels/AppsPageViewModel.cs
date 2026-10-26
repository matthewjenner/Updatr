using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Updatr.App.Services;
using Updatr.Core.Operations;
using Updatr.Core.Packages;
using Updatr.Core.Sources;
using Updatr.Platform;

namespace Updatr.App.ViewModels;

/// <summary>
/// The Apps screen: everything installed, what has updates, and every action on it - upgrade,
/// reinstall, uninstall, hold - on one selection, so an app found while updating can be removed
/// without listing everything again elsewhere. Leftovers from uninstalls are its second tab. A thin
/// adapter over the CLI's <c>list</c>, <c>upgrade</c>, <c>reinstall</c>, <c>uninstall</c>,
/// <c>hold</c>, <c>unhold</c>, and <c>leftovers</c>.
/// </summary>
public sealed partial class AppsPageViewModel : PageViewModel
{
    private readonly UpdatrHost _host;
    private readonly IDialogService _dialogs;
    private readonly Dictionary<string, PackageRowViewModel> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _run;
    private bool _loaded;

    /// <summary>Keys listed during a quiet refresh; rows not among them have been uninstalled since.</summary>
    private HashSet<string>? _seen;

    public AppsPageViewModel(UpdatrHost host, IDialogService dialogs)
    {
        _host = host;
        _dialogs = dialogs;
        Leftovers = new LeftoversViewModel(host, dialogs);
        View = new DataGridCollectionView(Rows) { Filter = Matches };

        // Status starts on "something to do"; clear it to see every installed app.
        _status = new FilterGroupViewModel("Status",
            [("update", "Update available"), ("fresh", "Needs fresh install"), ("problem", "Could not check"), ("held", "Held / self-updating"),
             ("store", "Updates via Store"), ("launcher", "Managed by launcher"), ("current", "Up to date")],
            r => ((PackageRowViewModel)r).StatusKey, "update", "fresh", "problem");
        _kind = PackageFilters.KindGroup();
        _source = PackageFilters.SourceGroup();
        Filters = new FilterBarViewModel(_status, _kind, _source);
        Filters.Changed += () =>
        {
            View.Refresh();
            UpdateCounts();
        };
        View.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(PackageRowViewModel.Name), System.ComponentModel.ListSortDirection.Ascending));
    }

    public override string Title => "Apps";

    /// <summary>"Apps (5)": what "upgrade all" would take with no filters, so updates show from any page.</summary>
    public override string NavTitle => _available == 0 ? Title : string.Create(CultureInfo.CurrentCulture, $"{Title} ({_available})");

    private int _available;

    /// <summary>When the list was last complete (a refresh, the one after a run, or a quiet check). Null before the first.</summary>
    public DateTime? LastListed { get; private set; }

    public bool IsLoaded => _loaded;

    public ObservableCollection<PackageRowViewModel> Rows { get; } = [];

    public DataGridCollectionView View { get; }

    public LeftoversViewModel Leftovers { get; }

    private readonly FilterGroupViewModel _status;
    private readonly FilterGroupViewModel _kind;
    private readonly FilterGroupViewModel _source;

    /// <summary>Status, Kind, and Source chips.</summary>
    public FilterBarViewModel Filters { get; }

    /// <summary>0 = installed apps, 1 = leftovers.</summary>
    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(UpgradeCheckedCommand), nameof(UpgradeAllCommand), nameof(ReinstallCheckedCommand),
        nameof(UninstallCheckedCommand), nameof(HoldCheckedCommand), nameof(UnholdCheckedCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>Selected rows on screen - what the "selected" actions take.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpgradeCheckedCommand), nameof(ReinstallCheckedCommand), nameof(UninstallCheckedCommand),
        nameof(HoldCheckedCommand), nameof(UnholdCheckedCommand))]
    public partial int SelectedCount { get; set; }

    [ObservableProperty]
    public partial string BusyText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double? BusyFraction { get; set; }

    [ObservableProperty]
    public partial string Counts { get; set; } = string.Empty;

    private bool Idle => !IsBusy;

    private bool CanActOnSelection => !IsBusy && SelectedCount > 0;

    partial void OnSearchTextChanged(string value)
    {
        View.Refresh();
        UpdateCounts();
    }

    /// <summary>"Upgrade all shown (N)": what "upgrade all" would take from the rows on screen.</summary>
    [ObservableProperty]
    public partial string UpgradeAllText { get; set; } = "Upgrade all shown";

    [ObservableProperty]
    public partial string UninstallText { get; set; } = "Uninstall selected";

    /// <summary>Loads the list the first time the page is shown.</summary>
    public void EnsureLoaded()
    {
        if (_loaded)
            return;

        _loaded = true;
        RefreshCommand.Execute(null);
        _ = Leftovers.RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(Idle))]
    private async Task RefreshAsync()
    {
        await RunAsync("Checking for updates...", async (sink, ct) =>
        {
            // Rows stream in as each winget group finishes; replace the list from scratch.
            Rows.Clear();
            _byKey.Clear();
            await Task.Run(() => _host.List.RunAsync(new ListRequest(), sink, ct), ct);
            LastListed = DateTime.UtcNow;
            return null;
        });
    }

    /// <summary>
    /// The background check: lists again with the least winget load, updating rows in place so the
    /// selection, filters, and scroll position survive. Returns how many updates are new since the
    /// last list, or null when it did not run (busy) or did not finish.
    /// </summary>
    public async Task<int?> QuietRefreshAsync()
    {
        if (IsBusy)
            return null;

        HashSet<string> before = [.. Rows.Where(UpgradesInAll).Select(UpdateKey)];
        bool finished = false;
        _seen = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            await RunAsync("Checking for updates in the background...", async (sink, ct) =>
            {
                await Task.Run(() => _host.List.RunAsync(new ListRequest(Quietly: true), sink, ct), ct);
                finished = true;
                return null;
            });
        }
        finally
        {
            if (finished)
            {
                foreach (PackageRowViewModel gone in Rows.Where(r => !_seen.Contains(r.Listed.Package.Key.ToString())).ToList())
                {
                    _byKey.Remove(gone.Listed.Package.Key.ToString());
                    Rows.Remove(gone);
                }

                LastListed = DateTime.UtcNow;
                UpdateCounts();
            }

            _seen = null;
        }

        return finished ? Rows.Where(UpgradesInAll).Count(r => !before.Contains(UpdateKey(r))) : null;
    }

    private static string UpdateKey(PackageRowViewModel row) => $"{row.Listed.Package.Key}|{row.Listed.Package.AvailableVersion}";

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private Task UpgradeCheckedAsync()
        => RunBatchAsync("Upgrading", (sink, ct) => _host.Upgrade.RunAsync(new PackageSelection { Packages = Checked() }, sink, ct));

    /// <summary>
    /// "Upgrade all" for what is on screen: the filters and search narrow it, and the usual rules
    /// (holds, Store, launchers, unknown versions) still apply within it. With nothing filtered it
    /// is every update, as before.
    /// </summary>
    [RelayCommand(CanExecute = nameof(Idle))]
    private Task UpgradeAllAsync()
    {
        List<ListedPackage> shown = [.. Shown().Select(r => r.Listed)];
        return RunBatchAsync("Upgrading everything shown", (sink, ct) => _host.Upgrade.RunAsync(new PackageSelection { All = true, Within = shown }, sink, ct));
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private Task ReinstallCheckedAsync()
        => RunBatchAsync("Reinstalling", (sink, ct) => _host.Reinstall.RunAsync(new PackageSelection { Packages = Checked() }, sink, ct));

    /// <summary>
    /// Runs each selected app's own uninstaller. Removed rows leave the list without a re-list (that
    /// is the slow part); anything left behind is shown under Leftovers straight away.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private async Task UninstallCheckedAsync()
    {
        List<InstalledPackage> chosen = Checked();

        foreach (PackageRowViewModel row in Rows)
            row.ResetActivity();

        RunSummary? summary = null;
        await RunAsync("Uninstalling...", async (sink, ct) =>
            summary = await Task.Run(() => _host.Uninstall.RunAsync(new PackageSelection { Packages = chosen }, sink, ct), CancellationToken.None));

        if (summary is not { Items.Count: > 0 })
            return;

        await _dialogs.ShowSummaryAsync(summary);

        // Removed rows go; the rest keep their outcome so failures stay visible.
        foreach (ItemFinished done in summary.Items.Where(i => i.Outcome.Kind == OutcomeKind.Ok))
        {
            if (_byKey.Remove(done.ItemId, out PackageRowViewModel? row))
                Rows.Remove(row);
        }

        UpdateCounts();

        // Something was left behind: show it now, while the user is thinking about that app.
        int before = Leftovers.Scans.Count;
        await Leftovers.RefreshAsync();

        if (Leftovers.Scans.Count > before)
            SelectedTab = 1;
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private Task HoldCheckedAsync() => ChangeHoldsAsync(hold: true);

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private Task UnholdCheckedAsync() => ChangeHoldsAsync(hold: false);

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        _run?.Cancel();
        BusyText = "Canceling - the installer that is running will finish first...";
    }

    /// <summary>Selects the upgradable rows on screen; selections on hidden rows are left as they are.</summary>
    [RelayCommand]
    private void CheckUpdates()
    {
        foreach (PackageRowViewModel row in Shown())
            row.IsSelected = UpgradesInAll(row);
    }

    [RelayCommand]
    private void ClearChecks()
    {
        foreach (PackageRowViewModel row in Rows)
            row.IsSelected = false;
    }

    /// <summary>Selected rows that are on screen. Hidden selections are kept for later, never acted on.</summary>
    private List<InstalledPackage> Checked() => [.. Shown().Where(r => r.IsSelected).Select(r => r.Listed.Package)];

    private IEnumerable<PackageRowViewModel> Shown() => Rows.Where(Matches);

    /// <summary>Whether "upgrade all" would take this row (mirrors PackageBatch's rules, for the button's count).</summary>
    private static bool UpgradesInAll(PackageRowViewModel row)
        => row.CanUpgrade && !row.Listed.IsHeld && !row.Listed.IsExplicitOnly && row.Listed.Package.CheckProblem is null && !row.Listed.Package.HasUnknownVersion;

    private async Task ChangeHoldsAsync(bool hold)
    {
        List<PackageKey> keys = [.. Checked().Select(p => p.Key)];

        if (keys.Count == 0)
            return;

        await RunAsync(hold ? "Holding..." : "Releasing...", async (sink, ct) =>
        {
            HoldRequest request = new(keys);
            _ = hold ? await _host.Hold.RunAsync(request, sink, ct) : await _host.Unhold.RunAsync(request, sink, ct);
            return null;
        });

        // Holds change classification only; re-list to pick up the new state.
        await RefreshAsync();
    }

    private async Task RunBatchAsync(string what, Func<IOperationSink, CancellationToken, Task<RunSummary>> run)
    {
        foreach (PackageRowViewModel row in Rows)
            row.ResetActivity();

        RunSummary? summary = null;
        await RunAsync($"{what}...", async (sink, ct) => summary = await Task.Run(() => run(sink, ct), CancellationToken.None));

        if (summary is not null)
        {
            if (summary.Items.Count > 0)
                await _dialogs.ShowSummaryAsync(summary);

            await RefreshAsync();
        }
    }

    private async Task RunAsync(string busyText, Func<IOperationSink, CancellationToken, Task<object?>> body)
    {
        _run = new CancellationTokenSource();
        IsBusy = true;
        BusyText = busyText;
        BusyFraction = null;

        try
        {
            await body(new GuiSink(OnEvent, _dialogs.ConfirmAsync), _run.Token);
        }
        catch (OperationCanceledException)
        {
            BusyText = "Canceled.";
        }
        catch (Exception ex)
        {
            Core.Diagnostics.DiagLog.UnhandledException("Apps page", ex);
            BusyText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            BusyFraction = null;
            _run.Dispose();
            _run = null;
            UpdateCounts();

            // Rows that streamed in after the view was built are not sorted or filtered until it refreshes.
            View.Refresh();
        }
    }

    private void OnEvent(OperationEvent operationEvent)
    {
        switch (operationEvent)
        {
            case PackageListed listed:
                AddOrUpdate(listed.Listed);
                _seen?.Add(listed.Listed.Package.Key.ToString());
                break;
            case StepStarted step:
                BusyText = step.Step;
                break;
            case ProgressReported { ItemId: null } overall:
                BusyFraction = overall.Fraction;
                if (overall.Message is not null)
                    BusyText = overall.Message;
                break;
            case ProgressReported item when _byKey.TryGetValue(item.ItemId!, out PackageRowViewModel? row):
                row.Activity = Describe(item);
                row.Progress = item.Fraction;
                break;
            case ItemFinished done when _byKey.TryGetValue(done.ItemId, out PackageRowViewModel? row):
                row.Outcome = done.Outcome.Kind;
                row.Progress = null;
                row.Activity = done.Outcome.Kind switch
                {
                    OutcomeKind.Ok => done.Outcome.RebootRequired ? "Done - reboot needed" : "Done",
                    OutcomeKind.AlreadyCurrent => done.Outcome.Detail.Length > 0 ? done.Outcome.Detail : "Already current",
                    OutcomeKind.Skipped => "Skipped: " + done.Outcome.Detail,
                    OutcomeKind.Pending => "Pending: " + done.Outcome.Detail,
                    _ => "Failed: " + done.Outcome.Detail,
                };
                break;
        }
    }

    private void AddOrUpdate(ListedPackage listed)
    {
        string key = listed.Package.Key.ToString();

        if (_byKey.TryGetValue(key, out PackageRowViewModel? existing))
        {
            existing.Listed = listed;
        }
        else
        {
            PackageRowViewModel row = new(listed);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PackageRowViewModel.IsSelected))
                    UpdateCounts();
            };
            _byKey[key] = row;
            Rows.Add(row);
        }

        UpdateCounts();
    }

    private bool Matches(object item)
    {
        if (item is not PackageRowViewModel row)
            return false;

        // A row this run touched stays visible whatever its new status, so its result can be read.
        if (!(row.Outcome is not null || _status.Matches(row)) || !_kind.Matches(row) || !_source.Matches(row))
            return false;

        return SearchText.Length == 0
            || row.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || row.Id.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || row.Publisher.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateCounts()
    {
        int updates = Rows.Count(r => r.Listed.Package.HasUpdate && r.Listed.Package.ManagedBy is null);
        int held = Rows.Count(r => r.Listed.IsHeld || r.Listed.IsExplicitOnly);
        int launcher = Rows.Count(r => r.Listed.Package.ManagedBy is not null);
        int selected = Rows.Count(r => r.IsSelected);
        int hidden = Rows.Count(r => r.IsSelected && !Matches(r));
        string selection = selected == 0 ? "" : hidden == 0 ? $" - {selected} selected" : $" - {selected - hidden} selected ({hidden} hidden by filters)";
        Counts = string.Create(CultureInfo.CurrentCulture, $"{Rows.Count} installed - {updates} with updates - {held} held - {launcher} managed by game launchers{selection}");
        SelectedCount = selected - hidden;
        UpgradeAllText = string.Create(CultureInfo.CurrentCulture, $"Upgrade all shown ({Shown().Count(UpgradesInAll)})");
        UninstallText = SelectedCount == 0 ? "Uninstall selected" : string.Create(CultureInfo.CurrentCulture, $"Uninstall {SelectedCount} selected");
        Filters.Recount([.. Rows]);

        int available = Rows.Count(UpgradesInAll);

        if (available != _available)
        {
            _available = available;
            OnPropertyChanged(nameof(NavTitle));
        }
    }

    private static string Describe(ProgressReported p)
    {
        string text = p.Message ?? "Working";

        if (p.Bytes is { } bytes && p.TotalBytes is { } total)
            return string.Create(CultureInfo.CurrentCulture, $"{text} {bytes / 1048576.0:0.0} / {total / 1048576.0:0.0} MB");

        return p.Fraction is { } f ? string.Create(CultureInfo.CurrentCulture, $"{text} {f:P0}") : text;
    }
}
