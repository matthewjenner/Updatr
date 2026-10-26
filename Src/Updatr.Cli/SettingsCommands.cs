using System.CommandLine;
using System.Text.Json.Nodes;
using Updatr.Core.Settings;

namespace Updatr.Cli;

/// <summary>The user's settings (they live on the drive, beside the exe).</summary>
internal static class SettingsCommands
{
    public static Command Command()
    {
        Command show = new("show", "Show the settings.");
        show.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            Print(sink, await host.SettingsShow.RunAsync(true, sink, ct).ConfigureAwait(false));
            return CliApp.ExitCodes.Ok;
        }));

        Option<int?> updatrHours = new("--updatr-check-hours")
        {
            Description = $"While the window is open, check for a newer Updatr and catalog every N hours (0 = only at start; default {UserSettings.DefaultUpdatrCheckHours}).",
        };
        Option<int?> appHours = new("--app-check-hours")
        {
            Description = $"While the window is open, quietly re-check installed apps for updates every N hours (0 = never; default {UserSettings.DefaultAppCheckHours}).",
        };
        Command set = new("set", "Change settings. Only the options given change.") { updatrHours, appHours };
        set.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            Print(sink, await host.SettingsSet.RunAsync(new SettingsChange(parse.GetValue(updatrHours), parse.GetValue(appHours)), sink, ct).ConfigureAwait(false));
            return CliApp.ExitCodes.Ok;
        }));

        return new Command("settings", "Updatr's own settings.") { show, set };
    }

    private static void Print(ConsoleSink sink, UserSettings settings)
        => sink.Output(
            $"Updatr and catalog check: {Hours(settings.UpdatrCheckEvery, "only at start")}\nInstalled apps check:     {Hours(settings.AppCheckEvery, "never")}",
            new JsonObject { ["updatrCheckHours"] = settings.UpdatrCheckEvery, ["appCheckHours"] = settings.AppCheckEvery });

    private static string Hours(int hours, string whenZero) => hours switch
    {
        0 => whenZero,
        1 => "every hour",
        _ => $"every {hours} hours",
    };
}
