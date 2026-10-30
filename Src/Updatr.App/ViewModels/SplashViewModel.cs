using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Updatr.Core;
using Updatr.Core.Preflight;
using Updatr.Platform;

namespace Updatr.App.ViewModels;

/// <summary>
/// The startup splash: starts the host, runs preflight, and hands over to the main window - by
/// itself when nothing failed, or after the user chooses Continue anyway.
/// </summary>
public sealed partial class SplashViewModel : ObservableObject
{
    /// <summary>
    /// Shortest time the splash stays up, counted from when it appears. A healthy machine finishes
    /// preflight in about a second; without this the results flash past unread.
    /// </summary>
    public static readonly TimeSpan MinimumDisplay = TimeSpan.FromSeconds(8);

    private readonly HostOptions _options;
    private readonly CancellationTokenSource _cancel = new();
    private readonly System.Diagnostics.Stopwatch _shown = System.Diagnostics.Stopwatch.StartNew();

    public SplashViewModel(HostOptions options)
    {
        _options = options;
    }

    /// <summary>Raised on the UI thread when the main window should take over.</summary>
    public event Action<UpdatrHost, PreflightReport, PreflightRunner>? Finished;

    /// <summary>Raised when the user quits from the splash.</summary>
    public event Action? QuitRequested;

    public string Title => ProductInfo.Name;

    public string Version => $"v{ProductInfo.Version}";

    [ObservableProperty]
    public partial PreflightRunner? Runner { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Starting...";

    /// <summary>True when preflight finished with failures and the user must choose.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand), nameof(ContinueCommand))]
    public partial bool NeedsDecision { get; set; }

    private UpdatrHost? Host { get; set; }

    public async Task StartAsync()
    {
        try
        {
            Host = await UpdatrHost.StartAsync(_options, _cancel.Token);
            Runner = new PreflightRunner(Host.Preflight)
            {
                FixHandler = async row =>
                {
                    NeedsDecision = false;
                    Status = $"{row.Fix!.Label} - this can take a few minutes. Quit stops it.";
                    string outcome = await FixRunner.RunAsync(Host, row, _cancel.Token);
                    Status = outcome;

                    if (_cancel.IsCancellationRequested)
                        return;

                    await RunChecksAsync();

                    // The rerun's "Some checks failed" would hide why the fix did not work.
                    if (NeedsDecision)
                        Status = outcome;
                },
            };
            Runner.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PreflightRunner.CurrentStep))
                    Status = Runner.CurrentStep;
            };

            await RunChecksAsync();
        }
        catch (OperationCanceledException)
        {
            // Quit while starting.
        }
    }

    [RelayCommand(CanExecute = nameof(NeedsDecision))]
    private async Task RetryAsync()
    {
        NeedsDecision = false;
        await RunChecksAsync();
    }

    [RelayCommand(CanExecute = nameof(NeedsDecision))]
    private void Continue()
    {
        NeedsDecision = false;
        HandOver();
    }

    [RelayCommand]
    private async Task QuitAsync()
    {
        await _cancel.CancelAsync();

        if (Host is not null)
            await Host.DisposeAsync();

        QuitRequested?.Invoke();
    }

    private async Task RunChecksAsync()
    {
        PreflightReport report = await Runner!.RunAsync(_cancel.Token);

        if (report.HasFailures)
        {
            Status = "Some checks failed. Fix them and retry, or continue with what works.";
            NeedsDecision = true;
            return;
        }

        Status = "Ready";

        // Hold until the splash has been up for MinimumDisplay, and always for a short beat so the
        // last row visibly lands. A slow preflight is never held longer than it took.
        TimeSpan remaining = MinimumDisplay - _shown.Elapsed;
        await Task.Delay(remaining > TimeSpan.FromMilliseconds(600) ? remaining : TimeSpan.FromMilliseconds(600));
        HandOver();
    }

    private void HandOver() => Finished?.Invoke(Host!, Runner!.Report!, Runner);
}
