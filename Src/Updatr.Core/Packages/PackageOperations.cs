using System.Diagnostics;
using Updatr.Core.Catalog;
using Updatr.Core.Errors;
using Updatr.Core.Operations;
using Updatr.Core.Sources;

namespace Updatr.Core.Packages;

/// <summary>An installed package and, if it is held, why.</summary>
public sealed record ListedPackage(InstalledPackage Package, HoldMatch? Hold)
{
    /// <summary>What sort of package it is (app, game, runtime, ...), for filters.</summary>
    public AppKind Kind { get; } = AppKinds.Classify(Package);

    public bool IsHeld => Hold?.Rule.Behavior == HoldBehavior.Hold;

    public bool IsStoreManaged => Hold?.Rule.Behavior == HoldBehavior.StoreManaged;

    /// <summary>Self-updating: upgraded only when named, never by "upgrade all".</summary>
    public bool IsExplicitOnly => Hold?.Rule.Behavior == HoldBehavior.Explicit;
}

/// <summary>A package was found while listing. Front ends add its row immediately.</summary>
public sealed record PackageListed(ListedPackage Listed) : OperationEvent;

/// <summary>What to list. Each filter is "any of these"; empty or null means no filter.</summary>
/// <param name="UpdatesOnly">Only packages with an update available.</param>
/// <param name="Sources">Only these sources (winget, npm, ...); sources not named are not even queried.</param>
/// <param name="Kinds">Only these kinds (apps, games, ...).</param>
public sealed record ListRequest(bool UpdatesOnly = false, IReadOnlyList<string>? Sources = null, IReadOnlyList<AppKind>? Kinds = null);

/// <summary>Everything a run did, for the summary dialog and the CLI's last lines.</summary>
public sealed record RunSummary(string Action, IReadOnlyList<ItemFinished> Items, TimeSpan Duration)
{
    public int Count(OutcomeKind kind) => Items.Count(i => i.Outcome.Kind == kind);

    public bool RebootRequired => Items.Any(i => i.Outcome.RebootRequired);

    public bool HasFailures => Count(OutcomeKind.Failed) > 0;
}

/// <summary>Lists installed packages across sources, with holds applied. Streams as it goes.</summary>
[Operation("list")]
public sealed class ListPackages(IReadOnlyList<IPackageSource> sources, Func<CancellationToken, Task<HoldPolicy>> holds)
    : IOperation<ListRequest, IReadOnlyList<ListedPackage>>
{
    public async Task<IReadOnlyList<ListedPackage>> RunAsync(ListRequest request, IOperationSink sink, CancellationToken cancellationToken)
    {
        HoldPolicy policy = await holds(cancellationToken).ConfigureAwait(false);
        List<ListedPackage> all = [];
        Lock gate = new();

        await Task.WhenAll(sources
            .Where(s => request.Sources is not { Count: > 0 } sources || sources.Contains(s.Id, StringComparer.OrdinalIgnoreCase))
            .Select(async source =>
            {
                sink.Report(new StepStarted($"Checking {source.Id}"));

                await foreach (InstalledPackage package in source.ListInstalledAsync(sink, cancellationToken).ConfigureAwait(false))
                {
                    if (request.UpdatesOnly && !package.HasUpdate)
                        continue;

                    ListedPackage listed = new(package, policy.Match(package.Source, package.Id));

                    if (request.Kinds is { Count: > 0 } kinds && !kinds.Contains(listed.Kind))
                        continue;

                    lock (gate)
                        all.Add(listed);

                    sink.Report(new PackageListed(listed));
                }
            })).ConfigureAwait(false);

        return [.. all.OrderBy(p => p.Package.Name, StringComparer.OrdinalIgnoreCase)];
    }
}

/// <summary>Which packages an upgrade or reinstall applies to. Exactly one of the three ways.</summary>
public sealed record PackageSelection
{
    /// <summary>Packages already listed (the GUI's selection).</summary>
    public IReadOnlyList<InstalledPackage>? Packages { get; init; }

    /// <summary>Package ids typed by the user (the CLI); looked up in each source.</summary>
    public IReadOnlyList<string>? Ids { get; init; }

    /// <summary>Everything with an update available (upgrade only).</summary>
    public bool All { get; init; }

    /// <summary>With <see cref="All"/>: include held packages too.</summary>
    public bool IncludeHeld { get; init; }

    /// <summary>With <see cref="All"/>: include packages whose installed version is unknown.</summary>
    public bool IncludeUnknown { get; init; }

    /// <summary>Uninstall only: do not look for leftovers afterwards.</summary>
    public bool SkipLeftovers { get; init; }
}

/// <summary>Upgrades packages, one installer at a time.</summary>
[Operation("upgrade")]
public sealed class UpgradePackages(PackageBatch batch) : IOperation<PackageSelection, RunSummary>
{
    public Task<RunSummary> RunAsync(PackageSelection request, IOperationSink sink, CancellationToken cancellationToken)
        => batch.RunAsync(PackageBatch.Action.Upgrade, request, sink, cancellationToken);
}

/// <summary>Installs packages again over themselves, to repair a broken install.</summary>
[Operation("reinstall")]
public sealed class ReinstallPackages(PackageBatch batch) : IOperation<PackageSelection, RunSummary>
{
    public Task<RunSummary> RunAsync(PackageSelection request, IOperationSink sink, CancellationToken cancellationToken)
        => request.All
            ? throw new ArgumentException("Reinstall needs specific packages, not --all.")
            : batch.RunAsync(PackageBatch.Action.Reinstall, request, sink, cancellationToken);
}

/// <summary>Removes packages through their own uninstallers, one at a time.</summary>
[Operation("uninstall")]
public sealed class UninstallPackages(PackageBatch batch) : IOperation<PackageSelection, RunSummary>
{
    public Task<RunSummary> RunAsync(PackageSelection request, IOperationSink sink, CancellationToken cancellationToken)
        => request.All
            ? throw new ArgumentException("Uninstall needs specific packages, not --all.")
            : batch.RunAsync(PackageBatch.Action.Uninstall, request, sink, cancellationToken);
}

/// <summary>
/// The shared batch runner behind upgrade, reinstall, and uninstall: resolve the selection, confirm, then run
/// each package in turn - waiting out other installers, retrying once when the system was busy,
/// and stopping cleanly between packages on cancel.
/// </summary>
public sealed class PackageBatch(
    IReadOnlyList<IPackageSource> sources,
    ListPackages list,
    IInstallerGate gate,
    Func<CancellationToken, Task<HoldPolicy>> holds,
    Leftovers.LeftoverService? leftovers = null)
{
    public enum Action
    {
        Upgrade,
        Reinstall,
        Uninstall,
    }

    /// <summary>
    /// What Updatr itself stands on. Removing winget mid-run would strand the rest of the batch and
    /// every later run on this machine.
    /// </summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase) { "Microsoft.AppInstaller", "Microsoft.DesktopAppInstaller" };

    /// <summary>Codes that mean "something else was installing" - worth one retry after waiting.</summary>
    private static readonly HashSet<uint> BusyCodes = [0x00000652, 0x8A150102];

    public async Task<RunSummary> RunAsync(Action action, PackageSelection selection, IOperationSink sink, CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        string verb = action.ToString();
        List<ItemFinished> finished = [];
        List<InstalledPackage> targets = await ResolveAsync(action, selection, sink, finished, cancellationToken).ConfigureAwait(false);

        if (targets.Count == 0)
            return new RunSummary(verb, finished, timer.Elapsed);

        string names = string.Join("\n", targets.Select(p => $"  {p.Name} ({p.Id}) {p.InstalledVersion}{(action == Action.Upgrade && p.AvailableVersion is not null ? " -> " + p.AvailableVersion : "")}"));

        if (action == Action.Uninstall)
            names += "\n\nEach app's own uninstaller runs. Leftover files and settings are not touched.";

        if (!await sink.ConfirmAsync(new Confirmation($"{verb} {targets.Count} package(s)?", names), cancellationToken).ConfigureAwait(false))
        {
            foreach (InstalledPackage p in targets)
                Finish(sink, finished, p, PackageOutcome.Skipped("Not confirmed"));

            return new RunSummary(verb, finished, timer.Elapsed);
        }

        for (int i = 0; i < targets.Count; i++)
        {
            InstalledPackage package = targets[i];

            if (cancellationToken.IsCancellationRequested)
            {
                Finish(sink, finished, package, PackageOutcome.Skipped("Canceled before it started"));
                continue;
            }

            sink.Report(new StepStarted($"[{i + 1}/{targets.Count}] {verb} {package.Name}"));
            PackageOutcome outcome = action == Action.Uninstall && leftovers is not null && !selection.SkipLeftovers
                ? await UninstallWithScanAsync(package, sink, cancellationToken).ConfigureAwait(false)
                : await RunOneAsync(action, package, sink, cancellationToken).ConfigureAwait(false);
            Finish(sink, finished, package, outcome);
        }

        return new RunSummary(verb, finished, timer.Elapsed);
    }

    /// <summary>
    /// Captures the app's details first (the uninstaller erases its registration), uninstalls, then
    /// scans for what it left behind and saves that for review. Nothing is removed here.
    /// </summary>
    private async Task<PackageOutcome> UninstallWithScanAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
    {
        Leftovers.LeftoverTarget? target = await leftovers!.DescribeAsync(package, cancellationToken).ConfigureAwait(false);
        PackageOutcome outcome = await RunOneAsync(Action.Uninstall, package, sink, cancellationToken).ConfigureAwait(false);

        if (target is null || outcome.Kind != OutcomeKind.Ok)
            return outcome;

        int found = await leftovers.ScanAsync(target, sink, package.Key.ToString(), CancellationToken.None).ConfigureAwait(false);

        if (found == 0)
            return outcome;

        string note = $"{found} possible leftover(s) - review them under Leftovers";
        return outcome with { Detail = outcome.Detail.Length == 0 ? note : $"{outcome.Detail}; {note}" };
    }

    private async Task<PackageOutcome> RunOneAsync(Action action, InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
    {
        IPackageSource? source = sources.FirstOrDefault(s => s.Id == package.Source);

        if (source is null)
            return PackageOutcome.Failed($"No source named '{package.Source}' is available");

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await gate.WaitUntilIdleAsync(sink, package.Key.ToString(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return PackageOutcome.Skipped("Canceled while waiting for another installer");
            }

            // Not passed the cancellation token: an installer that has started is allowed to finish.
            PackageOutcome outcome = action switch
            {
                Action.Upgrade => await source.UpgradeAsync(package, sink, CancellationToken.None).ConfigureAwait(false),
                Action.Reinstall => await source.ReinstallAsync(package, sink, CancellationToken.None).ConfigureAwait(false),
                _ => await source.UninstallAsync(package, sink, CancellationToken.None).ConfigureAwait(false),
            };

            if (attempt == 1 && outcome.Code is { } code && BusyCodes.Contains(code.Code) && !cancellationToken.IsCancellationRequested)
            {
                sink.Report(new LogLine(LogLevel.Warning, $"{package.Name}: another install was running; retrying once it finishes", package.Key.ToString()));
                continue;
            }

            return outcome;
        }
    }

    private async Task<List<InstalledPackage>> ResolveAsync(Action action, PackageSelection selection, IOperationSink sink, List<ItemFinished> finished, CancellationToken cancellationToken)
    {
        List<InstalledPackage> chosen = [];
        HoldPolicy policy = await holds(cancellationToken).ConfigureAwait(false);

        if (selection.All)
        {
            IReadOnlyList<ListedPackage> listed = await list.RunAsync(new ListRequest(UpdatesOnly: true), sink, cancellationToken).ConfigureAwait(false);

            foreach (ListedPackage item in listed)
            {
                InstalledPackage p = item.Package;

                if (p.ManagedBy is { } launcher)
                    Finish(sink, finished, p, PackageOutcome.Skipped($"Managed by {launcher} - update it there"));
                else if (item.IsStoreManaged)
                    Finish(sink, finished, p, PackageOutcome.Skipped("Updates through the Microsoft Store, not winget"));
                else if (item.IsExplicitOnly)
                    Finish(sink, finished, p, PackageOutcome.Skipped($"Updates itself - upgrade it by name if you want winget to: {item.Hold!.Rule.Reason}"));
                else if (item.IsHeld && !selection.IncludeHeld)
                    Finish(sink, finished, p, PackageOutcome.Skipped($"Held: {item.Hold!.Rule.Reason}"));
                else if (p.HasUnknownVersion && !selection.IncludeUnknown)
                    Finish(sink, finished, p, PackageOutcome.Skipped("Installed version unknown; upgrades from it are unreliable"));
                else if (p.IsManaged && p.CheckProblem is null)
                    chosen.Add(p);
            }

            return chosen;
        }

        List<InstalledPackage> explicitly = [.. selection.Packages ?? []];

        foreach (string id in selection.Ids ?? [])
        {
            InstalledPackage? found = null;
            string? lookupProblem = null;

            foreach (IPackageSource source in sources)
            {
                try
                {
                    found = await source.FindInstalledAsync(id, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lookupProblem = ex.Message;
                }

                if (found is not null)
                    break;
            }

            if (found is null && lookupProblem is not null)
            {
                Finish(sink, finished, new InstalledPackage { Source = "?", Id = id, Name = id, InstalledVersion = "" }, PackageOutcome.Failed(lookupProblem));
                continue;
            }

            if (found is null)
                Finish(sink, finished, new InstalledPackage { Source = "?", Id = id, Name = id, InstalledVersion = "" }, PackageOutcome.Skipped("Not installed (or no source knows this id)"));
            else
                explicitly.Add(found);
        }

        // Named explicitly = the user means it: holds do not apply, but impossible or unsafe cases still skip.
        foreach (InstalledPackage p in explicitly)
        {
            if (action == Action.Uninstall)
            {
                // Anything installed can be removed - catalog or not - except what a launcher owns
                // (its uninstall goes through the launcher) and what Updatr needs.
                if (p.ManagedBy is { } owner)
                    Finish(sink, finished, p, PackageOutcome.Skipped($"Managed by {owner} - uninstall it there"));
                else if (Protected.Contains(p.Id))
                    Finish(sink, finished, p, PackageOutcome.Skipped("Updatr needs winget (App Installer); it is not removed"));
                else
                    chosen.Add(p);

                continue;
            }

            if (p.ManagedBy is { } launcher)
                Finish(sink, finished, p, PackageOutcome.Skipped($"Managed by {launcher} - update it there; changing it outside the launcher risks a broken install"));
            else if (policy.Match(p.Source, p.Id) is { Rule.Behavior: HoldBehavior.StoreManaged })
                Finish(sink, finished, p, PackageOutcome.Skipped("Updates through the Microsoft Store, not winget"));
            else if (!p.IsManaged)
                Finish(sink, finished, p, PackageOutcome.Skipped("No catalog knows this app, so it cannot be changed from here"));
            else if (action == Action.Upgrade && !p.HasUpdate && p.NewerVersionNeedsFreshInstall is { } newer)
                Finish(sink, finished, p, PackageOutcome.Skipped($"{newer} exists but {p.FreshInstallReason ?? "cannot be applied in place"}; uninstall and install it fresh"));
            else if (action == Action.Upgrade && !p.HasUpdate)
                Finish(sink, finished, p, new PackageOutcome(OutcomeKind.AlreadyCurrent, "No update available"));
            else
                chosen.Add(p);
        }

        return chosen;
    }

    private static void Finish(IOperationSink sink, List<ItemFinished> finished, InstalledPackage package, PackageOutcome outcome)
    {
        ItemFinished item = new(package.Key.ToString(), package.Name, outcome);
        finished.Add(item);
        sink.Report(item);
    }
}

/// <summary>Hold or release packages: by id (CLI) or from a selection (GUI).</summary>
public sealed record HoldRequest(IReadOnlyList<PackageKey> Packages, string Reason = "Held by you");

/// <summary>Holds packages: "upgrade all" will leave them alone.</summary>
[Operation("hold")]
public sealed class HoldPackages(HoldStore store) : IOperation<HoldRequest, int>
{
    public async Task<int> RunAsync(HoldRequest request, IOperationSink sink, CancellationToken cancellationToken)
    {
        UserHoldsFile file = await store.LoadAsync(cancellationToken).ConfigureAwait(false);

        foreach (PackageKey key in request.Packages)
        {
            file = HoldStore.Hold(file, key.Source, key.Id, request.Reason);
            sink.Report(new LogLine(LogLevel.Info, $"Held {key.Id}", key.ToString()));
        }

        await store.SaveAsync(file, cancellationToken).ConfigureAwait(false);
        return request.Packages.Count;
    }
}

/// <summary>Releases holds - the user's own, and catalog holds on the named packages.</summary>
[Operation("unhold")]
public sealed class UnholdPackages(HoldStore store, Func<CancellationToken, Task<IReadOnlyList<HoldRule>>> catalogHolds) : IOperation<HoldRequest, int>
{
    public async Task<int> RunAsync(HoldRequest request, IOperationSink sink, CancellationToken cancellationToken)
    {
        UserHoldsFile file = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<HoldRule> catalog = await catalogHolds(cancellationToken).ConfigureAwait(false);

        foreach (PackageKey key in request.Packages)
        {
            file = HoldStore.Unhold(file, catalog, key.Source, key.Id);
            sink.Report(new LogLine(LogLevel.Info, $"Released {key.Id}", key.ToString()));
        }

        await store.SaveAsync(file, cancellationToken).ConfigureAwait(false);
        return request.Packages.Count;
    }
}
