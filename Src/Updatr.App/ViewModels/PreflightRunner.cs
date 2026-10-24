using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Updatr.App.Services;
using Updatr.Core.Operations;
using Updatr.Core.Preflight;

namespace Updatr.App.ViewModels;

/// <summary>
/// Runs the preflight operation and keeps a row per check up to date. Shared by the splash and the
/// Settings page's "Run checks again", so both show the same thing the same way.
/// </summary>
public sealed partial class PreflightRunner : ObservableObject
{
    private readonly RunPreflight _preflight;
    private readonly Dictionary<string, CheckRowViewModel> _byId;

    public PreflightRunner(RunPreflight preflight)
    {
        _preflight = preflight;
        Rows = new ObservableCollection<CheckRowViewModel>(preflight.Checks.Select(c => new CheckRowViewModel(c.Id, c.Title, row => FixHandler?.Invoke(row) ?? Task.CompletedTask)));
        _byId = Rows.ToDictionary(r => r.Id, StringComparer.Ordinal);
    }

    public ObservableCollection<CheckRowViewModel> Rows { get; }

    /// <summary>
    /// Runs a row's fix. Set by whoever is showing the rows (the splash, then Settings), because
    /// what happens after a fix - re-check, hand over to the main window - depends on the screen.
    /// </summary>
    public Func<CheckRowViewModel, Task>? FixHandler { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial int Completed { get; set; }

    [ObservableProperty]
    public partial string CurrentStep { get; set; } = string.Empty;

    [ObservableProperty]
    public partial PreflightReport? Report { get; set; }

    public int Total => Rows.Count;

    public async Task<PreflightReport> RunAsync(CancellationToken cancellationToken)
    {
        foreach (CheckRowViewModel row in Rows)
        {
            row.State = CheckRowState.Pending;
            row.Summary = "Waiting";
            row.Detail = null;
        }

        Completed = 0;
        IsRunning = true;

        try
        {
            // Off the UI thread entirely; events come back through the sink.
            PreflightReport report = await Task.Run(() => _preflight.RunAsync(new PreflightRequest(), new GuiSink(OnEvent), cancellationToken), cancellationToken);
            Report = report;
            CurrentStep = report.HasFailures ? "Some checks need attention" : "Ready";
            return report;
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void OnEvent(OperationEvent operationEvent)
    {
        switch (operationEvent)
        {
            case CheckStarted started when _byId.TryGetValue(started.CheckId, out CheckRowViewModel? row):
                row.Start();
                CurrentStep = $"Checking {started.Title.ToLowerInvariant()}...";
                break;
            case CheckFinished finished when _byId.TryGetValue(finished.CheckId, out CheckRowViewModel? row):
                row.Finish(finished.Result);
                Completed++;
                break;
        }
    }
}
