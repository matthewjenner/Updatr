namespace Updatr.Core.Tests;

/// <summary>
/// The files a person wrote in this repository, for the convention tests to walk. One list, so
/// adding a file type or folder extends every convention at once.
/// </summary>
internal static class RepositoryFiles
{
    /// <summary>Extensions the conventions apply to - anything a person writes.</summary>
    public static readonly string[] AuthoredExtensions =
        ["*.cs", "*.axaml", "*.md", "*.sh", "*.csproj", "*.props", "*.slnx", "*.yml", "*.json", "*.manifest"];

    /// <summary>Where to look. The repository root itself is walked for the root-level files.</summary>
    public static readonly string[] AuthoredRoots = ["Src", "Tests", "Docs", "Scripts", ".github", "catalog"];

    /// <summary>Every authored file, skipping build output.</summary>
    public static IEnumerable<string> Authored()
    {
        string repository = RepositoryPaths.Root;

        foreach (string extension in AuthoredExtensions)
        {
            foreach (string file in Directory.EnumerateFiles(repository, extension, SearchOption.TopDirectoryOnly))
            {
                yield return file;
            }
        }

        foreach (string root in AuthoredRoots)
        {
            string directory = Path.Combine(repository, root);

            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string extension in AuthoredExtensions)
            {
                foreach (string file in Directory.EnumerateFiles(directory, extension, SearchOption.AllDirectories))
                {
                    if (!Generated(file))
                    {
                        yield return file;
                    }
                }
            }
        }
    }

    /// <summary>Authored files under <c>Src/</c> only - what ships.</summary>
    public static IEnumerable<string> Source()
    {
        string src = RepositoryPaths.Resolve("Src") + Path.DirectorySeparatorChar;
        return Authored().Where(file => file.StartsWith(src, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Generated(string file)
    {
        string separator = Path.DirectorySeparatorChar.ToString();

        return file.Contains(separator + "obj" + separator, StringComparison.Ordinal)
            || file.Contains(separator + "bin" + separator, StringComparison.Ordinal);
    }
}
