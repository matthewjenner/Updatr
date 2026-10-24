using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
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
public sealed record BannerViewModel(BannerSeverity Severity, string Text);

/// <summary>A navigation entry and the page it shows.</summary>
public sealed record NavItem(string Title, PageViewModel Page);

/// <summary>Base for the pages in the main window.</summary>
public abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }
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
        Updates = new UpdatesPageViewModel(host, dialogs);

        Settings = new SettingsPageViewModel(host, preflight);
        Settings.ChecksRerun += ApplyReport;

        Navigation =
        [
            new NavItem("Updates", Updates),
            new NavItem("Install", new PlaceholderPageViewModel("Install", "Pick apps and templates into a cart, merge and save carts, and install them in one run.", "Phase 5")),
            new NavItem("Debloat", new PlaceholderPageViewModel("Debloat", "Remove ads and junk from Windows, safely re-runnable after feature updates.", "Phase 6")),
            new NavItem("Uninstall", new PlaceholderPageViewModel("Uninstall", "Remove apps through their own uninstallers, with leftover cleanup to follow.", "Phase 7")),
            new NavItem("Log", new LogPageViewModel()),
            new NavItem("Settings", Settings),
        ];

        SelectedNav = Navigation[0];

        host.Data.Failed += message => Dispatcher.UIThread.Post(() => SetDataBanner(message));
        ApplyReport(report);
    }

    public UpdatrHost Host { get; }

    public string Title => $"{ProductInfo.Name} v{ProductInfo.Version}";

    public IReadOnlyList<NavItem> Navigation { get; }

    public SettingsPageViewModel Settings { get; }

    public UpdatesPageViewModel Updates { get; }

    public ObservableCollection<BannerViewModel> Banners { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    public partial NavItem? SelectedNav { get; set; }

    public PageViewModel? CurrentPage => SelectedNav?.Page;

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value?.Page is UpdatesPageViewModel updates)
            updates.EnsureLoaded();
    }

    private BannerViewModel? _dataBanner;

    /// <summary>Turns preflight problems into banners. Replaces the previous set on a re-run.</summary>
    private void ApplyReport(PreflightReport report)
    {
        Banners.Clear();

        foreach (CheckFinished check in report.Results.Where(r => r.Result.Status is PreflightStatus.Fail or PreflightStatus.Warn))
        {
            Banners.Add(new BannerViewModel(
                check.Result.Status == PreflightStatus.Fail ? BannerSeverity.Error : BannerSeverity.Warning,
                $"{check.Title}: {check.Result.Summary}"));
        }

        if (report.Context.NewerVersion is { } newer)
            Banners.Add(new BannerViewModel(BannerSeverity.Info, $"Updatr v{newer} is available."));

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
