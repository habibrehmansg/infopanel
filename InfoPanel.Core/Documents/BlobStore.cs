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
/// <para>
/// Reads check content: <see cref="ReadVerified"/>, <see cref="OpenVerified"/> and <see cref="StoredSize"/> hash a
/// blob's file the first time they meet it in a run, and remember the verdict for that file's length and last-write
/// time, both read from the open handle; a file whose length or time changed is hashed again. At most
/// <see cref="VerdictCapacity"/> verdicts are kept, the least recently used dropped first, so its file is hashed again
/// on its next read. A file that doesn't hash to its id counts as not stored (logged each time it is hashed), and the
/// next <see cref="Put(ReadOnlyMemory{byte})"/> of the right bytes replaces it. The verdicts live in memory only, so a
/// file damaged after its first read in a run, with its length and time unchanged, is caught on the next start.
/// <see cref="Put(ReadOnlyMemory{byte})"/> remembers the verdict for a stored blob it compared with the content; a blob
/// it writes is left without one, hashed on its first read. <see cref="ReadHashed"/>, for bytes that leave the store (an export), hashes on every call.
/// </para>
/// </summary>
public sealed partial class BlobStore
{
    private static readonly ILogger Logger = Log.ForContext<BlobStore>();

    /// <summary>The prefix of a blob id.</summary>
    public const string IdPrefix = "sha256:";

    private readonly Lock _lock = new();
    private readonly IStorageFaults? _faults;
    private bool _opened, _changed;
    private readonly VerdictCache _verdicts;
    private long _hashes;

    /// <summary>What a read found a blob's file to hold: its length and last-write time, and whether it hashed to its id.</summary>
    private readonly record struct Verdict(long Length, DateTime LastWriteUtc, bool Good);

    /// <summary>
    /// The verdicts by blob id, at most a fixed number: storing one more drops the least recently used, whose file is
    /// hashed again on its next read. Thread-safe.
    /// </summary>
    private sealed class VerdictCache(int capacity)
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, LinkedListNode<(string Id, Verdict Verdict)>> _entries = new(StringComparer.Ordinal);

        /// <summary>Most recently used first.</summary>
        private readonly LinkedList<(string Id, Verdict Verdict)> _order = new();

        public int Count
        {
            get
            {
                lock (_lock)
                    return _entries.Count;
            }
        }

        public bool TryGetValue(string id, out Verdict verdict)
        {
            lock (_lock)
            {
                if (!_entries.TryGetValue(id, out var node))
                {
                    verdict = default;
                    return false;
                }
                _order.Remove(node);
                _order.AddFirst(node);
                verdict = node.Value.Verdict;
                return true;
            }
        }

        public void Set(string id, Verdict verdict)
        {
            lock (_lock)
            {
                if (_entries.TryGetValue(id, out var node))
                {
                    node.Value = (id, verdict);
                    _order.Remove(node);
                    _order.AddFirst(node);
                    return;
                }
                if (_entries.Count >= capacity)
                {
                    _entries.Remove(_order.Last!.Value.Id);
                    _order.RemoveLast();
                }
                _entries[id] = _order.AddFirst((id, verdict));
            }
        }

        public void Remove(string id)
        {
            lock (_lock)
            {
                if (_entries.Remove(id, out var node))
                    _order.Remove(node);
            }
        }
    }

    /// <param name="limits">Sets <see cref="MaxBytes"/> (<see cref="SpecLimits.VideoBytes"/>); the defaults when null.</param>
    public BlobStore(DataPaths paths, SpecLimits? limits = null) : this(paths, limits, faults: null)
    {
    }

    internal BlobStore(DataPaths paths, SpecLimits? limits, IStorageFaults? faults, int verdictCapacity = VerdictCapacity)
    {
        Paths = paths;
        MaxBytes = (limits ?? SpecLimits.Default).VideoBytes;
        MediaBytes = (limits ?? SpecLimits.Default).MediaBytes;
        _faults = faults;
        _verdicts = new VerdictCache(verdictCapacity);
    }

    public DataPaths Paths { get; }

    /// <summary>The largest blob stored: <c>videoBytes</c>, the largest single file the formats refer to.</summary>
    public int MaxBytes { get; }

    /// <summary><c>mediaBytes</c>: the largest picture a reader decodes, which <see cref="BlobMediaStore"/> reads whole.</summary>
    public int MediaBytes { get; }

    /// <summary>The name a streamed upload is written under in <c>blobs/</c> until its hash is known: <c>upload.{32 hex}.tmp</c>.</summary>
    internal const string UploadName = "upload";

    /// <summary>What <see cref="PutAsync"/> stored: the blob's id, its length, and whether this call wrote it.</summary>
    public readonly record struct StreamedBlob(string Id, long Length, bool Created);

    /// <summary>The store's lock, which <see cref="BlobSweep"/> holds for the whole sweep.</summary>
    internal Lock SyncRoot => _lock;

    /// <summary>Tests: called on a thread whose <see cref="Put(ReadOnlyMemory{byte}, out bool)"/> finds the store's lock held by another, before it waits.</summary>
    internal Action? WaitingForLock;

    /// <summary>Tests: called by <see cref="Put(ReadOnlyMemory{byte}, out bool)"/> once it found a stored blob's file to hold the content, before it reads the file's length and time again.</summary>
    internal Action? Compared;

    /// <summary>Tests: how many times a read hashed a blob's file (a cached verdict doesn't).</summary>
    internal long Hashes => Interlocked.Read(ref _hashes);

    /// <summary>How many blob files' verdicts are remembered at most.</summary>
    internal const int VerdictCapacity = 4096;

    /// <summary>Tests: how many verdicts are remembered.</summary>
    internal int VerdictCount => _verdicts.Count;

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
    /// the names <see cref="AtomicFile"/> gives them, and <c>upload.{32 hex}.tmp</c>, a streamed upload's).
    /// </summary>
    /// <exception cref="IOException">
    /// <c>blobs/</c> can't be listed: a link whose target can't be reached (<see cref="UnresolvedLinkException"/>), or
    /// something that isn't a folder. Nothing was changed. (One with more entries than a listing looks at is opened, its
    /// temp files left.)
    /// </exception>
    public void Open()
    {
        lock (_lock)
        {
            var entries = new List<FileSystemInfo>();
            try
            {
                foreach (var entry in RegularFile.List(Paths.BlobsDirectory, new ListingBudget(RegularFile.MaxListing, $"Listing {Paths.BlobsDirectory}")))
                    entries.Add(entry);
            }
            catch (ListingBudgetException ex)
            {
                Logger.Warning(ex, "Left the temp files in {Folder} past the first entries", Paths.BlobsDirectory);
            }
            _opened = true;
            foreach (var entry in entries)
            {
                var temp = entry.FullName;
                if (entry is not FileInfo || !AtomicFile.IsTempFile(entry.Name, name => name == UploadName || BlobFileName().IsMatch(name)))
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
    public string Put(ReadOnlyMemory<byte> content) => Put(content, out _);

    /// <summary>
    /// <see cref="Put(ReadOnlyMemory{byte})"/>, and whether this call wrote the blob (<paramref name="created"/> false
    /// when it was already stored), decided under the store's lock: of concurrent identical calls exactly one creates it.
    /// </summary>
    public string Put(ReadOnlyMemory<byte> content, out bool created)
    {
        if (content.Length > MaxBytes)
            throw new ArgumentOutOfRangeException(nameof(content), content.Length, $"A blob holds at most {MaxBytes} bytes.");

        var id = IdOf(content.Span);
        var path = Paths.BlobFile(id);
        using (Locks.Enter(_lock, WaitingForLock))
        {
            _changed = true;
            using (var existing = OpenFile(id))
            {
                if (existing is not null)
                {
                    if (RandomAccess.GetLength(existing.SafeFileHandle) == content.Length)
                    {
                        // Touched before the comparison, so the verdict is for the version compared: its length and time
                        // are read from the handle the bytes are compared through, before and after. Had another file
                        // been renamed into place, the touch went to that one, whose next read checks it; had this one
                        // changed during the comparison, nothing is remembered and its next read hashes it.
                        _faults?.Before(StorageStep.TouchFile, path);
                        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                        var compared = Identity(existing);
                        if (Holds(existing, content.Span))
                        {
                            Compared?.Invoke();
                            if (Identity(existing) == compared)
                                _verdicts.Set(id, new Verdict(compared.Length, compared.LastWriteUtc, Good: true));
                            else
                                _verdicts.Remove(id);

                            // A Put whose flush of blobs/ failed left the file in place; it is acknowledged only once flushed.
                            AtomicFile.SyncFolder(Paths.BlobsDirectory, _faults);
                            created = false;
                            return id;
                        }
                    }

                    Logger.Warning("Blob {BlobId} on disk ({Bytes} bytes) doesn't hash to its name; writing it again", id, existing.Length);
                }
            }

            // A new write is left without a verdict, not read back: its first checked read hashes it.
            _verdicts.Remove(id);
            AtomicFile.Write(path, content, Paths.Root, _faults);
            created = true;
            return id;
        }
    }

    /// <summary>
    /// Stores what <paramref name="content"/> holds, read to its end, without holding it in memory: written as it arrives
    /// to <c>blobs/upload.{32 hex}.tmp</c> and hashed as it is written, flushed, then, under the store's lock, renamed to
    /// the blob's name (or, when that blob is already stored with these bytes, dropped and the stored blob's time renewed)
    /// and <c>blobs/</c> flushed, as <see cref="Put(ReadOnlyMemory{byte}, out bool)"/> stores bytes. Null, with nothing
    /// stored and the temp file removed, when <paramref name="content"/> holds more than <see cref="MaxBytes"/>: it is read
    /// only until then. A temp file a crash leaves is removed by the next <see cref="Open"/>.
    /// </summary>
    /// <exception cref="IOException">The write, or flushing it to disk, failed; nothing is stored, and the temp file is removed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; nothing is stored.</exception>
    public System.Threading.Tasks.Task<StreamedBlob?> PutAsync(Stream content, CancellationToken ct = default) => PutAsync(content, null, ct);

    /// <summary>
    /// As <see cref="PutAsync(Stream, CancellationToken)"/>, storing the bytes only when they hash to
    /// <paramref name="expectedId"/> (any id when null): otherwise nothing is stored, the temp file is removed, and the
    /// answer's <see cref="StreamedBlob.Id"/> is the id they do hash to.
    /// </summary>
    /// <exception cref="IOException">The write, or flushing it to disk, failed; nothing is stored, and the temp file is removed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; nothing is stored.</exception>
    public async System.Threading.Tasks.Task<StreamedBlob?> PutAsync(Stream content, string? expectedId, CancellationToken ct = default)
    {
        AtomicFile.CreateFolder(Paths.BlobsDirectory, Paths.Root, _faults);
        var temp = Path.Combine(Paths.BlobsDirectory, $"{UploadName}.{AtomicFile.NewRandom()}{AtomicFile.TempSuffix}");
        var kept = false;
        try
        {
            string id;
            long length = 0;
            _faults?.Before(StorageStep.CreateTemp, temp);
            using (var file = new FileStream(temp, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 0 }))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    var first = true;
                    while (true)
                    {
                        // Never more than the room left plus one byte: the extra byte is what tells a body is over.
                        var room = (int)Math.Min(buffer.Length, MaxBytes + 1L - length);
                        var read = await content.ReadAsync(buffer.AsMemory(0, room), ct).ConfigureAwait(false);
                        if (read == 0)
                            break;
                        length += read;
                        if (length > MaxBytes)
                            return null;
                        hash.AppendData(buffer, 0, read);
                        await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        if (first)
                        {
                            _faults?.Before(StorageStep.WriteTemp, temp);
                            first = false;
                        }
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }
                _faults?.Before(StorageStep.FlushTemp, temp);
                file.Flush(flushToDisk: true);
                id = IdPrefix + Convert.ToHexStringLower(hash.GetHashAndReset());
            }
            if (expectedId is not null && id != expectedId)
                return new StreamedBlob(id, length, false); // not these bytes' name: nothing stored, the temp file removed

            var path = Paths.BlobFile(id);
            using (Locks.Enter(_lock, WaitingForLock))
            {
                _changed = true;
                using (var existing = OpenFile(id))
                {
                    if (existing is not null && RandomAccess.GetLength(existing.SafeFileHandle) == length)
                    {
                        // As Put: touched first, so the verdict is for the version hashed; the stored file is kept.
                        _faults?.Before(StorageStep.TouchFile, path);
                        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                        var compared = Identity(existing);
                        if (HashOf(existing) == id)
                        {
                            Compared?.Invoke();
                            if (Identity(existing) == compared)
                                _verdicts.Set(id, new Verdict(compared.Length, compared.LastWriteUtc, Good: true));
                            else
                                _verdicts.Remove(id);
                            DeleteTemp(temp);
                            AtomicFile.SyncFolder(Paths.BlobsDirectory, _faults);
                            return new StreamedBlob(id, length, false);
                        }
                    }
                    if (existing is not null)
                        Logger.Warning("Blob {BlobId} on disk ({Bytes} bytes) doesn't hash to its name; writing it again", id, existing.Length);
                }

                _verdicts.Remove(id);
                _faults?.Before(StorageStep.ReplaceFile, path);
                File.Move(temp, path, overwrite: true);
                kept = true;
                AtomicFile.SyncFolder(Paths.BlobsDirectory, _faults);
                return new StreamedBlob(id, length, true);
            }
        }
        finally
        {
            if (!kept)
            {
                try
                {
                    DeleteTemp(temp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.Warning(ex, "Could not remove {Path}", temp);
                }
            }
        }
    }

    /// <summary>Removes an upload's temp file when it is there (a fault test's crash leaves it for the next <see cref="Open"/>).</summary>
    private void DeleteTemp(string temp)
    {
        if (!File.Exists(temp))
            return;
        _faults?.Before(StorageStep.DeleteFile, temp);
        File.Delete(temp);
    }

    /// <summary>Whether the open file holds exactly <paramref name="content"/> (the length first, so a larger file isn't read).</summary>
    private static bool Holds(FileStream stream, ReadOnlySpan<byte> content)
    {
        if (RandomAccess.GetLength(stream.SafeFileHandle) != content.Length)
            return false;
        var buffer = new byte[Math.Min(content.Length, 81920)];
        long offset = 0;
        while (offset < content.Length)
        {
            var read = RandomAccess.Read(stream.SafeFileHandle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, content.Length - offset)), offset);
            if (read == 0 || !buffer.AsSpan(0, read).SequenceEqual(content.Slice((int)offset, read)))
                return false;
            offset += read;
        }
        return true;
    }

    /// <summary>
    /// Whether a file is stored under <paramref name="id"/>, unchecked: its content may not hash to the id. What a blob
    /// read can use is <see cref="StoredSize"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a valid blob id.</exception>
    public bool Exists(string id) => File.Exists(Paths.BlobFile(id));

    /// <summary>The size of the file stored under <paramref name="id"/>, unchecked, or null when there is none.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a valid blob id.</exception>
    public long? Size(string id)
    {
        var file = new FileInfo(Paths.BlobFile(id));
        return file.Exists ? file.Length : null;
    }

    /// <summary>The file stored under <paramref name="id"/>, unchecked, or null when there is none. The caller disposes the stream.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a valid blob id.</exception>
    public Stream? OpenRead(string id) => OpenFile(id);

    private FileStream? OpenFile(string id)
    {
        try
        {
            return RegularFile.OpenRead(Paths.BlobFile(id), FileShare.Read | FileShare.Delete);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or NotARegularFileException)
        {
            return null; // a FIFO or device under a blob's name isn't a stored blob, and is never waited on
        }
    }

    /// <summary>
    /// A blob's bytes, or null when it isn't stored, its file is over <see cref="MaxBytes"/> (only a file changed on
    /// disk can be; it isn't read), or its bytes don't hash to its id. The bytes are hashed only when this file version
    /// has no verdict yet.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a valid blob id.</exception>
    public byte[]? ReadVerified(string id) => Read(id, rehash: false);

    /// <summary>
    /// As <see cref="ReadVerified"/>, but the bytes read are hashed on every call, whatever verdict is remembered: for a
    /// copy that leaves the store (an export), which a same-length change with its time set back mustn't slip into.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a valid blob id.</exception>
    public byte[]? ReadHashed(string id) => Read(id, rehash: true);

    private byte[]? Read(string id, bool rehash)
    {
        using var stream = OpenFile(id);
        if (stream is null)
            return null;
        var (length, written) = Identity(stream);
        if (Oversized(id, length))
            return null;

        // The open file keeps its length: a blob is only ever replaced by a rename.
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        return Check(id, length, written, () => IdOf(bytes), rehash) ? bytes : null;
    }

    /// <summary>
    /// A blob's bytes for reading, at position 0, or null when it isn't stored, its file is over <see cref="MaxBytes"/>
    /// (not read), or its bytes don't hash to its id. A file version with no verdict yet is read through once to hash it.
    /// The caller disposes the stream.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a valid blob id.</exception>
    public Stream? OpenVerified(string id)
    {
        var stream = OpenFile(id);
        if (stream is null)
            return null;
        try
        {
            var (length, written) = Identity(stream);
            if (Oversized(id, length) || !Check(id, length, written, () => HashOf(stream)))
            {
                stream.Dispose();
                return null;
            }
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A blob's size in bytes, or null when it isn't stored, its file is over <see cref="MaxBytes"/> (not read), or its
    /// bytes don't hash to its id. A file version with no verdict yet is read through once to hash it.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a valid blob id.</exception>
    public long? StoredSize(string id)
    {
        using var stream = OpenFile(id);
        if (stream is null)
            return null;
        var (length, written) = Identity(stream);
        return !Oversized(id, length) && Check(id, length, written, () => HashOf(stream)) ? length : null;
    }

    /// <summary>
    /// Whether a blob's file is over <see cref="MaxBytes"/>, which only a file changed on disk can be: it counts as not
    /// stored, refused from its length so a request never reads it through.
    /// </summary>
    private bool Oversized(string id, long length)
    {
        if (length <= MaxBytes)
            return false;
        Logger.Warning("Blob {BlobId} is {Bytes} bytes on disk, over the {MaxBytes}-byte limit; it counts as not stored", id, length, MaxBytes);
        return true;
    }

    /// <summary>The open file's length and last-write time, from its handle: the identity of the bytes read through it.</summary>
    private static (long Length, DateTime LastWriteUtc) Identity(FileStream stream) =>
        (RandomAccess.GetLength(stream.SafeFileHandle), File.GetLastWriteTimeUtc(stream.SafeFileHandle));

    private static string HashOf(Stream stream)
    {
        stream.Position = 0;
        return IdPrefix + Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// The verdict for the file version (<paramref name="length"/>, <paramref name="written"/>): the remembered one
    /// (unless <paramref name="rehash"/>), or <paramref name="hash"/>'s, remembered and, when bad, logged.
    /// </summary>
    private bool Check(string id, long length, DateTime written, Func<string> hash, bool rehash = false)
    {
        if (!rehash && _verdicts.TryGetValue(id, out var known) && known.Length == length && known.LastWriteUtc == written)
            return known.Good;

        Interlocked.Increment(ref _hashes);
        var verdict = new Verdict(length, written, hash() == id);
        _verdicts.Set(id, verdict);
        if (!verdict.Good)
            Logger.Warning("Blob {BlobId} on disk ({Bytes} bytes, written {Written:o}) doesn't hash to its id; it counts as not stored until it is uploaded again", id, length, written);
        return verdict.Good;
    }

    /// <summary>The ids of the stored blobs (files named by 64 hex digits; anything else in <c>blobs/</c> is ignored).</summary>
    internal IReadOnlyList<string> List() => List(new ListingBudget(RegularFile.MaxListing, $"Listing {Paths.BlobsDirectory}"));

    /// <inheritdoc cref="List()"/>
    /// <param name="budget">What the listing counts against.</param>
    /// <exception cref="IOException">
    /// <c>blobs/</c> can't be listed: a link whose target can't be reached (<see cref="UnresolvedLinkException"/>),
    /// something that isn't a folder, or the budget ran out (<see cref="ListingBudgetException"/>).
    /// </exception>
    internal IReadOnlyList<string> List(ListingBudget budget) =>
        RegularFile.List(Paths.BlobsDirectory, budget)
            .Where(entry => entry is FileInfo && BlobFileName().IsMatch(entry.Name))
            .Select(entry => IdPrefix + entry.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

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
            _verdicts.Remove(id);
            return true;
        }
    }
}
