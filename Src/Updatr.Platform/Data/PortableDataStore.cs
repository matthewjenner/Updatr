using System.Text;
using Updatr.Core.Abstractions;

namespace Updatr.Platform.Data;

/// <summary>
/// <see cref="IDataStore"/> over a real folder - normally <c>Updatr.data\</c> on the thumb drive.
/// </summary>
/// <remarks>
/// Writes go to a temp file in the same folder, are flushed through to the device (a USB stick
/// pulled a second later must not hold a half-written cart), then replace the target. Nothing here
/// assumes NTFS: no ACLs, no alternate data streams, plain rename.
/// </remarks>
public sealed class PortableDataStore : IDataStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _root;

    public PortableDataStore(string root)
    {
        _root = Path.GetFullPath(root);
    }

    public string DisplayRoot => _root;

    public string? LastFailure { get; private set; }

    public event Action<string>? Failed;

    public Task<string?> ReadTextAsync(string relativePath, CancellationToken cancellationToken)
        => GuardAsync($"read {relativePath}", async () =>
        {
            string path = Resolve(relativePath);
            return File.Exists(path) ? await File.ReadAllTextAsync(path, Utf8NoBom, cancellationToken).ConfigureAwait(false) : null;
        });

    public Task WriteTextAsync(string relativePath, string content, CancellationToken cancellationToken)
        => GuardAsync($"save {relativePath}", async () =>
        {
            string path = Resolve(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = $"{path}.{Guid.NewGuid():N}.tmp";

            try
            {
                await using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
                {
                    byte[] bytes = Utf8NoBom.GetBytes(content);
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }

            return true;
        });

    public Task AppendTextAsync(string relativePath, string content, CancellationToken cancellationToken)
        => GuardAsync($"append to {relativePath}", async () =>
        {
            string path = Resolve(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.AppendAllTextAsync(path, content, Utf8NoBom, cancellationToken).ConfigureAwait(false);
            return true;
        });

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken)
        => GuardAsync($"delete {relativePath}", () =>
        {
            // File.Delete tolerates a missing file but not a missing folder; both mean "already gone".
            string path = Resolve(relativePath);

            if (File.Exists(path))
                File.Delete(path);

            return Task.FromResult(true);
        });

    public Task<IReadOnlyList<string>> ListAsync(string relativeFolder, CancellationToken cancellationToken)
        => GuardAsync($"list {relativeFolder}", () =>
        {
            string folder = Resolve(relativeFolder);

            IReadOnlyList<string> files = Directory.Exists(folder)
                ? [.. Directory.EnumerateFiles(folder).Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/')).Order(StringComparer.OrdinalIgnoreCase)]
                : [];

            return Task.FromResult(files);
        });

    public async Task<string?> ProbeWritableAsync(CancellationToken cancellationToken)
    {
        string probe = $".probe-{Guid.NewGuid():N}";

        try
        {
            await WriteTextAsync(probe, "probe", cancellationToken).ConfigureAwait(false);
            await DeleteAsync(probe, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (DataStoreException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Maps a relative path into the root. Rejects anything that could escape it - an absolute
    /// path, a drive letter, or <c>..</c> - because names may come from user-editable files.
    /// </summary>
    internal string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains(':', StringComparison.Ordinal)
            || relativePath.Split('/', '\\').Any(segment => segment == ".."))
        {
            throw new ArgumentException($"'{relativePath}' is not a relative path inside the data folder.", nameof(relativePath));
        }

        string full = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{relativePath}' resolves outside the data folder.", nameof(relativePath));

        return full;
    }

    private async Task<T> GuardAsync<T>(string what, Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string message = $"Could not {what} in {_root}: {ex.Message}";
            LastFailure = message;
            Failed?.Invoke(message);
            throw new DataStoreException(message, ex);
        }
    }
}
