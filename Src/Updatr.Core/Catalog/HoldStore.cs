using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Updatr.Core.Abstractions;

namespace Updatr.Core.Catalog;

/// <summary>A catalog hold the user turned off: upgrades of matching packages are allowed again.</summary>
public sealed record HoldRelease
{
    public string Source { get; init => field = value ?? "winget"; } = "winget";

    public required string Pattern { get; init; }
}

/// <summary>The user's own holds and releases, kept in <c>Updatr.data\holds.json</c>.</summary>
[Description("Your own hold rules, layered over the catalog's.")]
public sealed record UserHoldsFile
{
    public int SchemaVersion { get; init; } = 1;

    public IReadOnlyList<HoldRule> Holds { get; init => field = value ?? []; } = [];

    public IReadOnlyList<HoldRelease> Releases { get; init => field = value ?? []; } = [];
}

/// <summary>
/// Reads and writes the user's holds and builds the effective <see cref="HoldPolicy"/>. Holding or
/// releasing a single package from the UI or CLI goes through here, so both persist the same way.
/// </summary>
public sealed class HoldStore(IDataStore store)
{
    public const string FileName = "holds.json";

    public async Task<UserHoldsFile> LoadAsync(CancellationToken cancellationToken)
    {
        string? json = await store.ReadTextAsync(FileName, cancellationToken).ConfigureAwait(false);

        if (json is null)
            return new UserHoldsFile();

        try
        {
            return JsonSerializer.Deserialize(json, UserHoldsJsonContext.Default.UserHoldsFile) ?? new UserHoldsFile();
        }
        catch (JsonException)
        {
            // A hand-edit gone wrong must not stop Updatr; the catalog's holds still apply.
            return new UserHoldsFile();
        }
    }

    public Task SaveAsync(UserHoldsFile holds, CancellationToken cancellationToken)
        => store.WriteTextAsync(FileName, JsonSerializer.Serialize(holds, UserHoldsJsonContext.Default.UserHoldsFile), cancellationToken);

    /// <summary>Catalog rules plus the user's, with the user's taking precedence.</summary>
    public static HoldPolicy PolicyFor(IReadOnlyList<HoldRule> catalogRules, UserHoldsFile user)
        => new(catalogRules, user.Holds, user.Releases.Select(r => (r.Source, r.Pattern)));

    /// <summary>
    /// Holds one package exactly (its id as the pattern), removing any release of that same id.
    /// Returns the updated file; the caller saves it.
    /// </summary>
    public static UserHoldsFile Hold(UserHoldsFile file, string source, string packageId, string reason)
    {
        HoldRule rule = new() { Source = source, Pattern = packageId, Group = "Mine", Reason = reason };

        return file with
        {
            Holds = [.. file.Holds.Where(h => !Same(h.Source, h.Pattern, source, packageId)), rule],
            Releases = [.. file.Releases.Where(r => !Same(r.Source, r.Pattern, source, packageId))],
        };
    }

    /// <summary>
    /// Un-holds one package: drops the user's own exact hold, and if a catalog rule still holds it,
    /// records a release for that id. Returns the updated file; the caller saves it.
    /// </summary>
    public static UserHoldsFile Unhold(UserHoldsFile file, IReadOnlyList<HoldRule> catalogRules, string source, string packageId)
    {
        UserHoldsFile without = file with { Holds = [.. file.Holds.Where(h => !Same(h.Source, h.Pattern, source, packageId))] };

        bool catalogStillHolds = new HoldPolicy(catalogRules).Match(source, packageId) is not null;
        bool alreadyReleased = without.Releases.Any(r => Same(r.Source, r.Pattern, source, packageId));

        return catalogStillHolds && !alreadyReleased
            ? without with { Releases = [.. without.Releases, new HoldRelease { Source = source, Pattern = packageId }] }
            : without;
    }

    private static bool Same(string sourceA, string patternA, string sourceB, string patternB)
        => string.Equals(sourceA, sourceB, StringComparison.OrdinalIgnoreCase)
            && string.Equals(patternA, patternB, StringComparison.OrdinalIgnoreCase);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(UserHoldsFile))]
internal sealed partial class UserHoldsJsonContext : JsonSerializerContext;
