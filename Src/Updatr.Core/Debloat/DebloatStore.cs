using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Updatr.Core.Abstractions;
using Updatr.Core.Catalog;
using Updatr.Core.Sources;

namespace Updatr.Core.Debloat;

/// <summary>
/// What one action found before it changed anything. Stored in the backup file before the change
/// is made, with a copy of the action itself, so a revert works even after the catalog changes.
/// </summary>
public sealed record ActionBackup
{
    public required DebloatAction Action { get; init; }

    /// <summary>Registry: the value existed. Service / task: it existed.</summary>
    public bool Existed { get; init; }

    /// <summary>
    /// Registry: the prior value as text (numbers in decimal, binary as hex, multi-string joined
    /// with newlines). Service: the start type number. Task: "enabled" or "disabled".
    /// </summary>
    public string? Value { get; init; }

    /// <summary>Registry value kind (DWord, String, ...), or "delayed" for a delayed-start service.</summary>
    public string? ValueKind { get; init; }

    /// <summary>Appx: full names of the packages that were there.</summary>
    public IReadOnlyList<string> Packages { get; init => field = value ?? []; } = [];
}

/// <summary>One item's backups within a run.</summary>
public sealed record ItemBackup
{
    public required string ItemId { get; init; }

    public required string Title { get; init; }

    public IReadOnlyList<ActionBackup> Actions { get; init => field = value ?? []; } = [];
}

/// <summary>Everything one apply run backed up: <c>debloat/backups/&lt;timestamp&gt;.json</c>.</summary>
public sealed record BackupFile
{
    public required DateTimeOffset At { get; init; }

    public IReadOnlyList<ItemBackup> Items { get; init => field = value ?? []; } = [];
}

/// <summary>A line in the debloat history.</summary>
/// <param name="Kind">"apply" or "revert".</param>
/// <param name="Backup">The backup file an apply wrote (relative to the data folder).</param>
public sealed record HistoryEntry(DateTimeOffset At, string Kind, string ItemId, string Title, OutcomeKind Outcome, string Detail, string? Backup);

internal sealed record HistoryFile
{
    public IReadOnlyList<HistoryEntry> Entries { get; init => field = value ?? []; } = [];
}

internal sealed record KeepFile
{
    public IReadOnlyList<string> Apps { get; init => field = value ?? []; } = [];
}

/// <summary>The state after the last run, which drives "new since last run".</summary>
public sealed record DebloatSnapshot
{
    public required DateTimeOffset At { get; init; }

    /// <summary>Item ids that were applied after the run.</summary>
    public IReadOnlyList<string> Applied { get; init => field = value ?? []; } = [];

    /// <summary>Store app names present after the run.</summary>
    public IReadOnlyList<string> Seen { get; init => field = value ?? []; } = [];
}

/// <summary>Debloat history, backups, keep list, and the last-run snapshot in the data folder.</summary>
public sealed class DebloatStore(IDataStore store)
{
    public const string Folder = "debloat";
    public const string BackupFolder = "debloat/backups";
    private const string HistoryPath = "debloat/history.json";
    private const string KeepPath = "debloat/keep-list.json";
    private const string SnapshotPath = "debloat/last-run.json";

    /// <summary>History is capped; the backups it points at are small and kept.</summary>
    public const int HistoryLimit = 1000;

    public static string BackupPathFor(DateTimeOffset at)
        => string.Create(CultureInfo.InvariantCulture, $"{BackupFolder}/{at.ToUniversalTime():yyyyMMdd-HHmmss}.json");

    public async Task<IReadOnlyList<HistoryEntry>> LoadHistoryAsync(CancellationToken cancellationToken)
        => (await ReadAsync(HistoryPath, DebloatJsonContext.Default.HistoryFile, cancellationToken).ConfigureAwait(false))?.Entries ?? [];

    public async Task AppendHistoryAsync(IReadOnlyList<HistoryEntry> entries, CancellationToken cancellationToken)
    {
        IReadOnlyList<HistoryEntry> existing = await LoadHistoryAsync(cancellationToken).ConfigureAwait(false);
        HistoryFile file = new() { Entries = [.. existing.Concat(entries).TakeLast(HistoryLimit)] };
        await store.WriteTextAsync(HistoryPath, JsonSerializer.Serialize(file, DebloatJsonContext.Default.HistoryFile), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the backup and a <c>.reg</c> of its registry values beside it (for hand restores).</summary>
    public async Task SaveBackupAsync(string path, BackupFile backup, CancellationToken cancellationToken)
    {
        await store.WriteTextAsync(path, JsonSerializer.Serialize(backup, DebloatJsonContext.Default.BackupFile), cancellationToken).ConfigureAwait(false);
        await store.WriteTextAsync(path[..path.LastIndexOf('.')] + ".reg", RegExport.From(backup), cancellationToken).ConfigureAwait(false);
    }

    public Task<BackupFile?> LoadBackupAsync(string path, CancellationToken cancellationToken)
        => ReadAsync(path, DebloatJsonContext.Default.BackupFile, cancellationToken);

    public async Task<IReadOnlyList<string>> LoadKeepAsync(CancellationToken cancellationToken)
        => (await ReadAsync(KeepPath, DebloatJsonContext.Default.KeepFile, cancellationToken).ConfigureAwait(false))?.Apps ?? [];

    public Task SaveKeepAsync(IReadOnlyList<string> apps, CancellationToken cancellationToken)
        => store.WriteTextAsync(KeepPath, JsonSerializer.Serialize(new KeepFile { Apps = apps }, DebloatJsonContext.Default.KeepFile), cancellationToken);

    public Task<DebloatSnapshot?> LoadSnapshotAsync(CancellationToken cancellationToken)
        => ReadAsync(SnapshotPath, DebloatJsonContext.Default.DebloatSnapshot, cancellationToken);

    public Task SaveSnapshotAsync(DebloatSnapshot snapshot, CancellationToken cancellationToken)
        => store.WriteTextAsync(SnapshotPath, JsonSerializer.Serialize(snapshot, DebloatJsonContext.Default.DebloatSnapshot), cancellationToken);

    private async Task<T?> ReadAsync<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken cancellationToken) where T : class
    {
        string? json = await store.ReadTextAsync(path, cancellationToken).ConfigureAwait(false);

        if (json is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize(json, type);
        }
        catch (JsonException ex)
        {
            throw new DataStoreException($"{path} is damaged ({ex.Message}).", ex);
        }
    }
}

/// <summary>Serialization of debloat actions and backups - also the broker's wire format for them.</summary>
public static class DebloatJson
{
    public static string Serialize(DebloatAction action) => JsonSerializer.Serialize(action, DebloatJsonContext.Default.DebloatAction);

    public static DebloatAction? DeserializeAction(string json) => JsonSerializer.Deserialize(json, DebloatJsonContext.Default.DebloatAction);

    public static string Serialize(ActionBackup backup) => JsonSerializer.Serialize(backup, DebloatJsonContext.Default.ActionBackup);

    public static ActionBackup? DeserializeBackup(string json) => JsonSerializer.Deserialize(json, DebloatJsonContext.Default.ActionBackup);
}

/// <summary>A <c>.reg</c> file that puts back the registry values a backup recorded.</summary>
public static class RegExport
{
    public static string From(BackupFile backup)
    {
        StringBuilder text = new();
        text.Append("Windows Registry Editor Version 5.00\r\n\r\n");
        text.Append(CultureInfo.InvariantCulture, $"; Updatr debloat backup from {backup.At:yyyy-MM-dd HH:mm:ss zzz}. Restores the values as they were before.\r\n");

        foreach (ItemBackup item in backup.Items)
        {
            foreach (ActionBackup action in item.Actions)
            {
                if (action.Action is not RegistryAction registry)
                    continue;

                string root = registry.Hive == RegistryHive.LocalMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";
                text.Append(CultureInfo.InvariantCulture, $"\r\n; {item.Title}\r\n[{root}\\{registry.Key}]\r\n");
                text.Append(Line(registry.Name, action)).Append("\r\n");
            }
        }

        return text.ToString();
    }

    private static string Line(string name, ActionBackup backup)
    {
        string quoted = Quote(name);

        if (!backup.Existed)
            return $"{quoted}=-";

        return backup.ValueKind switch
        {
            "DWord" when int.TryParse(backup.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d)
                => string.Create(CultureInfo.InvariantCulture, $"{quoted}=dword:{unchecked((uint)d):x8}"),
            "String" => $"{quoted}={Quote(backup.Value ?? string.Empty)}",
            _ => $"; {quoted} was a {backup.ValueKind} value; it is in the .json backup beside this file",
        };
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(BackupFile))]
[JsonSerializable(typeof(HistoryFile))]
[JsonSerializable(typeof(KeepFile))]
[JsonSerializable(typeof(DebloatSnapshot))]
[JsonSerializable(typeof(DebloatAction))]
[JsonSerializable(typeof(ActionBackup))]
internal sealed partial class DebloatJsonContext : JsonSerializerContext;
