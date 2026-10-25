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

/// <summary>Colors for package row states.</summary>
public sealed class RowStatusBrushConverter : IValueConverter
{
    public static readonly RowStatusBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RowStatus.Update or RowStatus.Running => CheckStateBrushConverter.Running,
        RowStatus.Done => CheckStateBrushConverter.Pass,
        RowStatus.Held or RowStatus.Blocked => CheckStateBrushConverter.Warn,
        RowStatus.Failed or RowStatus.Problem => CheckStateBrushConverter.Fail,
        _ => CheckStateBrushConverter.Neutral,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>ASCII tag for an item outcome, matching the CLI's.</summary>
public sealed class OutcomeTagConverter : IValueConverter
{
    public static readonly OutcomeTagConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Core.Sources.OutcomeKind.Ok => "OK",
        Core.Sources.OutcomeKind.AlreadyCurrent => "SAME",
        Core.Sources.OutcomeKind.Skipped => "SKIP",
        Core.Sources.OutcomeKind.Pending => "WAIT",
        _ => "FAIL",
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Color for an item outcome.</summary>
public sealed class OutcomeBrushConverter : IValueConverter
{
    public static readonly OutcomeBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Core.Sources.OutcomeKind.Ok => CheckStateBrushConverter.Pass,
        Core.Sources.OutcomeKind.AlreadyCurrent => CheckStateBrushConverter.Neutral,
        Core.Sources.OutcomeKind.Skipped or Core.Sources.OutcomeKind.Pending => CheckStateBrushConverter.Warn,
        _ => CheckStateBrushConverter.Fail,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
