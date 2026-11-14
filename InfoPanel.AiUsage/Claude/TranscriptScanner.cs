using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using InfoPanel.Plugins;

namespace InfoPanel.AiUsage;

/// <summary>
/// What a transcript scan may take: the entries looked at to list one folder (files and folders alike), the bytes read
/// and the time taken a round (its listings included), and the messages remembered (to count each once) over every folder.
/// </summary>
internal sealed record TranscriptBudget(int EntriesPerListing, long BytesPerRound, TimeSpan TimePerRound, int Messages)
{
    public static readonly TranscriptBudget Default = new(10_000, 4L * 1024 * 1024 * 1024, TimeSpan.FromSeconds(3), 2_000_000);
}

/// <summary>
/// Claude Code's tokens and their estimated cost, today (since local midnight) and over 7 days (the last 7 × 24 hours);
/// null when they can't be given, with <see cref="Note"/> saying why (also set when they are given but the cost leaves
/// something out).
/// </summary>
internal sealed record TokenReading(long? TokensToday, long? Tokens7d, double? CostToday, double? Cost7d, string? Note)
{
    public static readonly TokenReading None = new(null, null, null, null, null);
}

/// <summary>Claude Code's tokens and cost: each folder's, by its path (<see cref="ClaudeFolder.Path"/>), and every folder's summed.</summary>
internal sealed record TranscriptReading(IReadOnlyDictionary<string, TokenReading> Folders, TokenReading Combined);

/// <summary>
/// Reads Claude Code's transcripts for its tokens and their cost: in each Claude folder,
/// <c>projects/{project}/{session}.jsonl</c> and its subagents' <c>projects/{project}/{session}/subagents/*.jsonl</c>,
/// only those written in the last <see cref="Days"/> days (as their folder's listing tells it: an older file is never
/// opened). Each file is read once, then only what was appended to it, by the streaming reader
/// (<see cref="UsageLineReader"/>), which takes only an assistant line's time, ids, model, speed and token counts and no
/// text. A message (written once per content block, and again when a session is resumed) counts once, by its message id
/// and request id: once in each folder that holds it, and once in the folders together; Claude Code's own
/// <c>&lt;synthetic&gt;</c> messages don't count. Kept: per file how far it was read, a digest of its first bytes (to
/// tell a rewritten file from a grown one, never the bytes) and the reader's place in a line still being written; per
/// set of folders holding them, minute, model and speed the tokens of each kind; and a hash of each message's ids with
/// its minute and the folders that hold it, for the last <see cref="Days"/> days. A scan runs every <see cref="Every"/>
/// (and on each call while it hasn't caught up), within its <see cref="TranscriptBudget"/>, and carries on where it
/// stopped, its listing included. Not thread-safe: one caller at a time.
/// </summary>
internal sealed class TranscriptScanner(UsageEnvironment env, Prices? prices = null, TranscriptBudget? budget = null)
{
    /// <summary>How many days back files and messages are read: a week and a day, so 7 days are whole.</summary>
    public const int Days = 8;

    /// <summary>How often the transcripts are looked at again once caught up.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(15);

    /// <summary>How many of a file's first bytes are digested to tell, when it changes, that it is still the file read.</summary>
    public const int HeadBytes = 1024;

    /// <summary>The most models told apart; past them, a model counts as one with no price.</summary>
    public const int MaxModels = 64;

    /// <summary>The most Claude folders told apart (each is a bit of a message's folders).</summary>
    public const int MaxFolders = 64;

    public const string Synthetic = "<synthetic>";

    public const string UnreadableNote = "a Claude Code transcript may not be read; tokens are left out";

    private const string Other = "another model";

    private readonly Prices _prices = prices ?? Prices.Default;
    private readonly TranscriptBudget _budget = budget ?? TranscriptBudget.Default;
    private readonly Dictionary<string, FileState> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, Seen> _seen = new();
    private readonly Dictionary<Bucket, TokenCounts> _buckets = new();
    private readonly List<string> _models = [];
    private readonly Dictionary<string, int> _modelIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _folderIndex = new(StringComparer.Ordinal);
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private readonly byte[] _buffer = new byte[256 * 1024];
    private readonly byte[] _head = new byte[HeadBytes];
    private Action<UsageLine>? _onLine;
    private DateTimeOffset? _lastScan;
    private string? _lastFolders;
    private bool _behind;
    private string? _crowded;
    private bool _dropped;
    private int _done, _listed;

    // The listing under way, carried on in the next round where a round's time ran out.
    private ListingState? _listing;

    // The file being read and when its scan started, for the line handler.
    private FileState? _current;
    private DateTimeOffset _scanNow;

    /// <summary>The messages remembered, over every folder.</summary>
    internal int RememberedMessages => _seen.Count;

    /// <summary>
    /// Scans <paramref name="folders"/>' transcripts when a scan is due (every <see cref="Every"/>, at once while one
    /// hasn't caught up or the folders changed) and answers the tokens and cost as of now. Cancelled, it stops where it
    /// is (what it read so far is kept) and throws.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> was cancelled.</exception>
    public TranscriptReading Read(IReadOnlyList<ClaudeFolder> folders, CancellationToken cancel = default)
    {
        var now = env.Now;
        var names = string.Join('\n', folders.Select(f => f.Path));
        if (names != _lastFolders)
        {
            Use(folders);
            _lastFolders = names;
            _lastScan = null;
        }
        if (_lastScan is not { } last || now - last >= Every || now < last || _behind)
        {
            _lastScan = now;
            Scan(folders, now, cancel);
        }
        return Sum(folders, now);
    }

    /// <summary>
    /// Takes <paramref name="folders"/> as the folders read: one no longer among them is forgotten (its files, and its
    /// hold on each message: a message no other folder holds no longer counts), a new one gets an index, and the
    /// listing starts again.
    /// </summary>
    private void Use(IReadOnlyList<ClaudeFolder> folders)
    {
        var paths = folders.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var (path, index) in _folderIndex.Where(f => !paths.Contains(f.Key)).ToList())
        {
            _folderIndex.Remove(path);
            Forget(index);
        }
        foreach (var folder in folders)
        {
            if (_folderIndex.ContainsKey(folder.Path))
                continue;
            var used = _folderIndex.Values.ToHashSet();
            var free = Enumerable.Range(0, MaxFolders).Where(i => !used.Contains(i)).DefaultIfEmpty(-1).First();
            if (free >= 0)
                _folderIndex[folder.Path] = free;
        }
        _listing = null;
    }

    /// <summary>Forgets the folder of index <paramref name="folder"/>: its files, its messages' bit, and the tokens only it held.</summary>
    private void Forget(int folder)
    {
        var bit = 1UL << folder;
        foreach (var gone in _files.Where(f => f.Value.Folder == folder).Select(f => f.Key).ToList())
            _files.Remove(gone);
        foreach (var (key, seen) in _seen.Where(s => (s.Value.Folders & bit) != 0).ToList())
        {
            if (seen.Folders == bit)
                _seen.Remove(key);
            else
                _seen[key] = seen with { Folders = seen.Folders & ~bit };
        }
        foreach (var (bucket, counts) in _buckets.Where(b => (b.Key.Folders & bit) != 0).ToList())
        {
            _buckets.Remove(bucket);
            if (bucket.Folders != bit)
                AddTo(bucket with { Folders = bucket.Folders & ~bit }, counts);
        }
    }

    private void Scan(IReadOnlyList<ClaudeFolder> folders, DateTimeOffset now, CancellationToken cancel)
    {
        var started = env.Time.GetTimestamp();
        var outOfTime = false;
        bool OutOfTime() => outOfTime |= env.Time.GetElapsedTime(started) > _budget.TimePerRound;
        bool Stop() => cancel.IsCancellationRequested || OutOfTime();
        var since = now - TimeSpan.FromDays(Days);
        _scanNow = now;
        // Forgotten first: a message older than the last days makes room before a new one could be refused.
        Prune(since);

        // The files written in the last days, each with its folder: the listing goes on from where the last round's
        // stopped, and what it found so far is read.
        var listing = _listing ??= new ListingState(since);
        var complete = List(folders, listing, Stop, cancel);
        cancel.ThrowIfCancellationRequested();
        var listed = listing.Found;
        if (complete)
        {
            // Listed to their ends: what wasn't listed is gone or older now, and its state goes (its messages still count).
            var kept = listed.Select(l => l.File.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in _files.Keys.Where(p => !kept.Contains(p)).ToList())
                _files.Remove(gone);
            _crowded = listing.Crowded;
            _listing = null;
        }

        long bytes = 0;
        var stopped = false;
        var readOne = false;
        foreach (var (listedFile, folder) in listed)
        {
            cancel.ThrowIfCancellationRequested();
            // The round's first file is read even past its time, so a round whose listing took it all reads something.
            if (bytes >= _budget.BytesPerRound || (readOne && OutOfTime()))
            {
                stopped = true;
                break;
            }
            if (!_folderIndex.TryGetValue(folder.Path, out var index))
                continue;
            var path = listedFile.Path;
            // Unchanged since its last read as the listing tells it: not even opened.
            if (_files.TryGetValue(path, out var known) && !known.Pending && !known.Unreadable && known.Stamp is { } stamp
                && stamp.Length == listedFile.Length && stamp.LastWriteUtc == listedFile.LastWriteUtc)
                continue;
            if (env.Files.Stat(folder.Path, path, out var unreadable) is not { } looked)
            {
                if (unreadable)
                    Unreadable(path, index);
                else
                    _files.Remove(path); // gone, a link, or not a regular file
                continue;
            }
            if (!_files.TryGetValue(path, out var state))
                _files[path] = state = new FileState(index);
            if (looked == state.Stamp && !state.Pending && !state.Unreadable)
                continue;
            bytes += ReadFile(folder.Path, path, state, _budget.BytesPerRound - bytes, started, cancel);
            readOne = true;
        }

        _behind = !complete || stopped || _files.Values.Any(s => s.Pending);
        _listed = listed.Count;
        _done = listed.Count(l => _files.TryGetValue(l.File.Path, out var s) && s.Stamp != null && !s.Pending);
    }

    /// <summary>Marks the file at <paramref name="path"/> as one that may not be read: its folder's tokens are unavailable while it is.</summary>
    private void Unreadable(string path, int folder)
    {
        if (!_files.TryGetValue(path, out var state))
            _files[path] = state = new FileState(folder);
        state.Unreadable = true;
        if (_logged.Add("unreadable"))
            PluginLog.Warning("AI usage: a Claude Code transcript may not be read (no permission); its folder's tokens are left out");
    }

    /// <summary>
    /// Lists, from where <paramref name="at"/> stopped, <paramref name="folders"/>' transcripts written since its
    /// <see cref="ListingState.Since"/> into its <see cref="ListingState.Found"/>: each project's sessions, and the
    /// subagents of each of those sessions (a subagent writes while its session does, so a session not written in the last
    /// days has none that was). One folder's listing is a step: a round's first step runs whatever the time, the others
    /// only while there is time, and one cut short by the time is taken again, first, in the next round. True when the
    /// listings ended; false when told to stop before (nothing listed is then taken as gone).
    /// </summary>
    private bool List(IReadOnlyList<ClaudeFolder> folders, ListingState at, Func<bool> stop, CancellationToken cancel)
    {
        var max = _budget.EntriesPerListing;
        var first = true;
        bool Cancelled() => cancel.IsCancellationRequested;

        bool Step<T>(Func<Func<bool>, T> list, Func<T, bool> truncated, out T result)
        {
            result = default!;
            if (first)
            {
                first = false;
                result = list(Cancelled);
                if (Cancelled())
                    return false;
            }
            else
            {
                if (stop())
                    return false;
                result = list(stop);
                if (stop())
                    return false; // maybe cut short: listed again in the next round
            }
            if (truncated(result))
                at.Crowded ??= $"a Claude Code folder holds more than {max} entries; tokens are left out";
            return true;
        }

        while (at.Folder < folders.Count)
        {
            var folder = folders[at.Folder];
            if (_folderIndex.ContainsKey(folder.Path))
            {
                if (at.Projects == null)
                {
                    if (!Step(s => env.Files.Directories(Path.Combine(folder.Path, "projects"), "*", max, s), l => l.Truncated, out var projects))
                        return false;
                    at.Projects = projects.Paths;
                    at.Project = 0;
                }
                while (at.Project < at.Projects.Count)
                {
                    var project = at.Projects[at.Project];
                    if (at.Sessions == null)
                    {
                        if (!Step(s => env.Files.RecentFiles(project, "*.jsonl", at.Since, max, max, s), l => l.Truncated, out var sessions))
                            return false;
                        at.Sessions = sessions.Files;
                        at.Session = 0;
                    }
                    while (at.Session < at.Sessions.Count)
                    {
                        var session = at.Sessions[at.Session];
                        var subagents = Path.Combine(project, Path.GetFileNameWithoutExtension(session.Path), "subagents");
                        if (!Step(s => env.Files.RecentFiles(subagents, "*.jsonl", at.Since, max, max, s), l => l.Truncated, out var agents))
                            return false;
                        at.Found.Add((session, folder));
                        at.Found.AddRange(agents.Files.Select(a => (a, folder)));
                        at.Session++;
                    }
                    at.Sessions = null;
                    at.Project++;
                }
                at.Projects = null;
            }
            at.Folder++;
        }
        return true;
    }

    /// <summary>
    /// Reads <paramref name="path"/> on from where it was left, at most <paramref name="allowance"/> bytes and, past a
    /// first chunk, within the round's time; answers the bytes read. A file that changed in a way an append can't
    /// (another file, no longer than it was, other first bytes) is read again from its start; its messages already
    /// counted don't count again.
    /// </summary>
    private long ReadFile(string root, string path, FileState state, long allowance, long started, CancellationToken cancel)
    {
        long read = 0;
        try
        {
            using var file = env.Files.Open(root, path);
            state.Unreadable = false;
            var stamp = file.Stamp;
            if (state.Stamp is { } old && old != stamp && !Continues(state, old, file, ref read))
                state.Reset();
            if (state.HeadLength < Math.Min(HeadBytes, stamp.Length) && !SameHead(state, file, stamp.Length, ref read))
            {
                state.Reset();
                SameHead(state, file, stamp.Length, ref read);
            }
            state.Stamp = stamp;
            state.Pending = true;
            state.Reader ??= new UsageLineReader();
            _current = state;
            file.Stream.Seek(state.Seen, SeekOrigin.Begin);
            var ended = false;
            while (state.Seen < stamp.Length && read < allowance)
            {
                cancel.ThrowIfCancellationRequested();
                var n = file.Stream.Read(_buffer, 0, (int)Math.Min(_buffer.Length, Math.Min(stamp.Length - state.Seen, allowance - read)));
                if (n == 0)
                {
                    ended = true; // shorter than it was at the open: the next round's stamp tells what it is now
                    break;
                }
                read += n;
                state.Reader.Feed(_buffer.AsSpan(0, n), _onLine ??= Line);
                state.Seen += n;
                if (env.Time.GetElapsedTime(started) > _budget.TimePerRound)
                    break;
            }
            state.Pending = !ended && state.Seen < stamp.Length;
        }
        catch (UnauthorizedAccessException)
        {
            Unreadable(path, state.Folder);
        }
        catch (IOException ex)
        {
            if (_logged.Add("read:" + ex.GetType().Name))
                PluginLog.Warning($"AI usage: a Claude Code transcript wasn't read ({(ex is RefusedFileException ? ex.Message : ex.GetType().Name)})");
        }
        finally
        {
            _current = null;
            if (state.Reader is { AtLineStart: true })
                state.Reader = null; // nothing of a line held: the reader is made again when there is more
        }
        return read;
    }

    /// <summary>
    /// Whether the file open as <paramref name="file"/>, whose stamp differs from <paramref name="old"/>, is the one read
    /// before grown by appends only: the same file, longer than it was, and its first bytes as they were.
    /// </summary>
    private bool Continues(FileState state, FileStamp old, UsageFile file, ref long read)
    {
        var now = file.Stamp;
        if (old.Id != default && now.Id != default && old.Id != now.Id)
            return false;
        if (now.Length <= old.Length || now.Length <= state.Seen)
            return false;
        return SameHead(state, file, now.Length, ref read);
    }

    /// <summary>
    /// Whether the first bytes of the file open as <paramref name="file"/> (<paramref name="length"/> long) are those its
    /// digest was taken of (any, when none was); when they are, the digest is taken again of its first
    /// <see cref="HeadBytes"/> (all of it, when shorter). The bytes are looked at in a fixed buffer, cleared after: only
    /// their digest and how many they were are kept.
    /// </summary>
    private bool SameHead(FileState state, UsageFile file, long length, ref long read)
    {
        var want = (int)Math.Min(HeadBytes, length);
        try
        {
            file.Stream.Seek(0, SeekOrigin.Begin);
            var got = 0;
            int n;
            while (got < want && (n = file.Stream.Read(_head, got, want - got)) > 0)
                got += n;
            read += got;
            if (state.HeadLength > 0 && (got < state.HeadLength || Digest(_head.AsSpan(0, state.HeadLength)) != state.HeadDigest))
                return false;
            state.HeadLength = got;
            state.HeadDigest = Digest(_head.AsSpan(0, got));
            return true;
        }
        finally
        {
            Array.Clear(_head);
        }
    }

    /// <summary>The first 8 bytes of the SHA-256 of <paramref name="bytes"/>.</summary>
    private static ulong Digest(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }

    /// <summary>
    /// One assistant line of the file being read: a message of the last days not counted before in the file's folder is
    /// counted there (and, when no folder held it, in the folders together).
    /// </summary>
    private void Line(UsageLine line)
    {
        var state = _current!;
        if (line.Model == Synthetic)
            return;
        if (line.Problem != null || line.MessageId == null || line.Model == null || line.Timestamp == null)
        {
            state.Error = line.Problem ?? (line.MessageId == null ? "message.id missing" : line.Model == null ? "message.model missing" : "timestamp missing");
            return;
        }
        if (!DateTimeOffset.TryParse(line.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time))
        {
            state.Error = "timestamp isn't a time";
            return;
        }
        state.Error = null;
        if (time < _scanNow - TimeSpan.FromDays(Days))
            return;

        var key = Hash(line.MessageId, line.RequestId);
        var bit = 1UL << state.Folder;
        var write1h = Math.Min(line.Write1h ?? 0, line.CacheWrite);
        var counts = new TokenCounts { Input = line.Input, Write1h = write1h, Write5m = line.CacheWrite - write1h, Read = line.CacheRead, Output = line.Output };
        var model = ModelIndex(line.Model);
        var fast = line.Speed == "fast";
        if (_seen.TryGetValue(key, out var seen))
        {
            if ((seen.Folders & bit) != 0)
                return;
            // Counted for other folders: its tokens move to the bucket of those folders and this one.
            var source = new Bucket(seen.Folders, seen.Minute, model, fast);
            if (_buckets.TryGetValue(source, out var held))
            {
                held.Subtract(counts);
                if (held.Total == 0)
                    _buckets.Remove(source);
            }
            AddTo(source with { Folders = seen.Folders | bit }, counts);
            _seen[key] = seen with { Folders = seen.Folders | bit };
            return;
        }
        if (_seen.Count >= _budget.Messages)
        {
            _dropped = true;
            state.Refused = true;
            return;
        }
        var minute = Minute(time);
        _seen[key] = new Seen(minute, bit);
        AddTo(new Bucket(bit, minute, model, fast), counts);
    }

    private void AddTo(Bucket bucket, TokenCounts counts)
    {
        if (_buckets.TryGetValue(bucket, out var kept))
            kept.Add(counts);
        else
            _buckets[bucket] = counts;
    }

    private int ModelIndex(string model)
    {
        if (_modelIndex.TryGetValue(model, out var index))
            return index;
        if (_models.Count >= MaxModels)
            model = Other;
        if (_modelIndex.TryGetValue(model, out index))
            return index;
        index = _models.Count;
        _models.Add(model);
        _modelIndex[model] = index;
        return index;
    }

    /// <summary>A message's key: the first 8 bytes of the SHA-256 of its message id and request id.</summary>
    private static ulong Hash(string messageId, string? requestId) => Digest(Encoding.UTF8.GetBytes(messageId + "\n" + requestId));

    private static long Minute(DateTimeOffset time) => time.UtcTicks / TimeSpan.TicksPerMinute;

    /// <summary>
    /// Forgets what is older than the last days: messages remembered and tokens counted. When that makes room after
    /// messages were refused, the files they were in are read again from their starts (what was counted doesn't count
    /// again), so none is lost.
    /// </summary>
    private void Prune(DateTimeOffset since)
    {
        var oldest = Minute(since);
        foreach (var (key, seen) in _seen)
        {
            if (seen.Minute < oldest)
                _seen.Remove(key);
        }
        foreach (var bucket in _buckets.Keys)
        {
            if (bucket.Minute < oldest)
                _buckets.Remove(bucket);
        }
        if (_dropped && _seen.Count < _budget.Messages)
        {
            foreach (var state in _files.Values)
            {
                if (state.Refused)
                    state.Reset();
            }
            _dropped = false;
        }
    }

    /// <summary>The tokens and cost as of <paramref name="now"/>, from what was counted.</summary>
    private TranscriptReading Sum(IReadOnlyList<ClaudeFolder> folders, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, env.Zone);
        var midnight = Minute(new DateTimeOffset(local.Date, env.Zone.GetUtcOffset(local.Date)));
        var weekStart = Minute(now - TimeSpan.FromDays(7));
        var end = Minute(now);
        const int Together = -1;
        var all = 0UL;
        foreach (var folder in folders)
        {
            if (_folderIndex.TryGetValue(folder.Path, out var index))
                all |= 1UL << index;
        }

        // Per folder (or the folders together), model and speed: today's and the 7 days' counts.
        var sums = new Dictionary<(int Set, int Model, bool Fast), (TokenCounts Today, TokenCounts Week)>();
        void Add(int set, Bucket bucket, TokenCounts counts)
        {
            var key = (set, bucket.Model, bucket.Fast);
            if (!sums.TryGetValue(key, out var sum))
                sums[key] = sum = (new TokenCounts(), new TokenCounts());
            sum.Week.Add(counts);
            if (bucket.Minute >= midnight)
                sum.Today.Add(counts);
        }
        foreach (var (bucket, counts) in _buckets)
        {
            if (bucket.Minute < weekStart || bucket.Minute > end || (bucket.Folders & all) == 0)
                continue;
            for (var mask = bucket.Folders & all; mask != 0; mask &= mask - 1)
                Add(BitOperations.TrailingZeroCount(mask), bucket, counts);
            Add(Together, bucket, counts);
        }

        string? Out()
        {
            if (_behind)
                return $"reading Claude Code's transcripts ({_done} of {_listed} files so far)";
            if (_crowded != null)
                return _crowded;
            if (_dropped)
                return $"Claude Code's transcripts hold more messages than AI usage keeps ({_budget.Messages}); tokens are left out";
            return null;
        }

        var notOut = Out();
        TokenReading Of(int set, ulong mask)
        {
            bool In(FileState f) => (mask & (1UL << f.Folder)) != 0;
            if (notOut != null)
                return new TokenReading(null, null, null, null, notOut);
            if (_files.Values.FirstOrDefault(f => In(f) && f.Error != null) is { } broken)
                return new TokenReading(null, null, null, null, Changed(broken.Error!));
            if (_files.Values.Any(f => In(f) && f.Unreadable))
                return new TokenReading(null, null, null, null, UnreadableNote);
            long tokensToday = 0, tokensWeek = 0;
            double costToday = 0, costWeek = 0;
            var unpriced = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var ((at, model, fast), (today, week)) in sums)
            {
                if (at != set)
                    continue;
                tokensToday += today.Total;
                tokensWeek += week.Total;
                var name = _models[model];
                if (name != Other && _prices.Cost(name, fast, week) is { } weekCost)
                {
                    costWeek += weekCost;
                    costToday += _prices.Cost(name, fast, today) ?? 0;
                }
                else if (week.Total > 0)
                    unpriced.Add(fast ? name + " (fast)" : name);
            }
            var note = unpriced.Count == 0 ? null : $"the cost leaves out {string.Join(", ", unpriced)}: no price known";
            return new TokenReading(tokensToday, tokensWeek, costToday, costWeek, note);
        }

        var readings = new Dictionary<string, TokenReading>(StringComparer.Ordinal);
        foreach (var folder in folders)
            readings[folder.Path] = _folderIndex.TryGetValue(folder.Path, out var index) ? Of(index, 1UL << index) : TokenReading.None;
        var combined = folders.Count == 0 ? TokenReading.None : Of(Together, all);
        return new TranscriptReading(readings, combined);
    }

    /// <summary>The note for a format change at <paramref name="what"/> (a field path and what's wrong with it); logged once a run.</summary>
    private string Changed(string what)
    {
        if (_logged.Add(what))
            PluginLog.Warning($"AI usage: Claude Code's transcript format changed ({what})");
        return $"Claude Code's transcript format changed ({what}); InfoPanel may need an update";
    }

    /// <summary>Tokens of one minute, model and speed, of the messages held by exactly the folders <see cref="Folders"/> (a bit each).</summary>
    private readonly record struct Bucket(ulong Folders, long Minute, int Model, bool Fast);

    /// <summary>A message remembered: its minute and the folders holding it (a bit each).</summary>
    private readonly record struct Seen(long Minute, ulong Folders);

    /// <summary>Where a listing got to: the folder, project and session next, and the files found so far.</summary>
    private sealed class ListingState(DateTimeOffset since)
    {
        /// <summary>The oldest last write time a file listed may have.</summary>
        public readonly DateTimeOffset Since = since;

        public readonly List<(ListedFile File, ClaudeFolder Folder)> Found = [];

        public int Folder, Project, Session;

        /// <summary>The projects of the folder at <see cref="Folder"/>, once listed.</summary>
        public IReadOnlyList<string>? Projects;

        /// <summary>The sessions of the project at <see cref="Project"/>, once listed.</summary>
        public IReadOnlyList<ListedFile>? Sessions;

        /// <summary>Set when a listing held more entries than it looks at.</summary>
        public string? Crowded;
    }

    private sealed class FileState(int folder)
    {
        /// <summary>The Claude folder the file is in (its index).</summary>
        public readonly int Folder = folder;

        /// <summary>The file's stamp at its last read; null before.</summary>
        public FileStamp? Stamp;

        /// <summary>Whether the last read stopped short of that stamp's length.</summary>
        public bool Pending;

        /// <summary>How far the file was read, a line still being written included.</summary>
        public long Seen;

        /// <summary>How many of the file's first bytes <see cref="HeadDigest"/> is of (up to <see cref="HeadBytes"/>); 0 before a read.</summary>
        public int HeadLength;

        /// <summary>A digest of the file's first <see cref="HeadLength"/> bytes (never the bytes).</summary>
        public ulong HeadDigest;

        /// <summary>The reader, while it holds part of a line still being written (or is reading).</summary>
        public UsageLineReader? Reader;

        /// <summary>What's wrong with the file's last assistant line, if anything: a format change.</summary>
        public string? Error;

        /// <summary>Whether the file may not be read (no permission) at its last look.</summary>
        public bool Unreadable;

        /// <summary>Whether a message of it was refused, the messages remembered being full: it is read again once there is room.</summary>
        public bool Refused;

        public void Reset()
        {
            Stamp = null;
            Pending = false;
            Seen = 0;
            HeadLength = 0;
            HeadDigest = 0;
            Reader?.Reset();
            Error = null;
            Refused = false;
        }
    }
}
