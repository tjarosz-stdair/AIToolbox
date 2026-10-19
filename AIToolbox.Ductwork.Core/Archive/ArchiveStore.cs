using System.Text.RegularExpressions;

namespace AIToolbox.Ductwork.Core.Archive;

/// <summary>
/// Where archive zips live. Kept apart from the drawing store: zips are kept long-term, are never deleted automatically, and
/// can sit on different (cheaper, larger) storage. Keys are built by the server and never come from a user.
/// </summary>
public interface IArchiveStore
{
    /// <summary>Writes a new object. The write is complete or absent: a failure or a crash leaves nothing at <paramref name="key"/>. Refuses to overwrite.</summary>
    Task WriteAsync(string key, Func<Stream, Task> write, CancellationToken cancellationToken);

    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);

    /// <summary>Removes an object. Used for a zip that never became a relied-on archive, never for one that did.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

public sealed partial class FileSystemArchiveStore : IArchiveStore
{
    private readonly string _root;

    public FileSystemArchiveStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("The archive store needs a folder.", nameof(root));
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(_root);
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9\-]*(/[a-z0-9][a-z0-9\-\.]*)*$")]
    private static partial Regex SafeKey();

    public static bool IsValidKey(string? key) => key is { Length: > 0 and <= 200 } && SafeKey().IsMatch(key) && !key.Contains("..");

    private string Resolve(string key)
    {
        if (!IsValidKey(key)) throw new ArgumentException("That is not a valid archive key.", nameof(key));
        var path = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("That key points outside the archive store.", nameof(key));
        return path;
    }

    public async Task WriteAsync(string key, Func<Stream, Task> write, CancellationToken cancellationToken)
    {
        var path = Resolve(key);
        if (File.Exists(path)) throw new IOException("An archive already exists at that location.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var partial = path + ".part";
        try
        {
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await write(file);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }

            File.Move(partial, path, overwrite: false);
        }
        catch
        {
            if (File.Exists(partial)) File.Delete(partial);
            throw;
        }
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        var path = Resolve(key);
        return Task.FromResult<Stream?>(File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true) : null);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) => Task.FromResult(File.Exists(Resolve(key)));

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var path = Resolve(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
