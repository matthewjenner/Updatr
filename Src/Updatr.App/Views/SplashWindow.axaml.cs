using Avalonia.Controls;
using Avalonia.Input;

namespace Updatr.App.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();

        // No title bar, so the whole splash is the drag handle.
        PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
    }
}
