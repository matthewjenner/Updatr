using System.Text.Json.Nodes;
using Updatr.Core.Catalog;
using Updatr.Core.Leftovers;
using Updatr.Core.Operations;
using Updatr.Core.Sources;
using Updatr.Platform.Elevation;
using Updatr.Platform.Windows;

namespace Updatr.Platform.Leftovers;

/// <summary>
/// The last check before anything is moved, here and in the broker: only the kinds of places a
/// leftover scan offers. A tampered or confused request cannot reach beyond them.
/// </summary>
public static class LeftoverSafety
{
    /// <summary>First keys under SOFTWARE that are never removed whole.</summary>
    private static readonly HashSet<string> SharedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Classes", "Policies", "WOW6432Node", "Windows", "Clients", "RegisteredApplications", "Wow6432Node",
    };

    /// <summary>Why this item may not be moved, or null when it may.</summary>
    public static string? Problem(LeftoverKind kind, string location)
    {
        string[] folderRoots = [.. LeftoverScanner.FolderRoots().Select(r => r.Root)];

        return kind switch
        {
            LeftoverKind.Folder when !LeftoverRules.IsOfferableFolder(location, folderRoots, [Environment.GetFolderPath(Environment.SpecialFolder.Windows), Data.AppLocations.ExeDirectory])
                => "outside the folders Updatr may clean",
            LeftoverKind.Shortcut when !location.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !ShortcutRoots().Any(r => LeftoverRules.IsUnder(location, r))
                => "not a shortcut in a Start menu or desktop",
            LeftoverKind.RegistryKey when !RegistryKeyAllowed(location)
                => "outside the registry keys Updatr may clean",
            LeftoverKind.Service when !CatalogValidator.ServiceNamePattern().IsMatch(location)
                || ServiceControl.Read(location) is { } s && LeftoverRules.IsUnder(LeftoverRules.ExecutableOf(s.ImagePath), Environment.GetFolderPath(Environment.SpecialFolder.Windows))
                => "a Windows service",
            LeftoverKind.ScheduledTask when !location.StartsWith('\\') || location.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)
                => "a Windows task",
            LeftoverKind.FirewallRule when string.IsNullOrWhiteSpace(location) || location.StartsWith('@')
                => "a Windows firewall rule",
            _ => null,
        };
    }

    /// <summary>
    /// HKCU\Software or HKLM\SOFTWARE (WOW6432Node too), one or two keys down and not a shared key
    /// (Microsoft, Classes, Policies, ...) - or one app's own Uninstall entry.
    /// </summary>
    public static bool RegistryKeyAllowed(string location)
    {
        if (LeftoverScanner.ParseKey(location) is not { } key)
            return false;

        string[] parts = key.Path.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0 || !parts[0].Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] rest = parts.Length > 1 && parts[1].Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase) ? parts[2..] : parts[1..];

        if (rest.Length == 5 && string.Join('\\', rest[..4]).Equals(@"Microsoft\Windows\CurrentVersion\Uninstall", StringComparison.OrdinalIgnoreCase))
            return true;

        return rest.Length is 1 or 2 && !SharedKeys.Contains(rest[0]);
    }

    private static IEnumerable<string> ShortcutRoots() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
    ];

    /// <summary>Whether changing it needs administrator access.</summary>
    public static bool NeedsAdmin(LeftoverKind kind, string location) => kind switch
    {
        LeftoverKind.Service or LeftoverKind.ScheduledTask or LeftoverKind.FirewallRule => true,
        LeftoverKind.RegistryKey => location.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase),
        _ => !LeftoverRules.IsUnder(location, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
    };
}

/// <summary>
/// Quarantine in <c>Updatr.data\quarantine\&lt;machine&gt;\</c>. Runs in this process, or in the
/// elevated broker when any item is machine-wide (one UAC prompt).
/// </summary>
public sealed class Quarantine(string dataRoot, string machineId, ElevationService elevation) : IQuarantine
{
    public const string BrokerType = "leftover-run";

    private string Folder => Path.Combine(dataRoot, "quarantine", machineId);

    public async Task<QuarantineEntry> MoveAsync(string app, IReadOnlyList<Leftover> items, IOperationSink sink, CancellationToken cancellationToken)
    {
        DateTimeOffset at = DateTimeOffset.Now;
        string entryDir = Path.Combine(Folder, LeftoverStore.NewId(at, app));
        sink.Report(new StepStarted($"Moving {items.Count} leftover(s) of {app} into quarantine"));

        if (WindowsInfo.IsElevated || !items.Any(i => LeftoverSafety.NeedsAdmin(i.Kind, i.Location)))
            return await Task.Run(() => QuarantineEngine.Move(entryDir, app, items), CancellationToken.None).ConfigureAwait(false);

        BrokerClient? broker = await elevation.GetBrokerAsync(cancellationToken).ConfigureAwait(false);

        if (broker is null)
        {
            // Declined: move what this process can, and say why the rest stayed.
            Leftover[] mine = [.. items.Where(i => !LeftoverSafety.NeedsAdmin(i.Kind, i.Location))];
            QuarantineEntry partial = await Task.Run(() => QuarantineEngine.Move(entryDir, app, mine), CancellationToken.None).ConfigureAwait(false);
            return partial with
            {
                Items = [.. partial.Items, .. items.Where(i => LeftoverSafety.NeedsAdmin(i.Kind, i.Location))
                    .Select(i => new QuarantinedItem(i.Kind, i.Location, false, "needs administrator access, which was declined"))],
            };
        }

        JsonObject request = new()
        {
            ["op"] = "move",
            ["entry"] = entryDir,
            ["app"] = app,
            ["items"] = new JsonArray([.. items.Select(i => (JsonNode)new JsonObject { ["kind"] = i.Kind.ToString(), ["location"] = i.Location, ["size"] = i.SizeBytes })]),
        };

        JsonNode? reply = await broker.SendAsync(BrokerType, request, null, CancellationToken.None).ConfigureAwait(false);
        return QuarantineEngine.Read(entryDir) ?? throw new BrokerException(reply?["error"]?.GetValue<string>() ?? "The elevated helper did not write the quarantine entry.");
    }

    public Task<IReadOnlyList<QuarantineEntry>> ListAsync(CancellationToken cancellationToken)
        => Task.Run<IReadOnlyList<QuarantineEntry>>(() => Directory.Exists(Folder)
            ? [.. Directory.EnumerateDirectories(Folder).Select(QuarantineEngine.Read).OfType<QuarantineEntry>().OrderByDescending(e => e.At)]
            : [], cancellationToken);

    public async Task<PackageOutcome> RestoreAsync(string entryId, IOperationSink sink, CancellationToken cancellationToken)
    {
        string entryDir = EntryDir(entryId);
        QuarantineEntry entry = QuarantineEngine.Read(entryDir) ?? throw new ArgumentException($"No quarantine entry '{entryId}'.");
        sink.Report(new StepStarted($"Restoring the leftovers of {entry.App}"));
        IReadOnlyList<string> problems;

        if (WindowsInfo.IsElevated || !entry.Items.Any(i => i.Stored && LeftoverSafety.NeedsAdmin(i.Kind, i.Location)))
        {
            problems = await Task.Run(() => QuarantineEngine.Restore(entryDir), CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            BrokerClient? broker = await elevation.GetBrokerAsync(cancellationToken).ConfigureAwait(false);

            if (broker is null)
                return PackageOutcome.Skipped("Restoring needs administrator access, which was declined");

            JsonNode? reply = await broker.SendAsync(BrokerType, new JsonObject { ["op"] = "restore", ["entry"] = entryDir }, null, CancellationToken.None).ConfigureAwait(false);
            problems = [.. (reply?["problems"] as JsonArray ?? []).Select(p => p?.GetValue<string>() ?? string.Empty)];
        }

        if (problems.Count > 0)
            return PackageOutcome.Failed("Partly restored - " + string.Join("; ", problems));

        // Everything is back: the entry has done its job.
        await PurgeAsync(entryId, cancellationToken).ConfigureAwait(false);
        return PackageOutcome.Ok();
    }

    public async Task PurgeAsync(string entryId, CancellationToken cancellationToken)
    {
        string entryDir = EntryDir(entryId);

        try
        {
            await Task.Run(() => Directory.Delete(entryDir, recursive: true), cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            // Written by the elevated helper on a drive that keeps its owner: delete it there too.
        }

        BrokerClient broker = await elevation.GetBrokerAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("Some quarantined files belong to the administrator; purging them needs administrator access, which was declined.");
        await broker.SendAsync(BrokerType, new JsonObject { ["op"] = "purge", ["entry"] = entryDir }, null, CancellationToken.None).ConfigureAwait(false);
    }

    private string EntryDir(string entryId)
    {
        string dir = Path.GetFullPath(Path.Combine(Folder, entryId));

        return LeftoverRules.IsUnder(dir, Folder) && dir.Length > Folder.Length
            ? dir
            : throw new ArgumentException($"'{entryId}' is not a quarantine entry.");
    }
}

/// <summary>Quarantine moves, restores, and purges inside the elevated broker - only within the host's quarantine folder.</summary>
public sealed class LeftoverBrokerHandler : IBrokerHandler
{
    public string Type => Quarantine.BrokerType;

    public Task<JsonNode?> HandleAsync(JsonNode? payload, Func<JsonNode, Task> emit, CancellationToken cancellationToken)
        => Task.Run<JsonNode?>(() =>
        {
            string entry = Path.GetFullPath(payload?["entry"]?.GetValue<string>() ?? throw new ArgumentException("Missing entry."));
            string root = Path.Combine(BrokerHost.DataRoot ?? throw new InvalidOperationException("The broker was started without a data folder."), "quarantine");

            if (!LeftoverRules.IsUnder(entry, root) || entry.Length <= root.Length)
                throw new ArgumentException("The entry is not inside the quarantine folder.");

            switch (payload?["op"]?.GetValue<string>())
            {
                case "move":
                    // Every item is checked again by QuarantineEngine (LeftoverSafety) before it is touched.
                    Leftover[] items = [.. (payload["items"] as JsonArray ?? []).Select((n, i) => new Leftover(
                        (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Enum.Parse<LeftoverKind>(n!["kind"]!.GetValue<string>()),
                        n["location"]!.GetValue<string>(),
                        Confidence.Low,
                        string.Empty,
                        n["size"]?.GetValue<long?>()))];
                    QuarantineEngine.Move(entry, payload["app"]?.GetValue<string>() ?? "app", items);
                    return new JsonObject { ["ok"] = true };
                case "restore":
                    return new JsonObject { ["problems"] = new JsonArray([.. QuarantineEngine.Restore(entry).Select(p => (JsonNode)JsonValue.Create(p)!)]) };
                case "purge":
                    Directory.Delete(entry, recursive: true);
                    return new JsonObject { ["ok"] = true };
                default:
                    throw new ArgumentException("Unknown op.");
            }
        }, cancellationToken);
}
