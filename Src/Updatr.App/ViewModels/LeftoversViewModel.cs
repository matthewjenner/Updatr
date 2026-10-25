using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Updatr.App.Behaviors;
using Updatr.App.Services;
using Updatr.Core.Leftovers;
using Updatr.Core.Operations;
using Updatr.Core.Sources;
using Updatr.Platform;

namespace Updatr.App.ViewModels;

/// <summary>One leftover, picked by clicking its row. Never pre-selected.</summary>
public sealed partial class LeftoverRowViewModel(string scanId, Leftover item) : ObservableObject, ISelectableRow
{
    public string ScanId { get; } = scanId;

    public Leftover Item { get; } = item;

    public string Location => Item.Location;

    public string Detail => $"{Kind} - {Item.Reason}{(Item.SizeBytes is > 0 ? string.Create(CultureInfo.CurrentCulture, $" - {Item.SizeBytes / 1048576.0:0.#} MB") : "")}{(Item.NeedsAdmin ? " - needs admin" : "")}";

    public string Kind => Item.Kind switch
    {
        LeftoverKind.RegistryKey => "Registry key",
        LeftoverKind.ScheduledTask => "Scheduled task",
        LeftoverKind.FirewallRule => "Firewall rule",
        _ => Item.Kind.ToString(),
    };

    public string ConfidenceText => Item.Confidence.ToString().ToUpperInvariant();

    /// <summary>High is green (safe bet), medium amber, low grey - read the reason before picking those.</summary>
    public CheckRowState Tone => Item.Confidence switch
    {
        Confidence.High => CheckRowState.Pass,
        Confidence.Medium => CheckRowState.Warn,
        _ => CheckRowState.Pending,
    };

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>The leftovers of one uninstalled app.</summary>
public sealed partial class LeftoverScanViewModel(LeftoverScan scan, LeftoversViewModel owner) : ObservableObject
{
    public LeftoverScan Scan { get; } = scan;

    public string Title => $"{Scan.Target.Name} - uninstalled {Scan.At.ToLocalTime():yyyy-MM-dd HH:mm}";

    public ObservableCollection<LeftoverRowViewModel> Items { get; } = [.. scan.Items.Select(i => new LeftoverRowViewModel(scan.Id, i))];

    [RelayCommand]
    private Task Dismiss() => owner.DismissAsync(Scan.Id);
}

/// <summary>One quarantine entry: restore or purge.</summary>
public sealed partial class QuarantineRowViewModel(QuarantineEntry entry, LeftoversViewModel owner) : ObservableObject
{
    public QuarantineEntry Entry { get; } = entry;

    public string Title => Entry.App;

    public string Detail => string.Create(CultureInfo.CurrentCulture,
        $"{Entry.Items.Count(i => i.Stored)} item(s) - {Entry.SizeBytes / 1048576.0:0.#} MB - {Entry.At.ToLocalTime():yyyy-MM-dd HH:mm}");

    [RelayCommand]
    private Task Restore() => owner.RestoreAsync(Entry.Id);

    [RelayCommand]
    private Task Purge() => owner.PurgeAsync(Entry.Id);
}

/// <summary>
/// The Leftovers tab: what uninstallers left behind on this PC, grouped per app, with nothing
/// pre-selected; and the quarantine, where everything moved can be restored or purged.
/// </summary>
public sealed partial class LeftoversViewModel(UpdatrHost host, IDialogService dialogs) : ObservableObject
{
    public ObservableCollection<LeftoverScanViewModel> Scans { get; } = [];

    public ObservableCollection<QuarantineRowViewModel> Quarantine { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(QuarantineSelectedCommand), nameof(RefreshCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string QuarantineText { get; set; } = "Quarantine selected";

    private bool Idle => !IsBusy;

    private IEnumerable<LeftoverRowViewModel> Rows => Scans.SelectMany(s => s.Items);

    [RelayCommand(CanExecute = nameof(Idle))]
    public async Task RefreshAsync()
    {
        try
        {
            IReadOnlyList<LeftoverScan> scans = await Task.Run(() => host.LeftoversList.RunAsync(true, NullSink, CancellationToken.None));
            IReadOnlyList<QuarantineEntry> entries = await Task.Run(() => host.QuarantineList.RunAsync(true, NullSink, CancellationToken.None));

            Scans.Clear();

            foreach (LeftoverScan scan in scans.Reverse())
            {
                LeftoverScanViewModel group = new(scan, this);

                foreach (LeftoverRowViewModel row in group.Items)
                    row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LeftoverRowViewModel.IsSelected)) Count(); };

                Scans.Add(group);
            }

            Quarantine.Clear();

            foreach (QuarantineEntry entry in entries)
                Quarantine.Add(new QuarantineRowViewModel(entry, this));

            Count();
            Status = Scans.Count == 0 ? "Nothing to review. Leftovers show up here after an uninstall." : string.Empty;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Core.Diagnostics.DiagLog.UnhandledException("Leftovers", ex);
            Status = $"Could not read leftovers: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SelectHigh()
    {
        foreach (LeftoverRowViewModel row in Rows)
            row.IsSelected = row.Item.Confidence == Confidence.High;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (LeftoverRowViewModel row in Rows)
            row.IsSelected = false;
    }

    [RelayCommand(CanExecute = nameof(Idle))]
    private async Task QuarantineSelectedAsync()
    {
        List<IGrouping<string, LeftoverRowViewModel>> picked = [.. Rows.Where(r => r.IsSelected).GroupBy(r => r.ScanId)];

        if (picked.Count == 0)
        {
            Status = "Click the leftovers to quarantine first.";
            return;
        }

        await RunAsync("Moving into quarantine...", async sink =>
        {
            List<string> problems = [];

            foreach (IGrouping<string, LeftoverRowViewModel> scan in picked)
            {
                QuarantineEntry? entry = await host.LeftoversQuarantine.RunAsync(new QuarantineRequest(scan.Key, [.. scan.Select(r => r.Item.Id)]), sink, CancellationToken.None);
                problems.AddRange(entry?.Items.Where(i => !i.Stored || i.Problem is not null).Select(i => $"{i.Location}: {i.Problem}") ?? []);
            }

            return problems.Count == 0 ? "Moved into quarantine. Restore it from the list on the right if anything misses it." : string.Join(Environment.NewLine, problems);
        });
    }

    public Task DismissAsync(string scanId)
        => RunAsync("Dismissing...", async sink =>
        {
            await host.LeftoversDismiss.RunAsync(scanId, sink, CancellationToken.None);
            return "Dismissed - nothing on the PC was touched.";
        });

    public Task RestoreAsync(string entryId)
        => RunAsync("Restoring...", async sink =>
        {
            PackageOutcome outcome = await host.QuarantineRestore.RunAsync(entryId, sink, CancellationToken.None);
            return outcome.Kind == OutcomeKind.Ok ? "Restored." : outcome.Detail;
        });

    public Task PurgeAsync(string entryId)
        => RunAsync("Purging...", async sink => await host.QuarantinePurge.RunAsync(entryId, sink, CancellationToken.None) ? "Purged." : "Not purged.");

    private async Task RunAsync(string busy, Func<IOperationSink, Task<string>> work)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        Status = busy;

        try
        {
            GuiSink sink = new(e =>
            {
                if (e is StepStarted step)
                    Status = step.Step;
            }, dialogs.ConfirmAsync);

            string result = await Task.Run(() => work(sink));
            IsBusy = false;
            await RefreshAsync();
            Status = result;
        }
        catch (ArgumentException ex)
        {
            Status = ex.Message;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Core.Diagnostics.DiagLog.UnhandledException("Leftovers", ex);
            Status = $"Failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Count()
    {
        int selected = Rows.Count(r => r.IsSelected);
        QuarantineText = selected == 0 ? "Quarantine selected" : $"Quarantine {selected} selected";
    }

    private static readonly IOperationSink NullSink = new GuiSink(_ => { });
}
