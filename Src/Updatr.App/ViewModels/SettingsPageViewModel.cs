using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Updatr.App.Services;
using Updatr.Core;
using Updatr.Core.Abstractions;
using Updatr.Core.Catalog;
using Updatr.Core.Operations;
using Updatr.Core.Preflight;
using Updatr.Core.Settings;
using Updatr.Platform;

namespace Updatr.App.ViewModels;

/// <summary>One entry in an interval picker: "Every 2 hours" = 2; 0 = off.</summary>
public sealed record IntervalChoice(string Label, int Hours)
{
    public override string ToString() => Label;

    public static IntervalChoice For(int hours, string whenZero) => new(hours switch
    {
        0 => whenZero,
        1 => "Every hour",
        _ => $"Every {hours} hours",
    }, hours);
}

/// <summary>Where things live, which catalog is loaded, and the machine checks with a re-run button.</summary>
public sealed partial class SettingsPageViewModel : PageViewModel
{
    private readonly UpdatrHost _host;
    private readonly IDialogService _dialogs;

    public SettingsPageViewModel(UpdatrHost host, PreflightRunner preflight, IDialogService dialogs)
    {
        _host = host;
        _dialogs = dialogs;
        Preflight = preflight;
        Preflight.FixHandler = async row =>
        {
            AdminTestResult = await FixRunner.RunAsync(host, row);
            await RerunChecksAsync();
        };
        Describe(preflight.Report);
    }

    /// <summary>Raised when the user wants to restart into a version self-update just put on the drive.</summary>
    public event Action? RestartRequested;

    /// <summary>Raised on the UI thread after "Run checks again" completes.</summary>
    public event Action<PreflightReport>? ChecksRerun;

    /// <summary>Raised after a setting is saved, with all of them.</summary>
    public event Action<UserSettings>? SettingsChanged;

    private const string UpdatrOff = "Only when Updatr starts";
    private const string AppsOff = "Never (only when I refresh)";

    public ObservableCollection<IntervalChoice> UpdatrIntervals { get; } = [.. new[] { 1, 2, 4, 12 }.Select(h => IntervalChoice.For(h, UpdatrOff)), IntervalChoice.For(0, UpdatrOff)];

    public ObservableCollection<IntervalChoice> AppIntervals { get; } = [.. new[] { 1, 2, 4, 8 }.Select(h => IntervalChoice.For(h, AppsOff)), IntervalChoice.For(0, AppsOff)];

    [ObservableProperty]
    public partial IntervalChoice? UpdatrInterval { get; set; }

    [ObservableProperty]
    public partial IntervalChoice? AppInterval { get; set; }

    [ObservableProperty]
    public partial string SettingsResult { get; set; } = string.Empty;

    private bool _showingSettings;

    /// <summary>Reads the saved settings into the pickers. Returns them for the background checker.</summary>
    public async Task<UserSettings> LoadSettingsAsync()
    {
        UserSettings settings = await _host.SettingsShow.RunAsync(true, new Services.GuiSink(_ => { }), CancellationToken.None);
        _showingSettings = true;
        UpdatrInterval = Pick(UpdatrIntervals, settings.UpdatrCheckEvery, UpdatrOff);
        AppInterval = Pick(AppIntervals, settings.AppCheckEvery, AppsOff);
        _showingSettings = false;
        return settings;
    }

    /// <summary>The matching entry; a value set from the CLI that is not on the list is added to it.</summary>
    private static IntervalChoice Pick(ObservableCollection<IntervalChoice> choices, int hours, string whenZero)
    {
        if (choices.FirstOrDefault(c => c.Hours == hours) is { } found)
            return found;

        IntervalChoice added = IntervalChoice.For(hours, whenZero);
        choices.Insert(choices.Count - 1, added);
        return added;
    }

    partial void OnUpdatrIntervalChanged(IntervalChoice? value)
    {
        if (value is not null && !_showingSettings)
            _ = SaveSettingsAsync(new SettingsChange(UpdatrCheckHours: value.Hours));
    }

    partial void OnAppIntervalChanged(IntervalChoice? value)
    {
        if (value is not null && !_showingSettings)
            _ = SaveSettingsAsync(new SettingsChange(AppCheckHours: value.Hours));
    }

    private async Task SaveSettingsAsync(SettingsChange change)
    {
        try
        {
            UserSettings saved = await _host.SettingsSet.RunAsync(change, new Services.GuiSink(_ => { }), CancellationToken.None);
            SettingsResult = string.Empty;
            SettingsChanged?.Invoke(saved);
        }
        catch (DataStoreException ex)
        {
            SettingsResult = $"Not saved: {ex.Message}";
        }
    }

    public override string Title => "Settings";

    public PreflightRunner Preflight { get; }

    public string DataFolder => _host.Data.DisplayRoot;

    public string LogFile => _host.LogFile;

    [ObservableProperty]
    public partial string CatalogDescription { get; set; } = "Not loaded";

    [ObservableProperty]
    public partial string AdminTestResult { get; set; } = "Not tested this session.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestAdminCommand))]
    public partial bool IsTestingAdmin { get; set; }

    private bool CanTestAdmin => !IsTestingAdmin;

    public string VersionText => $"Updatr v{ProductInfo.Version}";

    [ObservableProperty]
    public partial string UpdateResult { get; set; } = "Updates come from the latest GitHub release and are checked against its published checksums.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SelfUpdateCommand))]
    public partial bool IsUpdating { get; set; }

    private bool CanSelfUpdate => !IsUpdating;

    [RelayCommand(CanExecute = nameof(CanSelfUpdate))]
    private async Task SelfUpdateAsync()
    {
        IsUpdating = true;
        UpdateResult = "Checking...";

        try
        {
            GuiSink sink = new(e =>
            {
                if (e is StepStarted step)
                    UpdateResult = step.Step + "...";
            }, _dialogs.ConfirmAsync);

            Platform.Update.SelfUpdateResult result = await Task.Run(() => _host.SelfUpdate.RunAsync(false, sink, CancellationToken.None));
            UpdateResult = result.Summary;

            if (result.Updated && await _dialogs.ConfirmAsync(new Confirmation($"Restart into v{result.Version} now?", "Updatr closes and starts again from the drive.")))
                RestartRequested?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Core.Diagnostics.DiagLog.UnhandledException("Self-update", ex);
            UpdateResult = $"Failed: {ex.Message}";
        }
        finally
        {
            IsUpdating = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanTestAdmin))]
    private async Task TestAdminAsync()
    {
        IsTestingAdmin = true;
        AdminTestResult = "Approve the UAC prompt...";

        try
        {
            Platform.Elevation.ElevationTestResult result = await Task.Run(() =>
                _host.TestElevation.RunAsync(true, new Services.GuiSink(_ => { }), CancellationToken.None));
            AdminTestResult = result.Summary;
        }
        catch (Exception ex) when (ex is Platform.Elevation.BrokerException or IOException)
        {
            AdminTestResult = $"Failed: {ex.Message}";
        }
        finally
        {
            IsTestingAdmin = false;
        }
    }

    [RelayCommand]
    private async Task RerunChecksAsync()
    {
        PreflightReport report = await Preflight.RunAsync(CancellationToken.None);
        Describe(report);
        ChecksRerun?.Invoke(report);
    }

    private void Describe(PreflightReport? report)
    {
        CatalogLoadResult? catalog = report?.Context.Catalog;

        CatalogDescription = catalog is null
            ? "Not loaded"
            : $"{catalog.Origin switch { CatalogOriginKind.Remote => "Latest from GitHub", CatalogOriginKind.Cache => "Saved copy on this drive", _ => "Built into this version" }}"
              + (catalog.Timestamp is { } t ? $", fetched {t:yyyy-MM-dd HH:mm}" : string.Empty)
              + $" - {catalog.Catalog.Apps.Count} apps, {catalog.Catalog.Templates.Count} templates, {catalog.Catalog.Holds.Count} hold rules";
    }
}
