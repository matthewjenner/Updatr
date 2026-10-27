using System.Diagnostics;
using Updatr.Core.Catalog;
using Updatr.Core.Operations;
using Updatr.Core.Packages;
using Updatr.Core.Sources;

namespace Updatr.Core.Carts;

/// <summary>Installs apps from one kind of catalog source (winget, npm, direct URL, ...).</summary>
public interface IAppInstaller
{
    bool Handles(PackageSourceRef source);

    /// <summary>Whether it is already installed. False when the installer cannot tell (direct URL).</summary>
    Task<bool> IsInstalledAsync(PackageSourceRef source, CancellationToken cancellationToken);

    /// <summary>Installs it. Never throws for an install failure; the outcome says what happened.</summary>
    Task<PackageOutcome> InstallAsync(PackageSourceRef source, string itemId, IOperationSink sink, CancellationToken cancellationToken);
}

/// <summary>What the machine has, and a way to pick up tools a run just installed.</summary>
public interface IToolEnvironment
{
    /// <summary>Tools on PATH now ("npm", "code", ...).</summary>
    IReadOnlySet<string> ToolsPresent();

    /// <summary>
    /// Re-reads PATH from the registry. An installer adds its folder to PATH for new processes;
    /// this process keeps its old PATH, so without this the npm packages after a Node install fail.
    /// </summary>
    void Refresh();
}

/// <summary>What to install: any mix of apps, templates, and saved carts, or a cart built in the GUI.</summary>
public sealed record InstallRequest
{
    public IReadOnlyList<string> AppIds { get; init; } = [];

    public IReadOnlyList<string> Templates { get; init; } = [];

    public IReadOnlyList<string> Carts { get; init; } = [];

    /// <summary>A cart built in the GUI; merged with the rest.</summary>
    public Cart? Cart { get; init; }

    /// <summary>Install even what is already installed (reinstall over it).</summary>
    public bool IncludeInstalled { get; init; }
}

/// <summary>
/// Installs a cart: resolve prerequisites, confirm, then one installer at a time, prerequisites
/// first. Already-installed apps are skipped as "already current".
/// </summary>
[Operation("install")]
public sealed class InstallApps(
    Func<CancellationToken, Task<AppCatalog>> catalog,
    TemplateCatalog templates,
    CartStore carts,
    IReadOnlyList<IAppInstaller> installers,
    IInstallerGate gate,
    IToolEnvironment tools,
    Leftovers.IFootprintRecorder? footprints = null) : IOperation<InstallRequest, RunSummary>
{
    public async Task<RunSummary> RunAsync(InstallRequest request, IOperationSink sink, CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        AppCatalog apps = await catalog(cancellationToken).ConfigureAwait(false);
        List<ItemFinished> finished = [];

        Cart cart = await CartAssembly.BuildAsync(request.Cart, request.AppIds, request.Templates, request.Carts, templates, carts, cancellationToken).ConfigureAwait(false);
        (_, CartPlan plan) = CartBuilder.Resolve(cart, apps, tools.ToolsPresent());

        foreach (string problem in plan.Problems)
            sink.Report(new LogLine(LogLevel.Warning, problem));

        if (plan.Items.Count == 0)
            return new RunSummary("Install", finished, timer.Elapsed);

        Confirmation confirmation = new($"Install {plan.Items.Count} app(s)?", "Each app installs its newest version, one at a time.")
        {
            Items = [.. plan.Items.Select(p => new ConfirmationItem(p.App.Name, p.Source.SourceId,
                Note: p.Item.RequiredBy.Count > 0 ? $"Required by {string.Join(", ", p.Item.RequiredBy.Select(id => apps.FindApp(id)?.Name ?? id))}" : null))],
            Columns = new ConfirmationColumns(Name: "App", Id: "From"),
        };

        if (!await sink.ConfirmAsync(confirmation, cancellationToken).ConfigureAwait(false))
        {
            foreach (PlannedItem item in plan.Items)
                Finish(sink, finished, item, PackageOutcome.Skipped("Not confirmed"));

            return new RunSummary("Install", finished, timer.Elapsed);
        }

        HashSet<string> failed = new(StringComparer.Ordinal);

        for (int i = 0; i < plan.Items.Count; i++)
        {
            PlannedItem item = plan.Items[i];

            if (cancellationToken.IsCancellationRequested)
            {
                Finish(sink, finished, item, PackageOutcome.Skipped("Canceled before it started"));
                continue;
            }

            // A prerequisite that failed dooms what needs it; say so instead of a confusing tool error.
            if (plan.Items.Any(p => failed.Contains(p.App.Id) && p.Item.RequiredBy.Contains(item.App.Id)))
            {
                Finish(sink, finished, item, PackageOutcome.Skipped("A prerequisite failed to install"));
                failed.Add(item.App.Id);
                continue;
            }

            sink.Report(new StepStarted($"[{i + 1}/{plan.Items.Count}] Install {item.App.Name}"));
            PackageOutcome outcome = await InstallOneAsync(item, request.IncludeInstalled, sink, cancellationToken).ConfigureAwait(false);
            Finish(sink, finished, item, outcome);

            if (outcome.Kind == OutcomeKind.Failed)
                failed.Add(item.App.Id);
            else if (outcome.Kind == OutcomeKind.Ok && item.App.Provides.Count > 0)
                tools.Refresh(); // So the next items can find what this one put on PATH.
        }

        return new RunSummary("Install", finished, timer.Elapsed);
    }

    private async Task<PackageOutcome> InstallOneAsync(PlannedItem item, bool includeInstalled, IOperationSink sink, CancellationToken cancellationToken)
    {
        IAppInstaller? installer = installers.FirstOrDefault(i => i.Handles(item.Source));

        if (installer is null)
            return PackageOutcome.Failed($"Updatr cannot install {item.Source.SourceId} packages yet");

        try
        {
            if (!includeInstalled && await installer.IsInstalledAsync(item.Source, cancellationToken).ConfigureAwait(false))
                return new PackageOutcome(OutcomeKind.AlreadyCurrent, "Already installed");

            await gate.WaitUntilIdleAsync(sink, item.App.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return PackageOutcome.Skipped("Canceled before it started");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return PackageOutcome.Failed(ex.Message);
        }

        // winget installs are snapshotted before and after, so a later uninstall knows exactly what
        // this install created (its "footprint"). Other sources leave nothing a scan would look for.
        object? before = item.Source is WingetSourceRef ? await TryAsync(() => footprints?.BeforeAsync(CancellationToken.None)).ConfigureAwait(false) : null;

        // Not canceled mid-way: an installer that has started is allowed to finish.
        PackageOutcome outcome = await installer.InstallAsync(item.Source, item.App.Id, sink, CancellationToken.None).ConfigureAwait(false);

        if (before is not null && outcome.Kind == OutcomeKind.Ok && item.Source is WingetSourceRef winget)
        {
            sink.Report(new ProgressReported(item.App.Id, Message: "Recording what the install added"));
            await TryAsync(async () =>
            {
                await footprints!.AfterAsync(before, new PackageKey("winget", winget.Id), CancellationToken.None).ConfigureAwait(false);
                return (object?)null;
            }).ConfigureAwait(false);
        }

        return outcome;
    }

    /// <summary>Footprints are a bonus: a failed snapshot never fails the install.</summary>
    private static async Task<object?> TryAsync(Func<Task<object?>?> action)
    {
        try
        {
            return action() is { } task ? await task.ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static void Finish(IOperationSink sink, List<ItemFinished> finished, PlannedItem item, PackageOutcome outcome)
    {
        ItemFinished done = new(item.App.Id, item.App.Name, outcome);
        finished.Add(done);
        sink.Report(done);
    }
}

/// <summary>Built-in templates (catalog) plus the user's own (data folder).</summary>
public sealed class TemplateCatalog(Func<CancellationToken, Task<AppCatalog>> catalog, CartStore store)
{
    /// <summary>Built-in first, then the user's; a user template with a built-in's id shadows it.</summary>
    public async Task<IReadOnlyList<(CatalogTemplate Template, bool BuiltIn)>> AllAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<CatalogTemplate> mine = await store.LoadUserTemplatesAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> mineIds = [.. mine.Select(t => t.Id)];
        IReadOnlyList<CatalogTemplate> builtIn = (await catalog(cancellationToken).ConfigureAwait(false)).Templates;

        return [.. builtIn.Where(t => !mineIds.Contains(t.Id)).Select(t => (t, true)), .. mine.Select(t => (t, false))];
    }

    public async Task<CatalogTemplate?> FindAsync(string id, CancellationToken cancellationToken)
        => (await AllAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(t => string.Equals(t.Template.Id, id, StringComparison.OrdinalIgnoreCase)).Template;
}

/// <summary>Turns "these apps, these templates, these saved carts" into one cart.</summary>
public static class CartAssembly
{
    public static async Task<Cart> BuildAsync(
        Cart? start, IReadOnlyList<string> appIds, IReadOnlyList<string> templateIds, IReadOnlyList<string> cartNames,
        TemplateCatalog templates, CartStore carts, CancellationToken cancellationToken, string name = "Cart")
    {
        Cart cart = start ?? Cart.Empty(name);

        foreach (string cartName in cartNames)
        {
            Cart saved = await carts.LoadCartAsync(cartName, cancellationToken).ConfigureAwait(false)
                ?? throw new ArgumentException($"No saved cart named '{cartName}'.");
            cart = CartBuilder.Merge(cart, saved);
        }

        foreach (string templateId in templateIds)
        {
            CatalogTemplate template = await templates.FindAsync(templateId, cancellationToken).ConfigureAwait(false)
                ?? throw new ArgumentException($"No template named '{templateId}'.");
            cart = CartBuilder.AddTemplate(cart, template);
        }

        return appIds.Aggregate(cart, (c, id) => CartBuilder.Add(c, id));
    }
}
