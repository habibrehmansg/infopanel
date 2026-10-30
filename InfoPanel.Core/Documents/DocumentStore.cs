using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using InfoPanel.Extensions;
using InfoPanel.Persistence;
using InfoPanel.Spec;
using Serilog;

namespace InfoPanel.Documents;

/// <summary>
/// A create that would take the stored profile list (<c>profiles.json</c>) over its bound, <c>profileListBytes</c>
/// (spec/docs/limits.md): nothing was written.
/// </summary>
public sealed class ProfileListFullException(long bytes, long limit)
    : IOException($"The profile list would be {bytes} bytes, over the limit of {limit} (profileListBytes).")
{
    /// <summary>The bytes the list would take.</summary>
    public long Bytes { get; } = bytes;

    /// <summary>Its bound.</summary>
    public long Limit { get; } = limit;
}

/// <summary>
/// The saved documents on disk: <c>profiles/{id}/document.json</c> holds a profile's saved generation as the caller's
/// bytes (parsing and validation are the caller's; the workspace writes the canonical form, spec/docs/limits.md#sizes,
/// so a file's size is its document's measured <c>documentBytes</c>), <c>profiles.json</c> only the display order
/// (<c>{"order":[ids]}</c>, read tolerantly: members it doesn't know are kept when it is rewritten), within its own byte
/// bound, <see cref="ProfileListBytes"/>, so every order file the store writes reads back. A save keeps the document it
/// replaces as an earlier saved version, <c>profiles/{id}/versions/{n}.json</c> (<see cref="Save"/>; docs/2.0/data-folder.md,
/// "Earlier saves").
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

    /// <summary>
    /// The file in a profile's folder that keeps its unsaved changes across a restart (<see cref="WriteRecovery"/>;
    /// docs/2.0/data-folder.md, "Unsaved changes").
    /// </summary>
    public const string RecoveryFileName = "recovery.json";

    /// <summary>What a deleted profile's folder is renamed to before it is removed: <c>{id}.deleted-{32 hex}</c>.</summary>
    private const string DeletedMarker = ".deleted-";

    /// <summary>The length of a profile id.</summary>
    private const int IdLength = 36;

    /// <summary>The folder of a profile's earlier saved versions, in its folder: <c>versions/{n}.json</c>.</summary>
    public const string VersionsFolderName = "versions";

    /// <summary>The most earlier saved versions a profile keeps.</summary>
    public const int VersionsKept = 10;

    /// <summary>The most bytes a profile's earlier saved versions take, all together; the newest is kept even when it alone is larger.</summary>
    public const long VersionsKeptBytes = 64L * 1024 * 1024;

    /// <summary>The largest version number a name holds: 19 digits.</summary>
    public const ulong MaxVersionNumber = 9_999_999_999_999_999_999;

    /// <summary>The versions a profile keeps (<see cref="VersionsKept"/>); tests set it lower.</summary>
    internal int MaxVersions { get; set; } = VersionsKept;

    /// <summary>The bytes the versions take (<see cref="VersionsKeptBytes"/>); tests set it lower.</summary>
    internal long MaxVersionBytes { get; set; } = VersionsKeptBytes;

    private readonly Lock _lock = new();
    private readonly IStorageFaults? _faults;

    /// <summary>
    /// The profiles whose <c>versions</c> folder is known to be on disk with every folder above it this run: a flush of the
    /// data folder, <c>profiles/</c> and the profile's folder succeeded after it existed (<see cref="TryKeepVersion"/>).
    /// A folder that merely exists isn't known to be, after a flush that failed.
    /// </summary>
    private readonly HashSet<string> _versionsOnDisk = new(StringComparer.Ordinal);
    private List<string>? _order;
    private bool _changed;

    /// <summary>
    /// The members of <c>profiles.json</c> in their order, <c>order</c> as null (the list itself); the others are members
    /// this engine doesn't know, written back as they were read (<see cref="EngineJson"/>).
    /// </summary>
    private List<JsonMemberText?> _orderMembers = [null];

    /// <summary>
    /// Whether <c>profiles.json</c> is left as it is for this run: a file the store can't hold whole (over
    /// <see cref="ProfileListBytes"/>, nested deeper than jsonDepth, or one it can't open, such as a FIFO), so the order is
    /// kept in memory and never written, and nothing in the file is lost.
    /// </summary>
    private bool _orderFileKept;

    /// <param name="limits">Sets <see cref="MaxBytes"/> (<see cref="ReadCap"/> of them); the defaults when null.</param>
    public DocumentStore(DataPaths paths, SpecLimits? limits = null) : this(paths, limits, faults: null)
    {
    }

    internal DocumentStore(DataPaths paths, SpecLimits? limits, IStorageFaults? faults)
    {
        Paths = paths;
        MaxBytes = ReadCap(limits ?? SpecLimits.Default);
        ProfileListBytes = (limits ?? SpecLimits.Default).ProfileListBytes;
        _faults = faults;
    }

    public DataPaths Paths { get; }

    /// <summary>
    /// The most entries the start-up removal of what interrupted operations left (staging and deleted profiles' folders)
    /// looks at, all of them together (<see cref="RegularFile.List"/>); past it the rest is left for the next start, and
    /// start-up goes on. Tests set it lower.
    /// </summary>
    internal int CleanupEntries { get; set; } = RegularFile.MaxListing;

    /// <summary>
    /// The largest document read: <see cref="ReadCap"/>. A larger file is never read whole; one within it is admitted by
    /// its canonical size, <c>documentBytes</c> (spec/docs/limits.md#sizes), which a document the workspace stored (in the
    /// canonical form it was measured in) never exceeds.
    /// </summary>
    public int MaxBytes { get; }

    /// <summary>
    /// The bound on <c>profiles.json</c>'s bytes, <c>profileListBytes</c>: the store never writes a larger one (a create
    /// that would is refused, <see cref="ProfileListFullException"/>) and never reads one whole.
    /// </summary>
    public int ProfileListBytes { get; }

    /// <summary>
    /// The room a document's bytes as sent or stored (whitespace, escapes, number spellings) may take beyond
    /// <c>documentBytes</c> before they are read: a create body's allowance for its envelope, 64 KiB.
    /// </summary>
    public const int ReadAllowanceBytes = 64 * 1024;

    /// <summary>
    /// The raw cap on a document's bytes before they are read, the same for a create's body, an archive's document and a
    /// stored file: <c>documentBytes</c> plus <see cref="ReadAllowanceBytes"/>. It only bounds reading; the canonical size
    /// decides admission.
    /// </summary>
    public static int ReadCap(SpecLimits limits) => limits.DocumentBytes + ReadAllowanceBytes;

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
    /// folder, rewriting <c>profiles.json</c> when that changed it (or it is missing or damaged: not JSON, or without an
    /// <c>order</c> array), unless the order is then over <see cref="ProfileListBytes"/> itself: it is kept for this run,
    /// and no profile can be created until enough are deleted. A file the store can't hold whole is never rewritten in
    /// the run (<see cref="_orderFileKept"/>). Entries of <c>profiles/</c> that are none of these (1.x
    /// <c>{guid}.xml</c> files, folders not named by a valid id) are left alone; a link named by a valid id whose target
    /// can't be reached is a profile (it can't be read), never an absent one. Returns the order.
    /// </summary>
    /// <exception cref="IOException">
    /// <c>profiles/</c> can't be listed: a link whose target can't be reached (<see cref="UnresolvedLinkException"/>),
    /// something that isn't a folder, or more entries than a listing looks at. Nothing was changed.
    /// </exception>
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
            {
                try
                {
                    WriteOrder(order);
                }
                catch (ProfileListFullException ex)
                {
                    Logger.Warning("Left {Path} as it was: the order of {Count} profiles is over its limit ({Message})", Paths.ProfileOrderFile, order.Count, ex.Message);
                }
            }
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

    /// <summary>
    /// Stores a new profile with <paramref name="document"/> as its saved document and appends it to the order. The
    /// document is written in a staging folder that is renamed into place, so the profile exists with its whole document
    /// or not at all. Once it is in place, a failure to rewrite the order is logged, not thrown: the next
    /// <see cref="Open"/> lists it.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a valid profile id.</exception>
    /// <exception cref="InvalidOperationException">A profile (or a folder) with this id already exists.</exception>
    /// <exception cref="ProfileListFullException">The order would be over <see cref="ProfileListBytes"/>; nothing was written.</exception>
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
            if (order.Contains(id) || RegularFile.StateOf(folder) != PathState.Absent)
                throw new InvalidOperationException($"Profile {id} already exists.");
            // Checked before anything is written, so the order file the create rewrites stays within its bound.
            var listBytes = OrderBytes([.. order, id]).Length;
            if (listBytes > ProfileListBytes)
                throw new ProfileListFullException(listBytes, ProfileListBytes);

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

    /// <summary>
    /// Replaces a profile's saved document, keeping the one it replaces as an earlier saved version
    /// (<see cref="TryKeepVersion"/>) unless it holds the same bytes. The version is whole and on disk before the document
    /// is replaced, and versions past <see cref="VersionsKept"/> or <see cref="VersionsKeptBytes"/> are dropped once the new
    /// document is in place, after every save that wrote it (<see cref="TryDropVersions"/>); a version that can't be kept
    /// or dropped is logged, and the save goes on.
    /// </summary>
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
            TryKeepVersion(id, document);
            AtomicFile.Write(Path.Combine(folder, DocumentFileName), document, Paths.Root, _faults);
            TryDropVersions(id);
        }
    }

    /// <summary>
    /// A profile's recovery file as stored (<see cref="RecoveryFileName"/>), read whole within <see cref="MaxBytes"/>;
    /// null when there is none.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No profile has this id.</exception>
    /// <exception cref="IOException">It can't be read (a FIFO or another file that isn't a regular one included).</exception>
    /// <exception cref="InvalidDataException">It is larger than <see cref="MaxBytes"/>.</exception>
    public byte[]? ReadRecovery(string id)
    {
        lock (_lock)
        {
            RequireListed(id);
            var path = Paths.RecoveryFile(id);
            return RegularFile.StateOf(path) == PathState.Absent ? null : ReadAtMost(path, MaxBytes);
        }
    }

    /// <summary>
    /// Replaces a profile's recovery file with <paramref name="bytes"/> (<see cref="AtomicFile"/>), only while the profile
    /// is listed and its folder there: a write that comes after its delete writes nothing, so none can land in a profile
    /// created again under the id.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No profile has this id (deleted meanwhile); nothing was written.</exception>
    /// <exception cref="IOException">The write failed (the previous file is unchanged), or its folder couldn't be flushed.</exception>
    public void WriteRecovery(string id, ReadOnlyMemory<byte> bytes)
    {
        lock (_lock)
        {
            RequireListed(id);
            // AtomicFile makes a missing folder: never the profile's, which only a create makes.
            if (RegularFile.StateOf(Paths.ProfileFolder(id)) != PathState.Folder)
                throw new KeyNotFoundException($"Profile {id} has no folder.");
            AtomicFile.Write(Paths.RecoveryFile(id), bytes, Paths.Root, _faults);
        }
    }

    /// <summary>
    /// Removes a profile's recovery file when there is one, and flushes its folder so the removal is on disk before this
    /// returns; the folder is flushed when the file is gone already too, since an earlier removal's flush may have
    /// failed. Nothing to do for a profile that isn't listed (its folder went with it).
    /// </summary>
    /// <exception cref="IOException">It couldn't be removed, or the folder couldn't be flushed.</exception>
    public void RemoveRecovery(string id)
    {
        lock (_lock)
        {
            if (!IsValidId(id) || !Order.Contains(id))
                return;
            var path = Paths.RecoveryFile(id);
            if (RegularFile.StateOf(path) != PathState.Absent)
            {
                _faults?.Before(StorageStep.DeleteFile, path);
                File.Delete(path);
            }
            AtomicFile.SyncFolder(Paths.ProfileFolder(id), _faults);
        }
    }

    /// <summary>
    /// Whether <paramref name="name"/> is an earlier saved version's: <c>{n}.json</c>, <c>n</c> from 1 in decimal, without
    /// leading zeros, at most 19 digits (<see cref="MaxVersionNumber"/>); exactly, so every other name is left alone.
    /// </summary>
    internal static bool IsVersionName(string name) => VersionName().IsMatch(name);

    [GeneratedRegex("^[1-9][0-9]{0,18}\\.json\\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionName();

    /// <summary>A version's name in any letter case, which numbering steps past (a case-insensitive drive holds one of them only).</summary>
    [GeneratedRegex("^[1-9][0-9]{0,18}\\.json\\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex VersionNameAnyCase();

    private static ulong VersionNumber(string name) => ulong.Parse(name.AsSpan(0, name.IndexOf('.')), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Keeps the saved document that <paramref name="document"/> replaces as the next version, <c>versions/{n}.json</c>,
    /// its bytes as they are, through <see cref="AtomicFile"/>: whole, its file and <c>versions</c> flushed before this
    /// returns. When the newest version already holds those bytes (a save that failed after keeping it), that one is
    /// reused, its content and <c>versions</c> flushed again, rather than kept twice; when its flush fails, a new one is kept. The folders above are flushed only when
    /// <c>versions</c> isn't yet known to be on disk this run (<see cref="_versionsOnDisk"/>): created now, or found but
    /// not yet flushed into the profile's folder with everything above it. None is kept when the saved document holds
    /// the same bytes, when it can't be read (missing, not a regular file, over <see cref="MaxBytes"/>), or when
    /// <c>versions</c> is anything but a folder of the profile's own (a link, to a folder or to nothing, or a file is never
    /// followed or written). Returns whether a version holds the replaced document; a failure is logged.
    /// </summary>
    private bool TryKeepVersion(string id, ReadOnlyMemory<byte> document)
    {
        byte[] previous;
        try
        {
            previous = ReadAtMost(Paths.DocumentFile(id), MaxBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Logger.Warning(ex, "Kept no earlier version of profile {ProfileId}: its saved document can't be read", id);
            return false;
        }

        if (previous.AsSpan().SequenceEqual(document.Span))
            return false;

        var folder = Paths.VersionsFolder(id);
        try
        {
            if (!IsOwnFolderOrAbsent(folder))
            {
                Logger.Warning("Kept no earlier version of profile {ProfileId}: {Folder} isn't a folder of its own (a link or a file), so it is left alone", id, folder);
                return false;
            }

            // Every entry with a version's name, of any type and in any letter case, holds its number.
            ulong highest = 0;
            var entries = RegularFile.List(folder, new ListingBudget(RegularFile.MaxListing, $"Listing {folder}")).ToList();
            foreach (var entry in entries)
            {
                if (VersionNameAnyCase().IsMatch(entry.Name))
                    highest = Math.Max(highest, VersionNumber(entry.Name));
            }

            var newest = highest == 0 ? null : entries.Find(entry => entry.Name == VersionName(highest));
            var reuse = newest is FileInfo { LinkTarget: null } file && file.Length == previous.Length && HoldsBytesOnDisk(file.FullName, previous);
            if (!reuse && highest >= MaxVersionNumber)
            {
                Logger.Warning("Kept no earlier version of profile {ProfileId}: {Folder} holds the highest version number a name can", id, folder);
                return false;
            }

            if (!_versionsOnDisk.Contains(id))
            {
                AtomicFile.CreateFolder(folder, Paths.Root, _faults);
                _versionsOnDisk.Add(id);
            }

            if (reuse)
                AtomicFile.SyncFolder(folder, _faults);
            else
                AtomicFile.Write(Paths.VersionFile(id, highest + 1), previous, root: null, _faults);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(ex, "Could not keep the earlier version of profile {ProfileId}; the save goes on", id);
            return false;
        }
    }

    /// <summary>The name of version <paramref name="n"/>: <c>{n}.json</c>.</summary>
    private static string VersionName(ulong n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json";

    /// <summary>
    /// Whether the regular file at <paramref name="path"/> holds exactly <paramref name="bytes"/>, on disk: its content is
    /// then flushed through the handle it was read on (<see cref="RegularFile.OpenToFlush"/>) with the store's checked flush (<see cref="AtomicFile.FlushFile"/>: any failure counts, <c>EINVAL</c> and <c>ENOTSUP</c> too), since a file that holds them
    /// may have been written without a flush. False when it can't be read, holds other bytes, or can't be flushed (logged).
    /// </summary>
    private bool HoldsBytesOnDisk(string path, byte[] bytes)
    {
        try
        {
            using var stream = RegularFile.OpenToFlush(path);
            var content = new byte[bytes.Length + 1];
            var read = stream.ReadAtLeast(content, content.Length, throwOnEndOfStream: false);
            if (!content.AsSpan(0, read).SequenceEqual(bytes))
                return false;
            AtomicFile.FlushFile(stream.SafeFileHandle, path, _faults);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(ex, "Could not reuse {Path} as an earlier saved version; a new one is kept", path);
            return false;
        }
    }

    /// <summary>
    /// The length of the file at <paramref name="path"/> when it is a regular file, its type checked on an open handle
    /// (<see cref="RegularFile.OpenRead"/>, which never waits on a FIFO); null when it isn't one or can't be opened.
    /// </summary>
    private static long? RegularLength(string path)
    {
        try
        {
            using var stream = RegularFile.OpenRead(path);
            return stream.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Drops a profile's oldest versions until at most <see cref="MaxVersions"/> are left within
    /// <see cref="MaxVersionBytes"/>, the newest always kept; only regular files of a version's exact name (the type
    /// checked on an open handle, <see cref="RegularLength"/>) count, and only they are removed; any other entry is left
    /// alone. Not flushed here: the blob sweep flushes <c>versions</c> before it deletes any blob
    /// (<see cref="FlushBeforeBlobsGo"/>), and a dropped version that comes back after a power loss is dropped at the
    /// next save. A failure is logged.
    /// </summary>
    private void TryDropVersions(string id)
    {
        var folder = Paths.VersionsFolder(id);
        try
        {
            if (!IsOwnFolderOrAbsent(folder))
                return;
            var versions = new List<(ulong Number, string Path, long Length)>();
            foreach (var entry in RegularFile.List(folder, new ListingBudget(RegularFile.MaxListing, $"Listing {folder}")))
            {
                if (entry is FileInfo && entry.LinkTarget == null && IsVersionName(entry.Name) && RegularLength(entry.FullName) is { } length)
                    versions.Add((VersionNumber(entry.Name), entry.FullName, length));
            }

            versions.Sort((x, y) => y.Number.CompareTo(x.Number));
            var keep = 0;
            long bytes = 0;
            foreach (var version in versions)
            {
                bytes += version.Length;
                if (keep > 0 && (keep >= MaxVersions || bytes > MaxVersionBytes))
                    break;
                keep++;
            }

            for (var i = versions.Count - 1; i >= keep; i--)
            {
                var path = versions[i].Path;
                try
                {
                    _faults?.Before(StorageStep.DeleteFile, path);
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.Warning(ex, "Could not drop {Path}, an earlier saved version; it is dropped at a later save", path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(ex, "Could not drop profile {ProfileId}'s oldest saved versions; they are dropped at a later save", id);
        }
    }

    /// <summary>
    /// For <see cref="BlobSweep"/>, before it deletes any blob: flushes <c>profiles/</c>, then each listed profile's
    /// folder, and its <c>versions</c> folder when it is one of its own, so that a version's removal, a document's
    /// replacement or a profile's delete (its folder renamed, and left when that rename couldn't be flushed) that a power
    /// loss could still undo is on disk before a blob the undone file refers to can go.
    /// </summary>
    /// <exception cref="IOException">A folder couldn't be flushed; the sweep then deletes nothing.</exception>
    /// <exception cref="UnauthorizedAccessException">A folder couldn't be opened to flush it.</exception>
    internal void FlushBeforeBlobsGo()
    {
        lock (_lock)
        {
            if (RegularFile.StateOf(Paths.ProfilesDirectory) != PathState.Absent)
                AtomicFile.SyncFolder(Paths.ProfilesDirectory, _faults);
            foreach (var id in Order)
            {
                var folder = Paths.ProfileFolder(id);
                if (RegularFile.StateOf(folder) != PathState.Folder)
                    continue;
                var versions = Paths.VersionsFolder(id);
                if (RegularFile.StateOf(versions) == PathState.Folder && IsOwnFolderOrAbsent(versions))
                    AtomicFile.SyncFolder(versions, _faults);
                AtomicFile.SyncFolder(folder, _faults);
            }
        }
    }

    /// <summary>Whether nothing is at <paramref name="folder"/> or a folder that isn't a link is: never one reached through a link.</summary>
    private static bool IsOwnFolderOrAbsent(string folder) => RegularFile.StateOf(folder) switch
    {
        PathState.Absent => true,
        PathState.Folder => new DirectoryInfo(folder).LinkTarget == null,
        _ => false,
    };

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
            _versionsOnDisk.Remove(id);
            var state = RegularFile.StateOf(folder);
            if (state != PathState.Absent)
            {
                _faults?.Before(StorageStep.MoveFolder, deleted);
                // A link to nothing (a profile on a drive that isn't there) is renamed itself, never what it leads to.
                if (state == PathState.Folder)
                    Directory.Move(folder, deleted);
                else
                    File.Move(folder, deleted, overwrite: true);
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
    /// The sweep's roots (<see cref="BlobSweep"/>), by the root rule of docs/2.0/data-folder.md ("The blob sweep"): under
    /// each folder of <c>profiles/</c> (profile folders, and folders not named by a valid id; never staging or deleted
    /// folders), every file at any depth but known OS metadata and the engine's write leftovers (<see cref="IsIgnored"/>),
    /// read through links (a linked folder is walked, each real folder once; a linked file is read through its link); and
    /// each folder's <c>document.json</c> (<see cref="SweepRoot.IsDocument"/>), listed even when it is missing. Every
    /// listing counts against <paramref name="budget"/>; none when <c>profiles/</c> isn't there at all.
    /// </summary>
    /// <exception cref="IOException">
    /// Whether a folder or a document exists, what a folder holds or where a link leads can't be told, a link leads
    /// nowhere (<c>profiles/</c> itself included: <see cref="UnresolvedLinkException"/>), or the budget ran out
    /// (<see cref="ListingBudgetException"/>): a link could hide roots that can't be read.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">What a folder holds can't be told.</exception>
    internal IReadOnlyList<SweepRoot> SweepRoots(ListingBudget budget)
    {
        lock (_lock)
        {
            var roots = new SortedDictionary<string, bool>(StringComparer.Ordinal);
            var walked = new HashSet<string>(StringComparer.Ordinal);
            var entries = RegularFile.List(Paths.ProfilesDirectory, budget).ToArray();
            if (entries.Length == 0)
                return [];

            var profilesReal = PathExtensions.ResolveRealPath(Paths.ProfilesDirectory);
            foreach (var entry in entries)
            {
                if (entry is not DirectoryInfo folder)
                {
                    // A link to nothing may stand for a profile's folder on a drive that isn't there (a link that loops
                    // throws here too).
                    if (entry.LinkTarget != null && entry.ResolveLinkTarget(returnFinalTarget: true) is not { Exists: true })
                        throw new IOException($"{entry.FullName} is a link to nothing, so the roots it may lead to can't be read.");
                    continue;
                }

                if (IsLeftover(folder.Name))
                    continue;
                var document = Path.Combine(folder.FullName, DocumentFileName);
                if (IsValidId(folder.Name) || FileExists(document))
                    roots[document] = true;
                var real = folder.LinkTarget != null ? PathExtensions.ResolveRealPath(folder.FullName) : Path.Combine(profilesReal, folder.Name);
                Walk(folder, real, roots, walked, budget);
            }

            return roots.Select(root => new SweepRoot(root.Key, root.Value)).ToList();
        }
    }

    /// <summary>
    /// Adds every file under <paramref name="top"/> but the ignored ones to <paramref name="roots"/>, through links; each
    /// folder is walked once, by its real path (<paramref name="walked"/>), so a link back to a folder already walked adds
    /// nothing. A link to nothing is added as a file: a root that can't be read. Every listing counts against
    /// <paramref name="budget"/>.
    /// </summary>
    private static void Walk(DirectoryInfo top, string topReal, SortedDictionary<string, bool> roots, HashSet<string> walked, ListingBudget budget)
    {
        var pending = new Stack<(DirectoryInfo Folder, string Real)>();
        if (walked.Add(topReal))
            pending.Push((top, topReal));
        while (pending.TryPop(out var item))
        {
            foreach (var entry in RegularFile.List(item.Folder.FullName, budget))
            {
                if (entry is DirectoryInfo inner)
                {
                    var real = inner.LinkTarget != null ? PathExtensions.ResolveRealPath(inner.FullName) : Path.Combine(item.Real, inner.Name);
                    if (walked.Add(real))
                        pending.Push((inner, real));
                }
                else if (!IsIgnored(entry.Name))
                {
                    roots.TryAdd(entry.FullName, false);
                }
            }
        }
    }

    /// <summary>
    /// Files under a profile's folder that aren't sweep roots: known OS metadata (Finder's <c>.DS_Store</c> and
    /// AppleDouble <c>._*</c> files, Windows' <c>Thumbs.db</c> and <c>desktop.ini</c>) and what an interrupted write left,
    /// discarded (<c>{name}.{32 hex}.tmp</c>, the engine's temp files).
    /// </summary>
    internal static bool IsIgnored(string name) =>
        name.StartsWith("._", StringComparison.Ordinal)
        || name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
        || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
        || AtomicFile.IsTempFile(name, _ => true);

    /// <summary>
    /// Reads <paramref name="path"/> whole, a regular file only, reading at most <paramref name="maxBytes"/> + 1 bytes of
    /// it (<see cref="RegularFile.ReadAtMost"/>).
    /// </summary>
    /// <exception cref="InvalidDataException">The file is larger than <paramref name="maxBytes"/>.</exception>
    /// <exception cref="NotARegularFileException">A FIFO, socket or device is at the path; it wasn't read.</exception>
    internal static byte[] ReadAtMost(string path, int maxBytes) => RegularFile.ReadAtMost(path, maxBytes);

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

    /// <summary>
    /// The folders in <paramref name="path"/> (<see cref="RegularFile.List"/>), and the links in it whose target can't be
    /// reached (as <see cref="FileInfo"/>s: each may be a folder on a drive that isn't there); none when nothing is at the
    /// path. A path that is a link to nothing, or isn't a folder, is thrown, never read as empty.
    /// </summary>
    /// <exception cref="IOException">The folder can't be listed (<see cref="UnresolvedLinkException"/>, <see cref="ListingBudgetException"/>).</exception>
    /// <exception cref="UnauthorizedAccessException">The folder may not be listed.</exception>
    private static FileSystemInfo[] Subfolders(string path) =>
        RegularFile.List(path, new ListingBudget(RegularFile.MaxListing, $"Listing {path}"))
            .Where(entry => entry is DirectoryInfo || (entry.LinkTarget != null && RegularFile.StateOf(entry.FullName) == PathState.Unresolved))
            .ToArray();

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

    /// <summary>
    /// Removes what interrupted operations left; an entry that can't be removed is logged and left. <c>profiles/</c> is
    /// listed first, so a folder that can't be listed stops the open before anything is removed.
    /// </summary>
    private void RemoveLeftovers()
    {
        var entries = Subfolders(Paths.ProfilesDirectory);
        foreach (var file in RegularFile.List(Paths.Root, new ListingBudget(RegularFile.MaxListing, $"Listing {Paths.Root}")))
        {
            if (file is FileInfo && AtomicFile.IsTempFile(file.Name, name => name == Path.GetFileName(Paths.ProfileOrderFile)))
                TryDeleteTree(file.FullName);
        }

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

        var cleanup = new ListingBudget(CleanupEntries, "Removing what interrupted operations left in " + Paths.ProfilesDirectory);
        foreach (var entry in entries)
        {
            if (IsLeftover(entry.Name))
            {
                if (IsDeleted(entry.Name) && !deletedFlushed)
                    continue;
                Logger.Information("Removing {Folder}, left by an interrupted operation", entry.FullName);
                TryDeleteTree(entry.FullName, cleanup);
            }
            else if (IsValidId(entry.Name) && entry is DirectoryInfo && entry.LinkTarget == null)
            {
                foreach (var file in RegularFile.List(entry.FullName, new ListingBudget(RegularFile.MaxListing, $"Listing {entry.FullName}")))
                {
                    if (file is FileInfo && AtomicFile.IsTempFile(file.Name, name => name is DocumentFileName or RecoveryFileName))
                        TryDeleteTree(file.FullName);
                }

                RemoveVersionLeftovers(Path.Combine(entry.FullName, VersionsFolderName));
            }
        }
    }

    /// <summary>
    /// Removes the temp files an interrupted version write left in a profile's <c>versions</c> folder
    /// (<c>{n}.json.{32 hex}.tmp</c>), only in a folder of the profile's own (never through a link); a folder that can't be
    /// listed is logged and left, and start-up goes on.
    /// </summary>
    private void RemoveVersionLeftovers(string folder)
    {
        try
        {
            if (!IsOwnFolderOrAbsent(folder))
                return;
            foreach (var file in RegularFile.List(folder, new ListingBudget(RegularFile.MaxListing, $"Listing {folder}")))
            {
                if (file is FileInfo && AtomicFile.IsTempFile(file.Name, IsVersionName))
                    TryDeleteTree(file.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(ex, "Could not remove what interrupted writes left in {Folder}; it is removed at a later start", folder);
        }
    }

    /// <summary>
    /// The ids <c>profiles.json</c> lists (valid ones, in order), and whether it could be read; its other members are kept,
    /// as written, for its next write (<see cref="_orderMembers"/>), even when the order in it is damaged. A file that
    /// isn't JSON, or has no <c>order</c> array, is damaged: the caller rebuilds it. A file the store can't hold whole
    /// (over <see cref="ProfileListBytes"/>, well-formed but nested deeper than jsonDepth, or one it can't open, a FIFO say)
    /// is left as it is for the run (<see cref="_orderFileKept"/>).
    /// </summary>
    private (List<string> Ids, bool Readable) ReadOrder()
    {
        var path = Paths.ProfileOrderFile;
        _orderMembers = [null];
        _orderFileKept = false;
        if (RegularFile.StateOf(path) == PathState.Absent)
            return ([], false);

        byte[] bytes;
        List<JsonMemberText> members;
        try
        {
            bytes = ReadAtMost(path, ProfileListBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return KeepOrderFile(ex.Message);
        }

        try
        {
            members = EngineJson.Members(bytes);
        }
        catch (JsonException ex)
        {
            if (EngineJson.IsTooDeep(bytes))
                return KeepOrderFile($"it nests deeper than {EngineJson.MaxDepth} levels");
            Logger.Warning(ex, "Could not read {Path}; the profiles are listed by id", path);
            return ([], false);
        }

        // The order keeps its first place; its value is the last one, as a JSON reader takes a repeated member.
        var kept = new List<JsonMemberText?>();
        JsonMemberText? order = null;
        foreach (var member in members)
        {
            if (member.Name != "order")
                kept.Add(member);
            else if (order is null)
                kept.Add(null);
            if (member.Name == "order")
                order = member;
        }
        if (order is null)
            kept.Insert(0, null);
        _orderMembers = kept;

        if (order is null || order.Value[0] != (byte)'[')
        {
            Logger.Warning("Could not read {Path}: it has no \"order\" array; the profiles are listed by id", path);
            return ([], false);
        }

        var ids = new List<string>();
        using var list = JsonDocument.Parse(order.Value, EngineJson.DocumentOptions);
        foreach (var entry in list.RootElement.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String && TextOf(entry) is { } id && IsValidId(id))
                ids.Add(id);
            else
                Logger.Warning("Ignored {Entry} in {Path}: not a profile id", entry.GetRawText(), path);
        }

        return (ids, true);

        static string? TextOf(JsonElement entry)
        {
            try
            {
                return entry.GetString();
            }
            catch (InvalidOperationException)
            {
                return null; // a string that holds no text
            }
        }
    }

    /// <summary>Leaves <c>profiles.json</c> as it is for the run (<see cref="_orderFileKept"/>): the profiles are listed by id.</summary>
    private (List<string> Ids, bool Readable) KeepOrderFile(string why)
    {
        Logger.Warning("Left {Path} as it is: it can't be read whole ({Reason}); the profiles are listed by id, and their order is kept in memory and not written this run",
            Paths.ProfileOrderFile, why);
        _orderFileKept = true;
        return ([], false);
    }

    /// <exception cref="ProfileListFullException">The order file would be over <see cref="ProfileListBytes"/>; nothing was written.</exception>
    private void WriteOrder(List<string> order, Action? replaced = null)
    {
        var bytes = OrderBytes(order);
        if (bytes.Length > ProfileListBytes)
            throw new ProfileListFullException(bytes.Length, ProfileListBytes);
        if (_orderFileKept)
        {
            // Left as it is for the run: the order lives in memory.
            replaced?.Invoke();
            return;
        }
        AtomicFile.Write(Paths.ProfileOrderFile, stream => stream.Write(bytes), Paths.Root, _faults, replaced);
    }

    /// <summary><c>profiles.json</c>'s bytes for <paramref name="order"/>, with the members this engine doesn't know as they were read.</summary>
    private byte[] OrderBytes(List<string> order)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var id in order)
                writer.WriteStringValue(id);
            writer.WriteEndArray();
        }

        var list = new JsonMemberText("\"order\""u8.ToArray(), "order", stream.ToArray());
        return EngineJson.Object(_orderMembers.Select(member => member ?? list));
    }

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

    /// <summary>
    /// Removes a file or a folder and everything in it; links are removed, never followed. Its folders are listed within
    /// <paramref name="budget"/> (by default <see cref="RegularFile.MaxListing"/> entries for this tree): past it, what is
    /// left stays for the next start. Failures are logged.
    /// </summary>
    private void TryDeleteTree(string path, ListingBudget? budget = null)
    {
        try
        {
            FileSystemInfo entry = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (entry.Exists || entry.LinkTarget != null)
                DeleteTree(entry, budget ?? new ListingBudget(RegularFile.MaxListing, $"Removing {path}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(ex, "Could not remove {Path}; it is removed at the next start", path);
        }
    }

    private void DeleteTree(FileSystemInfo entry, ListingBudget budget)
    {
        if (entry is DirectoryInfo directory && entry.LinkTarget == null)
        {
            foreach (var child in RegularFile.List(directory.FullName, budget))
                DeleteTree(child, budget);
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

/// <summary>A root of the blob sweep: a file whose text may refer to blobs; <see cref="IsDocument"/> for a profile folder's <c>document.json</c>, which must be JSON.</summary>
internal readonly record struct SweepRoot(string Path, bool IsDocument);
