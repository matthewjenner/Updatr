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
/// The Uninstall screen: everything installed, across sources, and removal of the checked apps
/// through their own uninstallers. A thin adapter over the CLI's <c>list</c> and <c>uninstall</c>.
/// </summary>
public sealed partial class UninstallPageViewModel : PageViewModel
{
    private readonly UpdatrHost _host;
    private readonly IDialogService _dialogs;
    private readonly Dictionary<string, PackageRowViewModel> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _run;
    private bool _loaded;

    public UninstallPageViewModel(UpdatrHost host, IDialogService dialogs)
    {
        _host = host;
        _dialogs = dialogs;
        Leftovers = new LeftoversViewModel(host, dialogs);
        View = new DataGridCollectionView(Rows) { Filter = Matches };
        View.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(PackageRowViewModel.Name), System.ComponentModel.ListSortDirection.Ascending));
    }

    public override string Title => "Uninstall";

    public ObservableCollection<PackageRowViewModel> Rows { get; } = [];

    public DataGridCollectionView View { get; }

    public LeftoversViewModel Leftovers { get; }

    /// <summary>0 = installed apps, 1 = leftovers.</summary>
    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(UninstallCheckedCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string BusyText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Counts { get; set; } = string.Empty;

    private bool Idle => !IsBusy;

    partial void OnSearchTextChanged(string value) => View.Refresh();

    public void EnsureLoaded()
    {
        if (_loaded)
            return;

        _loaded = true;
        RefreshCommand.Execute(null);
        _ = Leftovers.RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(Idle))]
    private Task RefreshAsync() => RunAsync("Listing installed apps...", async (sink, ct) =>
    {
        Rows.Clear();
        _byKey.Clear();
        await Task.Run(() => _host.List.RunAsync(new ListRequest(), sink, ct), ct);
    });

    [RelayCommand(CanExecute = nameof(Idle))]
    private async Task UninstallCheckedAsync()
    {
        List<InstalledPackage> chosen = [.. Rows.Where(r => r.IsSelected).Select(r => r.Listed.Package)];

        if (chosen.Count == 0)
        {
            BusyText = "Select the apps to remove first (click a row to select it).";
            return;
        }

        foreach (PackageRowViewModel row in Rows)
            row.ResetActivity();

        RunSummary? summary = null;
        await RunAsync("Uninstalling...", async (sink, ct) =>
            summary = await Task.Run(() => _host.Uninstall.RunAsync(new PackageSelection { Packages = chosen }, sink, ct), CancellationToken.None));

        if (summary is { Items.Count: > 0 })
        {
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
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        _run?.Cancel();
        BusyText = "Canceling - the uninstaller that is running will finish first...";
    }

    private async Task RunAsync(string busyText, Func<IOperationSink, CancellationToken, Task> body)
    {
        _run = new CancellationTokenSource();
        IsBusy = true;
        BusyText = busyText;

        try
        {
            await body(new GuiSink(OnEvent, _dialogs.ConfirmAsync), _run.Token);
            BusyText = string.Empty;
        }
        catch (OperationCanceledException)
        {
            BusyText = "Canceled.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Core.Diagnostics.DiagLog.UnhandledException("Uninstall page", ex);
            BusyText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _run.Dispose();
            _run = null;
            UpdateCounts();
            View.Refresh();
        }
    }

    private void OnEvent(OperationEvent operationEvent)
    {
        switch (operationEvent)
        {
            case PackageListed listed:
                string key = listed.Listed.Package.Key.ToString();

                if (!_byKey.ContainsKey(key))
                {
                    PackageRowViewModel row = new(listed.Listed);
                    row.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(PackageRowViewModel.IsSelected))
                            UpdateCounts();
                    };
                    _byKey[key] = row;
                    Rows.Add(row);
                }

                break;
            case StepStarted step:
                BusyText = step.Step;
                break;
            case ProgressReported { ItemId: null, Message: { } message }:
                BusyText = message;
                break;
            case ProgressReported item when _byKey.TryGetValue(item.ItemId!, out PackageRowViewModel? row):
                row.Activity = item.Message ?? "Working";
                row.Progress = item.Fraction;
                break;
            case ItemFinished done when _byKey.TryGetValue(done.ItemId, out PackageRowViewModel? row):
                row.Outcome = done.Outcome.Kind;
                row.Progress = null;
                row.Activity = done.Outcome.Kind switch
                {
                    OutcomeKind.Ok => done.Outcome.RebootRequired ? "Removed - reboot needed" : "Removed",
                    OutcomeKind.AlreadyCurrent => done.Outcome.Detail,
                    OutcomeKind.Skipped => "Skipped: " + done.Outcome.Detail,
                    _ => "Failed: " + done.Outcome.Detail,
                };
                break;
        }
    }

    private bool Matches(object item)
        => item is PackageRowViewModel row
            && (SearchText.Length == 0
                || row.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || row.Id.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || (row.Listed.Package.Publisher?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false));

    private void UpdateCounts()
    {
        int selected = Rows.Count(r => r.IsSelected);
        Counts = string.Create(CultureInfo.CurrentCulture, $"{Rows.Count} installed - {selected} selected");
        UninstallText = selected == 0 ? "Uninstall selected" : $"Uninstall {selected} selected";
    }

    [ObservableProperty]
    public partial string UninstallText { get; set; } = "Uninstall selected";

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (PackageRowViewModel row in Rows)
            row.IsSelected = false;
    }
}
