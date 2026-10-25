using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Updatr.Core.Abstractions;
using Updatr.Core.Carts;
using Updatr.Core.Operations;
using Updatr.Core.Sources;

namespace Updatr.Core.Leftovers;

/// <summary>
/// Scans waiting for review, in <c>leftovers/&lt;machine&gt;/</c>. Per machine: a scan only means
/// something on the PC it was made on, and the drive visits many.
/// </summary>
public sealed class LeftoverStore(IDataStore store, string machineId)
{
    private string Folder => $"leftovers/{machineId}";

    public async Task<IReadOnlyList<LeftoverScan>> ListAsync(CancellationToken cancellationToken)
    {
        List<LeftoverScan> scans = [];

        foreach (string file in await store.ListAsync(Folder, cancellationToken).ConfigureAwait(false))
        {
            string? json = await store.ReadTextAsync(file, cancellationToken).ConfigureAwait(false);

            try
            {
                if (json is not null && JsonSerializer.Deserialize(json, LeftoverJsonContext.Default.LeftoverScan) is { } scan)
                    scans.Add(scan);
            }
            catch (JsonException)
            {
                // A damaged file: skip it rather than hide every other scan.
            }
        }

        return [.. scans.OrderBy(s => s.At)];
    }

    public async Task<LeftoverScan> GetAsync(string id, CancellationToken cancellationToken)
        => (await ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No leftover scan '{id}' on this PC (see: leftovers list).");

    public Task SaveAsync(LeftoverScan scan, CancellationToken cancellationToken)
        => store.WriteTextAsync($"{Folder}/{scan.Id}.json", JsonSerializer.Serialize(scan, LeftoverJsonContext.Default.LeftoverScan), cancellationToken);

    public Task DeleteAsync(string id, CancellationToken cancellationToken)
        => store.DeleteAsync($"{Folder}/{CartStore.Slug(id)}.json", cancellationToken);

    /// <summary>"20261025-142233-everything": sorts by time, readable, file-safe.</summary>
    public static string NewId(DateTimeOffset at, string app)
        => string.Create(CultureInfo.InvariantCulture, $"{at.ToUniversalTime():yyyyMMdd-HHmmss}-{CartStore.Slug(app)}");
}

/// <summary>Describe before an uninstall, scan after it. Used by the uninstall batch.</summary>
public sealed class LeftoverService(ILeftoverScanner scanner, LeftoverStore store, IFootprintRecorder? footprints = null)
{
    public LeftoverStore Store => store;

    /// <summary>The app's details while it is still installed, or null (nothing to scan, or it could not be read).</summary>
    public async Task<LeftoverTarget?> DescribeAsync(InstalledPackage package, CancellationToken cancellationToken)
    {
        try
        {
            LeftoverTarget? target = await scanner.DescribeAsync(package, cancellationToken).ConfigureAwait(false);

            if (target is not null && footprints is not null)
                target = target with { Footprint = await footprints.LoadAsync(package.Key, cancellationToken).ConfigureAwait(false) };

            return target;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return null; // Leftover scanning is a bonus; never fail an uninstall over it.
        }
    }

    /// <summary>Scans and saves the findings for review. Returns how many were found.</summary>
    public async Task<int> ScanAsync(LeftoverTarget target, IOperationSink sink, string itemId, CancellationToken cancellationToken)
    {
        sink.Report(new ProgressReported(itemId, Message: "Looking for leftovers"));

        try
        {
            IReadOnlyList<Leftover> items = await scanner.ScanAsync(target, cancellationToken).ConfigureAwait(false);

            if (items.Count == 0)
                return 0;

            DateTimeOffset at = DateTimeOffset.Now;
            await store.SaveAsync(new LeftoverScan { Id = LeftoverStore.NewId(at, target.Name), At = at, Target = target, Items = items }, cancellationToken).ConfigureAwait(false);
            return items.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            sink.Report(new LogLine(LogLevel.Warning, $"{target.Name}: could not scan for leftovers ({ex.Message})", itemId));
            return 0;
        }
    }
}

/// <summary>
/// Leftover scans waiting for review on this PC. Items that have gone since the scan (an
/// uninstaller finishing late, the user cleaning up by hand) are dropped, and empty scans with them.
/// </summary>
[Operation("leftovers list")]
public sealed class ListLeftovers(LeftoverStore store, ILeftoverScanner scanner) : IOperation<bool, IReadOnlyList<LeftoverScan>>
{
    public async Task<IReadOnlyList<LeftoverScan>> RunAsync(bool request, IOperationSink sink, CancellationToken cancellationToken)
    {
        List<LeftoverScan> current = [];

        foreach (LeftoverScan scan in await store.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            IReadOnlyList<Leftover> present = await scanner.StillPresentAsync(scan.Items, cancellationToken).ConfigureAwait(false);

            if (present.Count == scan.Items.Count)
            {
                current.Add(scan);
                continue;
            }

            try
            {
                if (present.Count == 0)
                {
                    await store.DeleteAsync(scan.Id, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                LeftoverScan pruned = scan with { Items = present };
                await store.SaveAsync(pruned, cancellationToken).ConfigureAwait(false);
                current.Add(pruned);
            }
            catch (DataStoreException)
            {
                // A read-only drive: show the pruned view anyway.
                if (present.Count > 0)
                    current.Add(scan with { Items = present });
            }
        }

        return current;
    }
}

/// <summary>Which leftovers of a scan to quarantine.</summary>
/// <param name="ItemIds">Item ids within the scan.</param>
/// <param name="AllHigh">Also every high-confidence item.</param>
public sealed record QuarantineRequest(string ScanId, IReadOnlyList<string> ItemIds, bool AllHigh = false);

/// <summary>Moves picked leftovers into quarantine (restorable). Nothing is deleted.</summary>
[Operation("leftovers quarantine")]
public sealed class QuarantineLeftovers(LeftoverStore store, IQuarantine quarantine) : IOperation<QuarantineRequest, QuarantineEntry?>
{
    public async Task<QuarantineEntry?> RunAsync(QuarantineRequest request, IOperationSink sink, CancellationToken cancellationToken)
    {
        LeftoverScan scan = await store.GetAsync(request.ScanId, cancellationToken).ConfigureAwait(false);
        string[] unknown = [.. request.ItemIds.Where(id => scan.Items.All(i => i.Id != id))];

        if (unknown.Length > 0)
            throw new ArgumentException($"Scan '{scan.Id}' has no item {string.Join(", ", unknown)}.");

        List<Leftover> picked = [.. scan.Items.Where(i => request.ItemIds.Contains(i.Id) || (request.AllHigh && i.Confidence == Confidence.High))];

        if (picked.Count == 0)
            throw new ArgumentException("Pick leftovers to quarantine (--item <n>, or --high).");

        long bytes = picked.Sum(p => p.SizeBytes ?? 0);
        string detail = string.Join("\n", picked.Select(p => $"  [{p.Confidence}] {p.Kind}: {p.Location}"))
            + string.Create(CultureInfo.InvariantCulture, $"\n\nThey move to Updatr's quarantine on the drive ({bytes / 1048576.0:0.#} MB) and can be restored until you purge them.");

        if (!await sink.ConfirmAsync(new Confirmation($"Quarantine {picked.Count} leftover(s) of {scan.Target.Name}?", detail), cancellationToken).ConfigureAwait(false))
            return null;

        QuarantineEntry entry = await quarantine.MoveAsync(scan.Target.Name, picked, sink, cancellationToken).ConfigureAwait(false);

        // What moved is no longer a leftover; what could not move stays for another try.
        HashSet<string> moved = [.. entry.Items.Where(i => i.Stored).Select(i => $"{i.Kind}|{i.Location}")];
        LeftoverScan rest = scan with { Items = [.. scan.Items.Where(i => !moved.Contains($"{i.Kind}|{i.Location}"))] };

        if (rest.Items.Count == 0)
            await store.DeleteAsync(scan.Id, cancellationToken).ConfigureAwait(false);
        else
            await store.SaveAsync(rest, cancellationToken).ConfigureAwait(false);

        return entry;
    }
}

/// <summary>Drops a scan from review without touching anything on the PC.</summary>
[Operation("leftovers dismiss")]
public sealed class DismissLeftovers(LeftoverStore store) : IOperation<string, bool>
{
    public async Task<bool> RunAsync(string request, IOperationSink sink, CancellationToken cancellationToken)
    {
        LeftoverScan scan = await store.GetAsync(request, cancellationToken).ConfigureAwait(false);
        await store.DeleteAsync(scan.Id, cancellationToken).ConfigureAwait(false);
        return true;
    }
}

[Operation("quarantine list")]
public sealed class ListQuarantine(IQuarantine quarantine) : IOperation<bool, IReadOnlyList<QuarantineEntry>>
{
    public Task<IReadOnlyList<QuarantineEntry>> RunAsync(bool request, IOperationSink sink, CancellationToken cancellationToken)
        => quarantine.ListAsync(cancellationToken);
}

/// <summary>Puts a quarantine entry back where it came from.</summary>
[Operation("quarantine restore")]
public sealed class RestoreQuarantine(IQuarantine quarantine) : IOperation<string, PackageOutcome>
{
    public async Task<PackageOutcome> RunAsync(string request, IOperationSink sink, CancellationToken cancellationToken)
    {
        QuarantineEntry entry = await FindAsync(quarantine, request, cancellationToken).ConfigureAwait(false);

        if (!await sink.ConfirmAsync(new Confirmation($"Restore {entry.Items.Count(i => i.Stored)} item(s) of {entry.App}?",
            "They go back where they were. Files that exist there again are not overwritten."), cancellationToken).ConfigureAwait(false))
            return PackageOutcome.Skipped("Not confirmed");

        return await quarantine.RestoreAsync(entry.Id, sink, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<QuarantineEntry> FindAsync(IQuarantine quarantine, string id, CancellationToken cancellationToken)
        => (await quarantine.ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No quarantine entry '{id}' for this PC (see: quarantine list).");
}

/// <summary>Deletes a quarantine entry for good.</summary>
[Operation("quarantine purge")]
public sealed class PurgeQuarantine(IQuarantine quarantine) : IOperation<string, bool>
{
    public async Task<bool> RunAsync(string request, IOperationSink sink, CancellationToken cancellationToken)
    {
        QuarantineEntry entry = await RestoreQuarantine.FindAsync(quarantine, request, cancellationToken).ConfigureAwait(false);

        if (!await sink.ConfirmAsync(new Confirmation($"Permanently delete the quarantined leftovers of {entry.App}?",
            string.Create(CultureInfo.InvariantCulture, $"Frees {entry.SizeBytes / 1048576.0:0.#} MB on the drive. They cannot be restored after this.")), cancellationToken).ConfigureAwait(false))
            return false;

        await quarantine.PurgeAsync(entry.Id, cancellationToken).ConfigureAwait(false);
        return true;
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(LeftoverScan))]
[JsonSerializable(typeof(QuarantineEntry))]
[JsonSerializable(typeof(List<FootprintItem>))]
internal sealed partial class LeftoverJsonContext : JsonSerializerContext;

/// <summary>Leftover and quarantine records as JSON, for Platform's manifests and footprints.</summary>
public static class LeftoverJson
{
    public static string Serialize(QuarantineEntry entry) => JsonSerializer.Serialize(entry, LeftoverJsonContext.Default.QuarantineEntry);

    public static QuarantineEntry? DeserializeEntry(string json) => JsonSerializer.Deserialize(json, LeftoverJsonContext.Default.QuarantineEntry);

    public static string Serialize(List<FootprintItem> items) => JsonSerializer.Serialize(items, LeftoverJsonContext.Default.ListFootprintItem);

    public static List<FootprintItem>? DeserializeFootprint(string json) => JsonSerializer.Deserialize(json, LeftoverJsonContext.Default.ListFootprintItem);
}
