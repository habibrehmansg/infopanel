using System.Globalization;
using System.Text.Json;
using InfoPanel.Plugins;

namespace InfoPanel.AiUsage;

/// <summary>
/// What a Codex scan may take: the logs listed in a day folder and the entries looked at to list them (files and
/// folders alike), the bytes read and the time taken a round (its listings included), and the token totals kept over
/// every file.
/// </summary>
internal sealed record CodexBudget(int FilesPerDay, int EntriesPerDay, long BytesPerRound, TimeSpan TimePerRound, int Samples)
{
    public static readonly CodexBudget Default = new(1000, 2000, 64L * 1024 * 1024, TimeSpan.FromSeconds(2), 262_144);
}

/// <summary>What Codex's logs say: its limits, its credits and plan, and its tokens today and over 7 days (null: none seen, or unknown).</summary>
internal sealed record CodexReading(LimitsReading Limits, string? Credits, string? Plan, long? TokensToday, long? Tokens7d);

/// <summary>
/// Reads Codex's session logs (<c>{codex}/sessions/{yyyy}/{MM}/{dd}/rollout-*.jsonl</c>, day folders by local date) for
/// the last <see cref="Days"/> days, newest first. Each file is read once, then only what was appended to it, and only
/// lines holding <c>"token_count"</c> are parsed: Codex writes one such event per model answer, with its limits
/// (<c>payload.rate_limits</c>) and the file's cumulative tokens (<c>payload.info.total_token_usage</c>). Per file it
/// keeps how far it was read, its first bytes (to tell a rewritten file from a grown one), a line still being written,
/// its token totals with their times (a total once a minute, a repeated one not again) and its newest limits; nothing
/// else. A round reads within its <see cref="CodexBudget"/> and carries on where it stopped in the next.
/// Windows are told apart by their length (<c>window_minutes</c>: 300 the five-hour, 10080 the weekly), never by being
/// primary or secondary. Not thread-safe: one caller at a time.
/// </summary>
internal sealed class CodexReader(UsageEnvironment env, CodexBudget? budget = null)
{
    /// <summary>How many day folders back are read, today's included: a week and a day, so 7 days are whole.</summary>
    public const int Days = 8;

    /// <summary>A line longer than this is skipped unparsed (Codex's own longest are under 1 MiB).</summary>
    public const int MaxLineBytes = 16 * 1024 * 1024;

    /// <summary>How many of a file's first bytes are kept to tell, when it changes, that it is still the file read.</summary>
    public const int HeadBytes = 1024;

    /// <summary>
    /// The most bytes of lines still being written kept over every file between rounds; past it a file's line is read
    /// again from its start once there is more of it.
    /// </summary>
    public const long MaxPartialBytes = 2L * MaxLineBytes;

    public const int FiveHourMinutes = 300;
    public const int WeeklyMinutes = 10080;

    private const string Pattern = "rollout-*.jsonl";

    private static readonly byte[] Marker = "\"token_count\""u8.ToArray();

    private readonly CodexBudget _budget = budget ?? CodexBudget.Default;
    private readonly Dictionary<string, FileState> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private MemoryStream _line = new();
    private string? _folder;
    private int _samples;

    /// <summary>The token totals kept, over every file.</summary>
    internal int RetainedSamples => _samples;

    /// <summary>
    /// Reads what changed in <paramref name="folder"/>'s logs since the last call, within the budget, and answers the
    /// whole reading. Cancelled, it stops where it is (what it read so far is kept) and throws.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> was cancelled.</exception>
    public CodexReading Read(string folder, CancellationToken cancel = default)
    {
        if (_folder != folder)
        {
            Clear();
            _folder = folder;
        }
        if (!env.Files.DirectoryExists(folder))
        {
            Clear();
            return new CodexReading(LimitsReading.None($"Codex's folder wasn't found ({env.Show(folder)})", "Codex not found"), null, null, null, null);
        }

        var now = env.Now;
        // The round's time counts from here: listing the day folders spends it too.
        var started = env.Time.GetTimestamp();
        var outOfTime = false;
        bool OutOfTime() => outOfTime |= env.Time.GetElapsedTime(started) > _budget.TimePerRound;
        bool Stop() => cancel.IsCancellationRequested || OutOfTime();

        var today = TimeZoneInfo.ConvertTime(now, env.Zone).Date;
        var listed = new List<string>();
        string? crowded = null;
        var stopped = false;
        for (var day = 0; day < Days && !stopped; day++)
        {
            cancel.ThrowIfCancellationRequested();
            var date = today.AddDays(-day);
            var dir = Path.Combine(folder, "sessions", date.ToString("yyyy", CultureInfo.InvariantCulture),
                date.ToString("MM", CultureInfo.InvariantCulture), date.ToString("dd", CultureInfo.InvariantCulture));
            var found = Stop() ? Listing.None : env.Files.Files(dir, Pattern, _budget.FilesPerDay + 1, _budget.EntriesPerDay, Stop);
            cancel.ThrowIfCancellationRequested();
            if (outOfTime)
            {
                // Not listed to their end: what was listed is read on the next round, nothing taken as gone.
                stopped = true;
                break;
            }
            if (found.Paths.Count > _budget.FilesPerDay)
                crowded ??= $"Codex's day folders hold more than {_budget.FilesPerDay} logs; tokens are left out";
            else if (found.Truncated)
                crowded ??= $"a Codex day folder holds more than {_budget.EntriesPerDay} entries; tokens are left out";
            // Newest first: a log's name starts with the time its session began.
            listed.AddRange(found.Paths.Order(StringComparer.Ordinal).Reverse().Take(_budget.FilesPerDay));
        }
        if (!stopped)
        {
            foreach (var gone in _files.Keys.Except(listed).ToList())
                Remove(gone);
        }

        long bytes = 0;
        foreach (var path in listed)
        {
            cancel.ThrowIfCancellationRequested();
            if (stopped || bytes >= _budget.BytesPerRound || OutOfTime())
            {
                stopped = true;
                break;
            }
            if (env.Files.Stat(folder, path) is not { } looked)
            {
                Remove(path); // gone, a link, or not a regular file
                continue;
            }
            if (!_files.TryGetValue(path, out var state))
                _files[path] = state = new FileState();
            if (looked == state.Stamp && !state.Pending)
                continue;
            bytes += ReadFile(folder, path, state, _budget.BytesPerRound - bytes, started, cancel);
        }

        var behind = stopped || _files.Values.Any(s => s.Pending);
        var done = listed.Count(p => _files.TryGetValue(p, out var s) && s.Stamp != null && !s.Pending);
        return Combine(now, behind ? $"reading Codex's logs ({done} of {listed.Count} files so far)" : null, crowded);
    }

    /// <summary>
    /// The whole reading from every file's state; <paramref name="behind"/> (the round didn't catch up) and
    /// <paramref name="crowded"/> (a day folder wasn't listed to its end) say why the tokens can't be given.
    /// </summary>
    private CodexReading Combine(DateTimeOffset now, string? behind, string? crowded)
    {
        CodexEvent? latest = null;
        string? tokensError = null;
        bool anySamples = false, dropped = false;
        long today = 0, week = 0;
        var local = TimeZoneInfo.ConvertTime(now, env.Zone);
        var midnight = new DateTimeOffset(local.Date, env.Zone.GetUtcOffset(local.Date));
        var weekStart = now - TimeSpan.FromDays(7);
        foreach (var state in _files.Values)
        {
            if (state.Limits is { } limits && (latest == null || limits.Time > latest.Time))
                latest = limits;
            tokensError ??= state.TokensError;
            dropped |= state.SamplesDropped;
            Prune(state, weekStart);
            if (state.Samples.Count == 0)
                continue;
            anySamples = true;
            var last = LastAtOrBefore(state.Samples, now);
            today += Math.Max(0, last - LastBefore(state.Samples, midnight));
            week += Math.Max(0, last - LastBefore(state.Samples, weekStart));
        }

        // Tokens are given only when every total that counts was read and kept.
        var tokensOut = behind
            ?? crowded
            ?? (dropped ? $"Codex's logs hold more token counts than AI usage keeps ({_budget.Samples}); tokens are left out" : null);
        long? tokensToday = anySamples ? today : null, tokens7d = anySamples ? week : null;
        if (tokensOut != null || tokensError != null)
            tokensToday = tokens7d = null;

        if (latest == null)
        {
            var none = tokensError is { } error ? LimitsReading.None(Changed(error), "format changed")
                : behind != null ? LimitsReading.None(behind, "reading")
                : tokensOut != null ? LimitsReading.None(tokensOut, "no limits")
                : anySamples ? LimitsReading.None("Codex reports no limits", "no limits")
                : LimitsReading.None($"no Codex use seen in the last {Days} days", "no use in 8 days");
            return new CodexReading(none, null, null, tokensToday, tokens7d);
        }
        if (latest.Error is { } limitsError)
            return new CodexReading(LimitsReading.None(Changed(limitsError), "format changed") with { AsOf = latest.Time }, null, null, tokensToday, tokens7d);

        var status = tokensError is { } tokens ? Changed(tokens)
            : tokensOut
            ?? (latest.FiveHour == null ? "Codex reports no five-hour window for this plan"
            : latest.Weekly == null ? "Codex reports no weekly window for this plan"
            : LimitsReading.Ok);
        return new CodexReading(new LimitsReading(latest.FiveHour, latest.Weekly, latest.Time, status, "–"), latest.Credits, latest.Plan, tokensToday, tokens7d);
    }

    /// <summary>The status for a format change at <paramref name="what"/> (a field path and what's wrong with it); logged once a run.</summary>
    private string Changed(string what)
    {
        if (_logged.Add(what))
            PluginLog.Warning($"AI usage: Codex's log format changed ({what})");
        return $"Codex's log format changed ({what}); InfoPanel may need an update";
    }

    private static long LastAtOrBefore(List<(DateTimeOffset Time, long Total)> samples, DateTimeOffset time)
    {
        long total = 0;
        foreach (var sample in samples)
        {
            if (sample.Time <= time)
                total = Math.Max(total, sample.Total);
        }
        return total;
    }

    private static long LastBefore(List<(DateTimeOffset Time, long Total)> samples, DateTimeOffset time)
    {
        long total = 0;
        foreach (var sample in samples)
        {
            if (sample.Time < time)
                total = Math.Max(total, sample.Total);
        }
        return total;
    }

    /// <summary>Drops a file's totals from before <paramref name="weekStart"/> but the last of them, the only one that still counts.</summary>
    private void Prune(FileState state, DateTimeOffset weekStart)
    {
        var before = state.Samples.FindLastIndex(s => s.Time < weekStart);
        if (before <= 0)
            return;
        state.Samples.RemoveRange(0, before);
        _samples -= before;
    }

    /// <summary>
    /// Reads <paramref name="path"/> on from where it was left, at most <paramref name="allowance"/> bytes and, past a first
    /// chunk, within the round's time; answers the bytes read. A file that changed in a way an append can't (another
    /// file, shorter, the same length at another time, other first bytes) is read again from its start.
    /// </summary>
    private long ReadFile(string root, string path, FileState state, long allowance, long started, CancellationToken cancel)
    {
        long read = 0;
        _line.SetLength(0);
        if (state.Partial != null)
            _line.Write(state.Partial);
        try
        {
            using var file = env.Files.Open(root, path);
            var stamp = file.Stamp;
            if (state.Stamp is { } old && old != stamp && !Continues(state, old, file, ref read))
            {
                Reset(state);
                _line.SetLength(0);
            }
            state.Stamp = stamp;
            state.Pending = true;
            file.Stream.Seek(state.Seen, SeekOrigin.Begin);
            var buffer = new byte[64 * 1024];
            var ended = false;
            while (state.Seen < stamp.Length && read < allowance)
            {
                cancel.ThrowIfCancellationRequested();
                var n = file.Stream.Read(buffer, 0, (int)Math.Min(buffer.Length, Math.Min(stamp.Length - state.Seen, allowance - read)));
                if (n == 0)
                {
                    ended = true; // shorter than it was at the open: the next round's stamp tells what it is now
                    break;
                }
                read += n;
                Take(state, buffer.AsSpan(0, n), stamp);
                if (env.Time.GetElapsedTime(started) > _budget.TimePerRound)
                    break;
            }
            state.Pending = !ended && state.Seen < stamp.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_logged.Add("read:" + ex.GetType().Name))
                PluginLog.Warning($"AI usage: a Codex log wasn't read ({(ex is RefusedFileException ? ex.Message : ex.GetType().Name)})");
        }
        finally
        {
            KeepPartial(state);
        }
        return read;
    }

    /// <summary>
    /// Whether the file open as <paramref name="file"/>, whose stamp differs from <paramref name="old"/> (its stamp at the
    /// last open), is the one read before grown by appends only: the same file, longer than it was then (an append
    /// always grows it, so a new time at the same length or less is a rewrite, however far its scan got), and its first
    /// bytes as they were.
    /// </summary>
    private static bool Continues(FileState state, FileStamp old, UsageFile file, ref long read)
    {
        var now = file.Stamp;
        if (old.Id != default && now.Id != default && old.Id != now.Id)
            return false;
        if (now.Length <= old.Length || now.Length <= state.Seen)
            return false;
        if (state.Head.Count == 0)
            return true;
        var head = new byte[state.Head.Count];
        file.Stream.Seek(0, SeekOrigin.Begin);
        var got = 0;
        int n;
        while (got < head.Length && (n = file.Stream.Read(head, got, head.Length - got)) > 0)
            got += n;
        read += got;
        return got == head.Length && head.AsSpan().SequenceEqual(state.Head.ToArray());
    }

    /// <summary>Bytes at <see cref="FileState.Seen"/>: its first bytes noted, its complete lines taken, the rest kept as the line still being written.</summary>
    private void Take(FileState state, ReadOnlySpan<byte> chunk, FileStamp stamp)
    {
        var from = state.Head.Count - state.Seen;
        if (state.Head.Count < HeadBytes && from >= 0 && from < chunk.Length)
            state.Head.AddRange(chunk.Slice((int)from, (int)Math.Min(HeadBytes - state.Head.Count, chunk.Length - from)));

        var at = 0;
        while (at < chunk.Length)
        {
            var rest = chunk[at..];
            var end = rest.IndexOf((byte)'\n');
            var part = end < 0 ? rest : rest[..end];
            if (!state.Skipping)
            {
                // A line past the limit is passed over to its end, never held.
                if (_line.Length + part.Length > MaxLineBytes)
                {
                    state.Skipping = true;
                    _line.SetLength(0);
                }
                else
                    _line.Write(part);
            }
            if (end < 0)
            {
                state.Seen += part.Length;
                break;
            }
            if (!state.Skipping)
                Line(state, _line.GetBuffer().AsSpan(0, (int)_line.Length), stamp);
            state.Skipping = false;
            _line.SetLength(0);
            at += end + 1;
            state.Seen += end + 1;
            state.LineStart = state.Seen;
        }
    }

    /// <summary>
    /// Keeps the line still being written with its file, within <see cref="MaxPartialBytes"/> over every file; past it the
    /// file is set back to the line's start, read again once there is more of it.
    /// </summary>
    private void KeepPartial(FileState state)
    {
        state.Partial = null;
        if (_line.Length > 0 && !state.Skipping)
        {
            var kept = _files.Values.Sum(s => (long)(s.Partial?.Length ?? 0));
            if (kept + _line.Length <= MaxPartialBytes)
                state.Partial = _line.ToArray();
            else
                state.Seen = state.LineStart;
        }
        _line.SetLength(0);
        if (_line.Capacity > 1024 * 1024)
            _line = new MemoryStream();
    }

    /// <summary>One complete line: a token_count event's tokens and limits are kept; any other line is passed over.</summary>
    private void Line(FileState state, ReadOnlySpan<byte> line, FileStamp stamp)
    {
        if (line.IndexOf(Marker) < 0)
            return;
        CodexEvent? parsed;
        long? total;
        string? tokensError;
        try
        {
            using var doc = JsonDocument.Parse(line.ToArray());
            (parsed, total, tokensError) = Event(doc.RootElement, stamp);
        }
        catch (JsonException)
        {
            return; // not a whole JSON line: never an event
        }
        if (parsed == null)
            return;
        if (total is { } tokens)
            AddSample(state, parsed.Time, tokens);
        // A tokens error lasts until a valid total comes: an event with no counts (info null) between leaves it, or
        // the total before the error would show again as current.
        if (tokensError != null)
            state.TokensError = tokensError;
        else if (total != null)
            state.TokensError = null;
        if ((parsed.Error != null || parsed.FiveHour != null || parsed.Weekly != null) && (state.Limits == null || parsed.Time >= state.Limits.Time))
            state.Limits = parsed;
    }

    /// <summary>
    /// A file's token total at <paramref name="time"/>: not kept again when it repeats the last; in the last's minute, in
    /// its place (so a total is kept at most once a minute: the 7 days' start is minute-exact, midnight exact); past
    /// <see cref="CodexBudget.Samples"/> over every file, not kept, and the file's tokens unknown.
    /// </summary>
    private void AddSample(FileState state, DateTimeOffset time, long total)
    {
        if (state.Samples.Count > 0)
        {
            var last = state.Samples[^1];
            if (total == last.Total)
                return;
            if (time >= last.Time && Minute(time) == Minute(last.Time))
            {
                state.Samples[^1] = (time, Math.Max(total, last.Total));
                return;
            }
        }
        if (_samples >= _budget.Samples)
        {
            state.SamplesDropped = true;
            return;
        }
        state.Samples.Add((time, total));
        _samples++;
    }

    private static long Minute(DateTimeOffset time) => time.UtcTicks / TimeSpan.TicksPerMinute;

    private void Remove(string path)
    {
        if (_files.Remove(path, out var state))
            _samples -= state.Samples.Count;
    }

    private void Reset(FileState state)
    {
        _samples -= state.Samples.Count;
        state.Reset();
    }

    private void Clear()
    {
        _files.Clear();
        _samples = 0;
    }

    /// <summary>
    /// A <c>token_count</c> event's limits (null when <paramref name="root"/> isn't one) with its file's token total and
    /// what's wrong with its tokens, if anything. <c>info</c> and <c>rate_limits</c> (and its <c>primary</c> and
    /// <c>secondary</c>) must be there; null is how Codex says it has none.
    /// </summary>
    internal static (CodexEvent? Event, long? Total, string? TokensError) Event(JsonElement root, FileStamp stamp)
    {
        if (root.ValueKind != JsonValueKind.Object || Text(root, "type") != "event_msg"
            || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object || Text(payload, "type") != "token_count")
            return (null, null, null);

        string? error = null;
        DateTimeOffset time;
        if (Text(root, "timestamp") is { } stampText && DateTimeOffset.TryParse(stampText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedTime))
            time = parsedTime;
        else
        {
            time = stamp.LastWriteUtc;
            error = "timestamp missing";
        }

        long? total = null;
        string? tokensError = null;
        if (!payload.TryGetProperty("info", out var info))
            tokensError = "info missing";
        else if (info.ValueKind != JsonValueKind.Null)
        {
            if (info.ValueKind == JsonValueKind.Object && info.TryGetProperty("total_token_usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("total_tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Number && tokens.TryGetInt64(out var value) && value >= 0)
                total = value;
            else
                tokensError = "info.total_token_usage.total_tokens missing";
        }

        Window? fiveHour = null, weekly = null;
        string? credits = null, plan = null;
        if (!payload.TryGetProperty("rate_limits", out var limits))
            error ??= "rate_limits missing";
        else if (limits.ValueKind != JsonValueKind.Null)
        {
            if (limits.ValueKind != JsonValueKind.Object)
                error ??= "rate_limits isn't an object";
            else
            {
                foreach (var slot in new[] { "primary", "secondary" })
                {
                    if (!limits.TryGetProperty(slot, out var window))
                    {
                        error ??= $"rate_limits.{slot} missing";
                        continue;
                    }
                    if (window.ValueKind == JsonValueKind.Null)
                        continue;
                    var (minutes, read, problem) = WindowOf(window, "rate_limits." + slot);
                    if (problem != null)
                        error ??= problem;
                    else if (minutes == FiveHourMinutes)
                        fiveHour = read;
                    else if (minutes == WeeklyMinutes)
                        weekly = read;
                    else
                        error ??= $"rate_limits.{slot}.window_minutes unknown";
                }
                credits = CreditsOf(limits);
                plan = Text(limits, "plan_type");
            }
        }

        if (error != null)
            return (new CodexEvent(time, null, null, null, null, error), total, tokensError);
        return (new CodexEvent(time, fiveHour, weekly, credits, plan, null), total, tokensError);
    }

    private static (int Minutes, Window? Window, string? Problem) WindowOf(JsonElement window, string path)
    {
        if (window.ValueKind != JsonValueKind.Object)
            return (0, null, $"{path} isn't an object");
        if (!window.TryGetProperty("used_percent", out var used) || used.ValueKind != JsonValueKind.Number || !double.IsFinite(used.GetDouble()))
            return (0, null, $"{path}.used_percent missing");
        if (!window.TryGetProperty("window_minutes", out var minutes) || minutes.ValueKind != JsonValueKind.Number || !minutes.TryGetInt32(out var length))
            return (0, null, $"{path}.window_minutes missing");
        if (!window.TryGetProperty("resets_at", out var resets) || resets.ValueKind != JsonValueKind.Number || !resets.TryGetInt64(out var seconds)
            || seconds is < 0 or > 253402300799)
            return (0, null, $"{path}.resets_at missing");
        return (length, new Window(Math.Clamp(used.GetDouble(), 0, 100), DateTimeOffset.FromUnixTimeSeconds(seconds)), null);
    }

    /// <summary>"unlimited", "none", or the balance as written; null when Codex doesn't say.</summary>
    private static string? CreditsOf(JsonElement limits)
    {
        if (!limits.TryGetProperty("credits", out var credits) || credits.ValueKind != JsonValueKind.Object)
            return null;
        if (credits.TryGetProperty("unlimited", out var unlimited) && unlimited.ValueKind == JsonValueKind.True)
            return "unlimited";
        if (credits.TryGetProperty("has_credits", out var has) && has.ValueKind == JsonValueKind.False)
            return "none";
        if (credits.TryGetProperty("balance", out var balance))
        {
            if (balance.ValueKind == JsonValueKind.String)
                return Cut(balance.GetString()!);
            if (balance.ValueKind == JsonValueKind.Number)
                return balance.GetRawText();
        }
        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? Cut(value.GetString()!) : null;

    private static string Cut(string text) => text.Length > 64 ? text[..64] : text;

    internal sealed record CodexEvent(DateTimeOffset Time, Window? FiveHour, Window? Weekly, string? Credits, string? Plan, string? Error);

    private sealed class FileState
    {
        /// <summary>The file's stamp at its last read; null before.</summary>
        public FileStamp? Stamp;

        /// <summary>Whether the last read stopped short of that stamp's length (the round's budget ran out).</summary>
        public bool Pending;

        /// <summary>How far the file was read, the line still being written included.</summary>
        public long Seen;

        /// <summary>Where the line still being written starts: just past the last complete line.</summary>
        public long LineStart;

        /// <summary>The line still being written (bytes <see cref="LineStart"/> to <see cref="Seen"/>), when kept.</summary>
        public byte[]? Partial;

        /// <summary>Whether the line still being written is past <see cref="MaxLineBytes"/>, passed over to its end.</summary>
        public bool Skipping;

        /// <summary>The file's first bytes, up to <see cref="HeadBytes"/>.</summary>
        public readonly List<byte> Head = [];

        /// <summary>Its cumulative token totals, each with the time of its event.</summary>
        public readonly List<(DateTimeOffset Time, long Total)> Samples = [];

        /// <summary>Whether a total of its wasn't kept (<see cref="CodexBudget.Samples"/>), so its tokens are unknown.</summary>
        public bool SamplesDropped;

        /// <summary>Its newest event with limits (or a format change).</summary>
        public CodexEvent? Limits;

        /// <summary>What's wrong with its tokens, if anything: set by an event whose total can't be read, cleared only by one whose total can.</summary>
        public string? TokensError;

        /// <summary>As new: read again from its start.</summary>
        public void Reset()
        {
            Stamp = null;
            Pending = false;
            Seen = LineStart = 0;
            Partial = null;
            Skipping = false;
            Head.Clear();
            Samples.Clear();
            SamplesDropped = false;
            Limits = null;
            TokensError = null;
        }
    }
}
