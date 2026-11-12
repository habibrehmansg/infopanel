using InfoPanel.Sensors.Volume;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Sensors.NowPlaying;

/// <summary>
/// Windows: what the system's current media session plays, through the Global System Media Transport Controls
/// (<c>Windows.Media.Control</c>, Windows 10 version 1809 and later; no capability, no prompt): the session Windows' own
/// media flyout shows (<c>GetCurrentSession</c>; none is nothing playing), read once a round. Each round reads the
/// session's app id, its media properties (title, artist, album, track number, genres; an async operation), its playback
/// info (status, shuffle, repeat, rate) and its timeline (start, end, position and when it was last updated, from which
/// the position moves on while playing). The cover art (the thumbnail) is read when the session's app, title, artist or
/// album changes, and again each round while that track has none yet, at most <see cref="NowPlayingSource.MaxArtBytes"/>
/// (a larger one is left out for the track); one that fails to read is asked for again after <see cref="ArtRetry"/>,
/// doubled after each failure up to <see cref="ArtRetryMax"/>. The app's name is the shell's Apps folder's name for its id
/// (<c>shell:AppsFolder\{id}</c>: "Microsoft Edge" for <c>MSEdge</c>, packaged apps too), else the file description of a
/// running process of a <c>.exe</c> id, else the id; once per id.
/// <para>
/// One thread of the reader's own, its owner (started at the first <see cref="Start"/>, ended by <see cref="Dispose"/>),
/// holds every native object: it asks for the manager, reads the rounds, starts the work that can block for good and takes
/// its results, keeps and closes the operations it started, and lets go of all of it. The other threads only ask it.
/// <see cref="TryRead"/> asks for a round and answers at once what the owner last read for the current start (nothing
/// yet just after a start; nothing older than <see cref="AnswerAge"/>, so an owner held up in Windows reads as failed).
/// <see cref="Stop"/> and <see cref="Dispose"/> set a flag and wake the owner; they never wait and never call Windows.
/// The owner, woken, starts nothing more for the round it was reading, lets go of the manager, and after
/// <see cref="Dispose"/> waits out its operations still running (at most <see cref="PendingWaitMax"/>, then lets go of
/// them unclosed) and ends. A call that stalls in Windows stalls only the owner.
/// </para>
/// <para>
/// What can block for good inside a single call (the cover art's stream, the shell's lookup of a name) runs on a worker
/// the owner starts, one at a time of each kind, and only while the round's start is current (the check and the start
/// are one step under the reader's lock, so nothing starts after a stop). The owner hands the worker what it reads (the
/// art's media properties) and never touches it again; the worker waits out its own operations and lets go of what it was
/// handed when it ends. A round waits for it at most <see cref="WorkWait"/> and takes its result then or in a later round.
/// </para>
/// <para>
/// The manager is asked for at the first round in use, let go of when the reader is stopped, and after a failed
/// activation or read asked for again at most every <see cref="RetryInterval"/>; a logon without the media service reads
/// nothing playing meanwhile, but only in session 0 (which services and SSH share: <c>ERROR_SERVICE_DOES_NOT_EXIST</c>),
/// elsewhere the read fails. Before Windows 10 version 1809, or where the class isn't registered, its state is
/// <c>unavailable</c> · <c>notSupported</c> and it reads nothing.
/// </para>
/// <para>
/// WinRT by hand, no package and no projection: the activation factory from <c>RoGetActivationFactory</c>, strings as
/// HSTRINGs, every interface source-generated COM (<see cref="GeneratedComInterfaceAttribute"/>) whole up to its last
/// slot used, IInspectable's three first, every method PreserveSig with an int HRESULT; a parameterised interface's
/// IID is WinRT's signature rule's (<c>WinNowPlayingAbiTests</c> pins the IIDs, slots and layouts on any OS). Async
/// operations, the stream's reads included, are waited for by asking their status, at most <see cref="AsyncTimeout"/>
/// (the cover art's at most <see cref="ArtTimeout"/> in all); one not done by then is cancelled and kept by the thread
/// that started it (<see cref="WinAsyncSlot"/>: the owner's own, <see cref="WinPending"/>, or the art worker's; never
/// shared between threads or readers) until it ends, closed only then, and no other of its kind starts meanwhile. The
/// owner looks at those it keeps every <see cref="ReapInterval"/>, read or not. The multithreaded apartment is kept alive
/// once (<c>CoIncrementMTAUsage</c>), so the owner's and the workers' threads need no COM init.
/// </para>
/// <para>Titles, artists, albums, app ids and art never reach the log: it logs failures by type and code, and counts.</para>
/// </summary>
public sealed class WinNowPlayingReader : INowPlayingReader
{
    /// <summary>An async operation not done within this long is cancelled and the round fails.</summary>
    internal static readonly TimeSpan AsyncTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The cover art's whole read, its stream opened and read, takes at most this long.</summary>
    internal static readonly TimeSpan ArtTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A round waits at most this long for work it starts on a thread of its own (the cover art, an app's name).</summary>
    internal static readonly TimeSpan WorkWait = TimeSpan.FromSeconds(1);

    /// <summary>After a failed activation or read, the manager is asked for again at most this often.</summary>
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    /// <summary>Cover art that failed to read is asked for again after this, doubled after each failure up to <see cref="ArtRetryMax"/>.</summary>
    internal static readonly TimeSpan ArtRetry = TimeSpan.FromSeconds(5);

    internal static readonly TimeSpan ArtRetryMax = TimeSpan.FromMinutes(1);

    /// <summary>What the owner read is answered while it is at most this old: an owner held up in Windows reads as failed, not as its last read.</summary>
    internal static readonly TimeSpan AnswerAge = TimeSpan.FromSeconds(5);

    /// <summary>While operations it started haven't ended, the owner looks at them this often, read or not.</summary>
    internal static readonly TimeSpan ReapInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Operations still running when the thread that started them ends (the owner after <see cref="Dispose"/>, a worker at
    /// its end) are waited for at most this long, then let go of unclosed.
    /// </summary>
    internal static readonly TimeSpan PendingWaitMax = TimeSpan.FromMinutes(1);

    /// <summary>At most this many genres are read.</summary>
    internal const int MaxGenres = 64;

    /// <summary>App names and ids are kept at most this long: one past what the sampler shows, so it cuts them itself.</summary>
    internal const int MaxKeptChars = NowPlayingSource.MaxTextChars + 1;

    /// <summary>App names kept, by id; more and the names are looked up afresh.</summary>
    private const int MaxAppNames = 64;

    /// <summary>REGDB_E_CLASSNOTREG: the manager's class isn't on this Windows.</summary>
    internal const int ClassNotRegistered = unchecked((int)0x80040154);

    /// <summary>HRESULT_FROM_WIN32(ERROR_SERVICE_DOES_NOT_EXIST): the logon has no media service (session 0, over SSH).</summary>
    internal const int ServiceMissing = unchecked((int)0x80070424);

    internal static readonly SourceState NotSupportedState = SourceState.UnavailableBecause(new SourceReason(NowPlayingSource.NotSupported,
        "This version of Windows has no media sessions to read (they need Windows 10 version 1809 or later)"));

    private readonly Func<WinPending, IWinMediaSessions> _activate;
    private readonly Func<string, string?> _appName;
    private readonly Func<bool> _sessionZero;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly TimeSpan _answerWait;

    // Under the lock: the life cycle, the rounds asked for and the owner's last answer. Nothing under it calls Windows or
    // waits for what does.
    private readonly object _lock = new();
    private bool _supported;
    private bool _started;
    private bool _disposed;
    private long _generation;
    private long _asked;
    private long _answered;
    private Answer? _answer;
    private Thread? _owner;

    private sealed record Answer(long Generation, bool Read, NowPlayingSnapshot? Snapshot, long At);

    // The owner's alone: every native object, the operations it started and kept, and the work it started.
    private readonly WinPending _pending = new();
    private IWinMediaSessions? _sessions;
    private long _heldGeneration = -1;
    private long? _failedAt;
    private bool _noService;
    private bool _warned;
    private readonly Dictionary<string, string> _appNames = new(StringComparer.Ordinal);
    private Work<string?>? _nameWork;
    private string _nameId = "";

    // The track the art is read for and its art: read again each round until the track has some (or has art too large),
    // after a failure only once its delay has passed.
    private ArtTrack? _artTrack;
    private bool _artDone;
    private ReadOnlyMemory<byte>? _art;
    private string? _artType;
    private int _artFailures;
    private long _artFailedAt;
    private bool _artWarned;
    private Work<WinThumbnail?>? _artWork;
    private ArtTrack? _artWorkTrack;

    private readonly record struct ArtTrack(string App, string? Title, string? Artist, string? Album);

    /// <summary>
    /// A reader over <paramref name="activate"/>'s manager (a test's), handed the owner's <see cref="WinPending"/>;
    /// <paramref name="supported"/> false is a Windows without media sessions; <paramref name="sessionZero"/> says whether
    /// this process runs in session 0 (none: it doesn't). <paramref name="answerWait"/> (tests only; none for the system's)
    /// is how long <see cref="TryRead"/> waits for the round it asks for before it answers what is ready.
    /// </summary>
    internal WinNowPlayingReader(Func<WinPending, IWinMediaSessions> activate, bool supported, TimeProvider? time = null, Func<string, string?>? appName = null,
        ILogger? log = null, Func<bool>? sessionZero = null, TimeSpan answerWait = default)
    {
        _activate = activate;
        _supported = supported;
        _time = time ?? TimeProvider.System;
        _appName = appName ?? (static _ => null);
        _sessionZero = sessionZero ?? (static () => false);
        _log = (log ?? Log.Logger).ForContext<WinNowPlayingReader>();
        _answerWait = answerWait;
    }

    /// <summary>The reader over this computer's media sessions.</summary>
    [SupportedOSPlatform("windows")]
    public static WinNowPlayingReader CreateSystem(TimeProvider time) =>
        new(WinMediaSessions.Activate, OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763), time, WinAppNames.Of, sessionZero: InSessionZero);

    /// <summary>Whether this process runs in session 0 (services, and SSH's logons); false when that can't be told.</summary>
    [SupportedOSPlatform("windows")]
    internal static bool InSessionZero()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.SessionId == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public SourceState? State
    {
        get
        {
            lock (_lock)
                return _supported ? null : NotSupportedState;
        }
    }

    /// <summary>Marks it started, its owner started with the first start; the manager is asked for at the first round.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || _started)
                return;
            _started = true;
            if (_supported && _owner is null)
            {
                _owner = new Thread(Own) { IsBackground = true, Name = "Now Playing" };
                _owner.Start();
            }

            Monitor.PulseAll(_lock);
        }
    }

    public void Stop() => Halt(dispose: false);

    public void Dispose() => Halt(dispose: true);

    /// <summary>Stops at once: a flag set and the owner woken, which lets go of what it holds (and after a dispose ends).</summary>
    private void Halt(bool dispose)
    {
        lock (_lock)
        {
            _disposed |= dispose;
            _started = false;
            _generation++;
            _answer = null;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>
    /// Asks the owner for a round and answers what it last read for this start, at once (a test's reader waits for the
    /// round it asked for, at most its wait, or until it is stopped).
    /// </summary>
    public bool TryRead(out NowPlayingSnapshot? snapshot)
    {
        snapshot = null;
        lock (_lock)
        {
            if (!_started || !_supported)
                return false;
            var generation = _generation;
            var asked = ++_asked;
            Monitor.PulseAll(_lock);
            if (_answerWait > TimeSpan.Zero)
            {
                var start = Stopwatch.GetTimestamp();
                while (_answered < asked && _generation == generation)
                {
                    var left = _answerWait - Stopwatch.GetElapsedTime(start);
                    if (left <= TimeSpan.Zero || !Monitor.Wait(_lock, left))
                        break;
                }
            }

            if (_answer is not { } answer || answer.Generation != _generation || !_started || _time.GetElapsedTime(answer.At) > AnswerAge)
                return false;
            snapshot = answer.Snapshot;
            return answer.Read;
        }
    }

    /// <summary>
    /// The owner: a round each time one is asked for while started; at each stop the manager let go of; the operations it
    /// keeps looked at meanwhile; after a dispose, those waited out and the thread ended.
    /// </summary>
    private void Own()
    {
        while (true)
        {
            long generation;
            long asked = 0;
            bool ending;
            lock (_lock)
            {
                while (true)
                {
                    ending = _disposed;
                    generation = _generation;
                    if (ending || generation != _heldGeneration)
                        break;
                    if (_started && _asked > _answered)
                    {
                        asked = _asked;
                        break;
                    }

                    // Idle until asked or stopped; the operations it keeps are looked at now and then meanwhile.
                    if (!Monitor.Wait(_lock, _pending.Busy ? ReapInterval : Timeout.InfiniteTimeSpan))
                        break;
                }
            }

            if (ending)
            {
                End();
                return;
            }

            try
            {
                _pending.Reap();
                if (generation != _heldGeneration)
                {
                    // Stopped (and maybe started again since): what the earlier start held is let go of, here.
                    _heldGeneration = generation;
                    LetGoAll();
                }
            }
            catch (Exception ex)
            {
                _log.Warning("Now Playing: letting go of what the reader held failed ({Error})", ex.GetType().Name);
            }

            if (asked == 0)
                continue;

            bool read;
            NowPlayingSnapshot? snapshot;
            try
            {
                read = Round(generation, out snapshot);
            }
            catch (Exception ex)
            {
                Warn("Now Playing: reading the media session failed ({Error})", ex);
                (read, snapshot) = (false, null);
            }

            lock (_lock)
            {
                if (_answered < asked)
                    _answered = asked;
                if (_generation == generation)
                    _answer = new Answer(generation, read, snapshot, _time.GetTimestamp());
                Monitor.PulseAll(_lock);
            }
        }
    }

    /// <summary>
    /// The owner's end, after a dispose: everything let go of, its operations still running waited out (closed as each
    /// ends; at most <see cref="PendingWaitMax"/>, then let go of unclosed). A worker still running ends by itself.
    /// </summary>
    private void End()
    {
        try
        {
            LetGoAll();
            var abandoned = WinAsyncSlot.WaitOut(_pending.Slots, PendingWaitMax);
            if (abandoned > 0)
                _log.Warning("Now Playing: {Count} operations hadn't ended {Seconds} s after the reader stopped; let go of unclosed", abandoned, PendingWaitMax.TotalSeconds);
        }
        catch (Exception ex)
        {
            _log.Warning("Now Playing: letting go of what the reader held failed ({Error})", ex.GetType().Name);
        }
    }

    /// <summary>Whether <paramref name="generation"/> is still the current start (not stopped since).</summary>
    private bool IsCurrent(long generation)
    {
        lock (_lock)
            return _generation == generation;
    }

    /// <summary>One round, on the owner: the manager had (or asked for), the current session read.</summary>
    private bool Round(long generation, out NowPlayingSnapshot? snapshot)
    {
        snapshot = null;
        if (_sessions is null && (_sessions = Activate()) is null)
        {
            // A logon without the media service (session 0, which services and SSH share) plays nothing.
            if (!_noService)
                return false;
            ForgetArt();
            return true;
        }

        var sessions = _sessions;
        try
        {
            using var session = sessions.Current();
            if (session is null)
            {
                ForgetArt();
                _warned = false;
                return true;
            }

            snapshot = Read(generation, session);
            _warned = false;
            return snapshot != null;
        }
        catch (TimeoutException ex)
        {
            // The session didn't answer in time: this round fails, the manager stays.
            Warn("Now Playing: the media session didn't answer in time ({Error})", ex);
            return false;
        }
        catch (Exception ex)
        {
            // The manager or the session failed (the media service restarted, say): asked for afresh later.
            Warn("Now Playing: reading the media session failed ({Error}); asked again in a few seconds", ex);
            _sessions = null;
            ForgetArt();
            LetGo(sessions);
            _failedAt = _time.GetTimestamp();
            return false;
        }
    }

    /// <summary>The manager asked for, at most every <see cref="RetryInterval"/> after a failure; null while it can't be had.</summary>
    private IWinMediaSessions? Activate()
    {
        if (_failedAt is { } at && _time.GetElapsedTime(at) < RetryInterval)
            return null;
        IWinMediaSessions sessions;
        try
        {
            sessions = _activate(_pending);
        }
        catch (COMException ex) when (ex.HResult == ClassNotRegistered)
        {
            // This Windows has no media sessions (a Windows without the class): as before 1809.
            lock (_lock)
                _supported = false;
            Warn("Now Playing: this Windows has no media sessions to read ({Error})", ex);
            return null;
        }
        catch (COMException ex) when (ex.HResult == ServiceMissing && _sessionZero())
        {
            // Session 0 has no media service: nothing plays here; asked again later all the same.
            _failedAt = _time.GetTimestamp();
            if (!_noService)
                _log.Information("Now Playing: this logon session has no media service ({Error}); nothing plays here", $"{ex.GetType().Name} 0x{ex.HResult:X8}");
            _noService = true;
            return null;
        }
        catch (Exception ex)
        {
            _noService = false;
            _failedAt = _time.GetTimestamp();
            Warn("Now Playing: the media sessions couldn't be asked for ({Error}); asked again in a few seconds", ex);
            return null;
        }

        _failedAt = null;
        _noService = false;
        return sessions;
    }

    /// <summary>What the last start held let go of, on the owner: the manager, and the art read for it.</summary>
    private void LetGoAll()
    {
        ForgetArt();
        var sessions = _sessions;
        _sessions = null;
        LetGo(sessions);
    }

    /// <summary>
    /// The session's snapshot; null when its media properties didn't come, or the reader was stopped while they were
    /// asked for (then nothing more is read or started for the round).
    /// </summary>
    private NowPlayingSnapshot? Read(long generation, IWinMediaSession session)
    {
        var appId = session.AppId;
        var properties = session.Properties(AsyncTimeout);
        if (properties is null)
            return null;
        WinPlayback playback;
        WinTimeline timeline;
        string? title, artist, album;
        int trackNumber;
        IReadOnlyList<string> genres;
        try
        {
            if (!IsCurrent(generation))
                return null;
            playback = session.Playback();
            timeline = session.Timeline();
            (title, artist, album, trackNumber, genres) = (properties.Title, properties.Artist, properties.Album, properties.TrackNumber, properties.Genres);
            var track = new ArtTrack(appId, title, artist, album);
            if (_artTrack != track)
            {
                ForgetArt();
                _artTrack = track;
            }

            TakeArt(TimeSpan.Zero);
            if (ArtDue() && StartArt(generation, track, properties))
            {
                properties = null;
                TakeArt(WorkWait);
            }
        }
        finally
        {
            properties?.Dispose();
        }

        var position = timeline.Position - timeline.Start;
        var duration = timeline.End - timeline.Start;
        return new NowPlayingSnapshot
        {
            Title = Empty(title),
            Artist = Empty(artist),
            Album = Empty(album),
            App = AppName(generation, appId),
            State = playback.Status switch
            {
                WinPlayback.Playing => NowPlayingState.Playing,
                WinPlayback.Paused => NowPlayingState.Paused,
                _ => NowPlayingState.Stopped,
            },
            Position = position.TotalSeconds,
            PositionAt = timeline.LastUpdated,
            Rate = playback.Rate,
            Duration = duration > TimeSpan.Zero ? duration.TotalSeconds : null,
            TrackNumber = trackNumber > 0 ? trackNumber : null,
            Genre = genres.Count == 0 ? null : string.Join(", ", genres),
            Shuffle = playback.Shuffle,
            Repeat = playback.Repeat switch
            {
                0 => NowPlayingRepeat.Off,
                1 => NowPlayingRepeat.One,
                2 => NowPlayingRepeat.All,
                _ => null,
            },
            Art = _art,
            ArtType = _art is null ? null : _artType,
        };
    }

    /// <summary>Whether the art is read this round: the track has none yet, no read runs, and a failure's delay has passed.</summary>
    private bool ArtDue()
    {
        if (_artDone || _artWork != null)
            return false;
        if (_artFailures == 0)
            return true;
        var delay = ArtRetry * Math.Pow(2, Math.Min(_artFailures - 1, 16));
        return _time.GetElapsedTime(_artFailedAt) >= (delay < ArtRetryMax ? delay : ArtRetryMax);
    }

    /// <summary>
    /// The art read on a worker, which is handed <paramref name="properties"/> and lets go of them when it ends; started
    /// only while <paramref name="generation"/> is current (false, and nothing started, once stopped).
    /// </summary>
    private bool StartArt(long generation, ArtTrack track, IWinMediaProperties properties)
    {
        lock (_lock)
        {
            if (_generation != generation)
                return false;
            _artWorkTrack = track;
            _artWork = new Work<WinThumbnail?>(() =>
            {
                using (properties)
                    return properties.Thumbnail(NowPlayingSource.MaxArtBytes, ArtTimeout);
            }, "Now Playing art");
        }

        return true;
    }

    /// <summary>The art read's result, once it has ended (waiting at most <paramref name="wait"/>); kept when it is this track's.</summary>
    private void TakeArt(TimeSpan wait)
    {
        if (_artWork is not { } work || !work.Wait(wait))
            return;
        _artWork = null;
        if (_artWorkTrack != _artTrack || _artDone)
            return; // Another track's.

        var (thumbnail, error) = work.Result;
        if (error != null)
        {
            // Asked for again after a delay; one warning a track.
            _artFailures++;
            _artFailedAt = _time.GetTimestamp();
            if (!_artWarned)
            {
                _artWarned = true;
                _log.Warning("Now Playing: the cover art couldn't be read ({Error}); asked again later", $"{error.GetType().Name} 0x{error.HResult:X8}");
            }

            return;
        }

        if (thumbnail is null)
            return; // None yet: asked again next round.
        _artDone = true;
        if (thumbnail.Bytes is not { } bytes)
        {
            _log.Warning("Now Playing: cover art of {Bytes} bytes is over {Max} bytes; left out", thumbnail.Size, NowPlayingSource.MaxArtBytes);
            return;
        }

        _art = bytes;
        _artType = thumbnail.ContentType;
        _log.Information("Now Playing: cover art read ({Bytes} bytes)", bytes.Length);
    }

    private void ForgetArt()
    {
        // A read still running stays: no other starts until it ends, and its result is dropped unless its track is back.
        _artTrack = null;
        _artDone = false;
        _art = null;
        _artType = null;
        _artFailures = 0;
        _artWarned = false;
    }

    /// <summary>
    /// The app's name for people, looked up once per id on a worker (started only while <paramref name="generation"/> is
    /// current); its id until then, or when it has none.
    /// </summary>
    private string? AppName(long generation, string appId)
    {
        if (appId.Length == 0)
            return null;
        TakeName(TimeSpan.Zero);
        if (_appNames.TryGetValue(appId, out var name))
            return name;
        if (_nameWork is null && StartName(generation, appId))
        {
            TakeName(WorkWait);
            if (_appNames.TryGetValue(appId, out name))
                return name;
        }

        return Kept(appId);
    }

    private bool StartName(long generation, string appId)
    {
        lock (_lock)
        {
            if (_generation != generation)
                return false;
            var lookup = _appName;
            _nameId = appId;
            _nameWork = new Work<string?>(() => lookup(appId), "Now Playing app name");
        }

        return true;
    }

    /// <summary>The name lookup's result, once it has ended (waiting at most <paramref name="wait"/>), kept for its id.</summary>
    private void TakeName(TimeSpan wait)
    {
        if (_nameWork is not { } work || !work.Wait(wait))
            return;
        _nameWork = null;
        var (name, error) = work.Result;
        if (error != null)
            Warn("Now Playing: the app's name couldn't be looked up ({Error})", error);
        if (_appNames.Count >= MaxAppNames)
            _appNames.Clear();
        _appNames[_nameId] = Kept(string.IsNullOrWhiteSpace(name) ? _nameId : name);
    }

    private static string Kept(string text) => text.Length > MaxKeptChars ? text[..MaxKeptChars] : text;

    private static string? Empty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    /// <summary>One warning until a round reads again; the error's type and code only, never its text (it could hold what plays).</summary>
    private void Warn(string message, Exception ex)
    {
        if (_warned)
            return;
        _warned = true;
        _log.Warning(message, $"{ex.GetType().Name} 0x{ex.HResult:X8}");
    }

    private void LetGo(IWinMediaSessions? sessions)
    {
        try
        {
            sessions?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warning("Now Playing: letting go of the media sessions failed ({Error})", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Work on a worker of its own: its result, or what it threw, once it has ended. Nothing waits for it longer than it
    /// chooses, and it never throws out of its thread.
    /// </summary>
    private sealed class Work<T>
    {
        private readonly TaskCompletionSource<(T? Value, Exception? Error)> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Work(Func<T> run, string name)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    _done.SetResult((run(), null));
                }
                catch (Exception ex)
                {
                    _done.SetResult((default, ex));
                }
            })
            {
                IsBackground = true,
                Name = name,
            };
            thread.Start();
        }

        /// <summary>Whether it has ended, waiting at most <paramref name="timeout"/>.</summary>
        public bool Wait(TimeSpan timeout) => timeout > TimeSpan.Zero ? _done.Task.Wait(timeout) : _done.Task.IsCompleted;

        public (T? Value, Exception? Error) Result => _done.Task.Result;
    }
}

/// <summary>The media sessions' manager as <see cref="WinNowPlayingReader"/> asks it (the seam its tests replace); used and let go of on the reader's owner only.</summary>
internal interface IWinMediaSessions : IDisposable
{
    /// <summary>The current session, the caller's; null when there is none.</summary>
    IWinMediaSession? Current();
}

/// <summary>One media session: each read asks Windows afresh.</summary>
internal interface IWinMediaSession : IDisposable
{
    /// <summary>The app's id (an AppUserModelID: <c>MSEdge</c>, <c>Spotify.exe</c>).</summary>
    string AppId { get; }

    /// <summary>Its media properties, the caller's; null when it has none. Throws <see cref="TimeoutException"/> past <paramref name="timeout"/>.</summary>
    IWinMediaProperties? Properties(TimeSpan timeout);

    WinPlayback Playback();

    WinTimeline Timeline();
}

/// <summary>
/// A session's media properties, read when they were had; the thumbnail read on demand by the worker they are handed to,
/// which alone uses them from then on and lets go of them.
/// </summary>
internal interface IWinMediaProperties : IDisposable
{
    string? Title { get; }
    string? Artist { get; }
    string? Album { get; }
    int TrackNumber { get; }
    IReadOnlyList<string> Genres { get; }

    /// <summary>
    /// The thumbnail, read within <paramref name="timeout"/> in all (else <see cref="TimeoutException"/>); null when there
    /// is none. Its bytes are left out (null), and not read, when it says it is over <paramref name="maxBytes"/>. Its own
    /// operations that didn't end in time are waited out before it answers (at most
    /// <see cref="WinNowPlayingReader.PendingWaitMax"/>, then let go of unclosed).
    /// </summary>
    WinThumbnail? Thumbnail(int maxBytes, TimeSpan timeout);
}

/// <summary>A session's playback info: its status (<see cref="Playing"/>, <see cref="Paused"/>, else stopped), shuffle, repeat (0 none, 1 track, 2 list) and rate, each null when the app doesn't say.</summary>
internal readonly record struct WinPlayback(int Status, bool? Shuffle, int? Repeat, double? Rate)
{
    /// <summary><c>GlobalSystemMediaTransportControlsSessionPlaybackStatus</c>'s Playing and Paused.</summary>
    public const int Playing = 4;

    public const int Paused = 5;
}

/// <summary>A session's timeline: start, end and position, and when the position was last updated (null when never).</summary>
internal readonly record struct WinTimeline(TimeSpan Start, TimeSpan End, TimeSpan Position, DateTimeOffset? LastUpdated);

/// <summary>A thumbnail: its bytes (null when over the bound), its content type and its size as the stream gave it.</summary>
internal sealed record WinThumbnail(ReadOnlyMemory<byte>? Bytes, string? ContentType, ulong Size);

/// <summary>A thumbnail's stream as <see cref="WinRt.ReadThumbnail"/> reads it (the seam its tests replace).</summary>
internal interface IWinStream
{
    /// <summary>Its content type (<c>image/png</c>), cut at 256 characters; null when it says none.</summary>
    string? ContentType { get; }

    /// <summary>Its size in bytes, as it says.</summary>
    ulong Size { get; }

    /// <summary>Starts reading at most <paramref name="count"/> bytes on from what was read (<c>IInputStream.ReadAsync</c>).</summary>
    IWinRead Read(uint count);
}

/// <summary>A read started: its operation, and once completed what it read.</summary>
internal interface IWinRead
{
    IAsyncInfo Info { get; }

    /// <summary>Once completed: what it read copied into <paramref name="destination"/> (at most its length); the count.</summary>
    int CopyTo(Span<byte> destination);

    /// <summary>Lets go of the operation and its buffer; once, after it has ended or when it is given up.</summary>
    void Release();
}

// The slots call IAsyncInfo alone: Windows' operations, or a test's fake of one on any OS.
#pragma warning disable CA1416

/// <summary>
/// One kind of async operation of one thread (a reader's owner, or the worker reading the art; never shared between
/// threads or readers): at most one outstanding. An operation that didn't end in time is kept here (cancelled) with what
/// holds it, and closed and let go of only once it has ended: when its thread next makes way for another
/// (<see cref="Claim"/>) or looks at it (<see cref="Reap"/>, <see cref="WaitOut"/>), read or not; meanwhile no other of
/// its kind starts. Nothing kept is ever let go of for another.
/// </summary>
internal sealed class WinAsyncSlot(string what)
{
    private readonly List<(IAsyncInfo Info, Action Release)> _kept = [];

    /// <summary>Whether it keeps an operation that hadn't ended when last looked at.</summary>
    public bool Busy => _kept.Count > 0;

    /// <summary>
    /// Makes way for a new operation of its kind: those it keeps that have ended are closed and let go of (a failed close
    /// throws, once it is let go of); one still running throws <see cref="TimeoutException"/> (the round skips it).
    /// </summary>
    public void Claim()
    {
        var closed = Reap();
        if (_kept.Count > 0)
            throw new TimeoutException($"{what} is still running");
        WinRt.Check(closed, what + ".Close");
    }

    /// <summary>
    /// Those it keeps that have ended closed and let go of (one whose status can't be read only let go of: it can't be
    /// closed); the first failed close's HRESULT, else 0.
    /// </summary>
    public int Reap()
    {
        var failed = 0;
        for (var i = _kept.Count - 1; i >= 0; i--)
        {
            var (info, release) = _kept[i];
            var hr = info.get_Status(out var status);
            if (hr >= 0 && status is not (WinRt.Completed or WinRt.Canceled or WinRt.Error))
                continue;
            var closed = hr >= 0 ? info.Close() : 0;
            _kept.RemoveAt(i);
            release();
            if (closed < 0 && failed == 0)
                failed = closed;
        }

        return failed;
    }

    /// <summary>Keeps an operation that didn't end in time, with what lets go of it (beside any it keeps already: none is replaced).</summary>
    public void Keep(IAsyncInfo info, Action release) => _kept.Add((info, release));

    /// <summary>
    /// Waits for what <paramref name="slots"/> keep to end, at most <paramref name="max"/>, closing and letting go of each
    /// as it ends; those still running then are let go of unclosed. How many were let go of so.
    /// </summary>
    public static int WaitOut(IReadOnlyList<WinAsyncSlot> slots, TimeSpan max)
    {
        var start = Stopwatch.GetTimestamp();
        var pause = 1;
        while (true)
        {
            var busy = false;
            foreach (var slot in slots)
            {
                slot.Reap();
                busy |= slot.Busy;
            }

            if (!busy)
                return 0;
            if (Stopwatch.GetElapsedTime(start) >= max)
            {
                var count = 0;
                foreach (var slot in slots)
                    count += slot.Abandon();
                return count;
            }

            Thread.Sleep(pause);
            pause = Math.Min(pause * 2, 100);
        }
    }

    private int Abandon()
    {
        var count = _kept.Count;
        foreach (var (_, release) in _kept)
            release();
        _kept.Clear();
        return count;
    }
}

/// <summary>
/// A reader's owner's operations that didn't end in time, by kind: the manager's request and the media properties. One
/// a reader, used by its owner alone (the activation and the rounds), looked at by it every
/// <see cref="WinNowPlayingReader.ReapInterval"/> while it keeps any.
/// </summary>
internal sealed class WinPending
{
    public WinAsyncSlot Requests { get; } = new("RequestAsync");

    public WinAsyncSlot Properties { get; } = new("TryGetMediaPropertiesAsync");

    public IReadOnlyList<WinAsyncSlot> Slots => [Requests, Properties];

    public bool Busy => Requests.Busy || Properties.Busy;

    /// <summary>Those that have ended closed and let go of (a failed close's HRESULT dropped: nothing waits for it).</summary>
    public void Reap()
    {
        Requests.Reap();
        Properties.Reap();
    }
}

#pragma warning restore CA1416

/// <summary>The manager over Windows itself, made, used and let go of on its reader's owner, with the owner's <see cref="WinPending"/>.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WinMediaSessions : IWinMediaSessions
{
    private const string ManagerClass = "Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager";

    private readonly IGsmtcManager _manager;
    private readonly WinPending _pending;

    private WinMediaSessions(IGsmtcManager manager, WinPending pending) => (_manager, _pending) = (manager, pending);

    /// <summary>The multithreaded apartment kept and the manager asked for (<c>RequestAsync</c>); throws when that fails.</summary>
    public static IWinMediaSessions Activate(WinPending pending)
    {
        WinRt.KeepMta();
        var statics = WinRt.Factory<IGsmtcManagerStatics>(ManagerClass);
        try
        {
            pending.Requests.Claim();
            WinRt.Check(statics.RequestAsync(out var op), "RequestAsync");
            var manager = WinRt.Await<IAsyncOperationOfManager>(op, static (o, out r) => o.GetResults(out r), WinNowPlayingReader.AsyncTimeout, pending.Requests, "RequestAsync");
            return new WinMediaSessions(WinAudioWrappers.Wrap<IGsmtcManager>(manager), pending);
        }
        finally
        {
            WinAudioWrappers.Free(statics);
        }
    }

    public IWinMediaSession? Current()
    {
        WinRt.Check(_manager.GetCurrentSession(out var p), "GetCurrentSession");
        return p == 0 ? null : new Session(WinAudioWrappers.Wrap<IGsmtcSession>(p), _pending);
    }

    public void Dispose() => WinAudioWrappers.Free(_manager);

    private sealed class Session(IGsmtcSession session, WinPending pending) : IWinMediaSession
    {
        public string AppId
        {
            get
            {
                WinRt.Check(session.get_SourceAppUserModelId(out var id), "SourceAppUserModelId");
                return WinRt.TakeString(id, NowPlayingSource.MaxTextChars) ?? "";
            }
        }

        public IWinMediaProperties? Properties(TimeSpan timeout)
        {
            pending.Properties.Claim();
            WinRt.Check(session.TryGetMediaPropertiesAsync(out var op), "TryGetMediaPropertiesAsync");
            var p = WinRt.Await<IAsyncOperationOfMediaProperties>(op, static (o, out r) => o.GetResults(out r), timeout, pending.Properties, "TryGetMediaPropertiesAsync");
            return p == 0 ? null : MediaProperties.Of(WinAudioWrappers.Wrap<IGsmtcMediaProperties>(p));
        }

        public WinPlayback Playback()
        {
            WinRt.Check(session.GetPlaybackInfo(out var p), "GetPlaybackInfo");
            var info = WinAudioWrappers.Wrap<IGsmtcPlaybackInfo>(p);
            try
            {
                WinRt.Check(info.get_PlaybackStatus(out var status), "PlaybackStatus");
                WinRt.Check(info.get_IsShuffleActive(out var shuffle), "IsShuffleActive");
                bool? shuffleValue = WinRt.Value<IReferenceOfBoolean, byte>(shuffle, static (r, out v) => r.get_Value(out v)) is { } b ? b != 0 : null;
                WinRt.Check(info.get_AutoRepeatMode(out var repeat), "AutoRepeatMode");
                var repeatValue = WinRt.Value<IReferenceOfAutoRepeatMode, int>(repeat, static (r, out v) => r.get_Value(out v));
                WinRt.Check(info.get_PlaybackRate(out var rate), "PlaybackRate");
                var rateValue = WinRt.Value<IReferenceOfDouble, double>(rate, static (r, out v) => r.get_Value(out v));
                return new WinPlayback(status, shuffleValue, repeatValue, rateValue);
            }
            finally
            {
                WinAudioWrappers.Free(info);
            }
        }

        public WinTimeline Timeline()
        {
            WinRt.Check(session.GetTimelineProperties(out var p), "GetTimelineProperties");
            var timeline = WinAudioWrappers.Wrap<IGsmtcTimelineProperties>(p);
            try
            {
                WinRt.Check(timeline.get_StartTime(out var start), "StartTime");
                WinRt.Check(timeline.get_EndTime(out var end), "EndTime");
                WinRt.Check(timeline.get_Position(out var position), "Position");
                WinRt.Check(timeline.get_LastUpdatedTime(out var updated), "LastUpdatedTime");
                return new WinTimeline(TimeSpan.FromTicks(start), TimeSpan.FromTicks(end), TimeSpan.FromTicks(position), WinRt.Instant(updated));
            }
            finally
            {
                WinAudioWrappers.Free(timeline);
            }
        }

        public void Dispose() => WinAudioWrappers.Free(session);
    }

    private sealed class MediaProperties : IWinMediaProperties
    {
        private readonly IGsmtcMediaProperties _properties;

        private MediaProperties(IGsmtcMediaProperties properties) => _properties = properties;

        public string? Title { get; private init; }
        public string? Artist { get; private init; }
        public string? Album { get; private init; }
        public int TrackNumber { get; private init; }
        public IReadOnlyList<string> Genres { get; private init; } = [];

        /// <summary>The properties read whole (the object kept for the thumbnail), or let go of when a read fails.</summary>
        public static MediaProperties Of(IGsmtcMediaProperties p)
        {
            try
            {
                WinRt.Check(p.get_Title(out var title), "Title");
                var t = WinRt.TakeString(title, NowPlayingSource.MaxTextChars + 1);
                WinRt.Check(p.get_Artist(out var artist), "Artist");
                var a = WinRt.TakeString(artist, NowPlayingSource.MaxTextChars + 1);
                WinRt.Check(p.get_AlbumTitle(out var album), "AlbumTitle");
                var al = WinRt.TakeString(album, NowPlayingSource.MaxTextChars + 1);
                WinRt.Check(p.get_TrackNumber(out var track), "TrackNumber");
                WinRt.Check(p.get_Genres(out var genres), "Genres");
                return new MediaProperties(p) { Title = t, Artist = a, Album = al, TrackNumber = track, Genres = Strings(genres) };
            }
            catch
            {
                WinAudioWrappers.Free(p);
                throw;
            }
        }

        public WinThumbnail? Thumbnail(int maxBytes, TimeSpan timeout)
        {
            var start = Stopwatch.GetTimestamp();
            WinRt.Check(_properties.get_Thumbnail(out var reference), "Thumbnail");
            if (reference == 0)
                return null;
            var r = WinAudioWrappers.Wrap<IRandomAccessStreamReference>(reference);

            // This worker's own operations: one not done in time is kept here and waited out before what it reads from
            // is let go of.
            var opens = new WinAsyncSlot("OpenReadAsync");
            var reads = new WinAsyncSlot("ReadAsync");
            WinStream? s = null;
            try
            {
                WinRt.Check(r.OpenReadAsync(out var op), "OpenReadAsync");
                var wait = timeout < WinNowPlayingReader.AsyncTimeout ? timeout : WinNowPlayingReader.AsyncTimeout;
                var stream = WinRt.Await<IAsyncOperationOfStream>(op, static (o, out p) => o.GetResults(out p), wait, opens, "OpenReadAsync");
                if (stream == 0)
                    return null;
                s = new WinStream(WinAudioWrappers.Wrap<IRandomAccessStream>(stream));
                return WinRt.ReadThumbnail(s, maxBytes, timeout - Stopwatch.GetElapsedTime(start), reads);
            }
            finally
            {
                WinAsyncSlot.WaitOut([opens, reads], WinNowPlayingReader.PendingWaitMax);
                s?.Dispose();
                WinAudioWrappers.Free(r);
            }
        }

        public void Dispose() => WinAudioWrappers.Free(_properties);

        /// <summary>A vector of strings read (at most <see cref="WinNowPlayingReader.MaxGenres"/>, each cut), and let go of.</summary>
        private static IReadOnlyList<string> Strings(nint p)
        {
            if (p == 0)
                return [];
            var vector = WinAudioWrappers.Wrap<IVectorViewOfString>(p);
            try
            {
                WinRt.Check(vector.get_Size(out var size), "Genres.Size");
                var list = new List<string>();
                for (uint i = 0; i < size && i < WinNowPlayingReader.MaxGenres; i++)
                {
                    WinRt.Check(vector.GetAt(i, out var s), "Genres.GetAt");
                    if (WinRt.TakeString(s, NowPlayingSource.MaxTextChars + 1) is { Length: > 0 } text)
                        list.Add(text);
                }

                return list;
            }
            finally
            {
                WinAudioWrappers.Free(vector);
            }
        }
    }

    /// <summary>A thumbnail's stream, read through its <c>IInputStream</c> into buffers of Windows' own.</summary>
    private sealed class WinStream(IRandomAccessStream stream) : IWinStream, IDisposable
    {
        private const string BufferClass = "Windows.Storage.Streams.Buffer";

        public string? ContentType
        {
            get
            {
                WinRt.Check(((IContentTypeProvider)stream).get_ContentType(out var type), "ContentType");
                return WinRt.TakeString(type, 256);
            }
        }

        public ulong Size
        {
            get
            {
                WinRt.Check(stream.get_Size(out var size), "Size");
                return size;
            }
        }

        public IWinRead Read(uint count)
        {
            var buffers = WinRt.Factory<IBufferFactory>(BufferClass);
            nint buffer;
            try
            {
                WinRt.Check(buffers.Create(count, out buffer), "Buffer.Create");
            }
            finally
            {
                WinAudioWrappers.Free(buffers);
            }

            try
            {
                WinRt.Check(((IInputStream)stream).ReadAsync(buffer, count, 0 /* InputStreamOptions.None */, out var p), "ReadAsync");
                var op = WinAudioWrappers.Wrap<IAsyncOperationOfBuffer>(p);
                try
                {
                    return new PendingRead(op, buffer);
                }
                catch
                {
                    WinAudioWrappers.Free(op);
                    throw;
                }
            }
            catch
            {
                Marshal.Release(buffer);
                throw;
            }
        }

        public void Dispose() => WinAudioWrappers.Free(stream);
    }

    /// <summary>A read started: the operation and the buffer it reads into, both held until it is let go of.</summary>
    private sealed unsafe class PendingRead(IAsyncOperationOfBuffer op, nint buffer) : IWinRead
    {
        public IAsyncInfo Info { get; } = (IAsyncInfo)op;

        public int CopyTo(Span<byte> destination)
        {
            WinRt.Check(op.GetResults(out var p), "ReadAsync.GetResults");
            if (p == 0)
                return 0;
            var result = WinAudioWrappers.Wrap<IBuffer>(p);
            try
            {
                WinRt.Check(result.get_Length(out var length), "IBuffer.Length");
                var count = (int)Math.Min(length, (uint)destination.Length);
                if (count == 0)
                    return 0;
                WinRt.Check(((IBufferByteAccess)result).Buffer(out var bytes), "IBufferByteAccess.Buffer");
                new ReadOnlySpan<byte>((void*)bytes, count).CopyTo(destination);
                return count;
            }
            finally
            {
                WinAudioWrappers.Free(result);
            }
        }

        public void Release()
        {
            WinAudioWrappers.Free(op);
            Marshal.Release(buffer);
        }
    }
}

/// <summary>The app's name for people from its AppUserModelID.</summary>
[SupportedOSPlatform("windows")]
internal static class WinAppNames
{
    /// <summary>
    /// The shell's Apps folder's name for <paramref name="appId"/> (desktop and packaged apps that have one), else the file
    /// description of a running process named as a <c>.exe</c> id, else null.
    /// </summary>
    public static string? Of(string appId)
    {
        if (ShellName(appId) is { Length: > 0 } name)
            return name;
        return appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && appId.IndexOfAny(['\\', '/']) < 0 ? FileDescription(appId[..^4]) : null;
    }

    private static string? ShellName(string appId)
    {
        var hr = WinRtNative.SHCreateItemFromParsingName(@"shell:AppsFolder\" + appId, 0, typeof(IShellItem).GUID, out var p);
        if (hr < 0 || p == 0)
            return null;
        var item = WinAudioWrappers.Wrap<IShellItem>(p);
        try
        {
            if (item.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out var text) < 0 || text == 0)
                return null;
            return TakeText(text, WinNowPlayingReader.MaxKeptChars);
        }
        finally
        {
            WinAudioWrappers.Free(item);
        }
    }

    /// <summary>
    /// The text of the NUL-terminated UTF-16 string <paramref name="text"/> (the shell's, from the COM allocator), at most
    /// <paramref name="maxChars"/> characters, read no further than that; and the string freed.
    /// </summary>
    internal static unsafe string TakeText(nint text, int maxChars)
    {
        try
        {
            var chars = (char*)text;
            var length = 0;
            while (length < maxChars && chars[length] != '\0')
                length++;
            return new string(chars, 0, length);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    private static string? FileDescription(string processName)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } file && FileVersionInfo.GetVersionInfo(file).FileDescription is { Length: > 0 } description)
                        return description.Length > WinNowPlayingReader.MaxKeptChars ? description[..WinNowPlayingReader.MaxKeptChars] : description;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
                {
                    // Another user's or an ended process: the next one.
                }
            }
        }

        return null;
    }
}

/// <summary>WinRT's plumbing: activation, HSTRINGs, async operations, references, instants and streams.</summary>
[SupportedOSPlatform("windows")]
internal static class WinRt
{
    /// <summary><c>Windows.Foundation.AsyncStatus</c>.</summary>
    public const int Started = 0, Completed = 1, Canceled = 2, Error = 3;

    private static readonly Lock MtaLock = new();
    private static bool _mta;

    public delegate int Get<in TInterface, TValue>(TInterface target, out TValue value);

    public static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new COMException($"{what} failed (0x{hr:X8})", hr);
    }

    public static void KeepMta()
    {
        lock (MtaLock)
        {
            if (_mta)
                return;
            Check(WinRtNative.CoIncrementMTAUsage(out _), "CoIncrementMTAUsage");
            _mta = true;
        }
    }

    /// <summary>The activation factory of <paramref name="runtimeClass"/> as <typeparamref name="T"/>.</summary>
    public static unsafe T Factory<T>(string runtimeClass) where T : class
    {
        nint name;
        fixed (char* chars = runtimeClass)
            Check(WinRtNative.WindowsCreateString(chars, (uint)runtimeClass.Length, out name), "WindowsCreateString");
        try
        {
            Check(WinRtNative.RoGetActivationFactory(name, typeof(T).GUID, out var factory), "RoGetActivationFactory");
            return WinAudioWrappers.Wrap<T>(factory);
        }
        finally
        {
            WinRtNative.WindowsDeleteString(name);
        }
    }

    /// <summary>
    /// The text of <paramref name="hstring"/>, at most <paramref name="maxChars"/> characters, and the HSTRING deleted;
    /// null for the null HSTRING (WinRT's empty string).
    /// </summary>
    public static unsafe string? TakeString(nint hstring, int maxChars)
    {
        if (hstring == 0)
            return null;
        try
        {
            var chars = WinRtNative.WindowsGetStringRawBuffer(hstring, out var length);
            return chars == null ? null : new string(chars, 0, (int)Math.Min(length, (uint)maxChars));
        }
        finally
        {
            WinRtNative.WindowsDeleteString(hstring);
        }
    }

    /// <summary>
    /// The result of the async operation <paramref name="p"/> (its reference taken over) as <typeparamref name="TOperation"/>
    /// gives it once completed, the caller's: <see cref="Await{T}"/>, the operation's wrapper what <paramref name="slot"/>
    /// keeps when it doesn't end in time.
    /// </summary>
    public static nint Await<TOperation>(nint p, Get<TOperation, nint> results, TimeSpan timeout, WinAsyncSlot slot, string what) where TOperation : class
    {
        var op = WinAudioWrappers.Wrap<TOperation>(p);
        IAsyncInfo info;
        try
        {
            info = (IAsyncInfo)op;
        }
        catch
        {
            WinAudioWrappers.Free(op);
            throw;
        }

        return Await(info, () =>
        {
            Check(results(op, out var result), what + ".GetResults");
            return result;
        }, () => WinAudioWrappers.Free(op), static result =>
        {
            if (result != 0)
                Marshal.Release(result);
        }, timeout, slot, what);
    }

    /// <summary>
    /// Waits for <paramref name="info"/> (<see cref="Wait"/>). Completed: <paramref name="results"/> gives its result; it is
    /// closed (a failed close discards the result through <paramref name="discard"/> and throws) and let go of
    /// (<paramref name="release"/>). Failed or cancelled: closed, let go of, and its error thrown. Not done within
    /// <paramref name="timeout"/>: cancelled and handed to <paramref name="slot"/>, which closes and lets go of it once it
    /// has ended, and <see cref="TimeoutException"/> thrown. It is closed only once it has ended, and its close's HRESULT
    /// fails the call unless the call fails already.
    /// </summary>
    public static T Await<T>(IAsyncInfo info, Func<T> results, Action release, Action<T>? discard, TimeSpan timeout, WinAsyncSlot slot, string what)
    {
        int status;
        try
        {
            status = Wait(info, timeout, what);
        }
        catch (TimeoutException)
        {
            slot.Keep(info, release);
            throw;
        }
        catch
        {
            // Its status couldn't be read: it can't be closed, only let go of.
            release();
            throw;
        }

        ExceptionDispatchInfo? failure = null;
        T result = default!;
        try
        {
            if (status == Completed)
            {
                try
                {
                    result = results();
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
            }
            else if (status == Error)
            {
                info.get_ErrorCode(out var code);
                failure = ExceptionDispatchInfo.Capture(new COMException($"{what} failed (0x{code:X8})", code < 0 ? code : unchecked((int)0x80004005)));
            }
            else
            {
                failure = ExceptionDispatchInfo.Capture(new COMException($"{what} was cancelled", unchecked((int)0x80004004)));
            }

            var hr = info.Close();
            if (failure is null && hr < 0)
            {
                discard?.Invoke(result);
                failure = ExceptionDispatchInfo.Capture(new COMException($"{what}.Close failed (0x{hr:X8})", hr));
            }
        }
        finally
        {
            release();
        }

        failure?.Throw();
        return result;
    }

    /// <summary>
    /// Waits for <paramref name="info"/> by asking its status on this thread, and answers it once it has ended
    /// (<see cref="Completed"/>, <see cref="Canceled"/> or <see cref="Error"/>); not ended within <paramref name="timeout"/>,
    /// it is cancelled (not closed: it may still run) and <see cref="TimeoutException"/> is thrown.
    /// </summary>
    public static int Wait(IAsyncInfo info, TimeSpan timeout, string what)
    {
        var start = Stopwatch.GetTimestamp();
        var pause = 1;
        while (true)
        {
            Check(info.get_Status(out var status), what + ".Status");
            if (status is Completed or Canceled or Error)
                return status;

            if (Stopwatch.GetElapsedTime(start) >= timeout)
            {
                // A failed cancel changes nothing: it is kept until it ends either way.
                _ = info.Cancel();
                throw new TimeoutException($"{what} didn't answer within {timeout.TotalSeconds:0.#} s");
            }

            Thread.Sleep(pause);
            pause = Math.Min(pause * 2, 16);
        }
    }

    /// <summary>
    /// The thumbnail <paramref name="stream"/> holds: its size as it says checked against <paramref name="maxBytes"/> first
    /// (over it, nothing is allocated or read: its bytes are null), then read whole by timed reads
    /// (<see cref="Await{T}"/> through <paramref name="slot"/>), all within <paramref name="timeout"/>; fewer bytes when it
    /// ends sooner.
    /// </summary>
    public static WinThumbnail ReadThumbnail(IWinStream stream, int maxBytes, TimeSpan timeout, WinAsyncSlot slot)
    {
        var start = Stopwatch.GetTimestamp();
        var type = stream.ContentType;
        var size = stream.Size;
        if (size > (ulong)maxBytes)
            return new WinThumbnail(null, type, size);

        var bytes = new byte[(int)size];
        var total = 0;
        while (total < bytes.Length)
        {
            var left = timeout - Stopwatch.GetElapsedTime(start);
            if (left <= TimeSpan.Zero)
                throw new TimeoutException($"the thumbnail's stream wasn't read within {timeout.TotalSeconds:0.#} s");
            slot.Claim();
            var read = stream.Read((uint)(bytes.Length - total));
            var at = total;
            var count = Await(read.Info, () => read.CopyTo(bytes.AsSpan(at)), read.Release, null, left, slot, "ReadAsync");
            if (count == 0)
                break;
            total += count;
        }

        return new WinThumbnail(total == bytes.Length ? bytes : bytes[..total], type, size);
    }

    /// <summary>The value of the <c>IReference&lt;T&gt;</c> <paramref name="p"/> (its reference taken over); null for none.</summary>
    public static TValue? Value<TReference, TValue>(nint p, Get<TReference, TValue> get) where TReference : class where TValue : struct
    {
        if (p == 0)
            return null;
        var reference = WinAudioWrappers.Wrap<TReference>(p);
        try
        {
            Check(get(reference, out var value), "IReference.Value");
            return value;
        }
        finally
        {
            WinAudioWrappers.Free(reference);
        }
    }

    /// <summary>A <c>Windows.Foundation.DateTime</c> (100 ns since 1601 UTC) as an instant; null for 0 or one out of range.</summary>
    public static DateTimeOffset? Instant(long universalTime)
    {
        if (universalTime <= 0 || universalTime > DateTime.MaxValue.ToFileTimeUtc())
            return null;
        return new DateTimeOffset(DateTime.FromFileTimeUtc(universalTime));
    }
}

/// <summary>The exports <see cref="WinNowPlayingReader"/> calls; each answers an HRESULT but the raw buffer's.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WinRtNative
{
    [LibraryImport("combase.dll")]
    public static partial int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);

    [LibraryImport("combase.dll")]
    public static partial int WindowsCreateString(char* source, uint length, out nint hstring);

    [LibraryImport("combase.dll")]
    public static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    public static partial char* WindowsGetStringRawBuffer(nint hstring, out uint length);

    [LibraryImport("ole32.dll")]
    public static partial int CoIncrementMTAUsage(out nint cookie);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHCreateItemFromParsingName(string path, nint bindContext, in Guid iid, out nint item);
}

// ---- The interfaces, in the Windows SDK's vtable order; IInspectable's three first in each WinRT one ----

/// <summary><c>IGlobalSystemMediaTransportControlsSessionManagerStatics</c>, whole.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("2050c4ee-11a0-57de-aed7-c97c70338245")]
internal partial interface IGsmtcManagerStatics
{
    [PreserveSig] int GetIids(out uint count, out nint iids);
    [PreserveSig] int GetRuntimeClassName(out nint name);
    [PreserveSig] int GetTrustLevel(out int level);
    [PreserveSig] int RequestAsync(out nint operation);
}

/// <summary><c>IGlobalSystemMediaTransportControlsSessionManager</c>, up to <c>GetCurrentSession</c>.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("cace8eac-e86e-504a-ab31-5ff8ff1bce49")]
internal partial interface IGsmtcManager
{
    [PreserveSig] int GetIids(out uint count, out nint iids);
    [PreserveSig] int GetRuntimeClassName(out nint name);
    [PreserveSig] int GetTrustLevel(out int level);
    [PreserveSig] int GetCurrentSession(out nint session);
}

/// <summary><c>IGlobalSystemMediaTransportControlsSession</c>, up to <c>GetPlaybackInfo</c>.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("7148c835-9b14-5ae2-ab85-dc9b1c14e1a8")]
internal partial interface IGsmtcSession
{
    [PreserveSig] int GetIids(out uint count, out nint iids);
    [PreserveSig] int GetRuntimeClassName(out nint name);
    [PreserveSig] int GetTrustLevel(out int level);
    [PreserveSig] int get_SourceAppUserModelId(out nint value);
    [PreserveSig] int TryGetMediaPropertiesAsync(out nint operation);
    [PreserveSig] int GetTimelineProperties(out nint value);
    [PreserveSig] int GetPlaybackInfo(out nint value);
}

/// <summary><c>IGlobalSystemMediaTransportControlsSessionMediaProperties</c>, whole.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("68856cf6-adb4-54b2-ac16-05837907acb6")]
internal partial interface IGsmtcMediaProperties
{
    [PreserveSig] int GetIids(out uint count, out nint iids);
    [PreserveSig] int GetRuntimeClassName(out nint name);
    [PreserveSig] int GetTrustLevel(out int level);
    [PreserveSig] int get_Title(out nint value);
    [PreserveSig] int get_Subtitle(out nint value);
    [PreserveSig] int get_AlbumArtist(out nint value);
    [PreserveSig] int get_Artist(out nint value);
    [PreserveSig] int get_AlbumTitle(out nint value);
    [PreserveSig] int get_TrackNumber(out int value);
    [PreserveSig] int get_Genres(out nint value);
    [PreserveSig] int get_AlbumTrackCount(out int value);
    [PreserveSig] int get_PlaybackType(out nint value);
    [PreserveSig] int get_Thumbnail(out nint value);
}

/// <summary><c>IGlobalSystemMediaTransportControlsSessionPlaybackInfo</c>, whole.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("94b4b6cf-e8ba-51ad-87a7-c10ade106127")]
internal partial interface IGsmtcPlaybackInfo
{
    [PreserveSig] int GetIids(out uint count, out nint iids);
    [PreserveSig] int GetRuntimeClassName(out nint name);
    [PreserveSig] int GetTrustLevel(out int level);
    [PreserveSig] int get_Controls(out nint value);
    [PreserveSig] int get_PlaybackStatus(out int value);
    [PreserveSig] int get_PlaybackType(out nint value);
    [PreserveSig] int get_AutoRepeatMode(out nint value);
    [PreserveSig] int get_PlaybackRate(out nint value);
    [PreserveSig] int get_IsShuffleActive(out nint value);
}

/// <summary><c>IGlobalSystemMediaTransportControlsSessionTimelineProperties</c>, whole; TimeSpan and DateTime are one 64-bit count each.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("ede34136-6f25-588d-8ecf-ea5b6735aaa5")]
internal partial interface IGsmtcTimelineProperties
{
    [PreserveSig] int GetIids(out uint count, out nint iids);
    [PreserveSig] int GetRuntimeClassName(out nint name);
    [PreserveSig] int GetTrustLevel(out int level);
    [PreserveSig] int get_StartTime(out long value);
    [PreserveSig] int get_EndTime(out long value);
    [PreserveSig] int get_MinSeekTime(out long value);
    [PreserveSig] int get_MaxSeekTime(out long value);
    [PreserveSig] int get_Position(out long value);
    [PreserveSig] int get_LastUpdatedTime(out long value);
}

/// <summary><c>Windows.Foundation.IAsyncInfo</c>, whole.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("00000036-0000-0000-c000-000000000046")]
internal partial interface IAsyncInfo
{
    [PreserveSig] int GetIids(out uint count, out nint iids);
    [PreserveSig] int GetRuntimeClassName(out nint name);
    [PreserveSig] int GetTrustLevel(out int level);
    [PreserveSig] int get_Id(out uint id);
    [PreserveSig] int get_Status(out int status);
    [PreserveSig] int get_ErrorCode(out int code);
    [PreserveSig] int Cancel();
    [PreserveSig] int Close();
}

/// <summary><c>IAsyncOperation&lt;GlobalSystemMediaTransportControlsSessionManager&gt;</c>, whole.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("3eec115e-7346-5c27-8c5f-da78514a277b")]
internal partial interface IAsyncOperationOfManager
{
    [PreserveSig] int GetIids(out uint count, out nint iids);
    [PreserveSig] int GetRuntimeClassName(out nint name);
    [PreserveSig] int GetTrustLevel(out int level);
    [PreserveSig] int put_Completed(nint handler);
    [PreserveSig] int get_Completed(out nint handler);
    [PreserveSig] int GetResults(out nint results);
}

/// <summary><c>IAsyncOperation&lt;GlobalSystemMediaTransportControlsSessionMediaProperties&gt;</c>, whole.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("b185e6f3-e0d8-51cb-913f-c98d48c93c46")]
internal partial interface IAsyncOperationOfMediaProperties
{
    [PreserveSig] int GetIids(out uint count, out nint iids);
    [PreserveSig] int GetRuntimeClassName(out nint name);
    [PreserveSig] int GetTrustLevel(out int level);
    [PreserveSig] int put_Completed(nint handler);
    [PreserveSig] int get_Completed(out nint handler);
    [PreserveSig] int GetResults(out nint results);
}

/// <summary><c>IAsyncOperation&lt;IRandomAccessStreamWithContentType&gt;</c>, whole.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("c4a57c5e-32b0-55b3-ad13-ce1c23041ed6")]
internal partial interface IAsyncOperationOfStream
{
    [PreserveSig] int GetIids(out uint count, out nint iids);
    [PreserveSig] int GetRuntimeClassName(out nint name);
    [PreserveSig] int GetTrustLevel(out int level);
    [PreserveSig] int put_Completed(nint handler);
    [PreserveSig] int get_Completed(out nint handler);
    [PreserveSig] int GetResults(out nint results);
}

/// <summary><c>IAsyncOperationWithProgress&lt;IBuffer, UInt32&gt;</c> (what <c>IInputStream.ReadAsync</c> answers), whole.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("d26b2819-897f-5c7d-84d6-56d796561431")]
internal partial interface IAsyncOperationOfBuffer
{
    [PreserveSig] int GetIids(out uint count, out nint iids);
    [PreserveSig] int GetRuntimeClassName(out nint name);
    [PreserveSig] int GetTrustLevel(out int level);
    [PreserveSig] int put_Progress(nint handler);
    [PreserveSig] int get_Progress(out nint handler);
    [PreserveSig] int put_Completed(nint handler);
    [PreserveSig] int get_Completed(out nint handler);
    [PreserveSig] int GetResults(out nint results);
}

/// <summary><c>IReference&lt;Boolean&gt;</c>, whole (a WinRT boolean is one byte).</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("3c00fd60-2950-5939-a21a-2d12c5a01b8a")]
internal partial interface IReferenceOfBoolean
{
    [PreserveSig] int GetIids(out uint count, out nint iids);
    [PreserveSig] int GetRuntimeClassName(out nint name);
    [PreserveSig] int GetTrustLevel(out int level);
    [PreserveSig] int get_Value(out byte value);
}

/// <summary><c>IReference&lt;Double&gt;</c>, whole.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("2f2d6c29-5473-5f3e-92e7-96572bb990e2")]
internal partial interface IReferenceOfDouble
{
    [PreserveSig] int GetIids(out uint count, out nint iids);
    [PreserveSig] int GetRuntimeClassName(out nint name);
    [PreserveSig] int GetTrustLevel(out int level);
    [PreserveSig] int get_Value(out double value);
}

/// <summary><c>IReference&lt;MediaPlaybackAutoRepeatMode&gt;</c>, whole (the enum an Int32).</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("50a7f41f-58d5-5c4d-9475-8dd1acd65836")]
internal partial interface IReferenceOfAutoRepeatMode
{
    [PreserveSig] int GetIids(out uint count, out nint iids);
    [PreserveSig] int GetRuntimeClassName(out nint name);
    [PreserveSig] int GetTrustLevel(out int level);
    [PreserveSig] int get_Value(out int value);
}

/// <summary><c>IVectorView&lt;String&gt;</c>, up to <c>get_Size</c>.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("2f13c006-a03a-5f69-b090-75a43e33423e")]
internal partial interface IVectorViewOfString
{
    [PreserveSig] int GetIids(out uint count, out nint iids);
    [PreserveSig] int GetRuntimeClassName(out nint name);
    [PreserveSig] int GetTrustLevel(out int level);
    [PreserveSig] int GetAt(uint index, out nint value);
    [PreserveSig] int get_Size(out uint size);
}

/// <summary><c>Windows.Storage.Streams.IRandomAccessStreamReference</c>, whole.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("33ee3134-1dd6-4e3a-8067-d1c162e8642b")]
internal partial interface IRandomAccessStreamReference
{
    [PreserveSig] int GetIids(out uint count, out nint iids);
    [PreserveSig] int GetRuntimeClassName(out nint name);
    [PreserveSig] int GetTrustLevel(out int level);
    [PreserveSig] int OpenReadAsync(out nint operation);
}

/// <summary><c>Windows.Storage.Streams.IRandomAccessStream</c>, up to <c>get_Size</c>.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("905a0fe1-bc53-11df-8c49-001e4fc686da")]
internal partial interface IRandomAccessStream
{
    [PreserveSig] int GetIids(out uint count, out nint iids);
    [PreserveSig] int GetRuntimeClassName(out nint name);
    [PreserveSig] int GetTrustLevel(out int level);
    [PreserveSig] int get_Size(out ulong size);
}

/// <summary><c>Windows.Storage.Streams.IInputStream</c>, whole: <c>ReadAsync(IBuffer, UInt32, InputStreamOptions)</c>.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("905a0fe2-bc53-11df-8c49-001e4fc686da")]
internal partial interface IInputStream
{
    [PreserveSig] int GetIids(out uint count, out nint iids);
    [PreserveSig] int GetRuntimeClassName(out nint name);
    [PreserveSig] int GetTrustLevel(out int level);
    [PreserveSig] int ReadAsync(nint buffer, uint count, uint options, out nint operation);
}

/// <summary><c>Windows.Storage.Streams.IBuffer</c>, whole.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("905a0fe0-bc53-11df-8c49-001e4fc686da")]
internal partial interface IBuffer
{
    [PreserveSig] int GetIids(out uint count, out nint iids);
    [PreserveSig] int GetRuntimeClassName(out nint name);
    [PreserveSig] int GetTrustLevel(out int level);
    [PreserveSig] int get_Capacity(out uint value);
    [PreserveSig] int get_Length(out uint value);
    [PreserveSig] int put_Length(uint value);
}

/// <summary><c>Windows.Storage.Streams.IBufferFactory</c> (the <c>Buffer</c> class's activation factory), whole.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("71af914d-c10f-484b-bc50-14bc623b3a27")]
internal partial interface IBufferFactory
{
    [PreserveSig] int GetIids(out uint count, out nint iids);
    [PreserveSig] int GetRuntimeClassName(out nint name);
    [PreserveSig] int GetTrustLevel(out int level);
    [PreserveSig] int Create(uint capacity, out nint value);
}

/// <summary><c>IBufferByteAccess</c> (robuffer.h, an IUnknown one), whole: a buffer's bytes.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("905a0fef-bc53-11df-8c49-001e4fc686da")]
internal partial interface IBufferByteAccess
{
    [PreserveSig] int Buffer(out nint value);
}

/// <summary><c>Windows.Storage.Streams.IContentTypeProvider</c>, whole.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("97d098a5-3b99-4de9-88a5-e11d2f50c795")]
internal partial interface IContentTypeProvider
{
    [PreserveSig] int GetIids(out uint count, out nint iids);
    [PreserveSig] int GetRuntimeClassName(out nint name);
    [PreserveSig] int GetTrustLevel(out int level);
    [PreserveSig] int get_ContentType(out nint value);
}

/// <summary><c>IShellItem</c> (shobjidl_core.h), up to <c>GetDisplayName</c>.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
internal partial interface IShellItem
{
    [PreserveSig] int BindToHandler(nint bindContext, in Guid handler, in Guid iid, out nint value);
    [PreserveSig] int GetParent(out nint parent);
    [PreserveSig] int GetDisplayName(int form, out nint name);
}
