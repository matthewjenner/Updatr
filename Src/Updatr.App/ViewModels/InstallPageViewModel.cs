using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Updatr.App.Services;
using Updatr.Core.Carts;
using Updatr.Core.Catalog;
using Updatr.Core.Operations;
using Updatr.Core.Packages;
using Updatr.Core.Sources;
using Updatr.Platform;

namespace Updatr.App.ViewModels;

/// <summary>A catalog app's card in the store.</summary>
public sealed partial class CatalogRowViewModel(CatalogApp app, Action<CatalogRowViewModel, bool> toggled) : ObservableObject, Behaviors.ISelectableRow
{
    public CatalogApp App { get; } = app;

    public string Name => App.Name;

    public string Description => App.Description ?? string.Empty;

    public string Section { get; } = CatalogSections.SectionOf(app);

    /// <summary>Where it comes from, when that is not plain winget ("Store", "npm", "GitHub", ...).</summary>
    public string SourceTag { get; } = app.Sources.Count == 0 ? string.Empty : app.Sources[0] switch
    {
        WingetSourceRef { Source: "msstore" } => "Store",
        WingetSourceRef or VsCodeExtensionSourceRef => string.Empty,
        NpmSourceRef => "npm",
        PipxSourceRef => "pipx",
        DotnetToolSourceRef => ".NET tool",
        PsGallerySourceRef => "PowerShell",
        DirectUrlSourceRef { GithubRelease: not null } => "GitHub",
        _ => "Direct",
    };

    public bool HasSourceTag => SourceTag.Length > 0;

    /// <summary>In the cart. Selecting the card adds it; deselecting removes it.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsInstalled { get; set; }

    /// <summary>Not known yet: the installed list is still being read.</summary>
    [ObservableProperty]
    public partial bool IsChecking { get; set; } = true;

    /// <summary>Status filter key; null while checking, so neither chip hides it.</summary>
    public string? StatusKey => IsChecking ? null : IsInstalled ? "installed" : "missing";

    private bool _syncing;

    /// <summary>Sets the selection from the cart without echoing back as a user click.</summary>
    public void Sync(bool inCart)
    {
        _syncing = true;
        IsSelected = inCart;
        _syncing = false;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (!_syncing)
            toggled(this, value);
    }
}

/// <summary>
/// One aisle: a catalog section with its cards, or "All apps" (empty key). <see cref="Shown"/> is
/// what passes the search and filters.
/// </summary>
public sealed partial class CatalogSectionViewModel(string key, string title, bool apart) : ObservableObject
{
    public string Key { get; } = key;

    public string Title { get; } = title;

    /// <summary>Kept out of "All apps" (VS Code extensions); listed below a line in the aisles.</summary>
    public bool Apart { get; } = apart;

    public bool IsAll => Key.Length == 0;

    public List<CatalogRowViewModel> Rows { get; } = [];

    public ObservableCollection<CatalogRowViewModel> Shown { get; } = [];

    [ObservableProperty]
    public partial int Count { get; set; }

    public void Show(IEnumerable<CatalogRowViewModel> rows)
    {
        Shown.Clear();

        foreach (CatalogRowViewModel row in rows)
            Shown.Add(row);

        Count = Shown.Count;
    }
}

/// <summary>A template chip: add it, copy it, edit or delete it (yours only).</summary>
public sealed partial class TemplateRowViewModel(TemplateInfo info, InstallPageViewModel page) : ObservableObject
{
    public TemplateInfo Info { get; } = info;

    public string Label => $"{Info.Template.Name} ({Info.Template.Items.Count})";

    public bool IsMine => !Info.BuiltIn;

    [RelayCommand]
    private void Add() => page.AddTemplate(Info.Template);

    [RelayCommand]
    private Task CopyAsync() => page.CopyTemplateAsync(Info.Template);

    [RelayCommand]
    private void Edit() => page.EditTemplate(Info.Template);

    [RelayCommand]
    private Task DeleteAsync() => page.DeleteTemplateAsync(Info.Template);
}

/// <summary>One cart line: the app, its source, why it is there, and how its install is going.</summary>
public sealed partial class CartRowViewModel(PlannedItem planned, AppCatalog catalog, InstallPageViewModel page) : ObservableObject
{
    public PlannedItem Planned { get; } = planned;

    public string Name => Planned.App.Name;

    public IReadOnlyList<string> SourceChoices { get; } = [.. planned.App.Sources.Select(s => s.SourceId)];

    public bool HasSourceChoice => SourceChoices.Count > 1;

    public string Source
    {
        get => Planned.Source.SourceId;
        set
        {
            if (value != Planned.Source.SourceId)
                page.ChooseSource(Planned.App.Id, value);
        }
    }

    public string Why => Planned.Item.Automatic
        ? $"Required by {string.Join(", ", Planned.Item.RequiredBy.Select(id => catalog.FindApp(id)?.Name ?? id))}"
        : Planned.Item.AddedBy is { } by && by != CartBuilder.AddedByYou ? $"From {by}" : string.Empty;

    public bool IsAutomatic => Planned.Item.Automatic;

    [ObservableProperty]
    public partial string? Activity { get; set; }

    [ObservableProperty]
    public partial OutcomeKind? Outcome { get; set; }

    [RelayCommand]
    private void Remove() => page.Remove(Planned.App.Id);
}

/// <summary>
/// The Install screen: pick apps (Ninite-style), add templates, build and save carts, edit your own
/// templates, and install - all on the same operations as <c>updatr-cli install / cart / template</c>.
/// </summary>
public sealed partial class InstallPageViewModel : PageViewModel
{
    private readonly UpdatrHost _host;
    private readonly IDialogService _dialogs;
    private readonly AppsPageViewModel _installedApps;
    private AppCatalog? _catalog;
    private IReadOnlySet<string> _tools = new HashSet<string>();
    private Cart _cart = Cart.Empty();
    private CancellationTokenSource? _run;
    private bool _loaded;

    public InstallPageViewModel(UpdatrHost host, IDialogService dialogs, AppsPageViewModel installedApps)
    {
        _host = host;
        _dialogs = dialogs;
        _installedApps = installedApps;
        _installedApps.ListCompleted += MarkInstalled;
        Filters = new FilterBarViewModel(_status);
        Filters.Changed += Refresh;
    }

    public override string Title => "Install";

    public ObservableCollection<CatalogRowViewModel> Apps { get; } = [];

    /// <summary>The aisle list: "All apps", then each section; apart sections (VS Code extensions) last.</summary>
    public ObservableCollection<CatalogSectionViewModel> Aisles { get; } = [];

    /// <summary>The sections on screen: all of them under "All apps" (minus apart ones unless searching), else the chosen one.</summary>
    public ObservableCollection<CatalogSectionViewModel> VisibleSections { get; } = [];

    [ObservableProperty]
    public partial CatalogSectionViewModel? SelectedAisle { get; set; }

    partial void OnSelectedAisleChanged(CatalogSectionViewModel? value) => Refresh();

    [ObservableProperty]
    public partial string? EmptyText { get; set; }

    private readonly FilterGroupViewModel _status = new("Status", [("missing", "Not installed"), ("installed", "Installed")],
        r => ((CatalogRowViewModel)r).StatusKey);

    /// <summary>The Installed / Not installed chips.</summary>
    public FilterBarViewModel Filters { get; }

    public ObservableCollection<TemplateRowViewModel> Templates { get; } = [];

    public ObservableCollection<CartRowViewModel> CartRows { get; } = [];

    public ObservableCollection<string> SavedCarts { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? SelectedSavedCart { get; set; }

    [ObservableProperty]
    public partial string CartSummary { get; set; } = "Cart is empty";

    [ObservableProperty]
    public partial string? Problems { get; set; }

    /// <summary>The user template being edited in the cart, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingTemplate), nameof(EditingBanner))]
    public partial CatalogTemplate? EditingTemplate { get; set; }

    public bool IsEditingTemplate => EditingTemplate is not null;

    public string EditingBanner => EditingTemplate is null ? string.Empty : $"Editing template \"{EditingTemplate.Name}\" - the cart is the template. Save it, or cancel to discard.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string BusyText { get; set; } = string.Empty;

    private bool CanInstall => !IsBusy;

    partial void OnSearchTextChanged(string value) => Refresh();

    public void EnsureLoaded()
    {
        if (_loaded)
            return;

        _loaded = true;
        _ = LoadAsync();
    }

    /// <summary>Fire-and-forget from navigation, so failures are caught and shown here, never thrown.</summary>
    private async Task LoadAsync()
    {
        try
        {
            await ReloadAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Core.Diagnostics.DiagLog.UnhandledException("Install page", ex);
            Problems = $"Could not load the catalog: {ex.Message}";
        }
    }

    private async Task ReloadAsync()
    {
        _catalog = (await _host.GetCatalogAsync(CancellationToken.None)).Catalog;
        _tools = await Task.Run(_host.Tools.ToolsPresent);

        Apps.Clear();
        Aisles.Clear();

        foreach (CatalogApp app in _catalog.Apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            Apps.Add(new CatalogRowViewModel(app, OnToggled));

        CatalogSectionViewModel all = new(string.Empty, "All apps", apart: false);
        Aisles.Add(all);

        foreach (IGrouping<string, CatalogRowViewModel> group in Apps.GroupBy(r => r.Section).OrderBy(g => CatalogSections.Order(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            CatalogSections.Section info = CatalogSections.Describe(group.Key);
            CatalogSectionViewModel section = new(info.Key, info.Title, info.Apart);
            section.Rows.AddRange(group);
            Aisles.Add(section);
        }

        SelectedAisle = all;
        MarkInstalled();

        await RefreshTemplatesAsync();
        await RefreshSavedCartsAsync();
        Resolve();
    }

    /// <summary>
    /// Marks what is installed from the Apps screen's list - one listing for the whole store, not a
    /// lookup per app. Until that list is complete the cards say "checking"; this runs again each
    /// time it completes.
    /// </summary>
    private void MarkInstalled()
    {
        IReadOnlyList<InstalledPackage>? installed = _installedApps.Installed;
        InstalledIndex? index = installed is null ? null : new InstalledIndex(installed);

        foreach (CatalogRowViewModel row in Apps)
        {
            row.IsChecking = index is null;
            row.IsInstalled = index is not null && CatalogMatch.IsInstalled(row.App, index);
        }

        Refresh();
    }

    /// <summary>Re-applies search, filters, and the chosen aisle.</summary>
    private void Refresh()
    {
        if (Aisles.Count == 0)
            return;

        Filters.Recount([.. Apps]);
        List<CatalogSectionViewModel> sections = [.. Aisles.Where(a => !a.IsAll)];

        foreach (CatalogSectionViewModel section in sections)
            section.Show(section.Rows.Where(Matches));

        Aisles[0].Count = sections.Where(s => !s.Apart).Sum(s => s.Count);

        bool searching = SearchText.Length > 0;
        IEnumerable<CatalogSectionViewModel> chosen = SelectedAisle is null or { IsAll: true }
            ? sections.Where(s => !s.Apart || searching)
            : [SelectedAisle];

        VisibleSections.Clear();

        foreach (CatalogSectionViewModel section in chosen.Where(s => s.Count > 0))
            VisibleSections.Add(section);

        EmptyText = VisibleSections.Count > 0 ? null
            : searching ? string.Create(CultureInfo.CurrentCulture, $"Nothing here matches \"{SearchText}\".")
            : "Nothing here matches the filters.";
    }

    private async Task RefreshTemplatesAsync()
    {
        Templates.Clear();

        foreach (TemplateInfo info in await _host.TemplateList.RunAsync(true, NullSink, CancellationToken.None))
            Templates.Add(new TemplateRowViewModel(info, this));
    }

    private async Task RefreshSavedCartsAsync()
    {
        SavedCarts.Clear();

        foreach (string name in await _host.CartList.RunAsync(true, NullSink, CancellationToken.None))
            SavedCarts.Add(name);
    }

    // ---- cart edits (in memory; the cart is resolved after every change) ---------------------

    private void OnToggled(CatalogRowViewModel row, bool inCart)
    {
        _cart = inCart ? CartBuilder.Add(_cart, row.App.Id) : CartBuilder.Remove(_cart, row.App.Id);
        Resolve();
    }

    public void AddTemplate(CatalogTemplate template)
    {
        _cart = CartBuilder.AddTemplate(_cart, template);
        Resolve();
    }

    public void Remove(string appId)
    {
        _cart = CartBuilder.Remove(_cart, appId);
        Resolve();
    }

    public void ChooseSource(string appId, string sourceId)
    {
        _cart = CartBuilder.ChooseSource(_cart, appId, sourceId);
        Resolve();
    }

    [RelayCommand]
    private void ClearCart()
    {
        _cart = Cart.Empty();
        Resolve();
    }

    private void Resolve()
    {
        if (_catalog is null)
            return;

        (Cart resolved, CartPlan plan) = CartBuilder.Resolve(_cart, _catalog, _tools);
        _cart = resolved;

        CartRows.Clear();

        foreach (PlannedItem item in plan.Items)
            CartRows.Add(new CartRowViewModel(item, _catalog, this));

        HashSet<string> inCart = [.. plan.Items.Select(p => p.App.Id)];

        foreach (CatalogRowViewModel row in Apps)
            row.Sync(inCart.Contains(row.App.Id));

        int prerequisites = plan.Items.Count(p => p.Item.Automatic);
        CartSummary = plan.Items.Count == 0
            ? "Cart is empty"
            : $"{plan.Items.Count} app(s){(prerequisites > 0 ? $", {prerequisites} added as prerequisites" : "")}";
        Problems = plan.Problems.Count == 0 ? null : string.Join("\n", plan.Problems);
    }

    // ---- saved carts --------------------------------------------------------------------------

    [RelayCommand]
    private async Task SaveCartAsync()
    {
        if (await _dialogs.PromptAsync("Save cart", "Name for this cart (saving over a name replaces it):", _cart.Name == "Cart" ? "My cart" : _cart.Name) is not { } name)
            return;

        _cart = await _host.CartSave.RunAsync(new SaveCartRequest(name, [], [], [], _cart), NullSink, CancellationToken.None);
        await RefreshSavedCartsAsync();
        SelectedSavedCart = CartStore.Slug(name);
        Resolve();
    }

    [RelayCommand]
    private async Task LoadCartAsync()
    {
        if (SelectedSavedCart is { } name)
        {
            _cart = await _host.CartShow.RunAsync(name, NullSink, CancellationToken.None);
            Resolve();
        }
    }

    [RelayCommand]
    private async Task MergeCartAsync()
    {
        if (SelectedSavedCart is { } name)
        {
            _cart = CartBuilder.Merge(_cart, await _host.CartShow.RunAsync(name, NullSink, CancellationToken.None));
            Resolve();
        }
    }

    [RelayCommand]
    private async Task DeleteCartAsync()
    {
        if (SelectedSavedCart is not { } name
            || !await _dialogs.ConfirmAsync(new Confirmation($"Delete the saved cart \"{name}\"?", "This removes it from the drive. The current cart is not affected.")))
        {
            return;
        }

        await _host.CartDelete.RunAsync(name, NullSink, CancellationToken.None);
        await RefreshSavedCartsAsync();
    }

    // ---- templates ----------------------------------------------------------------------------

    public async Task CopyTemplateAsync(CatalogTemplate template)
    {
        CatalogTemplate copy = await _host.TemplateCopy.RunAsync(new CopyTemplateRequest(template.Id), NullSink, CancellationToken.None);
        await RefreshTemplatesAsync();
        EditTemplate(copy);
    }

    /// <summary>Loads one of the user's templates into the cart for editing.</summary>
    public void EditTemplate(CatalogTemplate template)
    {
        EditingTemplate = template;
        _cart = CartBuilder.AddTemplate(Cart.Empty(template.Name), template);
        Resolve();
    }

    public async Task DeleteTemplateAsync(CatalogTemplate template)
    {
        if (!await _dialogs.ConfirmAsync(new Confirmation($"Delete your template \"{template.Name}\"?", "Built-in templates are not affected.")))
            return;

        await _host.TemplateDelete.RunAsync(template.Id, NullSink, CancellationToken.None);
        await RefreshTemplatesAsync();
    }

    [RelayCommand]
    private async Task SaveTemplateAsync()
    {
        CatalogTemplate? editing = EditingTemplate;
        string? name = editing?.Name ?? await _dialogs.PromptAsync("Save as template", "Name for the new template:", "My template");

        if (name is null)
            return;

        string id = editing?.Id ?? CartStore.Slug(name);
        IReadOnlyList<string> apps = [.. _cart.Items.Where(i => !i.Automatic).Select(i => i.AppId)];

        try
        {
            await _host.TemplateSave.RunAsync(new SaveTemplateRequest(id, name, editing?.Description, apps), NullSink, CancellationToken.None);
        }
        catch (ArgumentException ex)
        {
            Problems = ex.Message;
            return;
        }

        EditingTemplate = null;
        await RefreshTemplatesAsync();
    }

    [RelayCommand]
    private void CancelTemplateEdit()
    {
        EditingTemplate = null;
        _cart = Cart.Empty();
        Resolve();
    }

    // ---- install ------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (CartRows.Count == 0)
            return;

        _run = new CancellationTokenSource();
        IsBusy = true;
        BusyText = "Installing...";
        RunSummary? summary = null;

        foreach (CartRowViewModel row in CartRows)
        {
            row.Activity = "Queued";
            row.Outcome = null;
        }

        try
        {
            GuiSink sink = new(OnEvent, _dialogs.ConfirmAsync);
            Cart cart = _cart;
            summary = await Task.Run(() => _host.Install.RunAsync(new InstallRequest { Cart = cart }, sink, _run.Token));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Core.Diagnostics.DiagLog.UnhandledException("Install page", ex);
            BusyText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _run.Dispose();
            _run = null;
        }

        if (summary is { Items.Count: > 0 })
            await _dialogs.ShowSummaryAsync(summary);

        _tools = await Task.Run(_host.Tools.ToolsPresent);

        // What this run installed is installed now; the rest keeps its mark until the next listing.
        foreach (ItemFinished done in summary?.Items.Where(i => i.Outcome.Kind is OutcomeKind.Ok or OutcomeKind.AlreadyCurrent) ?? [])
        {
            if (Apps.FirstOrDefault(r => r.App.Id == done.ItemId) is { } row)
            {
                row.IsChecking = false;
                row.IsInstalled = true;
            }
        }

        Refresh();
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        _run?.Cancel();
        BusyText = "Canceling - the installer that is running will finish first...";
    }

    private void OnEvent(OperationEvent operationEvent)
    {
        CartRowViewModel? Row(string? id) => CartRows.FirstOrDefault(r => r.Planned.App.Id == id);

        switch (operationEvent)
        {
            case StepStarted step:
                BusyText = step.Step;
                break;
            case ProgressReported progress when Row(progress.ItemId) is { } row:
                row.Activity = progress.Message ?? "Working";
                break;
            case ItemFinished done when Row(done.ItemId) is { } row:
                row.Outcome = done.Outcome.Kind;
                row.Activity = done.Outcome.Kind switch
                {
                    OutcomeKind.Ok => done.Outcome.RebootRequired ? "Installed - reboot needed" : "Installed",
                    OutcomeKind.AlreadyCurrent => "Already installed",
                    OutcomeKind.Skipped => "Skipped: " + done.Outcome.Detail,
                    OutcomeKind.Pending => "Pending: " + done.Outcome.Detail,
                    _ => "Failed: " + done.Outcome.Detail,
                };
                break;
        }
    }

    private bool Matches(CatalogRowViewModel row)
        => Filters.Matches(row)
            && (SearchText.Length == 0
                || row.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || row.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    private static readonly IOperationSink NullSink = new GuiSink(_ => { });
}
