using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Updatr.App.ViewModels;

/// <summary>One chip: a value rows can have, how many rows have it, and whether it is switched on.</summary>
public sealed partial class FilterOptionViewModel(string key, string label) : ObservableObject
{
    public string Key { get; } = key;

    public string Label { get; } = label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text), nameof(IsShown))]
    public partial int Count { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown))]
    public partial bool IsOn { get; set; }

    public string Text => string.Create(CultureInfo.CurrentCulture, $"{Label}  {Count}");

    /// <summary>Values no row has are hidden, unless switched on.</summary>
    public bool IsShown => Count > 0 || IsOn;
}

/// <summary>
/// A named set of chips over one property of a row (Kind, Source, Status). Any number can be on:
/// a row matches when its value is one of them. None on means the group does not filter.
/// </summary>
public sealed partial class FilterGroupViewModel : ObservableObject
{
    private readonly Func<object, string?> _keyOf;

    public FilterGroupViewModel(string title, IEnumerable<(string Key, string Label)> options, Func<object, string?> keyOf, params string[] onByDefault)
    {
        Title = title;
        _keyOf = keyOf;
        Options = [.. options.Select(o => new FilterOptionViewModel(o.Key, o.Label) { IsOn = onByDefault.Contains(o.Key) })];

        foreach (FilterOptionViewModel option in Options)
        {
            option.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FilterOptionViewModel.IsOn))
                {
                    OnPropertyChanged(nameof(HasSelection));
                    Changed?.Invoke();
                }
            };
        }
    }

    public string Title { get; }

    public IReadOnlyList<FilterOptionViewModel> Options { get; }

    public bool HasSelection => Options.Any(o => o.IsOn);

    /// <summary>Raised when a chip is switched on or off.</summary>
    public event Action? Changed;

    public bool Matches(object row)
    {
        if (!HasSelection)
            return true;

        string? key = _keyOf(row);
        return Options.Any(o => o.IsOn && o.Key == key);
    }

    /// <summary>Counts each value across all rows (not just those other groups let through).</summary>
    public void Recount(IReadOnlyCollection<object> rows)
    {
        Dictionary<string, int> counts = rows.Select(_keyOf).OfType<string>().GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());

        foreach (FilterOptionViewModel option in Options)
            option.Count = counts.GetValueOrDefault(option.Key);
    }

    [RelayCommand]
    private void Clear()
    {
        foreach (FilterOptionViewModel option in Options)
            option.IsOn = false;
    }
}

/// <summary>Option lists shared by the package pages (Updates, Uninstall).</summary>
public static class PackageFilters
{
    public static IEnumerable<(string Key, string Label)> Kinds()
        => Enum.GetValues<Core.Sources.AppKind>().Select(k => (k.ToString(), Core.Sources.AppKinds.Label(k)));

    public static IEnumerable<(string Key, string Label)> Sources()
        => new[] { "winget", "Microsoft Store", "Not in a catalog", "Game launchers", "npm", "pipx", "dotnet tool", "PSGallery", "VS Code" }.Select(s => (s, s));

    public static FilterGroupViewModel KindGroup() => new("Kind", Kinds(), r => ((PackageRowViewModel)r).KindKey);

    public static FilterGroupViewModel SourceGroup() => new("Source", Sources(), r => ((PackageRowViewModel)r).SourceKey);
}

/// <summary>A page's filter groups. A row is shown when every group matches it (AND across groups, OR within one).</summary>
public sealed class FilterBarViewModel
{
    public FilterBarViewModel(params FilterGroupViewModel[] groups)
    {
        Groups = groups;

        foreach (FilterGroupViewModel group in groups)
            group.Changed += () => Changed?.Invoke();
    }

    public IReadOnlyList<FilterGroupViewModel> Groups { get; }

    public event Action? Changed;

    public bool Matches(object row) => Groups.All(g => g.Matches(row));

    public void Recount(IReadOnlyCollection<object> rows)
    {
        foreach (FilterGroupViewModel group in Groups)
            group.Recount(rows);
    }
}
