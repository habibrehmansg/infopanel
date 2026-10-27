using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using InfoPanel.Persistence;
using InfoPanel.Spec;
using Serilog;

namespace InfoPanel.Documents;

/// <summary>
/// Content-addressed storage for media and other files the documents refer to. A blob's id is the SHA-256 of its
/// bytes, <c>sha256:</c> and 64 lower-case hex digits (the formats' <c>blobId</c>); on disk it is
/// <c>blobs/{64 hex}</c>, with no extension (a reader probes the format). A blob is written once, through
/// <see cref="AtomicFile"/>, so a blob file that exists is complete. Nothing is deleted while the engine runs: only
/// <see cref="BlobSweep"/> at start-up removes blobs. One store per data folder; writes are serialized.
/// </summary>
public sealed partial class BlobStore
{
    private static readonly ILogger Logger = Log.ForContext<BlobStore>();

    /// <summary>The prefix of a blob id.</summary>
    public const string IdPrefix = "sha256:";

    private readonly Lock _lock = new();
    private readonly IStorageFaults? _faults;
    private bool _opened, _changed;

    /// <param name="limits">Sets <see cref="MaxBytes"/> (<see cref="SpecLimits.MediaBytes"/>); the defaults when null.</param>
    public BlobStore(DataPaths paths, SpecLimits? limits = null) : this(paths, limits, faults: null)
    {
    }

    internal BlobStore(DataPaths paths, SpecLimits? limits, IStorageFaults? faults)
    {
        Paths = paths;
        MaxBytes = (limits ?? SpecLimits.Default).MediaBytes;
        _faults = faults;
    }

    public DataPaths Paths { get; }

    /// <summary>The largest blob stored: <c>mediaBytes</c>, the largest single file the formats refer to.</summary>
    public int MaxBytes { get; }

    /// <summary>The store's lock, which <see cref="BlobSweep"/> holds for the whole sweep.</summary>
    internal Lock SyncRoot => _lock;

    /// <summary>Whether <see cref="Open"/> ran and nothing has been stored since (or before).</summary>
    internal bool InStartup => _opened && !_changed;

    /// <summary>Whether <paramref name="id"/> is <c>sha256:</c> and exactly 64 lower-case hex digits.</summary>
    public static bool IsValidId(string? id) => id != null && BlobId().IsMatch(id);

    [GeneratedRegex("^sha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex BlobId();

    /// <summary>A blob's file name: exactly 64 lower-case hex digits.</summary>
    [GeneratedRegex("^[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    internal static partial Regex BlobFileName();

    /// <summary>The id of <paramref name="content"/>.</summary>
    public static string IdOf(ReadOnlySpan<byte> content) => IdPrefix + Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>
    /// Start-up only: removes the temp files interrupted writes left in <c>blobs/</c> (only <c>{64 hex}.{32 hex}.tmp</c>,
    /// the names <see cref="AtomicFile"/> gives them).
    /// </summary>
    public void Open()
    {
        lock (_lock)
        {
            _opened = true;
            if (!Directory.Exists(Paths.BlobsDirectory))
                return;
            foreach (var temp in Directory.EnumerateFiles(Paths.BlobsDirectory, "*" + AtomicFile.TempSuffix))
            {
                if (!AtomicFile.IsTempFile(Path.GetFileName(temp), name => BlobFileName().IsMatch(name)))
                    continue;
                try
                {
                    _faults?.Before(StorageStep.DeleteFile, temp);
                    File.Delete(temp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.Warning(ex, "Could not remove {Path}", temp);
                }
            }
        }
    }

    /// <summary>
    /// Stores <paramref name="content"/> and returns its id once the blob's file and its entry in <c>blobs/</c> are on
    /// disk. When the blob is already stored (a file of the same SHA-256) its file is kept and its modification time
    /// renewed, so a blob stored again gets <see cref="BlobSweep"/>'s whole grace period; a file under that name with
    /// other content is replaced.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="content"/> is larger than <see cref="MaxBytes"/>.</exception>
    /// <exception cref="IOException">
    /// The write, or flushing it to disk, failed; the blob is not stored (or stays as it was), and its id is not returned.
    /// </exception>
    public string Put(ReadOnlyMemory<byte> content)
    {
        if (content.Length > MaxBytes)
            throw new ArgumentOutOfRangeException(nameof(content), content.Length, $"A blob holds at most {MaxBytes} bytes.");

        var id = IdOf(content.Span);
        var path = Paths.BlobFile(id);
        lock (_lock)
        {
            _changed = true;
            var existing = new FileInfo(path);
            if (existing.Exists)
            {
                if (existing.Length == content.Length && HashOf(path) == id)
                {
                    _faults?.Before(StorageStep.TouchFile, path);
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);

                    // A Put whose flush of blobs/ failed left the file in place; it is acknowledged only once flushed.
                    AtomicFile.SyncFolder(Paths.BlobsDirectory, _faults);
                    return id;
                }

                Logger.Warning("Blob {BlobId} on disk ({Bytes} bytes) doesn't hash to its name; writing it again", id, existing.Length);
            }

            AtomicFile.Write(path, content, Paths.Root, _faults);
            return id;
        }
    }

    private static string HashOf(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return IdPrefix + Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <exception cref="ArgumentException"><paramref name="id"/> is not a valid blob id.</exception>
    public bool Exists(string id) => File.Exists(Paths.BlobFile(id));

    /// <summary>A blob's size in bytes, or null when it isn't stored.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a valid blob id.</exception>
    public long? Size(string id)
    {
        var file = new FileInfo(Paths.BlobFile(id));
        return file.Exists ? file.Length : null;
    }

    /// <summary>A blob's bytes for reading, or null when it isn't stored. The caller disposes the stream.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a valid blob id.</exception>
    public Stream? OpenRead(string id)
    {
        try
        {
            return new FileStream(Paths.BlobFile(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The ids of the stored blobs (files named by 64 hex digits; anything else in <c>blobs/</c> is ignored).</summary>
    internal IReadOnlyList<string> List()
    {
        if (!Directory.Exists(Paths.BlobsDirectory))
            return [];
        return Directory.EnumerateFiles(Paths.BlobsDirectory)
            .Select(Path.GetFileName)
            .Where(name => BlobFileName().IsMatch(name!))
            .Select(name => IdPrefix + name)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// For <see cref="BlobSweep"/>: deletes the blob when it was last written before <paramref name="cutoff"/>,
    /// checked under the store's lock so a blob stored again meanwhile is kept. Returns whether it was deleted.
    /// </summary>
    internal bool DeleteIfOlder(string id, DateTime cutoff)
    {
        var path = Paths.BlobFile(id);
        lock (_lock)
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.LastWriteTimeUtc >= cutoff)
                return false;
            _faults?.Before(StorageStep.DeleteFile, path);
            file.Delete();
            return true;
        }
    }
}
