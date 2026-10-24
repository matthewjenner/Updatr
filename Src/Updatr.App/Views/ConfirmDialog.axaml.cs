using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Updatr.App.Views;

/// <summary>A yes/no question with a scrollable detail (e.g. the list of packages about to change).</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public ConfirmDialog(string title, string detail) : this()
    {
        TitleText.Text = title;
        DetailText.Text = detail;
    }

    private void OnYes(object? sender, RoutedEventArgs e) => Close(true);

    private void OnNo(object? sender, RoutedEventArgs e) => Close(false);
}
