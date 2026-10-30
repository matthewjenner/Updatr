using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Updatr.Core.Preflight;

namespace Updatr.App.ViewModels;

/// <summary>Where a check row is in its life. Extends <see cref="PreflightStatus"/> with the two pre-result states.</summary>
public enum CheckRowState
{
    Pending,
    Running,
    Info,
    Pass,
    Warn,
    Fail,
}

/// <summary>One preflight check, as a row on the splash and in Settings.</summary>
public sealed partial class CheckRowViewModel(string id, string title, Func<CheckRowViewModel, Task> runFix) : ObservableObject
{
    public string Id { get; } = id;

    public string Title { get; } = title;

    [ObservableProperty]
    public partial CheckRowState State { get; set; } = CheckRowState.Pending;

    [ObservableProperty]
    public partial string Summary { get; set; } = "Waiting";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    public partial string? Detail { get; set; }

    /// <summary>An action that can fix this check, shown as a button on the row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFix))]
    public partial PreflightFix? Fix { get; set; }

    public bool HasFix => Fix is not null && State is not CheckRowState.Running && !IsFixing;

    /// <summary>A fix is running: the row shows its step and a progress bar instead of the button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFix), nameof(HasDetail))]
    public partial bool IsFixing { get; set; }

    /// <summary>0..100 while a fix reports how far it is; null shows a moving bar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FixProgressKnown))]
    public partial double? FixProgress { get; set; }

    public bool FixProgressKnown => FixProgress is not null;

    [RelayCommand]
    private Task RunFixAsync() => runFix(this);

    /// <summary>Detail is shown inline for problems only; it is in the tooltip for the rest.</summary>
    public bool HasDetail => Detail is not null && State is CheckRowState.Warn or CheckRowState.Fail && !IsFixing;

    public void Start()
    {
        State = CheckRowState.Running;
        Summary = "Checking...";
        Detail = null;
        Fix = null;
    }

    public void Finish(PreflightResult result)
    {
        State = result.Status switch
        {
            PreflightStatus.Pass => CheckRowState.Pass,
            PreflightStatus.Info => CheckRowState.Info,
            PreflightStatus.Warn => CheckRowState.Warn,
            _ => CheckRowState.Fail,
        };
        Summary = result.Summary;
        Detail = result.Detail;
        Fix = result.Fix;
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(HasFix));
    }
}
