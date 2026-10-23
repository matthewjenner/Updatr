using Avalonia.Controls;
using Updatr.Core;

namespace Updatr.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = $"{ProductInfo.Name} v{ProductInfo.Version}";
        TitleText.Text = Title;
    }
}
