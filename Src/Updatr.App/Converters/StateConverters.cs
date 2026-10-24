using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Updatr.App.ViewModels;

namespace Updatr.App.Converters;

/// <summary>Colors for check states and banner severities. ASCII tags carry the meaning for color-blind users.</summary>
public sealed class CheckStateBrushConverter : IValueConverter
{
    public static readonly CheckStateBrushConverter Instance = new();

    public static readonly IBrush Pass = new SolidColorBrush(Color.Parse("#2E9E5B"));
    public static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#D49A1A"));
    public static readonly IBrush Fail = new SolidColorBrush(Color.Parse("#D64545"));
    public static readonly IBrush Running = new SolidColorBrush(Color.Parse("#3B82F6"));
    public static readonly IBrush Neutral = new SolidColorBrush(Color.Parse("#8A8F98"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        CheckRowState.Pass => Pass,
        CheckRowState.Warn or BannerSeverity.Warning => Warn,
        CheckRowState.Fail or BannerSeverity.Error => Fail,
        CheckRowState.Running or BannerSeverity.Info => Running,
        _ => Neutral,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Short ASCII tag for a check state, matching the CLI's.</summary>
public sealed class CheckStateTagConverter : IValueConverter
{
    public static readonly CheckStateTagConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        CheckRowState.Pass => "OK",
        CheckRowState.Info => "INFO",
        CheckRowState.Warn => "WARN",
        CheckRowState.Fail => "FAIL",
        CheckRowState.Running => "...",
        _ => "",
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
