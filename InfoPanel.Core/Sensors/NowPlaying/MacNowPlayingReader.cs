using Serilog;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Sensors.NowPlaying;

/// <summary>
/// The Now Playing source's reader on macOS. MediaRemote, which knows what plays, answers only Apple's own programs since
/// macOS 15.4, so the engine runs the media helper (mediaremote-adapter, vendored in <c>native/mediaremote-adapter/</c>)
/// as <c>/usr/bin/perl {script} {framework} stream --no-diff --debounce=200 --micros --allow-missing-title</c>: Apple's
/// Perl loads the helper's framework, which prints the current session as a JSON line at each change until it gets
/// SIGTERM (a session without a title too: its title reads unavailable).
/// <para>
/// The helper's folder (<see cref="FolderName"/>, holding <see cref="ScriptName"/> and <see cref="FrameworkName"/>) is
/// looked for beside the engine, then in the folder <see cref="FolderVariable"/> names; with none it is
/// <c>helperMissing</c>. It runs with absolute paths and Perl's own variables (<see cref="RemovedVariables"/>) out of
/// its environment, ending when the engine does (<see cref="MediaHelperProcess"/>), only between <see cref="Start"/> and
/// <see cref="Stop"/> (SIGTERM, then SIGKILL after <see cref="StopGrace"/>). An exit of 0 is started again after
/// <see cref="RestartDelay"/>; any other exit is <c>helperFailed</c> and it isn't started again in this run (the
/// helper's own advice).
/// </para>
/// <para>
/// Bounded: a line over <see cref="MaxLineBytes"/> is dropped, art over <see cref="NowPlayingSource.MaxArtBytes"/> left
/// out, a line that isn't the JSON expected skipped (one warning a run each). Neither its output nor its standard error
/// is ever logged (either can hold what plays): only how many bytes it wrote to its standard error, with its exit code,
/// at most once a minute.
/// </para>
/// <para>
/// The stream can go quiet after its first track (on macOS 27 it writes the current session once and then nothing at a
/// change of track, whatever its options: MediaRemote's change notifications don't reach it), and it can give a track
/// without its cover while the helper's <c>get</c> has it (it sends a cover only when MediaRemote's answer to a change
/// carries it). So while the stream runs, the helper's <c>get</c> (<see cref="GetOf"/>) is run every
/// <see cref="PollInterval"/>, one at a time, across the stream's runs too (a poll that finds one under way starts none,
/// an earlier run's included, its start too, until it has exited and both its streams have ended), each killed when it
/// hasn't ended within <see cref="GetLimit"/> (its answer then never taken), its first line bounded as the stream's are,
/// its standard error counted, never kept. A <c>get</c> killed, abandoned, or exited with its output ended has
/// <see cref="PipeLimit"/> for its pipes to end (a child of the helper can hold one); then their reads are cut, so it
/// is retired once it has exited, whatever holds them. Its answer, known once it has exited and both its streams have
/// ended or been cut, is the reading, as a stream line is (another track, its state, position and cover, or nothing
/// playing: not when it came with anything on its standard error, a standard error cut at that deadline, or an exit
/// other than 0, as the helper's own timeout does; nor an output cut there), unless the stream gave a line while it
/// ran: then only its cover is taken, for the track that line gave and still without one.
/// A cover is the reading's own track's only (the same app, process, title, artist, album, ids and duration,
/// <see cref="TrackOf"/>) and kept with that track's later lines that lack one; a track with none of a title, the
/// player's ids or a duration can't be told from the app's next one, so no cover is kept for it. A <c>get</c> is started
/// outside the lock, so <see cref="Stop"/> never waits on a start; the stream's end and <see cref="Stop"/> end the polls
/// and kill a <c>get</c> under way (or the one a start under way returns), never waiting for it.
/// </para>
/// <para>
/// Each run's two streams are read on threads of their own, each taking its stream once and disposing it at its end; the
/// run is disposed only once both have ended and it has exited or been stopped; a line read after its run has exited or
/// been stopped (a child of the helper can hold the pipe) is never taken as a reading. Nothing a reader thread meets ends the
/// engine: an error it doesn't expect ends that thread with one warning naming the error's type, never its text.
/// </para>
/// </summary>
public sealed class MacNowPlayingReader : INowPlayingReader
{
    /// <summary>A folder holding the helper, looked in after the one beside the engine.</summary>
    public const string FolderVariable = "INFOPANEL_MEDIA_HELPER";

    /// <summary>The helper's folder beside the engine.</summary>
    public const string FolderName = "mediaremote-adapter";

    public const string ScriptName = "mediaremote-adapter.pl";

    public const string FrameworkName = "MediaRemoteAdapter.framework";

    /// <summary>The framework's library, inside <see cref="FrameworkName"/>.</summary>
    public const string LibraryName = "MediaRemoteAdapter";

    /// <summary>Apple's Perl: MediaRemote answers it.</summary>
    public const string Perl = "/usr/bin/perl";

    /// <summary>A line of the helper's output longer than this (base64 of the largest art, and the rest) is dropped.</summary>
    public const int MaxLineBytes = 12 * 1024 * 1024;

    /// <summary>At most this many apps' names are kept.</summary>
    public const int MaxAppNames = 64;

    public static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(3);

    public static readonly TimeSpan ErrorLogInterval = TimeSpan.FromMinutes(1);

    /// <summary>How often the helper's <c>get</c> is run while the stream runs: the stream can go quiet after its first track.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>A <c>get</c> that hasn't ended this long after its start is killed.</summary>
    public static readonly TimeSpan GetLimit = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A <c>get</c> killed, abandoned, or exited with its output ended has this long for its pipes to end (a child of the
    /// helper can hold one open); then their reads are cut and it is let go of once it has exited.
    /// </summary>
    public static readonly TimeSpan PipeLimit = TimeSpan.FromSeconds(3);

    /// <summary>Variables that would make Perl load other code, left out of the helper's environment.</summary>
    public static IReadOnlyList<string> RemovedVariables { get; } = ["PERL5LIB", "PERL5OPT", "PERLLIB", "PERL5DB"];

    private readonly IMediaHelperProcess _process;
    private readonly IReadOnlyList<string> _folders;
    private readonly Func<string, string?> _appName;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _appNames = new(StringComparer.Ordinal);

    // All under _lock.
    private bool _wanted;
    private bool _missing;
    private bool _failed;
    private long _generation;
    private HeldRun? _run;
    private ITimer? _restart;
    private bool _hasData;
    private NowPlayingSnapshot? _latest;
    private bool _lineWarned;
    private bool _jsonWarned;
    private bool _artWarned;
    private long _lastErrorLog = long.MinValue;
    private (string Script, string Framework)? _paths;
    private string? _latestTrack;
    private ITimer? _poll;
    // The stream's lines the current run has taken: a get's answer is the reading only when none came while it ran.
    private long _lines;
    private GetRun? _get;
    private bool _getWarned;
    // The current track's cover, from a get or the stream: kept with its lines that lack one.
    private (string Track, ReadOnlyMemory<byte> Art, string? Type)? _keptArt;

    /// <param name="process">Starts the helper: <see cref="MediaHelperProcess"/>, or a test's.</param>
    /// <param name="folders">The folders looked in for the helper, in order (<see cref="Candidates"/>).</param>
    /// <param name="appName">An app's name for people by its bundle id, or null (<see cref="MacAppNames.Of"/>).</param>
    public MacNowPlayingReader(IMediaHelperProcess process, IReadOnlyList<string> folders, Func<string, string?> appName, TimeProvider? time = null, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(appName);
        _process = process;
        _folders = folders;
        _appName = appName;
        _time = time ?? TimeProvider.System;
        _log = (log ?? Log.Logger).ForContext<MacNowPlayingReader>();
        _missing = Locate(folders) is null;
    }

    /// <summary>The reader of this Mac's media session through the helper beside the engine or in <see cref="FolderVariable"/>'s folder.</summary>
    [SupportedOSPlatform("macos")]
    public static MacNowPlayingReader CreateSystem(TimeProvider? time = null) =>
        new(new MediaHelperProcess(), Candidates(AppContext.BaseDirectory, Environment.GetEnvironmentVariable(FolderVariable)), MacAppNames.Of, time);

    /// <summary>The folders looked in: <see cref="FolderName"/> beside the engine, then <paramref name="variableFolder"/> when set.</summary>
    public static IReadOnlyList<string> Candidates(string engineFolder, string? variableFolder)
    {
        var folders = new List<string> { Path.Combine(engineFolder, FolderName) };
        if (!string.IsNullOrEmpty(variableFolder))
            folders.Add(variableFolder);
        return folders;
    }

    /// <summary>The script and the framework, full paths, of the first of <paramref name="folders"/> that holds both; else null.</summary>
    public static (string Script, string Framework)? Locate(IReadOnlyList<string> folders)
    {
        foreach (var folder in folders)
        {
            if (!Path.IsPathFullyQualified(folder))
                continue;
            var script = Path.Combine(folder, ScriptName);
            var framework = Path.Combine(folder, FrameworkName);
            if (File.Exists(script) && File.Exists(Path.Combine(framework, LibraryName)))
                return (Path.GetFullPath(script), Path.GetFullPath(framework));
        }

        return null;
    }

    /// <summary>The helper's command line: Apple's Perl, the script, the framework, and the stream's options.</summary>
    public static MediaHelperStart StartOf(string script, string framework) =>
        new(Perl, [script, framework, "stream", "--no-diff", "--debounce=200", "--micros", "--allow-missing-title"], RemovedVariables);

    /// <summary>The helper's <c>get</c>, asked for a cover the stream left out: one line, the current session's payload.</summary>
    public static MediaHelperStart GetOf(string script, string framework) =>
        new(Perl, [script, framework, "get", "--micros", "--allow-missing-title"], RemovedVariables);

    public SourceState? State
    {
        get
        {
            lock (_lock)
            {
                if (_missing)
                    return SourceState.UnavailableBecause(new SourceReason(NowPlayingSource.HelperMissing, "The engine was built or installed without its media helper"));
                if (_failed)
                    return SourceState.UnavailableBecause(new SourceReason(NowPlayingSource.HelperFailed, "The media helper ended with an error, such as macOS refusing the request; it isn't started again until the engine restarts"));
                return null;
            }
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            _wanted = true;
            if (_run != null || _restart != null || _failed)
                return;
            Launch();
        }
    }

    public void Stop() => StopAsync().Wait(StopGrace + TimeSpan.FromSeconds(1));

    /// <summary>Stops the helper: SIGTERM, then SIGKILL when it hasn't ended within <see cref="StopGrace"/>. Completes when it has ended.</summary>
    internal Task StopAsync()
    {
        HeldRun? held;
        IMediaHelperRun? asking;
        lock (_lock)
        {
            _wanted = false;
            _generation++;
            _restart?.Dispose();
            _restart = null;
            held = _run;
            _run = null;
            _hasData = false;
            _latest = null;
            asking = ForgetRun();
        }

        // A get under way is killed, never waited for.
        KillQuietly(asking);
        if (held == null)
            return Task.CompletedTask;
        var run = held.Run;
        _log.Information("Now Playing: stopping the media helper");
        var grace = Task.Delay(StopGrace, _time);
        run.Terminate();
        return Task.WhenAny(run.Exited, grace).ContinueWith(_ =>
        {
            if (!run.Exited.IsCompleted)
            {
                _log.Warning("Now Playing: the media helper didn't end within {Seconds} s of SIGTERM; killed", StopGrace.TotalSeconds);
                run.Kill();
            }

            held.Release();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public bool TryRead(out NowPlayingSnapshot? snapshot)
    {
        lock (_lock)
        {
            snapshot = _latest;
            return _hasData;
        }
    }

    public void Dispose() => Stop();

    /// <summary>Starts a run of the helper. Under the lock.</summary>
    private void Launch()
    {
        var found = Locate(_folders);
        if (found is not { } paths)
        {
            if (!_missing)
                _log.Warning("Now Playing: the media helper isn't beside the engine or in {Variable}: nothing is read", FolderVariable);
            _missing = true;
            return;
        }

        _missing = false;
        _paths = paths;
        IMediaHelperRun run;
        try
        {
            run = _process.Start(StartOf(paths.Script, paths.Framework));
        }
        catch (Exception ex)
        {
            _failed = true;
            _log.Warning("Now Playing: the media helper couldn't be started ({Error}); it isn't tried again in this run", ex.GetType().Name);
            return;
        }

        var generation = ++_generation;
        var held = new HeldRun(run, this);
        _run = held;
        _hasData = false;
        _lineWarned = _jsonWarned = _artWarned = _getWarned = false;
        _lines = 0;
        // While this run is current, the helper's get every PollInterval: the readings follow a stream gone quiet.
        _poll?.Dispose();
        _poll = _time.CreateTimer(_ => Poll(generation), null, PollInterval, PollInterval);
        _log.Information("Now Playing: the media helper started");
        new Thread(() => ReadOutput(held, generation)) { IsBackground = true, Name = "Now Playing output" }.Start();
        new Thread(() => ReadErrors(held)) { IsBackground = true, Name = "Now Playing errors" }.Start();
        run.Exited.ContinueWith(t => OnExit(generation, t), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// A run and who holds it: its owner (until it has ended or been stopped) and its two readers (until each has read its
    /// stream to its end). The last to let go disposes it, so it is never disposed under a reader; a get's run first calls
    /// <paramref name="retired"/>, once its exit and both its streams' ends are known.
    /// </summary>
    private sealed class HeldRun(IMediaHelperRun run, MacNowPlayingReader reader, Action? retired = null)
    {
        private int _holders = 3;

        public IMediaHelperRun Run => run;

        public void Release()
        {
            if (Interlocked.Decrement(ref _holders) != 0)
                return;
            if (retired is not null)
            {
                try
                {
                    retired();
                }
                catch (Exception ex)
                {
                    reader.ReaderFailed("get's answer", ex);
                }
            }

            try
            {
                run.Dispose();
            }
            catch (Exception ex)
            {
                reader._log.Warning("Now Playing: the media helper's run couldn't be disposed ({Error})", ex.GetType().Name);
            }
        }
    }

    /// <summary>How many times a reader thread met an error it didn't expect, which ended it (logged, never thrown). For tests.</summary>
    internal int ReaderFailures => Volatile.Read(ref _readerFailures);

    private int _readerFailures;

    /// <summary>A reader thread's last word on what ended it unexpectedly: a fixed line with the error's type, never its text.</summary>
    private void ReaderFailed(string stream, Exception ex)
    {
        Interlocked.Increment(ref _readerFailures);
        try
        {
            _log.Warning("Now Playing: reading the media helper's {Stream:l} ended on an error ({Error:l})", stream, ex.GetType().Name);
        }
        catch (Exception)
        {
            // Nothing a reader thread meets may end the engine.
        }
    }

    private void OnExit(long generation, Task<int> exited)
    {
        var code = exited.IsCompletedSuccessfully ? exited.Result : -1;
        IMediaHelperRun? asking;
        lock (_lock)
        {
            if (generation != _generation)
                return;
            // A generation of its own from here: a line of the ended run, read after its exit, is never taken.
            var next = ++_generation;
            _run?.Release();
            _run = null;
            _hasData = false;
            _latest = null;
            asking = ForgetRun();
            if (code != 0)
            {
                _failed = true;
                _log.Warning("Now Playing: the media helper ended with exit code {Code}; it isn't started again in this run", code);
            }
            else
            {
                _log.Information("Now Playing: the media helper ended; starting it again in {Seconds} s", RestartDelay.TotalSeconds);
                _restart = _time.CreateTimer(_ => Restart(next), null, RestartDelay, Timeout.InfiniteTimeSpan);
            }
        }

        KillQuietly(asking);
    }

    private void Restart(long generation)
    {
        lock (_lock)
        {
            if (generation != _generation)
                return;
            _restart?.Dispose();
            _restart = null;
            if (_wanted && _run == null && !_failed)
                Launch();
        }
    }

    /// <summary>The output thread: reads the helper's standard output to its end, then disposes it and lets go of the run.</summary>
    private void ReadOutput(HeldRun held, long generation)
    {
        try
        {
            using var output = held.Run.Output;
            ReadLines(output, MaxLineBytes, line => OnLine(line, generation), () => OnLongLine(generation));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe broke or closed under it: its end.
        }
        catch (Exception ex)
        {
            ReaderFailed("output", ex);
        }
        finally
        {
            held.Release();
        }
    }

    private void OnLongLine(long generation)
    {
        lock (_lock)
        {
            if (generation != _generation || _lineWarned)
                return;
            _lineWarned = true;
        }

        _log.Warning("Now Playing: a line of the media helper's output was over {Max} bytes and was dropped", MaxLineBytes);
    }

    private void OnLine(ReadOnlySpan<byte> line, long generation)
    {
        if (line.IsEmpty)
            return;
        bool parsed, artTooBig;
        NowPlayingSnapshot? snapshot;
        string? track;
        try
        {
            parsed = TryParse(line, out snapshot, out artTooBig, out track);
            if (snapshot is { App: { } id })
                snapshot = snapshot with { App = AppName(id) };
        }
        catch (Exception)
        {
            // Whatever a line's content makes fail, it is a line that isn't the JSON expected: skipped, never its text logged.
            parsed = artTooBig = false;
            snapshot = null;
            track = null;
        }

        bool warnJson = false, warnArt = false;
        lock (_lock)
        {
            // Only the current run's lines are taken: none once it has exited or been stopped (skipped, Parsed still told).
            var current = generation == _generation && _run != null;
            if (current && !parsed)
            {
                warnJson = !_jsonWarned;
                _jsonWarned = true;
            }
            else if (current)
            {
                _lines++;
                Take(snapshot, track, artTooBig);
                warnArt = artTooBig && !_artWarned;
                _artWarned |= artTooBig;
            }
        }

        if (warnJson)
            _log.Warning("Now Playing: a line of the media helper's output wasn't the JSON expected and was skipped");
        if (warnArt)
            _log.Warning("Now Playing: cover art over {Max} bytes was left out", NowPlayingSource.MaxArtBytes);
        Parsed?.Invoke();
    }

    /// <summary>Called after each line of the helper's output is taken (or skipped). For tests.</summary>
    internal Action? Parsed { get; set; }

    /// <summary>
    /// Under the lock, a reading taken (a stream line, or a get's answer when no stream line came while it ran): the cover
    /// kept for its track is put on it when it has none; a cover it has is kept for its track; a cover of another track
    /// is dropped. A cover never goes to another track than its own.
    /// </summary>
    private void Take(NowPlayingSnapshot? snapshot, string? track, bool artTooBig)
    {
        if (snapshot is { Art: null } && !artTooBig && _keptArt is { } kept && kept.Track == track)
            snapshot = snapshot with { Art = kept.Art, ArtType = kept.Type };
        else if (snapshot is { Art: { } art } && track is not null)
            _keptArt = (track, art, snapshot.ArtType);
        else if (_keptArt is { } other && other.Track != track)
            _keptArt = null;
        _latest = snapshot;
        _latestTrack = snapshot is null ? null : track;
        _hasData = true;
    }

    /// <summary>
    /// A run of the helper's <c>get</c>: at most one at a time, across the stream's runs too (<see cref="_get"/>, held from
    /// its poll until it is retired: its start failed, or its run has exited and both its streams have ended or been cut
    /// at its deadline).
    /// </summary>
    private sealed class GetRun(long generation, long lines)
    {
        /// <summary>The stream's run it was asked for.</summary>
        public readonly long Generation = generation;

        /// <summary>The stream's lines taken when it was asked for: when more have been since, its answer is older.</summary>
        public readonly long Lines = lines;

        // Under _lock.

        /// <summary>Its run once started; null while its start is under way, outside the lock (<see cref="StartGet"/>).</summary>
        public IMediaHelperRun? Running;

        /// <summary>Kills it <see cref="GetLimit"/> after its start.</summary>
        public ITimer? Limit;

        /// <summary>Its stream's run ended or was stopped: killed (or its run, once its start returns), its answer never taken.</summary>
        public bool Abandoned;

        /// <summary>Killed at <see cref="GetLimit"/>: whatever it wrote before, its answer is never taken.</summary>
        public bool TimedOut;

        /// <summary>Its exit and its output's end are known (each told before it lets go of the run).</summary>
        public bool HasExited, OutputEnded;

        /// <summary>Cuts its pipes' reads <see cref="PipeLimit"/> after it was killed, abandoned, or exited with its output ended.</summary>
        public ITimer? Deadline;

        /// <summary>Retired: the reservation it held has ended.</summary>
        public bool Done;

        /// <summary>Cancelled at its deadline: its pipes' reads end there, whatever holds the pipes.</summary>
        public readonly CancellationTokenSource Reads = new();

        // Written by its output thread, its error thread and its exit, each before it lets go of the run; read once all
        // have (HeldRun's last release orders them).

        /// <summary>Its first line was read whole and was the JSON expected: <see cref="Answer"/> (null: nothing plays).</summary>
        public bool Taken;
        public NowPlayingSnapshot? Answer;
        public string? Track;
        public bool TooLong;
        public bool Malformed;
        public bool ArtTooBig;

        /// <summary>It wrote to its standard error (only counted, never kept), or reading that failed.</summary>
        public bool WroteError;

        /// <summary>Its standard error hadn't ended at its deadline: whether it failed isn't known.</summary>
        public bool ErrorCut;

        /// <summary>Its exit code; -1 when unknown.</summary>
        public int ExitCode = -1;
    }

    /// <summary>
    /// Under the lock: the stream ended or was stopped: no polls, no track, no cover kept. Answers a get to kill: the get
    /// under way is abandoned but stays reserved until it is retired, so no poll of a later run starts one beside it.
    /// </summary>
    private IMediaHelperRun? ForgetRun()
    {
        _poll?.Dispose();
        _poll = null;
        _latestTrack = null;
        _keptArt = null;
        if (_get is not { Abandoned: false } get)
            return null;
        get.Abandoned = true;
        get.Limit?.Dispose();
        get.Limit = null;
        if (get.Running is not null)
            ArmDeadline(get);
        return get.Running;
    }

    /// <summary>
    /// Under the lock: a get killed, abandoned, or exited with its output ended gets <see cref="PipeLimit"/> for its pipes
    /// to end, once; then their reads are cut (<see cref="CutReads"/>), so its run is retired once it has exited, whatever
    /// holds its pipes.
    /// </summary>
    private void ArmDeadline(GetRun get)
    {
        if (get.Done || get.Deadline is not null || get.Reads.IsCancellationRequested)
            return;
        get.Deadline = _time.CreateTimer(_ => CutReads(get), null, PipeLimit, Timeout.InfiniteTimeSpan);
    }

    /// <summary>A get's deadline: its pipes' reads end now (each reader then lets go of the run as at its stream's end).</summary>
    private void CutReads(GetRun get)
    {
        lock (_lock)
        {
            // Kept (disposed) until its retirement, so the gap before the cancel below can't arm it again.
            get.Deadline?.Dispose();
            if (get.Done)
                return;
        }

        _log.Debug("Now Playing: the media helper's get's pipes didn't end within {Seconds} s; their reads are cut", PipeLimit.TotalSeconds);
        try
        {
            get.Reads.Cancel();
        }
        catch (Exception ex)
        {
            ReaderFailed("get's pipes", ex);
        }
    }

    /// <summary>
    /// One read of a get's pipe that ends when <paramref name="cut"/> is cancelled, whether or not the pipe does (a read
    /// left under way is let go of, what it meets observed); then <see cref="OperationCanceledException"/>.
    /// </summary>
    private static int ReadUntil(Stream stream, byte[] buffer, int offset, int count, CancellationToken cut)
    {
        cut.ThrowIfCancellationRequested();
        var read = stream.ReadAsync(buffer, offset, count, cut);
        if (!read.IsCompleted)
        {
            try
            {
                read.Wait(cut);
            }
            catch (OperationCanceledException)
            {
                read.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            catch (AggregateException)
            {
                // The read failed or was cancelled: its own exception, below.
            }
        }

        return read.GetAwaiter().GetResult();
    }

    /// <summary>
    /// The poll, every <see cref="PollInterval"/> while the stream's run <paramref name="generation"/> is current: one get,
    /// reserved under the lock and started outside it; none while one is under way, an earlier run's included.
    /// </summary>
    private void Poll(long generation)
    {
        GetRun get;
        MediaHelperStart start;
        lock (_lock)
        {
            if (generation != _generation || _run is null || _get is not null || _paths is not { } paths)
                return;
            get = new GetRun(generation, _lines);
            _get = get;
            start = GetOf(paths.Script, paths.Framework);
        }

        StartGet(get, start);
    }

    /// <summary>
    /// Outside the lock, so that a start that blocks never holds <see cref="Stop"/> or the stream's lines: starts the get
    /// <paramref name="get"/> reserved, then hands it its run under the lock, with its limit. A run returned after its
    /// stream's run ended (the stream's end, <see cref="Stop"/>) is killed at once, never waited for, and still holds the
    /// reservation until it is retired; a start that fails ends the reservation and leaves it to the next poll.
    /// </summary>
    private void StartGet(GetRun get, MediaHelperStart start)
    {
        IMediaHelperRun? run = null;
        Exception? failed = null;
        try
        {
            run = _process.Start(start);
        }
        catch (Exception ex)
        {
            failed = ex;
        }

        var late = false;
        var warn = false;
        lock (_lock)
        {
            get.Running = run;
            if (run is null)
            {
                if (_get == get)
                    _get = null;
                warn = !get.Abandoned && !_getWarned;
                _getWarned |= warn;
            }
            else if (get.Abandoned)
            {
                late = true;
                ArmDeadline(get);
            }
            else
            {
                get.Limit = _time.CreateTimer(_ => Limit(get, run), null, GetLimit, Timeout.InfiniteTimeSpan);
            }
        }

        if (warn)
            _log.Warning("Now Playing: the media helper's get couldn't be started ({Error}); it is tried at each poll", failed?.GetType().Name ?? "none");
        if (run is null)
            return;
        if (late)
            KillQuietly(run);
        // Read and let go of as any get's run; retired once it has exited and both its streams have ended (a late one's
        // answer is never taken).
        var held = new HeldRun(run, this, () => Retired(get));
        new Thread(() => ReadGet(held, get)) { IsBackground = true, Name = "Now Playing get" }.Start();
        new Thread(() => DrainGet(held, get)) { IsBackground = true, Name = "Now Playing get errors" }.Start();
        run.Exited.ContinueWith(t =>
        {
            get.ExitCode = t.IsCompletedSuccessfully ? t.Result : -1;
            lock (_lock)
            {
                get.HasExited = true;
                if (get.OutputEnded)
                    ArmDeadline(get);
            }

            held.Release();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// A get still under way <see cref="GetLimit"/> after its start is marked timed out under the lock, then killed:
    /// whatever it wrote before (a whole answer, even "nothing plays") is never taken.
    /// </summary>
    private void Limit(GetRun get, IMediaHelperRun run)
    {
        lock (_lock)
        {
            if (_get != get || get.Running != run || get.Abandoned || get.TimedOut)
                return;
            get.TimedOut = true;
            get.Limit?.Dispose();
            get.Limit = null;
            ArmDeadline(get);
        }

        _log.Debug("Now Playing: the media helper's get didn't end within {Seconds} s; killed", GetLimit.TotalSeconds);
        KillQuietly(run);
    }

    /// <summary>A get's output thread: takes its first line (bounded as the stream's) and reads on to its end.</summary>
    private void ReadGet(HeldRun held, GetRun get)
    {
        NowPlayingSnapshot? answer = null;
        string? track = null;
        bool seen = false, tooLong = false, malformed = false, artTooBig = false, cut = false;
        try
        {
            using var output = held.Run.Output;
            var reads = get.Reads.Token;
            ReadLines((buffer, offset, count) => ReadUntil(output, buffer, offset, count, reads), MaxLineBytes, line =>
            {
                if (seen || line.IsEmpty)
                    return;
                seen = true;
                try
                {
                    malformed = !TryParseGet(line, out answer, out artTooBig, out track);
                }
                catch (Exception)
                {
                    // Whatever its content makes fail: not the JSON expected, never its text logged.
                    malformed = true;
                    answer = null;
                }
            }, () =>
            {
                tooLong |= !seen;
                seen = true;
            });
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe broke or closed under it: its end.
        }
        catch (OperationCanceledException)
        {
            // Its deadline cut the read: its output never ended.
            cut = true;
        }
        catch (Exception ex)
        {
            ReaderFailed("get's output", ex);
        }
        finally
        {
            try
            {
                // An answer only when its first line was read whole and was the JSON expected, and its output ended; a get
                // that wrote nothing answers nothing (never "nothing playing").
                var taken = seen && !tooLong && !malformed && !cut;
                if (taken && answer is { App: { } id })
                    answer = answer with { App = AppName(id) };
                get.Answer = taken ? answer : null;
                get.Taken = taken;
                get.Track = track;
                get.TooLong = tooLong;
                get.Malformed = malformed;
                get.ArtTooBig = artTooBig;
            }
            catch (Exception ex)
            {
                ReaderFailed("get's output", ex);
            }

            // Its output is done with: once it has exited too, its standard error has PipeLimit to end.
            lock (_lock)
            {
                get.OutputEnded = true;
                if (get.HasExited)
                    ArmDeadline(get);
            }

            held.Release();
        }
    }

    /// <summary>
    /// A get retired, once: it has exited and both its streams have ended, or their reads were cut at its deadline
    /// (<see cref="PipeLimit"/>). The reservation ends. Its answer, when its stream's run is still current and it was
    /// neither abandoned nor killed at its limit, is the reading when no stream line came while it ran; else only its
    /// cover, for the track that line gave and still without one. "Nothing plays" that came with anything on its standard
    /// error, a standard error still open at its deadline, or an exit other than 0 isn't taken: the helper answers so when
    /// it failed, its own timeout included (its MediaRemote callbacks not answering within 2 s: a line on its standard
    /// error, then <c>null</c>, exit 0).
    /// </summary>
    private void Retired(GetRun get)
    {
        bool warnLine, warnJson, warnArt, failedNull;
        lock (_lock)
        {
            if (_get == get)
                _get = null;
            get.Done = true;
            get.Limit?.Dispose();
            get.Limit = null;
            get.Deadline?.Dispose();
            get.Deadline = null;
            get.Running = null;
            if (get.Abandoned || get.TimedOut || get.Generation != _generation || _run is null)
                return;
            warnLine = get.TooLong && !_lineWarned;
            _lineWarned |= get.TooLong;
            warnJson = get.Malformed && !_jsonWarned;
            _jsonWarned |= get.Malformed;
            warnArt = get.ArtTooBig && !_artWarned;
            _artWarned |= get.ArtTooBig;
            failedNull = get.Taken && get.Answer is null && (get.WroteError || get.ErrorCut || get.ExitCode != 0);
            if (get.Taken && !failedNull)
            {
                if (get.Lines == _lines)
                {
                    Take(get.Answer, get.Track, get.ArtTooBig);
                }
                else if (get.Answer is { Art: { } art } answer && get.Track is { } track && track == _latestTrack && _latest is { Art: null } latest)
                {
                    _keptArt = (track, art, answer.ArtType);
                    _latest = latest with { Art = art, ArtType = answer.ArtType };
                }
            }
        }

        if (warnLine)
            _log.Warning("Now Playing: a line of the media helper's output was over {Max} bytes and was dropped", MaxLineBytes);
        if (warnJson)
            _log.Warning("Now Playing: a line of the media helper's output wasn't the JSON expected and was skipped");
        if (warnArt)
            _log.Warning("Now Playing: cover art over {Max} bytes was left out", NowPlayingSource.MaxArtBytes);
        if (failedNull)
            _log.Debug("Now Playing: the media helper's get said nothing plays with an error or exit code {Code}; not taken", get.ExitCode);
    }

    /// <summary>A get's standard error, read to its end, its bytes counted and never kept (it can name what plays).</summary>
    private void DrainGet(HeldRun held, GetRun get)
    {
        bool wrote = false, cut = false;
        try
        {
            using var error = held.Run.Error;
            var reads = get.Reads.Token;
            var chunk = new byte[4096];
            while (ReadUntil(error, chunk, 0, chunk.Length, reads) > 0)
                wrote = true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe broke or closed under it: its end.
        }
        catch (OperationCanceledException)
        {
            // Its deadline cut the read: a child of the helper holds it.
            cut = true;
        }
        catch (Exception ex)
        {
            wrote = true;
            ReaderFailed("get's standard error", ex);
        }
        finally
        {
            get.WroteError = wrote;
            get.ErrorCut = cut;
            held.Release();
        }
    }

    /// <summary>Kills a get, never waiting for it; whatever that meets is logged by its type only.</summary>
    private void KillQuietly(IMediaHelperRun? run)
    {
        if (run is null)
            return;
        try
        {
            run.Kill();
        }
        catch (Exception ex)
        {
            _log.Debug("Now Playing: the media helper's get couldn't be killed ({Error})", ex.GetType().Name);
        }
    }

    /// <summary>
    /// An app's name for people: <see cref="_appName"/>'s, else the bundle id (a process's: none); a bundle id's once per
    /// id, a process's at each line (a process id can be another app's later).
    /// </summary>
    private string? AppName(string id)
    {
        var byProcess = id.StartsWith(ProcessPrefix, StringComparison.Ordinal);
        lock (_appNames)
        {
            if (!byProcess && _appNames.TryGetValue(id, out var known))
                return known;
        }

        string? name;
        try
        {
            name = _appName(id) is { Length: > 0 } n ? n : byProcess ? null : id;
        }
        catch (Exception)
        {
            name = byProcess ? null : id;
        }

        if (byProcess || name is null)
            return name;
        lock (_appNames)
        {
            if (_appNames.Count >= MaxAppNames)
                _appNames.Clear();
            _appNames[id] = name;
        }

        return name;
    }

    /// <summary>
    /// Reads the helper's standard error to its end, counting its bytes, never keeping them: a helper's error can name what
    /// plays. Once it has ended, how many bytes it wrote there is logged with its exit code, at most once a minute. The
    /// error thread: it disposes the stream and lets go of the run at its end.
    /// </summary>
    private void ReadErrors(HeldRun held)
    {
        try
        {
            ReportErrors(held.Run);
        }
        catch (Exception ex)
        {
            ReaderFailed("standard error", ex);
        }
        finally
        {
            held.Release();
        }
    }

    private void ReportErrors(IMediaHelperRun run)
    {
        long bytes = 0;
        try
        {
            using var error = run.Error;
            var chunk = new byte[4096];
            int read;
            while ((read = error.Read(chunk, 0, chunk.Length)) > 0)
                bytes += read;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe broke or closed under it: its end.
        }

        if (bytes == 0)
            return;
        lock (_lock)
        {
            var now = _time.GetTimestamp();
            if (_lastErrorLog != long.MinValue && _time.GetElapsedTime(_lastErrorLog, now) < ErrorLogInterval)
                return;
            _lastErrorLog = now;
        }

        string code;
        try
        {
            code = run.Exited.Wait(StopGrace + TimeSpan.FromSeconds(1)) ? run.Exited.Result.ToString(CultureInfo.InvariantCulture) : "unknown";
        }
        catch (AggregateException)
        {
            code = "unknown";
        }

        _log.Warning("Now Playing: the media helper wrote {Bytes} bytes to its standard error (not logged: it can name what plays); exit code {Code:l}", bytes, code);
    }

    /// <summary>
    /// Reads <paramref name="stream"/> to its end, handing each line (without its <c>\n</c>, or <c>\r\n</c>) to
    /// <paramref name="onLine"/>; a line longer than <paramref name="maxBytes"/> is dropped whole, with
    /// <paramref name="onTooLong"/>. A last line without its <c>\n</c> is handed over too.
    /// </summary>
    internal static void ReadLines(Stream stream, int maxBytes, SpanAction onLine, Action onTooLong) =>
        ReadLines(stream.Read, maxBytes, onLine, onTooLong);

    /// <summary><see cref="ReadLines(Stream, int, SpanAction, Action)"/> through <paramref name="readInto"/>, a stream's read.</summary>
    private static void ReadLines(Func<byte[], int, int, int> readInto, int maxBytes, SpanAction onLine, Action onTooLong)
    {
        var chunk = new byte[64 * 1024];
        var line = new ArrayBufferWriter<byte>(64 * 1024);
        var dropping = false;
        int read;
        while ((read = readInto(chunk, 0, chunk.Length)) > 0)
        {
            var span = chunk.AsSpan(0, read);
            while (!span.IsEmpty)
            {
                var end = span.IndexOf((byte)'\n');
                var part = end < 0 ? span : span[..end];
                if (!dropping)
                {
                    if (line.WrittenCount + part.Length > maxBytes)
                    {
                        dropping = true;
                        line.ResetWrittenCount();
                        onTooLong();
                    }
                    else
                    {
                        line.Write(part);
                    }
                }

                if (end < 0)
                    break;
                if (!dropping)
                    onLine(TrimCr(line.WrittenSpan));
                dropping = false;
                line.ResetWrittenCount();
                span = span[(end + 1)..];
            }
        }

        if (!dropping && line.WrittenCount > 0)
            onLine(TrimCr(line.WrittenSpan));
    }

    internal delegate void SpanAction(ReadOnlySpan<byte> line);

    private static ReadOnlySpan<byte> TrimCr(ReadOnlySpan<byte> line) => line.EndsWith((byte)'\r') ? line[..^1] : line;

    /// <summary>The app key of a session the helper names by its process alone: <c>pid:{id}</c>.</summary>
    public const string ProcessPrefix = "pid:";

    /// <summary>
    /// One line of the helper's stream: <c>{"type":"data","diff":false,"payload":{…}}</c>. True with null for a null or
    /// empty payload (nothing plays), true with a snapshot for a session (one without a title too: its title is null);
    /// false for a line that isn't that, text that isn't Unicode included. The snapshot's
    /// <see cref="NowPlayingSnapshot.App"/> is the app's key, named later: its parent app's bundle id, else its bundle id,
    /// else <see cref="ProcessPrefix"/> and its process id (the helper run by Perl may not get the bundle id).
    /// </summary>
    internal static bool TryParse(ReadOnlySpan<byte> line, out NowPlayingSnapshot? snapshot, out bool artTooBig) =>
        TryParse(line, out snapshot, out artTooBig, out _);

    /// <summary>
    /// One line of the helper's stream, as above, with the session's <paramref name="track"/> (<see cref="TrackOf"/>; null
    /// when nothing plays, or the track has no identity).
    /// </summary>
    internal static bool TryParse(ReadOnlySpan<byte> line, out NowPlayingSnapshot? snapshot, out bool artTooBig, out string? track)
    {
        snapshot = null;
        artTooBig = false;
        track = null;
        try
        {
            using var document = JsonDocument.Parse(line.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "data"
                || !root.TryGetProperty("payload", out var payload) || payload.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                return false;
            if (root.TryGetProperty("diff", out var diff) && diff.ValueKind == JsonValueKind.True)
                return false;
            return ReadPayload(payload, out snapshot, out artTooBig, out track);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: a string that isn't Unicode (an unpaired surrogate escaped), found when it is read.
            snapshot = null;
            artTooBig = false;
            track = null;
            return false;
        }
    }

    /// <summary>
    /// The helper's <c>get</c> line: the session's payload itself (<c>{…}</c>), or <c>null</c> when nothing plays; read
    /// as a stream line's payload, with its <paramref name="track"/>.
    /// </summary>
    internal static bool TryParseGet(ReadOnlySpan<byte> line, out NowPlayingSnapshot? snapshot, out bool artTooBig, out string? track)
    {
        snapshot = null;
        artTooBig = false;
        track = null;
        try
        {
            using var document = JsonDocument.Parse(line.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            return root.ValueKind is JsonValueKind.Object or JsonValueKind.Null && ReadPayload(root, out snapshot, out artTooBig, out track);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            snapshot = null;
            artTooBig = false;
            track = null;
            return false;
        }
    }

    /// <summary>
    /// The keys a track is known by: the helper's own (app, process, title, artist, album), the player's ids and the
    /// duration.
    /// </summary>
    private static readonly string[] TrackKeys =
        ["processIdentifier", "bundleIdentifier", "parentApplicationBundleIdentifier", "title", "artist", "album", "uniqueIdentifier", "contentItemIdentifier", "durationMicros"];

    /// <summary>
    /// A track's identity, the same in the stream's lines and the <c>get</c>'s: the SHA-256 of its <see cref="TrackKeys"/>'
    /// values (each absent or null one too), so what plays is never kept a second time; null for a track with none of a
    /// title, the player's ids or a duration, which nothing tells from the app's next one. Throws as reading the payload
    /// does.
    /// </summary>
    internal static string? TrackOf(JsonElement payload)
    {
        if (Text(payload, "title") is null && !HasId(payload, "uniqueIdentifier") && !HasId(payload, "contentItemIdentifier")
            && Number(payload, "durationMicros") is not > 0)
            return null;

        var text = new StringBuilder();
        foreach (var key in TrackKeys)
        {
            var value = payload.TryGetProperty(key, out var v) ? v.ValueKind switch
            {
                JsonValueKind.String => "s" + v.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => "n" + v.GetRawText(),
                JsonValueKind.Null => "",
                _ => "o",
            } : "";
            text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>A payload: null for none or an empty one (nothing plays), else its session.</summary>
    private static bool ReadPayload(JsonElement payload, out NowPlayingSnapshot? snapshot, out bool artTooBig, out string? track)
    {
        snapshot = null;
        artTooBig = false;
        track = null;
        if (payload.ValueKind == JsonValueKind.Null || !payload.EnumerateObject().MoveNext())
            return true;

        track = TrackOf(payload);
        var title = Text(payload, "title");
        var playing = payload.TryGetProperty("playing", out var p) && p.ValueKind == JsonValueKind.True;
        ReadOnlyMemory<byte>? art = null;
        string? artType = null;
        if (Text(payload, "artworkData") is { } base64)
        {
            // The bytes it decodes to: three for each four characters, less the padding.
            var padding = base64.EndsWith("==", StringComparison.Ordinal) ? 2 : base64.EndsWith('=') ? 1 : 0;
            if ((long)base64.Length / 4 * 3 - padding > NowPlayingSource.MaxArtBytes)
            {
                artTooBig = true;
            }
            else
            {
                var bytes = new byte[base64.Length / 4 * 3 + 3];
                if (Convert.TryFromBase64String(base64, bytes, out var written) && written > 0 && written <= NowPlayingSource.MaxArtBytes)
                {
                    art = bytes.AsMemory(0, written);
                    artType = Text(payload, "artworkMimeType");
                }
            }
        }

        var timestamp = Number(payload, "timestampEpochMicros");
        snapshot = new NowPlayingSnapshot
        {
            Title = title,
            Artist = Text(payload, "artist"),
            Album = Text(payload, "album"),
            App = Text(payload, "parentApplicationBundleIdentifier") ?? Text(payload, "bundleIdentifier")
                ?? (Number(payload, "processIdentifier") is { } pid && pid >= 1 && pid <= int.MaxValue ? ProcessPrefix + ((int)pid).ToString(CultureInfo.InvariantCulture) : null),
            State = playing ? NowPlayingState.Playing : NowPlayingState.Paused,
            Position = Number(payload, "elapsedTimeMicros") / 1e6,
            PositionAt = timestamp is { } t && t is > 0 and < 253402300799e6 ? DateTimeOffset.UnixEpoch.AddTicks((long)(t * 10)) : null,
            Rate = Number(payload, "playbackRate"),
            Duration = Number(payload, "durationMicros") / 1e6,
            TrackNumber = Number(payload, "trackNumber") is { } n && n >= 1 && n <= int.MaxValue ? (int)n : null,
            Genre = Text(payload, "genre"),
            Shuffle = Number(payload, "shuffleMode") switch { 1d => false, 2d or 3d => true, _ => null },
            Repeat = Number(payload, "repeatMode") switch { 1d => NowPlayingRepeat.Off, 2d => NowPlayingRepeat.One, 3d => NowPlayingRepeat.All, _ => null },
            Art = art,
            ArtType = art is null ? null : artType,
        };
        return true;
    }

    /// <summary>A player's id: a non-empty text or a number.</summary>
    private static bool HasId(JsonElement payload, string name) =>
        Text(payload, name) is not null || payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number;

    private static string? Text(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static double? Number(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
}

/// <summary>
/// An app's name for people by its bundle id or its process (<see cref="MacNowPlayingReader.ProcessPrefix"/>), from Launch
/// Services, the process's executable and its bundle's information.
/// </summary>
public static class MacAppNames
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const uint Utf8 = 0x08000100; // kCFStringEncodingUTF8
    private const int PathBytes = 4096; // PROC_PIDPATHINFO_MAXSIZE

    /// <summary>
    /// The app's <c>CFBundleDisplayName</c>, else its <c>CFBundleName</c>: for a bundle id, of the first app Launch
    /// Services knows by it; for a process (<c>pid:{id}</c>), of the outermost app bundle its executable is in (a
    /// browser's helper names the browser), else the executable's file name. Null when none is found.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static string? Of(string key)
    {
        if (key.StartsWith(MacNowPlayingReader.ProcessPrefix, StringComparison.Ordinal))
            return int.TryParse(key.AsSpan(MacNowPlayingReader.ProcessPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? OfProcess(pid) : null;

        var id = CFStringCreateWithCString(IntPtr.Zero, key, Utf8);
        if (id == IntPtr.Zero)
            return null;
        IntPtr urls = IntPtr.Zero;
        try
        {
            urls = LSCopyApplicationURLsForBundleIdentifier(id, IntPtr.Zero);
            if (urls == IntPtr.Zero || CFArrayGetCount(urls) < 1)
                return null;
            return OfBundle(CFArrayGetValueAtIndex(urls, 0));
        }
        finally
        {
            if (urls != IntPtr.Zero)
                CFRelease(urls);
            CFRelease(id);
        }
    }

    /// <summary>The app of the process <paramref name="pid"/>: its outermost app bundle's name, else its executable's file name.</summary>
    [SupportedOSPlatform("macos")]
    private static unsafe string? OfProcess(int pid)
    {
        var buffer = stackalloc byte[PathBytes];
        var length = proc_pidpath(pid, buffer, PathBytes);
        if (length <= 0)
            return null;
        var path = Encoding.UTF8.GetString(buffer, length);
        var app = path.IndexOf(".app/", StringComparison.Ordinal);
        if (app > 0)
        {
            var bundlePath = path[..(app + 4)];
            var url = CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, Encoding.UTF8.GetBytes(bundlePath), Encoding.UTF8.GetByteCount(bundlePath), true);
            if (url != IntPtr.Zero)
            {
                try
                {
                    if (OfBundle(url) is { } name)
                        return name;
                }
                finally
                {
                    CFRelease(url);
                }
            }

            return Path.GetFileNameWithoutExtension(bundlePath) is { Length: > 0 } folder ? folder : null;
        }

        return Path.GetFileName(path) is { Length: > 0 } file ? file : null;
    }

    [SupportedOSPlatform("macos")]
    private static string? OfBundle(IntPtr url)
    {
        var bundle = CFBundleCreate(IntPtr.Zero, url);
        if (bundle == IntPtr.Zero)
            return null;
        try
        {
            return Value(bundle, "CFBundleDisplayName") ?? Value(bundle, "CFBundleName");
        }
        finally
        {
            CFRelease(bundle);
        }
    }

    [SupportedOSPlatform("macos")]
    private static string? Value(IntPtr bundle, string key)
    {
        var name = CFStringCreateWithCString(IntPtr.Zero, key, Utf8);
        if (name == IntPtr.Zero)
            return null;
        try
        {
            var value = CFBundleGetValueForInfoDictionaryKey(bundle, name);
            if (value == IntPtr.Zero || CFGetTypeID(value) != CFStringGetTypeID())
                return null;
            var length = CFStringGetLength(value);
            if (length <= 0 || length > NowPlayingSource.MaxTextChars)
                return null;
            var chars = new char[length];
            CFStringGetCharacters(value, new CFRange(0, length), chars);
            var text = new string(chars).Trim();
            return text.Length > 0 ? text : null;
        }
        finally
        {
            CFRelease(name);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CFRange(nint location, nint length)
    {
        public readonly nint Location = location;
        public readonly nint Length = length;
    }

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);

    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetCount(IntPtr array);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFBundleCreate(IntPtr allocator, IntPtr url);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFBundleGetValueForInfoDictionaryKey(IntPtr bundle, IntPtr key);

    [DllImport(CoreFoundation)]
    private static extern nuint CFGetTypeID(IntPtr value);

    [DllImport(CoreFoundation)]
    private static extern nuint CFStringGetTypeID();

    [DllImport(CoreFoundation)]
    private static extern nint CFStringGetLength(IntPtr text);

    [DllImport(CoreFoundation, CharSet = CharSet.Unicode)]
    private static extern void CFStringGetCharacters(IntPtr text, CFRange range, [Out] char[] buffer);

    [DllImport(CoreServices)]
    private static extern IntPtr LSCopyApplicationURLsForBundleIdentifier(IntPtr bundleId, IntPtr error);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFURLCreateFromFileSystemRepresentation(IntPtr allocator, byte[] path, nint length, [MarshalAs(UnmanagedType.U1)] bool isDirectory);

    [DllImport(LibSystem)]
    private static extern unsafe int proc_pidpath(int pid, byte* buffer, uint bufferSize);
}
