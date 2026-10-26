using System.Diagnostics;
using Avalonia.Threading;
using Updatr.Core.Catalog;
using Updatr.Core.Diagnostics;
using Updatr.Core.Settings;
using Updatr.Platform;

namespace Updatr.App.ViewModels;

/// <summary>
/// Checks again while the window is open, so news does not wait for a restart: a newer Updatr and
/// the catalog every <see cref="UserSettings.UpdatrCheckEvery"/> hours (one small GitHub request
/// each), and installed apps every <see cref="UserSettings.AppCheckEvery"/> hours. The app check is
/// the heavy one (a full winget listing), so it lists quietly - one worker - and only when the
/// Apps page is idle and nothing has listed for that long; any refresh or run resets its clock.
/// Never installs anything.
/// </summary>
public sealed class BackgroundChecker
{
    /// <summary>How often it looks at the clocks. Checks themselves run at the hour intervals.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

    private readonly UpdatrHost _host;
    private readonly AppsPageViewModel _apps;
    private readonly DispatcherTimer _timer;

    // Preflight checked Updatr and the catalog at start.
    private DateTime _lastUpdatrCheck = DateTime.UtcNow;
    private bool _running;

    public BackgroundChecker(UpdatrHost host, AppsPageViewModel apps)
    {
        _host = host;
        _apps = apps;
        _timer = new DispatcherTimer { Interval = Tick };
        _timer.Tick += async (_, _) => await CheckAsync();
    }

    public UserSettings Settings { get; set; } = new();

    /// <summary>A newer Updatr is published (its version).</summary>
    public event Action<string>? NewerVersionFound;

    /// <summary>A quiet app check found this many updates that were not listed before.</summary>
    public event Action<int>? NewUpdatesFound;

    public void Start() => _timer.Start();

    /// <summary>Runs whatever is due. On the UI thread; the work itself runs off it.</summary>
    private async Task CheckAsync()
    {
        if (_running)
            return;

        _running = true;

        try
        {
            DateTime now = DateTime.UtcNow;

            if (Due(_lastUpdatrCheck, Settings.UpdatrCheckEvery, now))
            {
                _lastUpdatrCheck = now;
                string? newer = await Task.Run(() => _host.FindNewerVersionAsync(CancellationToken.None));
                string catalog;

                try
                {
                    CatalogLoadResult loaded = await Task.Run(() => _host.ReloadCatalogAsync(CancellationToken.None));
                    catalog = $"from {loaded.Origin}";
                }
                catch (CatalogUnavailableException)
                {
                    catalog = "unavailable; keeping the one loaded";
                }

                DiagLog.BackgroundUpdatrCheck(newer, catalog);

                if (newer is not null)
                    NewerVersionFound?.Invoke(newer);
            }

            if (_apps.IsLoaded && _apps.LastListed is { } listed && Due(listed, Settings.AppCheckEvery, now))
            {
                Stopwatch timer = Stopwatch.StartNew();
                int? fresh = await _apps.QuietRefreshAsync();
                DiagLog.BackgroundAppCheck(fresh, timer.Elapsed);

                if (fresh > 0)
                    NewUpdatesFound?.Invoke(fresh.Value);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagLog.UnhandledException("Background check", ex);
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>0 hours = off.</summary>
    private static bool Due(DateTime last, int hours, DateTime now) => hours > 0 && now - last >= TimeSpan.FromHours(hours);
}
