using CommunityToolkit.Mvvm.ComponentModel;
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
public sealed partial class CheckRowViewModel(string id, string title) : ObservableObject
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

    /// <summary>Detail is shown inline for problems only; it is in the tooltip for the rest.</summary>
    public bool HasDetail => Detail is not null && State is CheckRowState.Warn or CheckRowState.Fail;

    public void Start()
    {
        State = CheckRowState.Running;
        Summary = "Checking...";
        Detail = null;
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
        OnPropertyChanged(nameof(HasDetail));
    }
}
