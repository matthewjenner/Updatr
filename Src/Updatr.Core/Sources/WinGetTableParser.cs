namespace Updatr.Core.Sources;

/// <summary>
/// Parses the table <c>winget upgrade</c> prints. Only for the fallback when the WinGet COM API is
/// unavailable - structured data is always preferred (Docs/legacy-script.md).
/// </summary>
/// <remarks>
/// Columns are located by the header's text positions, not by splitting on whitespace, so names
/// and ids containing spaces survive. The header words are English; on another display language
/// the header is not found and the parser returns nothing (the caller reports that).
/// </remarks>
public static class WinGetTableParser
{
    /// <summary>The ellipsis (U+2026) winget uses to truncate a long cell. Numeric so no tool can turn it literal.</summary>
    public const char Ellipsis = (char)0x2026;

    /// <summary>One parsed row.</summary>
    /// <param name="IdTruncated">winget cut the id short; it cannot be used with <c>--exact</c>.</param>
    public sealed record Row(string Name, string Id, string Version, string Available, string Source, bool IdTruncated);

    /// <summary>The rows, or null when no header was found (unexpected output or a non-English UI).</summary>
    public static IReadOnlyList<Row>? Parse(string output)
    {
        string[] lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int header = Array.FindIndex(lines, IsHeader);

        if (header < 0)
            return null;

        string head = lines[header];
        int colId = head.IndexOf("Id", StringComparison.Ordinal);
        int colVersion = head.IndexOf("Version", StringComparison.Ordinal);
        int colAvailable = head.IndexOf("Available", StringComparison.Ordinal);
        int colSource = head.IndexOf("Source", StringComparison.Ordinal);
        List<Row> rows = [];

        // header + 1 is the dashes rule.
        for (int i = header + 2; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();

            if (string.IsNullOrWhiteSpace(line) || line.Length < colVersion)
                continue;

            // Footers: "5 upgrades available.", "2 package(s) have version numbers that cannot be determined..."
            if (line.Contains("upgrades available", StringComparison.OrdinalIgnoreCase)
                || line.Contains("upgrade available", StringComparison.OrdinalIgnoreCase)
                || line.Contains("package(s) have", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string id = Slice(line, colId, colVersion);

            if (id.Length == 0)
                continue;

            rows.Add(new Row(
                Name: Slice(line, 0, colId),
                Id: id,
                Version: Slice(line, colVersion, colAvailable >= 0 ? colAvailable : line.Length),
                Available: colAvailable >= 0 ? Slice(line, colAvailable, colSource >= 0 ? colSource : line.Length) : string.Empty,
                Source: colSource >= 0 ? Slice(line, colSource, line.Length) : string.Empty,
                IdTruncated: id.EndsWith(Ellipsis)));
        }

        return rows;
    }

    private static bool IsHeader(string line)
        => line.Contains("Name", StringComparison.Ordinal)
            && line.Contains(" Id ", StringComparison.Ordinal)
            && line.Contains(" Version", StringComparison.Ordinal);

    private static string Slice(string line, int start, int end)
    {
        if (start >= line.Length)
            return string.Empty;

        end = Math.Clamp(end, start, line.Length);
        return line[start..end].Trim();
    }
}
