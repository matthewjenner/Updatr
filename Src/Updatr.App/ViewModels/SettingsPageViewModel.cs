using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Updatr.App.Services;
using Updatr.Core;
using Updatr.Core.Catalog;
using Updatr.Core.Operations;
using Updatr.Core.Preflight;
using Updatr.Platform;

namespace Updatr.App.ViewModels;

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
            row.Summary = $"{row.Fix!.Label}...";
            AdminTestResult = await FixRunner.RunAsync(host, row.Fix);
            await RerunChecksAsync();
        };
        Describe(preflight.Report);
    }

    /// <summary>Raised when the user wants to restart into a version self-update just put on the drive.</summary>
    public event Action? RestartRequested;

    /// <summary>Raised on the UI thread after "Run checks again" completes.</summary>
    public event Action<PreflightReport>? ChecksRerun;

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
