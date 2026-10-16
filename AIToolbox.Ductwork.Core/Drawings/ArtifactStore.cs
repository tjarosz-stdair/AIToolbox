using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AIToolbox.Ductwork.Core.Drawings;

/// <summary>A piece arrived at an offset the server has not reached, or one it has already passed.</summary>
public sealed class UploadOffsetException(long expected, long actual)
    : Exception($"This piece starts at byte {actual:N0} but the upload is at byte {expected:N0}.")
{
    public long Expected { get; } = expected;
}

/// <summary>A piece that does not match the hash the sender gave for it.</summary>
public sealed class ChunkIntegrityException() : Exception("The piece did not match its checksum. Send it again.");

/// <summary>
/// Where drawing bytes live. Two areas: <i>staging</i> for a file still arriving, and <i>final</i> for a stored one.
/// Keys are built by the server from ids and are never a user's file name. This is the abstraction the plan's
/// storage providers (SMB, relay, SharePoint) plug in behind; the first implementation is a local folder.
/// </summary>
public interface IArtifactStore
{
    /// <summary>
    /// Appends a piece at <paramref name="offset"/>. The caller's record of how much was accepted is the
    /// authority: anything beyond it (left by a crash) is cut off first. Returns the bytes written and their SHA-256.
    /// </summary>
    Task<(long Written, byte[] Sha256)> AppendStagingAsync(string stagingKey, long offset, Stream data, long maxBytes, CancellationToken cancellationToken);

    /// <summary>Cuts staging back to <paramref name="length"/> bytes.</summary>
    Task TruncateStagingAsync(string stagingKey, long length, CancellationToken cancellationToken);

    /// <summary>The size, the SHA-256 and the first bytes of a staged file, read in one streaming pass.</summary>
    Task<(long Length, byte[] Sha256, byte[] Head)> InspectStagingAsync(string stagingKey, int headBytes, CancellationToken cancellationToken);

    Task PromoteAsync(string stagingKey, string finalKey, CancellationToken cancellationToken);
    Task DeleteStagingAsync(string stagingKey, CancellationToken cancellationToken);
    Task DeleteAsync(string finalKey, CancellationToken cancellationToken);
    Task<Stream?> OpenReadAsync(string finalKey, CancellationToken cancellationToken);
}

/// <summary>An artifact store on a local or mounted folder. Every path is checked to stay inside the root.</summary>
public sealed partial class FileSystemArtifactStore : IArtifactStore
{
    private readonly string _root;

    public FileSystemArtifactStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("The artifact store needs a folder.", nameof(root));
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(_root);
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9\-]*(/[a-z0-9][a-z0-9\-\.]*)*$")]
    private static partial Regex SafeKey();

    private string StagingPath(string key) => Resolve("staging", key);
    private string FinalPath(string key) => Resolve("final", key);

    private string Resolve(string area, string key)
    {
        if (key is null || key.Length > 200 || !SafeKey().IsMatch(key) || key.Contains(".."))
            throw new ArgumentException("That is not a valid artifact key.", nameof(key));

        var path = Path.GetFullPath(Path.Combine(_root, area, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(_root + area + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("That key points outside the artifact store.", nameof(key));
        return path;
    }

    public async Task<(long Written, byte[] Sha256)> AppendStagingAsync(string stagingKey, long offset, Stream data, long maxBytes, CancellationToken cancellationToken)
    {
        var path = StagingPath(stagingKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // FileShare.None: a second request for the same upload fails here rather than interleaving bytes.
        await using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        if (file.Length < offset) throw new UploadOffsetException(file.Length, offset);
        file.SetLength(offset);
        file.Position = offset;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await data.ReadAsync(buffer, cancellationToken)) > 0)
        {
            written += read;
            if (written > maxBytes)
            {
                file.SetLength(offset);
                throw new ArgumentException($"A piece may not be larger than {maxBytes:N0} bytes.");
            }

            hash.AppendData(buffer, 0, read);
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        await file.FlushAsync(cancellationToken);
        return (written, hash.GetHashAndReset());
    }

    public async Task TruncateStagingAsync(string stagingKey, long length, CancellationToken cancellationToken)
    {
        var path = StagingPath(stagingKey);
        if (!File.Exists(path)) return;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        if (file.Length > length) file.SetLength(length);
    }

    public async Task<(long Length, byte[] Sha256, byte[] Head)> InspectStagingAsync(string stagingKey, int headBytes, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(StagingPath(stagingKey), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var head = new byte[(int)Math.Min(headBytes, file.Length)];
        await file.ReadExactlyAsync(head, cancellationToken);
        file.Position = 0;
        using var hash = SHA256.Create();
        return (file.Length, await hash.ComputeHashAsync(file, cancellationToken), head);
    }

    public Task PromoteAsync(string stagingKey, string finalKey, CancellationToken cancellationToken)
    {
        var target = FinalPath(finalKey);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(StagingPath(stagingKey), target, overwrite: false);
        return Task.CompletedTask;
    }

    public Task DeleteStagingAsync(string stagingKey, CancellationToken cancellationToken)
    {
        var path = StagingPath(stagingKey);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string finalKey, CancellationToken cancellationToken)
    {
        var path = FinalPath(finalKey);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<Stream?> OpenReadAsync(string finalKey, CancellationToken cancellationToken)
    {
        var path = FinalPath(finalKey);
        return Task.FromResult<Stream?>(File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
            : null);
    }
}
