using Avalonia.Controls;
using Updatr.App.Services;
using Updatr.Core.Operations;
using Updatr.Core.Packages;

namespace Updatr.App.Views;

public partial class MainWindow : Window, IDialogService
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public async Task<bool> ConfirmAsync(Confirmation confirmation)
        => await new ConfirmDialog(confirmation).ShowDialog<bool?>(this) == true;

    public Task ShowSummaryAsync(RunSummary summary) => new RunSummaryWindow(summary).ShowDialog(this);

    public Task<string?> PromptAsync(string title, string label, string initial) => new PromptDialog(title, label, initial).ShowDialog<string?>(this);
}
