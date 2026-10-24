namespace Updatr.Core.Abstractions;

/// <summary>
/// The portable data folder (<c>Updatr.data\</c> beside the exe). Paths are relative, use
/// <c>/</c>, and never contain a drive letter - the drive letter changes between machines.
/// </summary>
/// <remarks>
/// The folder may live on a thumb drive that is read-only, full, FAT32, or pulled out mid-run.
/// Every member throws <see cref="DataStoreException"/> on an I/O failure (never a raw
/// <see cref="IOException"/>), so callers decide per call whether a failure matters; the store
/// also records it in <see cref="LastFailure"/> for the GUI's banner.
/// </remarks>
public interface IDataStore
{
    /// <summary>Where the folder is, for display only. Never persist it.</summary>
    string DisplayRoot { get; }

    /// <summary>The most recent write/read failure, or null if everything has worked.</summary>
    string? LastFailure { get; }

    /// <summary>Raised (on any thread) when an operation fails. The argument is the message.</summary>
    event Action<string>? Failed;

    /// <summary>Reads a whole text file, or returns null when it does not exist.</summary>
    Task<string?> ReadTextAsync(string relativePath, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a text file atomically: readers see the old content or the new, never a partial
    /// write. Creates missing folders.
    /// </summary>
    Task WriteTextAsync(string relativePath, string content, CancellationToken cancellationToken);

    /// <summary>Appends to a text file, creating it (and its folder) if needed. Not atomic; for logs.</summary>
    Task AppendTextAsync(string relativePath, string content, CancellationToken cancellationToken);

    /// <summary>Deletes a file if it exists.</summary>
    Task DeleteAsync(string relativePath, CancellationToken cancellationToken);

    /// <summary>Relative paths of the files directly in a folder; empty when the folder does not exist.</summary>
    Task<IReadOnlyList<string>> ListAsync(string relativeFolder, CancellationToken cancellationToken);

    /// <summary>
    /// Proves the folder can be written right now (creates and removes a probe file). Returns
    /// null on success, otherwise why not.
    /// </summary>
    Task<string?> ProbeWritableAsync(CancellationToken cancellationToken);
}

/// <summary>An <see cref="IDataStore"/> operation failed. The message is fit to show the user.</summary>
public sealed class DataStoreException(string message, Exception? inner = null) : Exception(message, inner);
