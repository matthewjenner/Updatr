using System.Globalization;
using System.Security;
using Microsoft.Win32;
using Updatr.Core.Catalog;
using Updatr.Core.Debloat;
using Updatr.Core.Operations;
using Updatr.Core.Sources;
using Updatr.Platform.Elevation;
using Hive = Updatr.Core.Catalog.RegistryHive;

namespace Updatr.Platform.Debloat;

/// <summary>
/// Registry values. Current-user values are written here (the signed-in user's hive); machine
/// values and current-user policies need administrator access and go through the broker.
/// </summary>
public sealed class RegistryTarget(ElevationService elevation) : IDebloatTarget
{
    public bool Handles(DebloatAction action) => action is RegistryAction;

    public bool NeedsAdmin(DebloatAction action) => NeedsAdminKey((RegistryAction)action);

    /// <summary>
    /// HKLM (rules may only use HKLM\SOFTWARE) and HKCU\Software\Policies are writable only by
    /// administrators. This is also exactly what the broker accepts.
    /// </summary>
    internal static bool NeedsAdminKey(RegistryAction action) => action.Hive == Hive.LocalMachine
        ? action.Key.StartsWith(@"SOFTWARE\", StringComparison.OrdinalIgnoreCase)
        : action.Key.StartsWith(@"Software\Policies\", StringComparison.OrdinalIgnoreCase);

    public Task<ActionStatus> DetectAsync(DebloatAction action, CancellationToken cancellationToken)
    {
        RegistryAction r = (RegistryAction)action;
        (object? value, RegistryValueKind kind) = Read(r);

        bool applied = r.Delete
            ? value is null
            : r.Dword is { } d ? kind == RegistryValueKind.DWord && value is int i && i == d
            : kind == RegistryValueKind.String && value is string s && s == r.String;

        return Task.FromResult(applied
            ? new ActionStatus(ActionState.Applied)
            : new ActionStatus(ActionState.NotApplied, value is null ? "Not set" : $"Currently {Format(value, kind)}"));
    }

    public Task<ActionBackup> CaptureAsync(DebloatAction action, CancellationToken cancellationToken)
    {
        RegistryAction r = (RegistryAction)action;
        (object? value, RegistryValueKind kind) = Read(r);

        return Task.FromResult(new ActionBackup
        {
            Action = r,
            Existed = value is not null,
            Value = value is null ? null : Format(value, kind),
            ValueKind = value is null ? null : kind.ToString(),
        });
    }

    public Task<PackageOutcome> ApplyAsync(DebloatAction action, string itemId, IOperationSink sink, CancellationToken cancellationToken)
    {
        RegistryAction r = (RegistryAction)action;
        return NeedsAdminKey(r) ? DebloatElevation.ApplyAsync(elevation, r, () => Task.FromResult(ApplyLocal(r))) : Task.FromResult(ApplyLocal(r));
    }

    public Task<PackageOutcome> RevertAsync(ActionBackup backup, string itemId, IOperationSink sink, CancellationToken cancellationToken)
    {
        RegistryAction r = (RegistryAction)backup.Action;
        return NeedsAdminKey(r) ? DebloatElevation.RevertAsync(elevation, backup, () => Task.FromResult(RevertLocal(r, backup))) : Task.FromResult(RevertLocal(r, backup));
    }

    internal static PackageOutcome ApplyLocal(RegistryAction r) => Write(r, () =>
    {
        if (r.Delete)
        {
            using RegistryKey? existing = Open(r.Hive, r.Key, writable: true);
            existing?.DeleteValue(r.Name, throwOnMissingValue: false);
            return;
        }

        using RegistryKey key = Create(r.Hive, r.Key);

        if (r.Dword is { } d)
            key.SetValue(r.Name, d, RegistryValueKind.DWord);
        else
            key.SetValue(r.Name, r.String!, RegistryValueKind.String);
    });

    internal static PackageOutcome RevertLocal(RegistryAction r, ActionBackup backup) => Write(r, () =>
    {
        if (!backup.Existed)
        {
            using RegistryKey? existing = Open(r.Hive, r.Key, writable: true);
            existing?.DeleteValue(r.Name, throwOnMissingValue: false);
            return;
        }

        RegistryValueKind kind = Enum.Parse<RegistryValueKind>(backup.ValueKind ?? nameof(RegistryValueKind.String));
        using RegistryKey key = Create(r.Hive, r.Key);
        key.SetValue(r.Name, Parse(backup.Value ?? string.Empty, kind), kind);
    });

    private static PackageOutcome Write(RegistryAction r, Action write)
    {
        try
        {
            write();
            return PackageOutcome.Ok();
        }
        catch (UnauthorizedAccessException) when (Windows.WindowsInfo.IsElevated)
        {
            // Administrators may write here by the ACL, so a refusal comes from Windows itself -
            // the User Choice Protection driver guards a few settings (Widgets, default apps).
            return PackageOutcome.Failed($"Windows blocks changes to {r.Name}, even for administrators (it protects this setting)");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return PackageOutcome.Failed($"Could not write {r.Name}: {ex.Message}");
        }
    }

    private static (object? Value, RegistryValueKind Kind) Read(RegistryAction r)
    {
        using RegistryKey? key = Open(r.Hive, r.Key, writable: false);
        object? value = key?.GetValue(r.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return (value, value is null ? RegistryValueKind.Unknown : key!.GetValueKind(r.Name));
    }

    private static RegistryKey? Open(Hive hive, string key, bool writable)
    {
        using RegistryKey root = Base(hive);
        return root.OpenSubKey(key, writable);
    }

    private static RegistryKey Create(Hive hive, string key)
    {
        using RegistryKey root = Base(hive);
        return root.CreateSubKey(key, writable: true);
    }

    private static RegistryKey Base(Hive hive) => RegistryKey.OpenBaseKey(
        hive == Hive.LocalMachine ? Microsoft.Win32.RegistryHive.LocalMachine : Microsoft.Win32.RegistryHive.CurrentUser, RegistryView.Registry64);

    internal static string Format(object value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => ((int)value).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => ((long)value).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.MultiString => string.Join("\n", (string[])value),
        RegistryValueKind.Binary => Convert.ToHexString((byte[])value),
        _ => value.ToString() ?? string.Empty,
    };

    internal static object Parse(string text, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => int.Parse(text, CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => long.Parse(text, CultureInfo.InvariantCulture),
        RegistryValueKind.MultiString => text.Split('\n'),
        RegistryValueKind.Binary => Convert.FromHexString(text),
        _ => text,
    };
}
