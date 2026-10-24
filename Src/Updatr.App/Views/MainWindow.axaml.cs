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
        => await new ConfirmDialog(confirmation.Title, confirmation.Detail).ShowDialog<bool?>(this) == true;

    public Task ShowSummaryAsync(RunSummary summary) => new RunSummaryWindow(summary).ShowDialog(this);
}
