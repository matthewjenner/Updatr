namespace Updatr.Core.Tests;

/// <summary>
/// Locates repository files from the test output directory, so tests can assert against the real
/// checked-in sources rather than against copies that could drift out of step with them.
/// </summary>
internal static class RepositoryPaths
{
    private const string SolutionFileName = "Updatr.slnx";

    /// <summary>The repository root.</summary>
    /// <exception cref="InvalidOperationException">No ancestor directory contains the solution file.</exception>
    public static string Root { get; } = FindRoot();

    /// <summary>Resolves a repository-relative path.</summary>
    /// <param name="relativePath">Path relative to the repository root.</param>
    /// <returns>The absolute path.</returns>
    public static string Resolve(string relativePath) => Path.GetFullPath(Path.Combine(Root, relativePath));

    private static string FindRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, SolutionFileName)))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root above '{AppContext.BaseDirectory}'.");
    }
}
