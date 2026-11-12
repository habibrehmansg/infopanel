using InfoPanel.Persistence;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Sensors.NowPlaying;

/// <summary>
/// Linux: what plays, through MPRIS on the D-Bus session bus (<see cref="MprisBus"/>; the bus at
/// <c>DBUS_SESSION_BUS_ADDRESS</c>, else <c>$XDG_RUNTIME_DIR/bus</c>; with neither, or one that can't be connected to,
/// its state is <c>unavailable</c> · <c>noSessionBus</c>). Each round (<see cref="MprisRounds"/>) lists the players
/// (<c>org.mpris.MediaPlayer2.*</c>), asks each for its <c>PlaybackStatus</c>, and reads the
/// <c>org.mpris.MediaPlayer2.Player</c> properties (status, metadata, position, rate, shuffle, loop status) and, once per
/// player, the <c>Identity</c> (the app's name) of the first in the order of choice that answers (those that play, the
/// one that started playing last first; then the one that played last; then the order listed). No player listed is
/// nothing playing; players listed of which none answers is a failed read. The cover art's <c>mpris:artUrl</c>: a
/// <c>file://</c> one is read when it changes (a regular file only, at most <see cref="NowPlayingSource.MaxArtBytes"/>;
/// a larger one is left out for the track; one that fails to read is read again after <see cref="ArtRetry"/>, doubled
/// after each failure up to <see cref="ArtRetryMax"/>; one read at a time, on a task of its own, waited for at most
/// <see cref="ArtTimeout"/>), an <c>http(s)://</c> one is handed on as the art's address, any other left out.
/// <para>
/// One owner per start holds the bus connection: a task started by <see cref="Start"/> connects, reads a round every
/// <see cref="PollInterval"/>, and lets go of the connection when it ends. <see cref="TryRead"/> answers at once what the
/// owner last read for the current start (nothing just after a start; nothing older than <see cref="AnswerAge"/>, so an
/// owner held up on the bus reads as failed). <see cref="Stop"/> and <see cref="Dispose"/> only ask the owner to end
/// (its callbacks run elsewhere) and return: they never wait for the bus. Every call on the bus goes through
/// <see cref="MprisCalls"/>: waited for at most <see cref="CallTimeout"/>, observed to its end with its error dropped,
/// at most one pending per player and operation. A failed listing, or <see cref="StalledRounds"/> rounds in a row whose
/// calls timed out with none answered, lets go of the connection, and the owner connects again after
/// <see cref="RetryInterval"/>; a player whose call fails is passed over for the round. Nothing a player sends ends the
/// owner or reaches the engine.
/// </para>
/// <para>Titles, artists, albums, apps, players' names and art never reach the log: it logs failures by type, and counts.</para>
/// </summary>
public sealed class LinuxNowPlayingReader : INowPlayingReader
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>A call on the bus not answered within this long fails.</summary>
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);

    /// <summary>After a failed connection or listing, the owner connects again after this long.</summary>
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    /// <summary>What the owner read is answered while it is at most this old.</summary>
    internal static readonly TimeSpan AnswerAge = TimeSpan.FromSeconds(5);

    /// <summary>Cover art that failed to read is read again after this, doubled after each failure up to <see cref="ArtRetryMax"/>.</summary>
    internal static readonly TimeSpan ArtRetry = TimeSpan.FromSeconds(5);

    internal static readonly TimeSpan ArtRetryMax = TimeSpan.FromMinutes(1);

    /// <summary>A round waits at most this long for the cover art's file; a read still running then fails for now, and is taken when it ends.</summary>
    internal static readonly TimeSpan ArtTimeout = TimeSpan.FromSeconds(2);

    /// <summary>After this many rounds in a row in which a player's call timed out (or was still pending) and none answered, the connection is let go of.</summary>
    internal const int StalledRounds = 3;

    internal static readonly SourceState NoSessionBusState = SourceState.UnavailableBecause(new SourceReason(NowPlayingSource.NoSessionBus,
        "There is no desktop session bus to read media players from"));

    private readonly Func<string?> _address;
    private readonly Func<string, IMprisBus> _open;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _callTimeout;
    private readonly TimeSpan _retry;
    private readonly TimeSpan _artTimeout;

    // The cover art's file reads, one at a time, shared by every start: a read that hangs holds no owner, and a new start
    // starts no second one beside it.
    private readonly MprisArtFiles _art;

    // Under the lock: the life cycle, the current start's run and the state. Nothing under it touches the bus.
    private readonly object _lock = new();
    private Run? _run;
    private bool _disposed;
    private SourceState? _state;

    /// <summary>One start's owner: asked to end by <see cref="Ending"/>; its last answer and when.</summary>
    private sealed class Run
    {
        public readonly CancellationTokenSource Ending = new();
        public NowPlayingSnapshot? Snapshot;
        public long? At;
    }

    /// <param name="address">The session bus's address, or null without one (asked at each connection).</param>
    /// <param name="open">A bus for an address, not yet connected.</param>
    internal LinuxNowPlayingReader(Func<string?> address, Func<string, IMprisBus> open, TimeProvider? time = null, ILogger? log = null,
        TimeSpan? interval = null, TimeSpan? callTimeout = null, TimeSpan? retry = null, Func<string, int, byte[]>? readFile = null, TimeSpan? artTimeout = null)
    {
        _art = new MprisArtFiles(readFile ?? RegularFile.ReadAtMost);
        _artTimeout = artTimeout ?? ArtTimeout;
        _address = address;
        _open = open;
        _time = time ?? TimeProvider.System;
        _log = (log ?? Log.Logger).ForContext<LinuxNowPlayingReader>();
        _interval = interval ?? PollInterval;
        _callTimeout = callTimeout ?? CallTimeout;
        _retry = retry ?? RetryInterval;
    }

    /// <summary>The reader of this computer's session bus.</summary>
    public static LinuxNowPlayingReader CreateSystem(TimeProvider? time = null) =>
        new(() => SessionBusAddress(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"), Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), File.Exists),
            address => new MprisBus(address), time);

    /// <summary>
    /// The session bus's address: <paramref name="variable"/> (<c>DBUS_SESSION_BUS_ADDRESS</c>) when set, else the socket
    /// <c>bus</c> in <paramref name="runtimeDir"/> (<c>XDG_RUNTIME_DIR</c>, a full path) when it is there, else null.
    /// </summary>
    internal static string? SessionBusAddress(string? variable, string? runtimeDir, Func<string, bool> exists)
    {
        if (!string.IsNullOrWhiteSpace(variable))
            return variable;
        if (string.IsNullOrEmpty(runtimeDir) || !Path.IsPathFullyQualified(runtimeDir))
            return null;
        var socket = Path.Combine(runtimeDir, "bus");
        return exists(socket) ? "unix:path=" + Escape(socket) : null;
    }

    /// <summary>A D-Bus address value: every byte but <c>[-0-9A-Za-z_/.\*]</c> as <c>%xx</c>.</summary>
    private static string Escape(string value)
    {
        var escaped = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (b is (byte)'-' or (byte)'_' or (byte)'/' or (byte)'.' or (byte)'\\' or (byte)'*' || char.IsAsciiLetterOrDigit((char)b))
                escaped.Append((char)b);
            else
                escaped.Append('%').Append(b.ToString("x2"));
        }

        return escaped.ToString();
    }

    public SourceState? State
    {
        get
        {
            lock (_lock)
                return _state;
        }
    }

    public void Start()
    {
        Run run;
        lock (_lock)
        {
            if (_disposed || _run is not null)
                return;
            run = _run = new Run();
        }

        _ = Task.Run(() => OwnAsync(run));
    }

    public void Stop() => End(dispose: false);

    public void Dispose() => End(dispose: true);

    private void End(bool dispose)
    {
        Run? run;
        lock (_lock)
        {
            if (dispose)
                _disposed = true;
            run = _run;
            _run = null;
        }

        // The owner's callbacks run on the thread pool, not here: nothing of the bus runs on the caller's thread.
        if (run is not null)
            _ = run.Ending.CancelAsync();
    }

    public bool TryRead(out NowPlayingSnapshot? snapshot)
    {
        lock (_lock)
        {
            if (_run is { At: { } at } run && _time.GetElapsedTime(at) <= AnswerAge)
            {
                snapshot = run.Snapshot;
                return true;
            }
        }

        snapshot = null;
        return false;
    }

    private void Publish(Run run, NowPlayingSnapshot? snapshot)
    {
        lock (_lock)
        {
            if (_run != run)
                return;
            run.Snapshot = snapshot;
            run.At = _time.GetTimestamp();
        }
    }

    private void SetState(Run run, SourceState? state)
    {
        lock (_lock)
        {
            if (_run == run)
                _state = state;
        }
    }

    /// <summary>The owner of one start: connects, reads rounds until asked to end, and lets go of the connection. Nothing escapes it.</summary>
    private async Task OwnAsync(Run run)
    {
        var ending = run.Ending.Token;
        var rounds = new MprisRounds(_time, _log, _callTimeout, _art, _artTimeout);
        IMprisBus? bus = null;
        var warnedConnect = false;
        var warnedBus = false;
        try
        {
            while (!ending.IsCancellationRequested)
            {
                if (bus is null)
                {
                    var address = _address();
                    if (address is null)
                    {
                        SetState(run, NoSessionBusState);
                        await Task.Delay(_retry, _time, ending).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        bus = _open(address);
                        await MprisCalls.Within(bus.ConnectAsync, _callTimeout, _time, ending).ConfigureAwait(false);
                    }
                    catch (Exception e) when (!ending.IsCancellationRequested)
                    {
                        if (!warnedConnect)
                        {
                            warnedConnect = true;
                            _log.Warning("Now Playing: couldn't connect to the session bus ({Error}); trying again every {Seconds} s", e.GetType().Name, _retry.TotalSeconds);
                        }

                        LetGo(bus);
                        bus = null;
                        SetState(run, NoSessionBusState);
                        await Task.Delay(_retry, _time, ending).ConfigureAwait(false);
                        continue;
                    }

                    SetState(run, null);
                    _log.Information("Now Playing: connected to the session bus");
                }

                MprisRound round;
                try
                {
                    round = await rounds.ReadAsync(bus, ending).ConfigureAwait(false);
                }
                catch (Exception e) when (!ending.IsCancellationRequested)
                {
                    if (!warnedBus)
                    {
                        warnedBus = true;
                        _log.Warning("Now Playing: the session bus failed ({Error}); connecting again in {Seconds} s", e.GetType().Name, _retry.TotalSeconds);
                    }

                    LetGo(bus);
                    bus = null;
                    await Task.Delay(_retry, _time, ending).ConfigureAwait(false);
                    continue;
                }

                if (round.Read)
                    Publish(run, round.Snapshot);
                await Task.Delay(_interval, _time, ending).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ending.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            _log.Error("Now Playing: the session bus's reader ended ({Error})", e.GetType().Name);
        }
        finally
        {
            LetGo(bus);
            // An owner that ended on its own (an error of its own) answers nothing more; the next start has a new one.
            lock (_lock)
            {
                if (_run == run)
                    _run = null;
            }
        }
    }

    private void LetGo(IMprisBus? bus)
    {
        try
        {
            bus?.Dispose();
        }
        catch (Exception e)
        {
            _log.Debug("Now Playing: letting go of the session bus failed ({Error})", e.GetType().Name);
        }
    }
}

/// <summary>
/// A round's outcome: <see cref="Read"/> false when players are listed but none answered with a reading (each failed, or
/// is still to answer); else the snapshot of the one read, null when no player is listed.
/// </summary>
internal readonly record struct MprisRound(bool Read, NowPlayingSnapshot? Snapshot);

/// <summary>
/// The owner's side of every call on one connection, and of every file read for cover art: each task is observed to its
/// end, whatever the wait for it came to (answered, failed, timed out, cancelled), and its exception (a player's error
/// text with it) dropped unread, so nothing of it reaches the log, the unobserved-task handler or the caller. A player's
/// call (<see cref="TryAsync"/>) has at most one pending per player and operation: a round that finds the last one still
/// pending doesn't ask again, and counts it as timed out. <see cref="EndRound"/> tells when <see cref="LinuxNowPlayingReader.StalledRounds"/>
/// rounds in a row had a call time out and none answered.
/// </summary>
internal sealed class MprisCalls(TimeProvider time, TimeSpan timeout)
{
    private readonly object _lock = new();
    private readonly Dictionary<(string Player, string Operation), Task> _pending = [];
    private bool _answered;
    private bool _timedOut;
    private int _stalls;

    /// <summary>Observes <paramref name="task"/>'s end: a fault is read (marking it observed) and dropped.</summary>
    internal static T Watch<T>(T task) where T : Task
    {
        task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    /// <summary>Starts a call, observed, and waits for it at most <paramref name="timeout"/> (or until <paramref name="ending"/>); throws as it fails.</summary>
    internal static Task Within(Func<Task> start, TimeSpan timeout, TimeProvider time, CancellationToken ending) =>
        Watch(Started(start)).WaitAsync(timeout, time, ending);

    internal static Task<T> Within<T>(Func<Task<T>> start, TimeSpan timeout, TimeProvider time, CancellationToken ending) =>
        Watch(Started(start)).WaitAsync(timeout, time, ending);

    // A call that throws as it starts fails as its task would.
    private static Task Started(Func<Task> start)
    {
        try
        {
            return start();
        }
        catch (Exception e)
        {
            return Task.FromException(e);
        }
    }

    private static Task<T> Started<T>(Func<Task<T>> start)
    {
        try
        {
            return start();
        }
        catch (Exception e)
        {
            return Task.FromException<T>(e);
        }
    }

    /// <summary>A call the bus itself answers (the listing): throws as it fails or times out.</summary>
    public Task<T> RunAsync<T>(Func<Task<T>> start, CancellationToken ending) => Within(start, timeout, time, ending);

    public void BeginRound()
    {
        lock (_lock)
            _answered = _timedOut = false;
    }

    /// <summary>Ends a round: true when it was the <see cref="LinuxNowPlayingReader.StalledRounds"/>th in a row in which calls timed out and none answered.</summary>
    public bool EndRound()
    {
        lock (_lock)
        {
            _stalls = !_timedOut || _answered ? 0 : _stalls + 1;
            return _stalls >= LinuxNowPlayingReader.StalledRounds;
        }
    }

    /// <summary>
    /// A player's call: its value, or not <c>Ok</c> when it failed, timed out or is still pending from an earlier round
    /// (then it isn't asked again). Throws only when <paramref name="ending"/> is cancelled.
    /// </summary>
    public async Task<(bool Ok, T? Value)> TryAsync<T>(string player, string operation, Func<Task<T>> start, CancellationToken ending)
    {
        Task<T> task;
        lock (_lock)
        {
            if (_pending.TryGetValue((player, operation), out var pending) && !pending.IsCompleted)
            {
                _timedOut = true;
                return (false, default);
            }

            task = Watch(Started(start));
            _pending[(player, operation)] = task;
        }

        try
        {
            var value = await task.WaitAsync(timeout, time, ending).ConfigureAwait(false);
            Answered();
            return (true, value);
        }
        catch (Exception) when (!ending.IsCancellationRequested)
        {
            lock (_lock)
            {
                if (task.IsCompleted)
                    _answered = true;
                else
                    _timedOut = true;
            }

            return (false, default);
        }
    }

    private void Answered()
    {
        lock (_lock)
            _answered = true;
    }

    /// <summary>Forgets the pending calls of players no longer listed.</summary>
    public void Keep(HashSet<string> listed)
    {
        lock (_lock)
        {
            foreach (var key in _pending.Keys.Where(k => !listed.Contains(k.Player) || _pending[k].IsCompleted).ToList())
                _pending.Remove(key);
        }
    }
}

/// <summary>
/// The cover art's file reads: at most one at a time, each on a task of its own, observed through <see cref="MprisCalls"/>;
/// a read is never waited for by more than a round's bound, and one that hangs keeps any other from starting.
/// </summary>
internal sealed class MprisArtFiles(Func<string, int, byte[]> read)
{
    private readonly object _lock = new();
    private Task<byte[]>? _running;
    private string? _path;

    /// <summary>The read of <paramref name="path"/>: the one running for it, else a new one; null while another path's read still runs.</summary>
    public Task<byte[]>? Read(string path)
    {
        lock (_lock)
        {
            if (_running is { IsCompleted: false })
                return _path == path ? _running : null;
            _path = path;
            _running = MprisCalls.Watch(Task.Run(() => read(path, NowPlayingSource.MaxArtBytes)));
            return _running;
        }
    }
}

/// <summary>
/// <see cref="LinuxNowPlayingReader"/>'s rounds over a bus: the player choice, the app names and the cover art, kept
/// between rounds. One owner's alone.
/// </summary>
internal sealed class MprisRounds(TimeProvider time, ILogger log, TimeSpan callTimeout, MprisArtFiles? files = null, TimeSpan? artTimeout = null)
{
    private readonly MprisArtFiles _files = files ?? new MprisArtFiles(RegularFile.ReadAtMost);
    private readonly TimeSpan _artTimeout = artTimeout ?? LinuxNowPlayingReader.ArtTimeout;

    // The calls of the bus last read (a new one for a new connection).
    private IMprisBus? _bus;
    private MprisCalls _calls = new(time, callTimeout);

    /// <summary>An art address longer than this is left out.</summary>
    internal const int MaxAddressChars = 2048;

    private long _round;

    // Per player name: the round it started playing in (while it plays), the last round it was seen playing, its app name.
    private readonly Dictionary<string, long> _playingSince = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastPlaying = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _identities = new(StringComparer.Ordinal);

    private Art _art = new(null);

    /// <summary>The cover art for one <c>mpris:artUrl</c>: what it resolved to, or when to try again.</summary>
    private sealed class Art(string? url)
    {
        public readonly string? Url = url;
        public bool Done;
        public ReadOnlyMemory<byte>? Bytes;
        public string? Address;
        public long RetryAt;
        public TimeSpan NextDelay = LinuxNowPlayingReader.ArtRetry;
        public bool Warned;

        /// <summary>The file read under way for it (kept past its deadline, and taken when it ends), and its deadline.</summary>
        public Task<byte[]>? Reading;
        public long Deadline;
    }

    /// <summary>
    /// One round. Throws when the players can't be listed (the bus failed), or when its calls have stalled
    /// (<see cref="MprisCalls.EndRound"/>: a <see cref="TimeoutException"/>, the connection to be let go of). The players
    /// are tried in the order of choice until one answers with its properties; one whose call fails is passed over.
    /// </summary>
    public async Task<MprisRound> ReadAsync(IMprisBus bus, CancellationToken ending)
    {
        if (!ReferenceEquals(bus, _bus))
        {
            _bus = bus;
            _calls = new MprisCalls(time, callTimeout);
        }

        var calls = _calls;
        calls.BeginRound();
        var names = await calls.RunAsync(bus.ListPlayersAsync, ending).ConfigureAwait(false);
        var listed = new HashSet<string>(names, StringComparer.Ordinal);
        calls.Keep(listed);
        var statuses = await Task.WhenAll(names.Select(name => StatusAsync(bus, calls, name, ending))).ConfigureAwait(false);
        ending.ThrowIfCancellationRequested();

        var round = new MprisRound(names.Count == 0, null);
        foreach (var name in Candidates(names, listed, statuses))
        {
            var (ok, player) = await calls.TryAsync(name, "GetAll", () => bus.GetAllAsync(name, MprisBus.PlayerInterface), ending).ConfigureAwait(false);
            if (!ok || player is null)
                continue;
            var identity = await IdentityAsync(bus, calls, name, ending).ConfigureAwait(false);
            var metadata = player.GetValueOrDefault("Metadata") as IReadOnlyDictionary<string, object?>;
            var art = await ArtOfAsync(metadata?.GetValueOrDefault("mpris:artUrl") as string, ending).ConfigureAwait(false);
            round = new MprisRound(true, Snapshot(name, player, metadata, identity, art.Bytes, art.Address, time.GetUtcNow()));
            break;
        }

        if (calls.EndRound())
            throw new TimeoutException("The players' calls stalled.");
        return round;
    }

    private static async Task<string?> StatusAsync(IMprisBus bus, MprisCalls calls, string name, CancellationToken ending)
    {
        var (ok, status) = await calls.TryAsync(name, "PlaybackStatus", () => bus.GetAsync(name, MprisBus.PlayerInterface, "PlaybackStatus"), ending)
            .ConfigureAwait(false);
        return ok ? status as string ?? "" : null;
    }

    private async Task<string?> IdentityAsync(IMprisBus bus, MprisCalls calls, string name, CancellationToken ending)
    {
        if (_identities.TryGetValue(name, out var known))
            return known;
        var (_, value) = await calls.TryAsync(name, "Identity", () => bus.GetAsync(name, MprisBus.RootInterface, "Identity"), ending).ConfigureAwait(false);
        var identity = value as string;

        // Kept for the player's name while it is listed (at most MprisBus.MaxPlayers names); a failed ask too, so a
        // player that doesn't answer it isn't asked every round.
        _identities[name] = identity;
        return identity;
    }

    /// <summary>
    /// The players to try, in the order of choice, from the names listed and their statuses (null: its call failed, left
    /// out): those that play, the one that started playing last first; then the others, the one seen playing last first,
    /// then in the order listed.
    /// </summary>
    private List<string> Candidates(IReadOnlyList<string> names, HashSet<string> listed, string?[] statuses)
    {
        _round++;
        foreach (var gone in _playingSince.Keys.Concat(_lastPlaying.Keys).Concat(_identities.Keys).Where(n => !listed.Contains(n)).ToList())
        {
            _playingSince.Remove(gone);
            _lastPlaying.Remove(gone);
            _identities.Remove(gone);
        }

        var playing = new List<(string Name, long Since, int At)>();
        var others = new List<(string Name, long Last, int At)>();
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            if (statuses[i] is not { } status)
                continue;
            if (status == "Playing")
            {
                if (!_playingSince.TryGetValue(name, out var since))
                    _playingSince[name] = since = _round;
                _lastPlaying[name] = _round;
                playing.Add((name, since, i));
            }
            else
            {
                _playingSince.Remove(name);
                others.Add((name, _lastPlaying.TryGetValue(name, out var last) ? last : long.MinValue, i));
            }
        }

        return
        [
            .. playing.OrderByDescending(p => p.Since).ThenBy(p => p.At).Select(p => p.Name),
            .. others.OrderByDescending(p => p.Last).ThenBy(p => p.At).Select(p => p.Name),
        ];
    }

    /// <summary>
    /// The cover art for <paramref name="url"/>: resolved when it changes; a file read through <see cref="MprisArtFiles"/>,
    /// waited for at most until its deadline (a read still running then fails for now and is taken when it ends; none
    /// other starts while it runs), a failed read again after its delay.
    /// </summary>
    private async Task<(ReadOnlyMemory<byte>? Bytes, string? Address)> ArtOfAsync(string? url, CancellationToken ending)
    {
        if (url != _art.Url)
            _art = new Art(url);
        var art = _art;
        if (art.Done || (art.Reading is null && time.GetTimestamp() < art.RetryAt))
            return (art.Bytes, art.Address);

        if (string.IsNullOrEmpty(url) || url.Length > MaxAddressChars || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            art.Done = true;
        }
        else if (uri.Scheme is "http" or "https")
        {
            art.Done = true;
            art.Address = url;
        }
        else if (uri.IsFile && uri.IsAbsoluteUri && (uri.Host is "" or "localhost"))
        {
            if (art.Reading is null)
            {
                art.Reading = _files.Read(uri.LocalPath);
                if (art.Reading is null)
                    return (art.Bytes, art.Address);
                art.Deadline = time.GetTimestamp() + (long)(_artTimeout.TotalSeconds * time.TimestampFrequency);
            }

            var reading = art.Reading;
            var left = TimeSpan.FromSeconds(Math.Max(0, art.Deadline - time.GetTimestamp()) / (double)time.TimestampFrequency);
            try
            {
                art.Bytes = await reading.WaitAsync(left, time, ending).ConfigureAwait(false);
                art.Reading = null;
                art.Done = true;
                log.Debug("Now Playing: cover art read ({Bytes} bytes)", art.Bytes.Value.Length);
            }
            catch (Exception) when (!ending.IsCancellationRequested && !reading.IsCompleted)
            {
                // Past its deadline: no cover art for now; the read is kept, and taken if it ends.
                if (!art.Warned)
                {
                    art.Warned = true;
                    log.Warning("Now Playing: the cover art's file took over {Seconds} s to read; going on without it", _artTimeout.TotalSeconds);
                }
            }
            catch (InvalidDataException) when (!ending.IsCancellationRequested)
            {
                art.Reading = null;
                art.Done = true;
                log.Warning("Now Playing: the cover art is over {Limit} bytes; left out for this track", NowPlayingSource.MaxArtBytes);
            }
            catch (Exception e) when (!ending.IsCancellationRequested)
            {
                art.Reading = null;
                if (!art.Warned)
                {
                    art.Warned = true;
                    log.Warning("Now Playing: the cover art's file couldn't be read ({Error}); trying again", e.GetType().Name);
                }

                art.RetryAt = time.GetTimestamp() + (long)(art.NextDelay.TotalSeconds * time.TimestampFrequency);
                var doubled = art.NextDelay * 2;
                art.NextDelay = doubled < LinuxNowPlayingReader.ArtRetryMax ? doubled : LinuxNowPlayingReader.ArtRetryMax;
            }
        }
        else
        {
            art.Done = true;
        }

        return (art.Bytes, art.Address);
    }

    /// <summary>A player's properties as a snapshot: MPRIS's microseconds as seconds, lists joined with ", ".</summary>
    internal static NowPlayingSnapshot Snapshot(string name, IReadOnlyDictionary<string, object?> player, IReadOnlyDictionary<string, object?>? metadata,
        string? identity, ReadOnlyMemory<byte>? art, string? artAddress, DateTimeOffset now)
    {
        var position = Number(player.GetValueOrDefault("Position"));
        return new NowPlayingSnapshot
        {
            Title = metadata?.GetValueOrDefault("xesam:title") as string,
            Artist = Joined(metadata?.GetValueOrDefault("xesam:artist")),
            Album = metadata?.GetValueOrDefault("xesam:album") as string,
            Genre = Joined(metadata?.GetValueOrDefault("xesam:genre")),
            App = string.IsNullOrWhiteSpace(identity) ? AppOf(name) : identity,
            State = (player.GetValueOrDefault("PlaybackStatus") as string) switch
            {
                "Playing" => NowPlayingState.Playing,
                "Paused" => NowPlayingState.Paused,
                _ => NowPlayingState.Stopped,
            },
            Position = position / 1e6,
            PositionAt = position is null ? null : now,
            Rate = Number(player.GetValueOrDefault("Rate")),
            Duration = Number(metadata?.GetValueOrDefault("mpris:length")) / 1e6,
            TrackNumber = Number(metadata?.GetValueOrDefault("xesam:trackNumber")) is { } n && n >= int.MinValue && n <= int.MaxValue ? (int)n : null,
            Shuffle = player.GetValueOrDefault("Shuffle") as bool?,
            Repeat = (player.GetValueOrDefault("LoopStatus") as string) switch
            {
                "None" => NowPlayingRepeat.Off,
                "Track" => NowPlayingRepeat.One,
                "Playlist" => NowPlayingRepeat.All,
                _ => null,
            },
            Art = art,
            ArtAddress = artAddress,
        };
    }

    private static double? Number(object? value) => value switch
    {
        long l => l,
        double d when double.IsFinite(d) => d,
        _ => null,
    };

    /// <summary>A list's items joined with ", " (the empty ones left out), or a single string; no further than a text is shown.</summary>
    private static string? Joined(object? value)
    {
        if (value is string text)
            return text;
        if (value is not IReadOnlyList<string> items)
            return null;
        var joined = new StringBuilder();
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item))
                continue;
            if (joined.Length > 0)
                joined.Append(", ");
            joined.Append(item);
            if (joined.Length > NowPlayingSource.MaxTextChars)
                break;
        }

        return joined.Length == 0 ? null : joined.ToString();
    }

    /// <summary>The app from a player's bus name, when it gives no identity: <c>org.mpris.MediaPlayer2.vlc.instance42</c> is <c>vlc</c>.</summary>
    internal static string AppOf(string name)
    {
        var app = name.StartsWith(MprisBus.NamePrefix, StringComparison.Ordinal) ? name[MprisBus.NamePrefix.Length..] : name;
        var instance = app.IndexOf(".instance", StringComparison.Ordinal);
        return instance > 0 ? app[..instance] : app;
    }
}
