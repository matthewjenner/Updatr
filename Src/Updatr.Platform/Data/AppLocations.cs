namespace Updatr.Platform.Data;

/// <summary>Where Updatr is running from, and where its portable data lives.</summary>
public static class AppLocations
{
    /// <summary>Folder name of the portable data folder beside the exe.</summary>
    public const string DataFolderName = "Updatr.data";

    /// <summary>
    /// The folder holding the running exe - the thumb drive folder in normal use. Uses the process
    /// path, not <see cref="AppContext.BaseDirectory"/>, so it is right for single-file builds too.
    /// </summary>
    public static string ExeDirectory { get; } =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>The default data folder: <c>Updatr.data\</c> beside the exe.</summary>
    public static string DefaultDataRoot => Path.Combine(ExeDirectory, DataFolderName);

    /// <summary>File system of the volume holding <paramref name="path"/> ("NTFS", "FAT32", ...), or null.</summary>
    public static string? FileSystemOf(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).DriveFormat;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
