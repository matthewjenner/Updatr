using CommunityToolkit.Mvvm.ComponentModel;
using Updatr.Core.Operations;
using Updatr.Core.Packages;
using Updatr.Core.Sources;

namespace Updatr.App.ViewModels;

/// <summary>What a row's status means, for its color.</summary>
public enum RowStatus
{
    UpToDate,
    Update,
    Held,
    Blocked,
    Problem,
    Running,
    Done,
    Failed,
}

/// <summary>One installed package in the Updates grid.</summary>
public sealed partial class PackageRowViewModel(ListedPackage listed) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Id), nameof(Installed), nameof(Available), nameof(Catalog), nameof(Status), nameof(StatusDetail), nameof(Kind), nameof(CanUpgrade), nameof(CanReinstall))]
    public partial ListedPackage Listed { get; set; } = listed;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>Live text while an action runs, then the outcome.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Kind))]
    public partial string? Activity { get; set; }

    [ObservableProperty]
    public partial double? Progress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Kind))]
    public partial OutcomeKind? Outcome { get; set; }

    private InstalledPackage P => Listed.Package;

    public string Key => P.Key.ToString();

    public string Name => P.Name;

    public string Id => P.Id;

    public string Installed => P.InstalledVersion;

    public string Available => P.AvailableVersion ?? (P.NewerVersionNeedsFreshInstall is { } v ? $"{v} (fresh install)" : string.Empty);

    public string Catalog => P.ManagedBy ?? P.Catalog ?? (P.IsManaged ? string.Empty : "not in winget");

    /// <summary>True when "upgrade" would do something for this row.</summary>
    public bool CanUpgrade => P.HasUpdate && P.ManagedBy is null && !Listed.IsStoreManaged;

    public bool CanReinstall => P.IsManaged && P.ManagedBy is null && !Listed.IsStoreManaged;

    public string Status => P switch
    {
        { ManagedBy: { } launcher } => $"Managed by {launcher}",
        { CheckProblem: not null } => "Could not check",
        _ when Listed.IsStoreManaged => "Updates via Store",
        _ when Listed.IsExplicitOnly && P.HasUpdate => "Update (self-updating)",
        _ when Listed.IsHeld && P.HasUpdate => "Update (held)",
        { HasUpdate: true } => "Update available",
        { NewerVersionNeedsFreshInstall: not null } => "Needs fresh install",
        { IsManaged: false } => "Not in winget",
        _ => "Up to date",
    };

    /// <summary>Tooltip: why the row says what it says.</summary>
    public string? StatusDetail => P.ManagedBy is { } launcher
        ? $"{launcher} installs and updates this. Updatr leaves it alone so the launcher's copy is never broken."
        : P.CheckProblem
          ?? (P.NewerVersionNeedsFreshInstall is { } v ? $"{v} exists, but {P.FreshInstallReason}. Uninstall and install it fresh." : null)
          ?? Listed.Hold?.Rule.Reason;

    public RowStatus Kind => Outcome switch
    {
        OutcomeKind.Ok or OutcomeKind.AlreadyCurrent => RowStatus.Done,
        OutcomeKind.Failed => RowStatus.Failed,
        OutcomeKind.Skipped => RowStatus.Held,
        _ when Activity is not null => RowStatus.Running,
        _ when P.ManagedBy is not null || Listed.IsStoreManaged || P.NewerVersionNeedsFreshInstall is not null => RowStatus.Blocked,
        _ when P.CheckProblem is not null => RowStatus.Problem,
        _ when (Listed.IsHeld || Listed.IsExplicitOnly) && P.HasUpdate => RowStatus.Held,
        _ when P.HasUpdate => RowStatus.Update,
        _ => RowStatus.UpToDate,
    };

    public void ResetActivity()
    {
        Activity = null;
        Progress = null;
        Outcome = null;
    }
}
