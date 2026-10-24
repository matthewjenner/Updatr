using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Updatr.App.Views;

/// <summary>Asks for one line of text, such as a cart or template name.</summary>
public partial class PromptDialog : Window
{
    public PromptDialog()
    {
        InitializeComponent();
    }

    public PromptDialog(string title, string label, string initial) : this()
    {
        TitleText.Text = title;
        LabelText.Text = label;
        Input.Text = initial;
        Opened += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    private void OnOk(object? sender, RoutedEventArgs e)
        => Close(string.IsNullOrWhiteSpace(Input.Text) ? null : Input.Text.Trim());

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
