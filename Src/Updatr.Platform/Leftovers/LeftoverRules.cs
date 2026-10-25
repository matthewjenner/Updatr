using System.Text;
using System.Text.RegularExpressions;
using Updatr.Core.Leftovers;

namespace Updatr.Platform.Leftovers;

/// <summary>
/// What may be offered as a leftover, and how sure it is. The scanner enumerates; these rules
/// decide. Everything here errs toward not listing something: a missed leftover costs a few MB,
/// a wrong one costs another app.
/// </summary>
public static partial class LeftoverRules
{
    /// <summary>
    /// Names never offered as a leftover folder or key, whatever the app is called: system and
    /// shared locations, and words too general to mean one app.
    /// </summary>
    private static readonly HashSet<string> Denied = new(StringComparer.Ordinal)
    {
        "microsoft", "windows", "commonfiles", "microsoftshared", "packagecache", "programs", "temp", "tmp",
        "cache", "caches", "packages", "windowsapps", "installer", "installshield", "installshieldinstallationinformation",
        "dotnet", "net", "nuget", "classes", "policies", "clients", "registeredapplications", "wow6432node",
        "software", "system", "default", "data", "config", "settings", "logs", "log", "users", "public", "app",
        "apps", "application", "applications", "update", "updates", "updater", "crashdumps", "connecteddevicesplatform",
        "comms", "d3dscache", "fontcache", "history", "inetcache", "virtualstore", "uninstall", "msbuild", "referenceassemblies",
        "modifiablewindowsapps", "windowspowershell", "powershell", "wsl", "google", "mozilla", "adobe", "intel", "nvidia",
        "nvidiacorporation", "amd", "realtek", "apple", "steam", "epicgames", "battlenet", "blizzardentertainment", "origin",
        "ea", "electronicarts", "ubisoft", "valve", "jetbrains", "oracle", "java", "python", "node", "nodejs", "npm",
    };

    /// <summary>Publisher suffixes that do not identify anyone.</summary>
    [GeneratedRegex(@"\b(inc|incorporated|llc|ltd|limited|corp|corporation|co|company|gmbh|ag|sa|srl|bv|plc|pty|software|technologies|technology|team)\b\.?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PublisherNoise();

    /// <summary>Versions, architectures, and editions in display names: "1.4.1.1032", "(x64)", "64-bit", "en-US".</summary>
    [GeneratedRegex(@"\(.*?\)|\[.*?\]|\bv?\d+(\.\d+)+\b|\b(x64|x86|arm64|amd64|win64|win32|64-bit|32-bit|64bit|32bit)\b|\b[a-z]{2}-[A-Z]{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex NameNoise();

    /// <summary>Lowercase letters and digits only: "Everything 1.4.1.1032 (x64)" -> "everything".</summary>
    public static string Key(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string cleaned = NameNoise().Replace(text, " ");
        StringBuilder key = new(cleaned.Length);

        foreach (char c in cleaned.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
                key.Append(c);
        }

        return key.ToString();
    }

    /// <summary>The publisher as it tends to appear in folder names: "Mozilla Corporation" -> "mozilla".</summary>
    public static string PublisherKey(string? publisher) => Key(PublisherNoise().Replace(publisher ?? string.Empty, " "));

    /// <summary>
    /// Names the app's folders and keys are likely to have: its full name, and its name without a
    /// leading publisher word ("Mozilla Firefox" -> "firefox", as in AppData\Mozilla\Firefox).
    /// </summary>
    public static IReadOnlyList<string> AppKeys(LeftoverTarget target)
    {
        List<string> keys = [];
        string full = Key(target.Name);
        string publisher = PublisherKey(target.Publisher);

        if (Usable(full))
            keys.Add(full);

        if (publisher.Length > 0 && full.StartsWith(publisher, StringComparison.Ordinal) && Usable(full[publisher.Length..]))
            keys.Add(full[publisher.Length..]);

        return keys;
    }

    /// <summary>Long enough to mean something and not a shared or system name.</summary>
    public static bool Usable(string key) => key.Length >= 3 && !Denied.Contains(key);

    /// <summary>How well a folder or key name matches the app, or null for no match.</summary>
    public static Confidence? MatchName(string name, IReadOnlyList<string> appKeys)
    {
        string key = Key(name);

        if (!Usable(key))
            return null;

        if (appKeys.Contains(key))
            return Confidence.Medium;

        // "Everything Search" for "Everything": only for distinctive names, to keep "Data" out.
        return appKeys.Any(a => a.Length >= 6 && (key.StartsWith(a, StringComparison.Ordinal) || a.StartsWith(key, StringComparison.Ordinal)) && key.Length >= 6)
            ? Confidence.Low
            : null;
    }

    /// <summary>
    /// Whether <paramref name="path"/> may be offered at all: under one of the allowed roots, at
    /// most two levels down, not a root itself, and neither inside nor around anything protected
    /// (other apps' install folders, Updatr itself, the data folder, Windows).
    /// </summary>
    public static bool IsOfferableFolder(string path, IReadOnlyList<string> roots, IReadOnlyCollection<string> protectedPaths)
    {
        string full = Normalize(path);
        string? root = roots.Select(Normalize).Where(r => IsUnder(full, r)).OrderByDescending(r => r.Length).FirstOrDefault();

        if (root is null || full.Length == root.Length)
            return false;

        int depth = full[(root.Length + 1)..].Count(c => c == '\\') + 1;

        if (depth > 2)
            return false;

        return !protectedPaths.Select(Normalize).Any(p => p.Length > 0 && (IsUnder(p, full) || IsUnder(full, p)));
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    public static bool IsUnder(string? path, string? folder)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder))
            return false;

        string p = Normalize(path);
        string f = Normalize(folder);
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Full path without a trailing separator; quotes and arguments stripped from command lines.</summary>
    public static string Normalize(string path)
    {
        string trimmed = path.Trim().Trim('"');

        try
        {
            return Path.GetFullPath(trimmed).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return trimmed.TrimEnd('\\');
        }
    }

    /// <summary>The executable path at the start of a command line ("\"C:\a b\x.exe\" /S" -> C:\a b\x.exe).</summary>
    public static string? ExecutableOf(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;

        string text = Environment.ExpandEnvironmentVariables(commandLine.Trim());

        if (text.StartsWith('"'))
        {
            int end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? text[..(exe + 4)] : text.Split(' ')[0];
    }
}
