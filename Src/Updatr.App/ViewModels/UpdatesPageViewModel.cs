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
/// The Updates screen: everything installed, what has updates, holds, and batch actions. A thin
/// adapter over the same operations the CLI's <c>list</c>, <c>upgrade</c>, <c>reinstall</c>,
/// <c>hold</c>, and <c>unhold</c> run.
/// </summary>
public sealed partial class UpdatesPageViewModel : PageViewModel
{
    private readonly UpdatrHost _host;
    private readonly IDialogService _dialogs;
    private readonly Dictionary<string, PackageRowViewModel> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _run;
    private bool _loaded;

    public UpdatesPageViewModel(UpdatrHost host, IDialogService dialogs)
    {
        _host = host;
        _dialogs = dialogs;
        View = new DataGridCollectionView(Rows) { Filter = Matches };
        View.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(PackageRowViewModel.Name), System.ComponentModel.ListSortDirection.Ascending));
    }

    public override string Title => "Updates";

    public ObservableCollection<PackageRowViewModel> Rows { get; } = [];

    public DataGridCollectionView View { get; }

    [ObservableProperty]
    public partial bool ShowUpdatesOnly { get; set; } = true;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>Source filter choices: "All sources", then each source as the grid's Source column names it.</summary>
    public IReadOnlyList<string> SourceChoices { get; } = [AllSources, "winget", "npm", "pipx", "dotnet tool", "PSGallery", "VS Code"];

    private const string AllSources = "All sources";

    [ObservableProperty]
    public partial string SourceFilter { get; set; } = AllSources;

    partial void OnSourceFilterChanged(string value) => View.Refresh();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(UpgradeCheckedCommand), nameof(UpgradeAllCommand), nameof(ReinstallCheckedCommand), nameof(HoldCheckedCommand), nameof(UnholdCheckedCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string BusyText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double? BusyFraction { get; set; }

    [ObservableProperty]
    public partial string Counts { get; set; } = string.Empty;

    private bool Idle => !IsBusy;

    partial void OnShowUpdatesOnlyChanged(bool value) => View.Refresh();

    partial void OnSearchTextChanged(string value) => View.Refresh();

    /// <summary>Loads the list the first time the page is shown.</summary>
    public void EnsureLoaded()
    {
        if (_loaded)
            return;

        _loaded = true;
        RefreshCommand.Execute(null);
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
            return null;
        });
    }

    [RelayCommand(CanExecute = nameof(Idle))]
    private Task UpgradeCheckedAsync()
        => RunBatchAsync("Upgrading", (sink, ct) => _host.Upgrade.RunAsync(new PackageSelection { Packages = Checked() }, sink, ct));

    [RelayCommand(CanExecute = nameof(Idle))]
    private Task UpgradeAllAsync()
        => RunBatchAsync("Upgrading everything", (sink, ct) => _host.Upgrade.RunAsync(new PackageSelection { All = true }, sink, ct));

    [RelayCommand(CanExecute = nameof(Idle))]
    private Task ReinstallCheckedAsync()
        => RunBatchAsync("Reinstalling", (sink, ct) => _host.Reinstall.RunAsync(new PackageSelection { Packages = Checked() }, sink, ct));

    [RelayCommand(CanExecute = nameof(Idle))]
    private Task HoldCheckedAsync() => ChangeHoldsAsync(hold: true);

    [RelayCommand(CanExecute = nameof(Idle))]
    private Task UnholdCheckedAsync() => ChangeHoldsAsync(hold: false);

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        _run?.Cancel();
        BusyText = "Canceling - the installer that is running will finish first...";
    }

    [RelayCommand]
    private void CheckUpdates()
    {
        foreach (PackageRowViewModel row in Rows)
            row.IsChecked = row.CanUpgrade && !row.Listed.IsHeld && !row.Listed.IsExplicitOnly;
    }

    [RelayCommand]
    private void ClearChecks()
    {
        foreach (PackageRowViewModel row in Rows)
            row.IsChecked = false;
    }

    private List<InstalledPackage> Checked() => [.. Rows.Where(r => r.IsChecked).Select(r => r.Listed.Package)];

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
            Core.Diagnostics.DiagLog.UnhandledException("Updates page", ex);
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
                    OutcomeKind.AlreadyCurrent => "Already current",
                    OutcomeKind.Skipped => "Skipped: " + done.Outcome.Detail,
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
            _byKey[key] = row;
            Rows.Add(row);
        }

        UpdateCounts();
    }

    private bool Matches(object item)
    {
        if (item is not PackageRowViewModel row)
            return false;

        // "Updates only": something newer exists, the check failed, or this run touched the row.
        // Launcher-managed games without a known update are just inventory, so they are hidden too.
        if (ShowUpdatesOnly)
        {
            InstalledPackage p = row.Listed.Package;
            bool relevant = p.HasUpdate || p.NewerVersionNeedsFreshInstall is not null || p.CheckProblem is not null || row.Outcome is not null;

            if (!relevant)
                return false;
        }

        if (SourceFilter != AllSources && !SourceMatches(row.Listed.Package, SourceFilter))
            return false;

        return SearchText.Length == 0
            || row.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || row.Id.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>winget rows carry their catalog (winget / msstore) or launcher; the others carry their ecosystem's name.</summary>
    private static bool SourceMatches(InstalledPackage package, string filter)
        => filter == "winget"
            ? package.Source == "winget"
            : string.Equals(package.Catalog, filter, StringComparison.OrdinalIgnoreCase)
              || (package.Catalog?.StartsWith(filter + " ", StringComparison.OrdinalIgnoreCase) ?? false);

    private void UpdateCounts()
    {
        int updates = Rows.Count(r => r.Listed.Package.HasUpdate && r.Listed.Package.ManagedBy is null);
        int held = Rows.Count(r => r.Listed.IsHeld || r.Listed.IsExplicitOnly);
        int launcher = Rows.Count(r => r.Listed.Package.ManagedBy is not null);
        Counts = string.Create(CultureInfo.CurrentCulture, $"{Rows.Count} installed - {updates} with updates - {held} held - {launcher} managed by game launchers");
    }

    private static string Describe(ProgressReported p)
    {
        string text = p.Message ?? "Working";

        if (p.Bytes is { } bytes && p.TotalBytes is { } total)
            return string.Create(CultureInfo.CurrentCulture, $"{text} {bytes / 1048576.0:0.0} / {total / 1048576.0:0.0} MB");

        return p.Fraction is { } f ? string.Create(CultureInfo.CurrentCulture, $"{text} {f:P0}") : text;
    }
}
