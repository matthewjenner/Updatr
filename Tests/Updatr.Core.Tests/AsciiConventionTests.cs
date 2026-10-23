using System.Globalization;

namespace Updatr.Core.Tests;

/// <summary>
/// Everything written in this repository is plain ASCII - code, UI strings, comments, docs, JSON.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why:</b> em dashes, curly quotes, and the single-character ellipsis read as machine-written
/// text, and they also break in the places Updatr cares about most: console output on a fresh
/// machine with a legacy code page, and winget's own column parsing. Write "-", "...", and
/// straight quotes.
/// </para>
/// <para>
/// A byte-order mark at the start of a file is not content and is allowed - Visual Studio adds
/// one to files it creates, and <c>File.ReadAllLines</c> consumes it during encoding detection.
/// </para>
/// </remarks>
public sealed class AsciiConventionTests
{
    [Fact]
    public void NothingInTheRepositoryUsesNonAsciiCharacters()
    {
        List<string> offenders = [];

        foreach (string file in RepositoryFiles.Authored())
        {
            string[] lines = File.ReadAllLines(file);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                for (int column = 0; column < line.Length; column++)
                {
                    char c = line[column];

                    if (c > 0x7F)
                    {
                        offenders.Add(string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}:{1}:{2}  U+{3:X4}",
                            Path.GetRelativePath(RepositoryPaths.Root, file),
                            i + 1,
                            column + 1,
                            (int)c));
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} non-ASCII character(s). Use '-' for dashes, '...' for ellipses, and "
                + "straight quotes. Where a non-ASCII character is data, write it as an escape "
                + "(\\u2026):\n  "
                + string.Join("\n  ", offenders));
    }
}
