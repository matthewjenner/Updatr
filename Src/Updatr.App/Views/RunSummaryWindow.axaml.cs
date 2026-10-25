using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Updatr.Core.Operations;
using Updatr.Core.Packages;
using Updatr.Core.Sources;

namespace Updatr.App.Views;

/// <summary>What a batch did, item by item - failures first, so they are not missed.</summary>
public partial class RunSummaryWindow : Window
{
    public RunSummaryWindow()
    {
        InitializeComponent();
    }

    public RunSummaryWindow(RunSummary summary) : this()
    {
        Headline.Text = string.Create(CultureInfo.CurrentCulture,
            $"{summary.Action}: {summary.Count(OutcomeKind.Ok)} done, {summary.Count(OutcomeKind.AlreadyCurrent)} already current, "
            + $"{(summary.Count(OutcomeKind.Pending) > 0 ? $"{summary.Count(OutcomeKind.Pending)} pending an app restart, " : "")}"
            + $"{summary.Count(OutcomeKind.Skipped)} skipped, {summary.Count(OutcomeKind.Failed)} failed ({summary.Duration.TotalMinutes:0.0} min)");
        RebootNote.IsVisible = summary.RebootRequired;

        Items.ItemsSource = summary.Items
            .OrderBy(i => i.Outcome.Kind switch { OutcomeKind.Failed => 0, OutcomeKind.Pending => 1, OutcomeKind.Skipped => 2, OutcomeKind.Ok => 3, _ => 4 })
            .ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList<ItemFinished>();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
