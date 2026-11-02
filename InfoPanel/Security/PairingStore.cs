using InfoPanel.Documents;
using InfoPanel.Hosting;
using InfoPanel.Persistence;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace InfoPanel.Security;

/// <summary>
/// A client's lasting pairing (spec/docs/api.md, "Pairing"): its id, name, creation, last use and its secret
/// (<see cref="Secret"/>, 32 bytes as 43 base64url characters), which the engine keeps to compute the proofs with. A
/// replaced pairing (<see cref="ReplacedBy"/>, since <see cref="Replaced"/>) is kept for its handover, unlisted.
/// </summary>
public sealed record PairingRecord(string Id, string Name, DateTimeOffset Created, DateTimeOffset? LastUsed, string Secret)
{
    /// <summary>The pairing that replaced this one; null while it is active.</summary>
    public string? ReplacedBy { get; init; }

    /// <summary>When it was replaced.</summary>
    public DateTimeOffset? Replaced { get; init; }

    public bool IsReplaced => ReplacedBy != null;

    /// <summary>The pairing's members in the file that this engine doesn't know, as written, kept when it rewrites the file.</summary>
    internal IReadOnlyList<JsonMemberText>? Unknown { get; init; }
}

/// <summary>
/// The paired clients and the engine's last port, kept in <c>{data}/pairings.json</c> (docs/2.0/data-folder.md):
/// <c>{"pairings":[{id, name, created, lastUsed?, secret, replacedBy?, replaced?}], "port": n}</c>. A pairing is made by
/// a one-time code's exchange (<see cref="PairingCodes"/>) and holds a secret the client keeps and never sends; the engine
/// keeps the secret itself, not a hash, since it computes the proofs with it. The file is private on every system:
/// written with <see cref="AtomicFile.WritePrivate(string, ReadOnlyMemory{byte}, string?, Func{string, FileStream}?, Action?)"/>,
/// 0600 on Unix and, on Windows, through a temp file with a protected access list for this user only
/// (<see cref="IWindowsSecurity.CreateFile"/>), which the rename keeps.
/// <list type="bullet">
/// <item>Tolerant, as the engine's own files are: members it doesn't know, at the top and in each pairing, are kept as
/// written; held to <see cref="MaxFileBytes"/>; only a regular file is read.</item>
/// <item>A file it can't read (over the cap, not JSON, not a regular file, a member it knows that it can't read) is logged
/// as an error and never rewritten that run: the run has no pairings (<see cref="Readable"/> false), and the engine still
/// starts.</item>
/// <item>A pairing a new one <c>replaces</c> is handed over, not removed: it gets <c>replacedBy</c> and
/// <c>replaced</c>, isn't listed, and goes when its replacement is removed, first at the limit, or
/// <see cref="HandoverPeriod"/> after it was replaced (checked at start and at each create).</item>
/// <item>At most <see cref="MaxPairings"/>: a create past it drops replaced pairings first, then the least recently used.</item>
/// <item><c>port</c> is the engine's last port: written with each new pairing and, at start, when pairings exist and it
/// differs (<see cref="RememberPort"/>); an engine with no pairings writes no file.</item>
/// </list>
/// Nothing here logs a secret.
/// </summary>
public sealed partial class PairingStore
{
    /// <summary>The most pairings the file holds, replaced ones included.</summary>
    public const int MaxPairings = 32;

    /// <summary>How long a replaced pairing is kept for its handover.</summary>
    public static readonly TimeSpan HandoverPeriod = TimeSpan.FromDays(30);

    /// <summary>The most bytes of <c>pairings.json</c>, the members it keeps included: a larger file isn't read.</summary>
    public const int MaxFileBytes = 1024 * 1024;

    private const int SecretBytes = 32;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = EngineJson.MaxDepth,
        // A number written as a string isn't one: the file is unreadable.
        NumberHandling = JsonNumberHandling.Strict,
    };

    private static readonly string[] FileMembers = ["pairings", "port"];

    private static readonly string[] PairingMembers = ["id", "name", "created", "lastUsed", "secret", "replacedBy", "replaced"];

    private readonly string _root;
    private readonly TimeProvider _time;
    private readonly Serilog.ILogger _logger;
    private readonly Func<string, FileStream>? _createTemp;
    private readonly Lock _lock = new();
    private readonly IReadOnlyList<JsonMemberText>? _unknown;

    /// <summary>Replaced, never changed, under <see cref="_lock"/>; read without it. As the file holds them, oldest first.</summary>
    private volatile PairingRecord[] _records;

    /// <summary>
    /// The pairings exactly as the file holds them (as read, or as last written), under <see cref="_lock"/>: as
    /// <see cref="_records"/> without the uses whose write failed. A create, removal or port is written from these, so a
    /// use that couldn't be written (the file at its cap) never makes a removal's file larger than the one on disk.
    /// </summary>
    private PairingRecord[] _saved;

    private int? _port;

    private PairingStore(string path, string root, TimeProvider time, Serilog.ILogger logger, Func<string, FileStream>? createTemp, bool readable,
        PairingRecord[] records, int? port, IReadOnlyList<JsonMemberText>? unknown)
    {
        FilePath = path;
        _root = root;
        _time = time;
        _logger = logger;
        _createTemp = createTemp;
        Readable = readable;
        _records = records;
        _saved = records;
        _port = port;
        _unknown = unknown;
    }

    /// <summary>The file's path.</summary>
    public string FilePath { get; }

    /// <summary>
    /// Whether the file could be read (or there was none). When it couldn't, the run has no pairings and the file is
    /// never rewritten.
    /// </summary>
    public bool Readable { get; }

    /// <summary>The engine's last port, as the file holds it; null without one.</summary>
    public int? Port
    {
        get
        {
            lock (_lock)
                return _port;
        }
    }

    /// <summary>Raised with a pairing's id after it is removed (by any path), so what is kept for it can go too.</summary>
    public event Action<string>? Removed;

    /// <summary>Tests: called before every write of the file; an exception thrown here is the write failing.</summary>
    internal Action? Saving;

    /// <summary>
    /// Tests: called once the file is replaced, before its folder is flushed; an exception thrown here is the flush failing,
    /// with the file already holding the new content.
    /// </summary>
    internal Action? Flushing;

    /// <summary>
    /// Reads <paramref name="path"/> (none yet: no pairings) and removes temp files an interrupted write left beside it.
    /// Never throws for the file: one it can't read is logged and leaves the store unreadable for the run.
    /// </summary>
    /// <param name="windows">Windows' file privacy (tests pass a fake on any system); the real one on Windows.</param>
    public static PairingStore Open(string path, string root, TimeProvider time, Serilog.ILogger logger, IWindowsSecurity? windows = null)
    {
        windows ??= OperatingSystem.IsWindows() ? new WindowsPrivateFiles() : null;
        Func<string, FileStream>? createTemp = windows is null ? null : temp => windows.CreateFile(temp, windows.CurrentUser);
        RemoveTempFiles(path, logger);
        try
        {
            var (records, port, unknown) = Parse(RegularFile.ReadAtMost(path, MaxFileBytes));
            var store = new PairingStore(path, root, time, logger, createTemp, true, records, port, unknown);
            store.EndHandovers();
            return store;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException && RegularFile.StateOf(path) == PathState.Absent)
        {
            return new PairingStore(path, root, time, logger, createTemp, true, [], null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or FormatException
            or InvalidOperationException or ArgumentException)
        {
            logger.Error("{Path} can't be read ({Reason}); this run has no paired clients, and the file is left as it is", path, ex.Message);
            return new PairingStore(path, root, time, logger, createTemp, false, [], null, null);
        }
    }

    /// <summary>The active pairings, oldest first: replaced ones aren't listed.</summary>
    public IReadOnlyList<PairingRecord> List() => [.. _records.Where(r => !r.IsReplaced)];

    /// <summary>The pairing with <paramref name="id"/>, active or replaced; null when there is none.</summary>
    public PairingRecord? Find(string id) => _records.FirstOrDefault(r => r.Id == id);

    /// <summary>
    /// Makes a pairing named <paramref name="name"/> and writes it with <paramref name="port"/>, the engine's port. The
    /// active pairing <paramref name="replaces"/> names, if any, is handed over; replaced pairings past
    /// <see cref="HandoverPeriod"/> go, and past <see cref="MaxPairings"/> replaced ones go first, then the least recently
    /// used. Null when the file can't be read (nothing is made).
    /// </summary>
    /// <exception cref="IOException">The file couldn't be written; nothing was made.</exception>
    public PairingRecord? Create(string name, string? replaces, int port)
    {
        if (!Readable)
            return null;

        var now = Truncate(_time.GetUtcNow());
        var record = new PairingRecord(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), name, now, null,
            Base64Url(RandomNumberGenerator.GetBytes(SecretBytes)));
        List<PairingRecord> gone;
        var dropped = new List<PairingRecord>();
        lock (_lock)
        {
            // Decided on the uses known (the limit's order), written from the file's own records.
            var next = Expired(_records, now, out gone);
            if (replaces != null && next.FindIndex(r => r.Id == replaces && !r.IsReplaced) is var index and >= 0)
                next[index] = next[index] with { ReplacedBy = record.Id, Replaced = now };
            next.Add(record);
            while (next.Count > MaxPairings)
            {
                var drop = next.Where(r => r.IsReplaced).OrderBy(r => r.Replaced).FirstOrDefault()
                    ?? next.Where(r => r.Id != record.Id).OrderBy(r => r.LastUsed ?? r.Created).First();
                next.Remove(drop);
                gone.Add(drop);
                dropped.Add(drop);
            }

            var saved = _saved.ToDictionary(r => r.Id, StringComparer.Ordinal);
            PairingRecord[] file = [.. next.Select(r => saved.TryGetValue(r.Id, out var s) ? s with { ReplacedBy = r.ReplacedBy, Replaced = r.Replaced } : r)];
            Save(file, port);
            _saved = file;
            _records = [.. next];
            _port = port;
        }

        foreach (var drop in dropped)
            _logger.Information("Removed the paired client {PairingId} ({PairingName}): over the limit of {Limit}", drop.Id, drop.Name, MaxPairings);
        _logger.Information("Paired the client {PairingId} ({PairingName}){Replaced}", record.Id, name,
            replaces != null && Find(replaces)?.ReplacedBy == record.Id ? $", replacing {replaces}" : "");
        Raise(gone);
        return record;
    }

    /// <summary>
    /// Removes an active pairing and every pairing it replaced: false when there is no active pairing with
    /// <paramref name="id"/>, or the file can't be read.
    /// </summary>
    /// <exception cref="IOException">The file couldn't be written; nothing was removed.</exception>
    public bool Remove(string id) => Remove(id, activeOnly: true);

    /// <summary>As <see cref="Remove"/>, for a pairing active or replaced.</summary>
    public bool RemoveAny(string id) => Remove(id, activeOnly: false);

    private bool Remove(string id, bool activeOnly)
    {
        if (!Readable)
            return false;

        List<PairingRecord> gone;
        lock (_lock)
        {
            if (_records.FirstOrDefault(r => r.Id == id) is not { } found || activeOnly && found.IsReplaced)
                return false;
            // The pairing and, through their replacedBy, every pairing it replaced, at any depth.
            var ids = new HashSet<string>(StringComparer.Ordinal) { id };
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var r in _records)
                    grew |= r.ReplacedBy is { } by && ids.Contains(by) && ids.Add(r.Id);
            }

            gone = [.. _records.Where(r => ids.Contains(r.Id))];
            // Written from the file's own records: never larger than the file on disk, whatever uses couldn't be written.
            var file = _saved.Where(r => !ids.Contains(r.Id)).ToArray();
            Save(file, _port);
            _saved = file;
            _records = _records.Where(r => !ids.Contains(r.Id)).ToArray();
        }

        foreach (var r in gone)
            _logger.Information("Removed the paired client {PairingId} ({PairingName})", r.Id, r.Name);
        Raise(gone);
        return true;
    }

    /// <summary>
    /// Records a pairing's use (a successful proof), written at most once per <see cref="TokenStore.LastUsedResolution"/>;
    /// a write that fails is logged and kept in memory only.
    /// </summary>
    public void Touch(string id)
    {
        if (!Readable)
            return;

        var now = Truncate(_time.GetUtcNow());
        lock (_lock)
        {
            var index = Array.FindIndex(_records, r => r.Id == id);
            if (index < 0 || _records[index].LastUsed is { } last && now - last < TokenStore.LastUsedResolution)
                return;
            var next = (PairingRecord[])_records.Clone();
            next[index] = next[index] with { LastUsed = now };
            try
            {
                Save(next, _port);
                _saved = next;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The use is kept in memory only (for the limit's order; tried again a minute later); the file and what a
                // create or removal writes from (_saved) are unchanged.
                _logger.Warning(ex, "Could not record the use of the paired client {PairingId} in {Path}", id, FilePath);
            }

            _records = next;
        }
    }

    /// <summary>
    /// The engine listens on <paramref name="port"/>: written when pairings exist and the file holds another port. A write
    /// that fails is logged.
    /// </summary>
    public void RememberPort(int port)
    {
        if (!Readable)
            return;

        lock (_lock)
        {
            if (_records.Length == 0 || _port == port)
                return;
            try
            {
                Save(_saved, port);
                _port = port;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warning(ex, "Could not record the port {Port} in {Path}", port, FilePath);
            }
        }
    }

    /// <summary>At start: the replaced pairings past their handover go.</summary>
    private void EndHandovers()
    {
        List<PairingRecord> gone;
        lock (_lock)
        {
            var next = Expired(_records, Truncate(_time.GetUtcNow()), out gone);
            if (gone.Count == 0)
                return;
            try
            {
                Save([.. next], _port);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Dropped for this run anyway: no proof is answered for them; the file loses them at the next write.
                _logger.Warning(ex, "Could not write {Path}", FilePath);
            }

            // Gone either way: a removal or create never writes them back (a smaller file than the one on disk).
            _saved = [.. _saved.Where(r => !gone.Contains(r))];
            _records = [.. next];
        }

        foreach (var r in gone)
            _logger.Information("The handover of the paired client {PairingId} ({PairingName}) ended", r.Id, r.Name);
        Raise(gone);
    }

    private static List<PairingRecord> Expired(PairingRecord[] records, DateTimeOffset now, out List<PairingRecord> gone)
    {
        gone = [.. records.Where(r => r.Replaced is { } at && now - at >= HandoverPeriod)];
        var expired = gone;
        return [.. records.Where(r => !expired.Contains(r))];
    }

    private void Raise(List<PairingRecord> gone)
    {
        foreach (var r in gone)
            Removed?.Invoke(r.Id);
    }

    /// <summary>
    /// Writes the file. A failure once it is replaced (its folder's flush) is logged and the write stands, since the file
    /// holds it: the caller's memory follows the file.
    /// </summary>
    /// <exception cref="IOException">The file couldn't be written (or would be over <see cref="MaxFileBytes"/>); it is unchanged.</exception>
    private void Save(PairingRecord[] records, int? port)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("pairings");
            foreach (var r in records)
            {
                var stored = new StoredPairing(r.Id, r.Name, TokenStore.Format(r.Created), r.LastUsed is { } used ? TokenStore.Format(used) : null,
                    r.Secret, r.ReplacedBy, r.Replaced is { } at ? TokenStore.Format(at) : null);
                writer.WriteRawValue(EngineJson.Append(JsonSerializer.SerializeToUtf8Bytes(stored, Json), r.Unknown), skipInputValidation: true);
            }
            writer.WriteEndArray();
            if (port is { } p)
                writer.WriteNumber("port", p);
            writer.WriteEndObject();
        }

        var bytes = EngineJson.Append(stream.ToArray(), _unknown);
        if (bytes.Length > MaxFileBytes)
            throw new IOException($"The paired clients would be {bytes.Length} bytes, over the limit of {MaxFileBytes}.");
        Saving?.Invoke();
        var replaced = false;
        try
        {
            AtomicFile.WritePrivate(FilePath, bytes, _root, _createTemp, () =>
            {
                replaced = true;
                Flushing?.Invoke();
            });
        }
        catch (Exception ex) when (replaced && ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "{Path} was written, but its folder couldn't be flushed to disk", FilePath);
        }
    }

    private static (PairingRecord[] Records, int? Port, IReadOnlyList<JsonMemberText>? Unknown) Parse(byte[] bytes)
    {
        var file = JsonSerializer.Deserialize<PairingFile>(bytes, Json) ?? throw new JsonException("The file is null.");
        if (file.Pairings == null)
            throw new JsonException("No \"pairings\" array.");
        if (file.Port is { } port && port is < 1 or > 65535)
            throw new JsonException($"The port {port} isn't a port.");

        var members = EngineJson.Members(bytes);
        var stored = members.LastOrDefault(m => IsKnown(m, FileMembers) && string.Equals(m.Name, "pairings", StringComparison.OrdinalIgnoreCase));
        List<byte[]> elements = stored is null ? [] : EngineJson.Elements(stored.Value);
        if (elements.Count != file.Pairings.Count)
            throw new JsonException("The pairings can't be told apart.");

        var records = new List<PairingRecord>();
        for (var i = 0; i < file.Pairings.Count; i++)
        {
            var p = file.Pairings[i];
            if (p == null || p.Id == null || !TokenStore.TokenId().IsMatch(p.Id) || records.Any(r => r.Id == p.Id))
                throw new JsonException("A pairing has a missing, malformed or repeated id.");
            if (p.Name == null || !TokenStore.IsValidName(p.Name))
                throw new JsonException($"Pairing {p.Id} has a malformed name.");
            if (p.Secret == null || !SecretText().IsMatch(p.Secret))
                throw new JsonException($"Pairing {p.Id} has a malformed secret.");
            if (p.Created == null)
                throw new JsonException($"Pairing {p.Id} has no creation time.");
            if (p.ReplacedBy != null && (!TokenStore.TokenId().IsMatch(p.ReplacedBy) || p.Replaced == null))
                throw new JsonException($"Pairing {p.Id} has a malformed replacement.");

            records.Add(new PairingRecord(p.Id, p.Name, ParseTime(p.Created), p.LastUsed is { } used ? ParseTime(used) : null, p.Secret)
            {
                ReplacedBy = p.ReplacedBy,
                Replaced = p.ReplacedBy != null ? ParseTime(p.Replaced!) : null,
                Unknown = Unknown(EngineJson.Members(elements[i]), PairingMembers),
            });
        }

        return ([.. records], file.Port, Unknown(members, FileMembers));
    }

    private static bool IsKnown(JsonMemberText member, string[] known) =>
        member.Name is { } name && known.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static List<JsonMemberText>? Unknown(List<JsonMemberText> members, string[] known) =>
        members.Where(m => !IsKnown(m, known)).ToList() is { Count: > 0 } unknown ? unknown : null;

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.ParseExact(text, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static DateTimeOffset Truncate(DateTimeOffset time) =>
        new(time.UtcTicks - time.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

    /// <summary>Unpadded base64url, as every binary value of the pairing is written.</summary>
    public static string Base64Url(ReadOnlySpan<byte> bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);

    private static void RemoveTempFiles(string path, Serilog.ILogger logger)
    {
        var folder = Path.GetDirectoryName(path)!;
        if (RegularFile.StateOf(folder) != PathState.Folder)
            return;
        var prefix = Path.GetFileName(path) + ".";
        foreach (var entry in RegularFile.List(folder, new ListingBudget(RegularFile.MaxListing, $"Listing {folder}")))
        {
            var name = entry.Name;
            if (entry is not FileInfo || !name.StartsWith(prefix, StringComparison.Ordinal) || !TempPart().IsMatch(name[(prefix.Length - 1)..]))
                continue;
            try
            {
                File.Delete(entry.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.Warning(ex, "Could not remove {Path}", entry.FullName);
            }
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{43}\\z")]
    private static partial Regex SecretText();

    [GeneratedRegex("^\\.[0-9a-f]{32}\\.tmp\\z")]
    private static partial Regex TempPart();

    private sealed record PairingFile(List<StoredPairing?>? Pairings, int? Port);

    private sealed record StoredPairing(string? Id, string? Name, string? Created, string? LastUsed, string? Secret, string? ReplacedBy, string? Replaced);
}
