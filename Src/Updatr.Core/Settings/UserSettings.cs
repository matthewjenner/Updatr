using System.Text.Json;
using System.Text.Json.Serialization;
using Updatr.Core.Abstractions;
using Updatr.Core.Operations;

namespace Updatr.Core.Settings;

/// <summary>
/// The user's settings, in <c>Updatr.data\settings.json</c> - they travel with the drive. Every
/// field is optional and null means "the default", so a file from an older version (or none)
/// reads as the defaults rather than as zeros.
/// </summary>
public sealed record UserSettings
{
    public const int DefaultUpdatrCheckHours = 1;

    public const int DefaultAppCheckHours = 2;

    /// <summary>Longest interval offered; anything longer is "only when Updatr starts".</summary>
    public const int MaxCheckHours = 24;

    /// <summary>Hours between checks for a newer Updatr and catalog while the GUI is open; 0 = only at start.</summary>
    public int? UpdatrCheckHours { get; init; }

    /// <summary>Hours between quiet re-checks of installed apps for updates while the GUI is open; 0 = never.</summary>
    public int? AppCheckHours { get; init; }

    [JsonIgnore]
    public int UpdatrCheckEvery => UpdatrCheckHours ?? DefaultUpdatrCheckHours;

    [JsonIgnore]
    public int AppCheckEvery => AppCheckHours ?? DefaultAppCheckHours;
}

/// <summary>Reads and writes <see cref="UserSettings"/>.</summary>
public sealed class SettingsStore(IDataStore store)
{
    public const string FileName = "settings.json";

    /// <summary>The saved settings, or the defaults when there are none or the file is unreadable.</summary>
    public async Task<UserSettings> LoadAsync(CancellationToken cancellationToken)
    {
        string? json;

        try
        {
            json = await store.ReadTextAsync(FileName, cancellationToken).ConfigureAwait(false);
        }
        catch (DataStoreException)
        {
            return new UserSettings(); // A yanked or unreadable drive: run on the defaults.
        }

        if (json is null)
            return new UserSettings();

        try
        {
            return JsonSerializer.Deserialize(json, UserSettingsJsonContext.Default.UserSettings) ?? new UserSettings();
        }
        catch (JsonException)
        {
            // A hand-edit gone wrong must not stop Updatr.
            return new UserSettings();
        }
    }

    public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken)
        => store.WriteTextAsync(FileName, JsonSerializer.Serialize(settings, UserSettingsJsonContext.Default.UserSettings), cancellationToken);
}

/// <summary>The current settings.</summary>
[Operation("settings show")]
public sealed class ShowSettings(SettingsStore store) : IOperation<bool, UserSettings>
{
    public Task<UserSettings> RunAsync(bool request, IOperationSink sink, CancellationToken cancellationToken) => store.LoadAsync(cancellationToken);
}

/// <summary>Changes to make; null leaves a setting as it is.</summary>
public sealed record SettingsChange(int? UpdatrCheckHours = null, int? AppCheckHours = null);

/// <summary>Changes settings and saves them. Returns the result.</summary>
[Operation("settings set")]
public sealed class ChangeSettings(SettingsStore store) : IOperation<SettingsChange, UserSettings>
{
    public async Task<UserSettings> RunAsync(SettingsChange request, IOperationSink sink, CancellationToken cancellationToken)
    {
        Validate(request.UpdatrCheckHours, "The Updatr check");
        Validate(request.AppCheckHours, "The installed apps check");

        UserSettings current = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        UserSettings changed = current with
        {
            UpdatrCheckHours = request.UpdatrCheckHours ?? current.UpdatrCheckHours,
            AppCheckHours = request.AppCheckHours ?? current.AppCheckHours,
        };

        await store.SaveAsync(changed, cancellationToken).ConfigureAwait(false);
        return changed;
    }

    private static void Validate(int? hours, string what)
    {
        if (hours is < 0 or > UserSettings.MaxCheckHours)
            throw new ArgumentException($"{what} interval must be 0 to {UserSettings.MaxCheckHours} hours (0 turns it off).");
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UserSettings))]
internal sealed partial class UserSettingsJsonContext : JsonSerializerContext;
