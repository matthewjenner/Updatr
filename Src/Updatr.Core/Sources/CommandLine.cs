using System.Text;

namespace Updatr.Core.Sources;

/// <summary>Splits a catalog's <c>silentArgs</c> string into arguments, the way Windows programs parse their command line.</summary>
public static class CommandLine
{
    /// <summary>
    /// Whitespace separates arguments; double quotes group (and are removed); <c>\"</c> is a
    /// literal quote. Enough for installer switches like <c>/VERYSILENT /DIR="C:\Program Files\X"</c>.
    /// </summary>
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        List<string> args = [];

        if (string.IsNullOrWhiteSpace(commandLine))
            return args;

        StringBuilder current = new();
        bool inQuotes = false;
        bool any = false;

        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];

            if (c == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
            {
                current.Append('"');
                any = true;
                i++;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (any)
                    args.Add(current.ToString());

                current.Clear();
                any = false;
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }

        if (any)
            args.Add(current.ToString());

        return args;
    }
}
