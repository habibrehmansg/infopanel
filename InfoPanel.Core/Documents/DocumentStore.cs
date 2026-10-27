using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using InfoPanel.Persistence;
using InfoPanel.Spec;
using Serilog;

namespace InfoPanel.Documents;

/// <summary>
/// The saved documents on disk: <c>profiles/{id}/document.json</c> holds a profile's saved generation as the caller's
/// bytes (parsing and validation are the caller's), <c>profiles.json</c> only the display order (<c>{"order":[ids]}</c>).
/// The folders are the source of truth: <see cref="Open"/> appends a folder missing from the order, drops a listed id
/// with no folder, and removes what an interrupted operation left (temp files, staging folders, deleted folders), so
/// there is no consistency between files to keep. Every write goes through <see cref="AtomicFile"/>; a new profile is
/// written in a staging folder that is then renamed into place, and a deleted one is renamed to
/// <c>{id}.deleted-{random}</c> before it is removed, so after a crash each profile is exactly its old or its new saved
/// document and a deleted one never comes back. One store per data folder (the engine holds the folder's lock); its
/// operations are serialized.
/// </summary>
public sealed partial class DocumentStore
{
    private static readonly ILogger Logger = Log.ForContext<DocumentStore>();

    /// <summary>The saved generation's file name in a profile's folder.</summary>
    public const string DocumentFileName = "document.json";

    /// <summary>What a deleted profile's folder is renamed to before it is removed: <c>{id}.deleted-{32 hex}</c>.</summary>
    private const string DeletedMarker = ".deleted-";

    /// <summary>The length of a profile id.</summary>
    private const int IdLength = 36;

    private readonly Lock _lock = new();
    private readonly IStorageFaults? _faults;
    private List<string>? _order;
    private bool _changed;

    /// <param name="limits">Sets <see cref="MaxBytes"/> (<see cref="SpecLimits.DocumentBytes"/>); the defaults when null.</param>
    public DocumentStore(DataPaths paths, SpecLimits? limits = null) : this(paths, limits, faults: null)
    {
    }

    internal DocumentStore(DataPaths paths, SpecLimits? limits, IStorageFaults? faults)
    {
        Paths = paths;
        MaxBytes = (limits ?? SpecLimits.Default).DocumentBytes;
        _faults = faults;
    }

    public DataPaths Paths { get; }

    /// <summary>
    /// The largest document (and order file) read: <c>documentBytes</c>, the largest document the formats allow. A larger
    /// file is never read whole.
    /// </summary>
    public int MaxBytes { get; }

    /// <summary>The store's lock, which <see cref="BlobSweep"/> holds for the whole sweep.</summary>
    internal Lock SyncRoot => _lock;

    /// <summary>Whether the store is open and nothing has been created, saved, deleted or reordered since.</summary>
    internal bool InStartup => _order != null && !_changed;

    /// <summary>
    /// A profile id: a UUID in lower case (<c>0f8fad5b-d9cb-469f-a165-70867728950e</c>), the form of a document's id.
    /// Safe as a folder name on every platform and never two names on a case-insensitive file system; anything else
    /// (upper case, braces, separators, dots, padding) is refused.
    /// </summary>
    public static bool IsValidId(string? id) => id != null && ProfileId().IsMatch(id);

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileId();

    /// <summary>
    /// Start-up reconciliation, before anything else: removes the order file's temp files, staging folders,
    /// <c>profiles/*.deleted-*</c> folders and the temp files in each profile's folder (only names the store writes); then
    /// appends every profile folder the order doesn't list (by id, in ordinal order) and drops every listed id without a
    /// folder, rewriting <c>profiles.json</c> when that changed it (or it is missing, unreadable or larger than
    /// <see cref="MaxBytes"/>). Entries of <c>profiles/</c> that are none of these (1.x <c>{guid}.xml</c> files, folders
    /// not named by a valid id) are left alone. Returns the order.
    /// </summary>
    public IReadOnlyList<string> Open()
    {
        lock (_lock)
        {
            RemoveLeftovers();

            var folders = ProfileFolders();
            var (listed, readable) = ReadOrder();
            var order = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in listed)
            {
                if (folders.Contains(id) && seen.Add(id))
                    order.Add(id);
                else
                    Logger.Information("Dropped {ProfileId} from the profile order: it has no folder", id);
            }

            foreach (var id in folders.Order(StringComparer.Ordinal))
            {
                if (!seen.Add(id))
                    continue;
                order.Add(id);
                Logger.Information("Added profile {ProfileId} to the profile order: its folder was not listed", id);
            }

            if (!readable || !order.SequenceEqual(listed))
                WriteOrder(order);
            _order = order;
            return order.ToArray();
        }
    }

    /// <summary>The profiles' ids in display order.</summary>
    public IReadOnlyList<string> List()
    {
        lock (_lock)
            return Order.ToArray();
    }

    public bool Contains(string id)
    {
        lock (_lock)
            return Order.Contains(id);
    }

    /// <summary>A profile's saved document, as stored.</summary>
    /// <exception cref="KeyNotFoundException">No profile has this id.</exception>
    /// <exception cref="IOException">Its document can't be read (missing included: <see cref="FileNotFoundException"/>).</exception>
    /// <exception cref="InvalidDataException">Its document is larger than <see cref="MaxBytes"/>.</exception>
    public byte[] Read(string id)
    {
        lock (_lock)
        {
            RequireListed(id);
            return ReadAtMost(Paths.DocumentFile(id), MaxBytes);
        }
    }

    /// <summary>A profile's saved document for reading as it is, whatever its size (an export). The caller disposes the stream.</summary>
    /// <exception cref="KeyNotFoundException">No profile has this id.</exception>
    /// <exception cref="IOException">Its document can't be opened (missing included: <see cref="FileNotFoundException"/>).</exception>
    public Stream OpenRead(string id)
    {
        lock (_lock)
        {
            RequireListed(id);
            return new FileStream(Paths.DocumentFile(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
    }

    /// <summary>
    /// Stores a new profile with <paramref name="document"/> as its saved document and appends it to the order. The
    /// document is written in a staging folder that is renamed into place, so the profile exists with its whole document
    /// or not at all. Once it is in place, a failure to rewrite the order is logged, not thrown: the next
    /// <see cref="Open"/> lists it.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a valid profile id.</exception>
    /// <exception cref="InvalidOperationException">A profile (or a folder) with this id already exists.</exception>
    /// <exception cref="IOException">
    /// The profile could not be stored (nothing of it is left), or it was renamed into place but <c>profiles/</c> could
    /// not be flushed to disk (it is then listed, and a power loss may lose it).
    /// </exception>
    public void Create(string id, ReadOnlyMemory<byte> document)
    {
        var folder = Paths.ProfileFolder(id);
        lock (_lock)
        {
            var order = Order;
            _changed = true;
            if (order.Contains(id) || Directory.Exists(folder))
                throw new InvalidOperationException($"Profile {id} already exists.");

            var staging = Path.Combine(Paths.ProfilesDirectory, $"{id}.{AtomicFile.NewRandom()}{AtomicFile.TempSuffix}");
            try
            {
                AtomicFile.Write(Path.Combine(staging, DocumentFileName), document, Paths.Root, _faults);
                _faults?.Before(StorageStep.MoveFolder, folder);
                Directory.Move(staging, folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDeleteTree(staging);
                throw;
            }

            order.Add(id);
            try
            {
                AtomicFile.SyncFolder(Paths.ProfilesDirectory, _faults);
            }
            finally
            {
                TryWriteOrder(order);
            }
        }
    }

    /// <summary>Replaces a profile's saved document.</summary>
    /// <exception cref="KeyNotFoundException">No profile has this id.</exception>
    /// <exception cref="IOException">
    /// The write failed (the previous saved document is unchanged), or the document was replaced but its folder could
    /// not be flushed to disk.
    /// </exception>
    public void Save(string id, ReadOnlyMemory<byte> document)
    {
        lock (_lock)
        {
            RequireListed(id);
            _changed = true;
            var folder = Paths.ProfileFolder(id);
            if (!Directory.Exists(folder))
                throw new KeyNotFoundException($"Profile {id} has no folder.");
            AtomicFile.Write(Path.Combine(folder, DocumentFileName), document, Paths.Root, _faults);
        }
    }

    /// <summary>
    /// Deletes a profile: its folder is renamed to <c>{id}.deleted-{random}</c> and <c>profiles/</c> flushed to disk (the
    /// delete takes effect there, and a crash after it can't bring the profile back), the order is rewritten without
    /// it, and the renamed folder is removed. Failures after the flush are logged, not thrown; the next
    /// <see cref="Open"/> finishes the job. Returns false when no profile has this id.
    /// </summary>
    /// <exception cref="IOException">
    /// The folder could not be renamed (the profile is unchanged), or <c>profiles/</c> could not be flushed after the
    /// rename (the profile is no longer listed and the renamed folder is left for the next <see cref="Open"/>).
    /// </exception>
    public bool Delete(string id)
    {
        lock (_lock)
        {
            var order = Order;
            _changed = true;
            if (!IsValidId(id) || !order.Contains(id))
                return false;

            var folder = Paths.ProfileFolder(id);
            var deleted = Path.Combine(Paths.ProfilesDirectory, $"{id}{DeletedMarker}{AtomicFile.NewRandom()}");
            if (Directory.Exists(folder))
            {
                _faults?.Before(StorageStep.MoveFolder, deleted);
                Directory.Move(folder, deleted);
                order.Remove(id);
                try
                {
                    // Removing the folder's content before its rename is on disk could bring back a profile without it.
                    AtomicFile.SyncFolder(Paths.ProfilesDirectory, _faults);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    TryWriteOrder(order);
                    throw;
                }
            }
            else
            {
                order.Remove(id);
            }

            TryWriteOrder(order);
            TryDeleteTree(deleted);
            return true;
        }
    }

    /// <summary>Sets the display order.</summary>
    /// <exception cref="ArgumentException"><paramref name="order"/> is not exactly the stored ids, each once.</exception>
    /// <exception cref="IOException">
    /// The write failed (the order is unchanged), or the order file was replaced but could not be flushed to disk (the
    /// new order holds).
    /// </exception>
    public void Reorder(IReadOnlyList<string> order)
    {
        lock (_lock)
        {
            var current = Order;
            _changed = true;
            if (order.Count != current.Count || order.Distinct(StringComparer.Ordinal).Count() != order.Count
                || !order.All(current.Contains))
                throw new ArgumentException("The order must list every profile exactly once.", nameof(order));

            var next = order.ToList();
            WriteOrder(next, replaced: () => _order = next);
        }
    }

    /// <summary>
    /// Every <c>profiles/*/document.json</c> path that is or may be a document: each profile folder's, and those of
    /// folders not named by a valid id (never staging or deleted folders), for <see cref="BlobSweep"/>. A profile folder
    /// is listed even when its document is missing.
    /// </summary>
    /// <exception cref="IOException">Whether a folder or a document exists can't be told.</exception>
    /// <exception cref="UnauthorizedAccessException">Whether a folder or a document exists can't be told.</exception>
    internal IReadOnlyList<string> DocumentFiles()
    {
        lock (_lock)
        {
            var files = new List<string>();
            foreach (var folder in Subfolders(Paths.ProfilesDirectory))
            {
                if (IsLeftover(folder.Name))
                    continue;
                var file = Path.Combine(folder.FullName, DocumentFileName);
                if (IsValidId(folder.Name) || FileExists(file))
                    files.Add(file);
            }

            files.Sort(StringComparer.Ordinal);
            return files;
        }
    }

    /// <summary>Reads <paramref name="path"/> whole, reading at most <paramref name="maxBytes"/> + 1 bytes of it.</summary>
    /// <exception cref="InvalidDataException">The file is larger than <paramref name="maxBytes"/>.</exception>
    internal static byte[] ReadAtMost(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var content = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, maxBytes + 1L - content.Length))) > 0)
        {
            content.Write(chunk, 0, read);
            if (content.Length > maxBytes)
                throw new InvalidDataException($"{path} is larger than {maxBytes} bytes.");
        }

        return content.ToArray();
    }

    private List<string> Order => _order ?? throw new InvalidOperationException("The document store is not open.");

    private void RequireListed(string id)
    {
        if (!IsValidId(id) || !Order.Contains(id))
            throw new KeyNotFoundException($"No profile {id}.");
    }

    /// <summary>A staging folder (<c>{id}.{32 hex}.tmp</c>) or a deleted folder (<c>{id}.deleted-{32 hex}</c>), exactly as the store names them.</summary>
    private static bool IsLeftover(string name)
    {
        if (name.Length <= IdLength || !IsValidId(name[..IdLength]))
            return false;
        var rest = name.AsSpan(IdLength);
        return AtomicFile.IsRandomPart(rest)
            || (rest.Length == DeletedMarker.Length + AtomicFile.RandomLength && rest.StartsWith(DeletedMarker, StringComparison.Ordinal)
                && AtomicFile.IsLowerHex(rest[DeletedMarker.Length..]));
    }

    /// <summary>Whether a leftover's <paramref name="name"/> is a deleted folder's rather than a staging folder's.</summary>
    private static bool IsDeleted(string name) => name.AsSpan(IdLength).StartsWith(DeletedMarker, StringComparison.Ordinal);

    /// <summary>The folders in <paramref name="path"/>, none when it doesn't exist; any other failure is thrown.</summary>
    private static DirectoryInfo[] Subfolders(string path)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateDirectories().ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    /// <summary>Whether <paramref name="path"/> exists; thrown when that can't be told (unlike <see cref="File.Exists"/>).</summary>
    private static bool FileExists(string path)
    {
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private HashSet<string> ProfileFolders()
    {
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Subfolders(Paths.ProfilesDirectory))
        {
            if (IsValidId(entry.Name))
                folders.Add(entry.Name);
            else
                Logger.Warning("Left {Folder} alone: it is not named by a profile id", entry.FullName);
        }

        return folders;
    }

    /// <summary>Removes what interrupted operations left; an entry that can't be removed is logged and left.</summary>
    private void RemoveLeftovers()
    {
        if (Directory.Exists(Paths.Root))
        {
            foreach (var file in Directory.EnumerateFiles(Paths.Root))
            {
                if (AtomicFile.IsTempFile(Path.GetFileName(file), name => name == Path.GetFileName(Paths.ProfileOrderFile)))
                    TryDeleteTree(file);
            }
        }

        var entries = Subfolders(Paths.ProfilesDirectory);
        var deletedFlushed = false;
        if (entries.Any(entry => IsLeftover(entry.Name) && IsDeleted(entry.Name)))
        {
            // A delete whose flush failed left its rename possibly not on disk: removing the folder's content before
            // it is could bring back the profile without its document after a power loss.
            try
            {
                AtomicFile.SyncFolder(Paths.ProfilesDirectory, _faults);
                deletedFlushed = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warning(ex, "Could not flush {Folder} to disk; deleted profiles' folders are removed at the next start", Paths.ProfilesDirectory);
            }
        }

        foreach (var entry in entries)
        {
            if (IsLeftover(entry.Name))
            {
                if (IsDeleted(entry.Name) && !deletedFlushed)
                    continue;
                Logger.Information("Removing {Folder}, left by an interrupted operation", entry.FullName);
                TryDeleteTree(entry.FullName);
            }
            else if (IsValidId(entry.Name) && entry.LinkTarget == null)
            {
                foreach (var file in entry.EnumerateFiles("*" + AtomicFile.TempSuffix))
                {
                    if (AtomicFile.IsTempFile(file.Name, name => name == DocumentFileName))
                        TryDeleteTree(file.FullName);
                }
            }
        }
    }

    /// <summary>The ids <c>profiles.json</c> lists (valid ones, in order), and whether it could be read.</summary>
    private (List<string> Ids, bool Readable) ReadOrder()
    {
        var path = Paths.ProfileOrderFile;
        if (!File.Exists(path))
            return ([], false);
        try
        {
            using var json = JsonDocument.Parse(ReadAtMost(path, MaxBytes));
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("order", out var order) || order.ValueKind != JsonValueKind.Array)
                throw new JsonException("it has no \"order\" array");

            var ids = new List<string>();
            foreach (var entry in order.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String && IsValidId(entry.GetString()))
                    ids.Add(entry.GetString()!);
                else
                    Logger.Warning("Ignored {Entry} in {Path}: not a profile id", entry.GetRawText(), path);
            }

            return (ids, true);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Logger.Warning(ex, "Could not read {Path}; the profiles are listed by id", path);
            return ([], false);
        }
    }

    private void WriteOrder(List<string> order, Action? replaced = null) =>
        AtomicFile.Write(Paths.ProfileOrderFile, stream =>
        {
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            writer.WriteStartArray("order");
            foreach (var id in order)
                writer.WriteStringValue(id);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }, Paths.Root, _faults, replaced);

    private void TryWriteOrder(List<string> order)
    {
        try
        {
            WriteOrder(order);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(ex, "Could not write {Path}; the profile order is corrected at the next start", Paths.ProfileOrderFile);
        }
    }

    /// <summary>Removes a file or a folder and everything in it; links are removed, never followed. Failures are logged.</summary>
    private void TryDeleteTree(string path)
    {
        try
        {
            FileSystemInfo entry = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (entry.Exists || entry.LinkTarget != null)
                DeleteTree(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(ex, "Could not remove {Path}; it is removed at the next start", path);
        }
    }

    private void DeleteTree(FileSystemInfo entry)
    {
        if (entry is DirectoryInfo directory && entry.LinkTarget == null)
        {
            foreach (var child in directory.EnumerateFileSystemInfos())
                DeleteTree(child);
            _faults?.Before(StorageStep.DeleteFolder, entry.FullName);
            directory.Delete();
        }
        else if (entry is DirectoryInfo link && OperatingSystem.IsWindows())
        {
            _faults?.Before(StorageStep.DeleteFile, entry.FullName);
            link.Delete(); // a directory link or junction: RemoveDirectory removes the link only
        }
        else
        {
            _faults?.Before(StorageStep.DeleteFile, entry.FullName);
            if (entry.LinkTarget == null)
                entry.Attributes = FileAttributes.Normal;
            File.Delete(entry.FullName);
        }
    }
}
