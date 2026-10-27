using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Updatr.Core;
using Updatr.Core.Preflight;
using Updatr.Platform;

namespace Updatr.App.ViewModels;

/// <summary>How loud a banner is.</summary>
public enum BannerSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>An app-level message in the strip above the content (offline, reboot pending, ...).</summary>
/// <param name="CanDismiss">Shows a close button - for news (a new version, new updates), not for problems.</param>
public sealed record BannerViewModel(BannerSeverity Severity, string Text, bool CanDismiss = false);

/// <summary>A navigation entry and the page it shows.</summary>
public sealed record NavItem(string Title, PageViewModel Page);

/// <summary>Base for the pages in the main window.</summary>
public abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>The navigation entry's text; a page may add a count.</summary>
    public virtual string NavTitle => Title;
}

/// <summary>A screen that arrives in a later phase - says which, so the shell can be explored now.</summary>
public sealed class PlaceholderPageViewModel(string title, string description, string arrives) : PageViewModel
{
    public override string Title => title;

    public string Description => description;

    public string Arrives => arrives;
}

/// <summary>The main window: navigation, banners, and the current page.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(UpdatrHost host, PreflightReport report, PreflightRunner preflight, Services.IDialogService dialogs)
    {
        Host = host;
        host.UseCatalog(report.Context.Catalog);
        Apps = new AppsPageViewModel(host, dialogs);
        Install = new InstallPageViewModel(host, dialogs, Apps);
        Debloat = new DebloatPageViewModel(host, dialogs);

        Settings = new SettingsPageViewModel(host, preflight, dialogs);
        Settings.ChecksRerun += ApplyReport;

        _checker = new BackgroundChecker(host, Apps);
        _checker.NewerVersionFound += ShowNewerVersion;
        _checker.NewUpdatesFound += ShowNewUpdates;
        Settings.SettingsChanged += settings => _checker.Settings = settings;

        Navigation =
        [
            new NavItem("Apps", Apps),
            new NavItem("Install", Install),
            new NavItem("Debloat", Debloat),
            new NavItem("Log", new LogPageViewModel()),
            new NavItem("Settings", Settings),
        ];

        SelectedNav = Navigation[0];

        host.Data.Failed += message => Dispatcher.UIThread.Post(() => SetDataBanner(message));
        ApplyReport(report);
        _ = StartCheckerAsync();
    }

    private readonly BackgroundChecker _checker;

    private async Task StartCheckerAsync()
    {
        _checker.Settings = await Settings.LoadSettingsAsync();
        _checker.Start();
    }

    public UpdatrHost Host { get; }

    public string Title => $"{ProductInfo.Name} v{ProductInfo.Version}";

    public IReadOnlyList<NavItem> Navigation { get; }

    public SettingsPageViewModel Settings { get; }

    public AppsPageViewModel Apps { get; }

    public InstallPageViewModel Install { get; }

    public DebloatPageViewModel Debloat { get; }

    public ObservableCollection<BannerViewModel> Banners { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    public partial NavItem? SelectedNav { get; set; }

    public PageViewModel? CurrentPage => SelectedNav?.Page;

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value?.Page is AppsPageViewModel apps)
            apps.EnsureLoaded();
        else if (value?.Page is InstallPageViewModel install)
            install.EnsureLoaded();
        else if (value?.Page is DebloatPageViewModel debloat)
            debloat.EnsureLoaded();
    }

    private BannerViewModel? _dataBanner;
    private BannerViewModel? _versionBanner;
    private BannerViewModel? _updatesBanner;
    private string? _dismissedVersion;

    /// <summary>Hides a news banner. A dismissed new version stays hidden; a later one shows again.</summary>
    [RelayCommand]
    private void DismissBanner(BannerViewModel banner)
    {
        Banners.Remove(banner);

        if (banner == _versionBanner)
        {
            _dismissedVersion = banner.Text;
            _versionBanner = null;
        }
        else if (banner == _updatesBanner)
        {
            _updatesBanner = null;
        }
    }

    private void ShowNewerVersion(string version)
    {
        BannerViewModel banner = new(BannerSeverity.Info, $"Updatr v{version} is available - update it from Settings.", CanDismiss: true);

        if (banner.Text == _dismissedVersion || banner.Text == _versionBanner?.Text)
            return;

        if (_versionBanner is not null)
            Banners.Remove(_versionBanner);

        _versionBanner = banner;
        Banners.Add(banner);
    }

    private void ShowNewUpdates(int count)
    {
        if (_updatesBanner is not null)
            Banners.Remove(_updatesBanner);

        _updatesBanner = new BannerViewModel(BannerSeverity.Info,
            string.Create(CultureInfo.CurrentCulture, $"{count} new update{(count == 1 ? "" : "s")} found at {DateTime.Now:t} - see Apps."), CanDismiss: true);
        Banners.Add(_updatesBanner);
    }

    /// <summary>Turns preflight problems into banners. Replaces the previous set on a re-run.</summary>
    private void ApplyReport(PreflightReport report)
    {
        Banners.Clear();
        _versionBanner = null;

        foreach (CheckFinished check in report.Results.Where(r => r.Result.Status is PreflightStatus.Fail or PreflightStatus.Warn))
        {
            Banners.Add(new BannerViewModel(
                check.Result.Status == PreflightStatus.Fail ? BannerSeverity.Error : BannerSeverity.Warning,
                $"{check.Title}: {check.Result.Summary}"));
        }

        if (report.Context.NewerVersion is { } newer)
            ShowNewerVersion(newer);

        if (_updatesBanner is not null)
            Banners.Add(_updatesBanner);

        if (_dataBanner is not null)
            Banners.Add(_dataBanner);
    }

    private void SetDataBanner(string message)
    {
        if (_dataBanner is not null)
            Banners.Remove(_dataBanner);

        _dataBanner = new BannerViewModel(BannerSeverity.Warning, $"Data folder: {message}");
        Banners.Add(_dataBanner);
    }
}
