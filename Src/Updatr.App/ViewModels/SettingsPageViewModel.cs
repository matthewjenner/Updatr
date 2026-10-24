using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Updatr.Core.Catalog;
using Updatr.Core.Preflight;
using Updatr.Platform;

namespace Updatr.App.ViewModels;

/// <summary>Where things live, which catalog is loaded, and the machine checks with a re-run button.</summary>
public sealed partial class SettingsPageViewModel : PageViewModel
{
    private readonly UpdatrHost _host;

    public SettingsPageViewModel(UpdatrHost host, PreflightRunner preflight)
    {
        _host = host;
        Preflight = preflight;
        Preflight.FixHandler = async row =>
        {
            row.Summary = $"{row.Fix!.Label}...";
            AdminTestResult = await FixRunner.RunAsync(host, row.Fix);
            await RerunChecksAsync();
        };
        Describe(preflight.Report);
    }

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
