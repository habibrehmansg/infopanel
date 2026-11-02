using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace InfoPanel.Preview.Video;

/// <summary>
/// What a probe trusts: one activation's whole identity, the entry's CLSID, hardware URL and device key (a PCI
/// device-instance path, else an adapter LUID) and the activated transform's driver version, kept as its text (a UINT64
/// written as Windows writes a driver version, or the string the SDK declares, as given). A start trusts an entry only
/// when all four are known and equal to the probed ones; otherwise it probes that entry first.
/// </summary>
internal sealed record MfIdentity(Guid? Clsid, string? HardwareUrl, string? DeviceKey, string? DriverVersion)
{
    public bool Complete => Clsid != null && HardwareUrl != null && DeviceKey != null && DriverVersion != null;

    /// <summary>The CLSID, hardware URL and device key are the entry's: all known and equal (the driver version is the transform's).</summary>
    public bool SameEntry(MfIdentity other) =>
        Clsid != null && HardwareUrl != null && DeviceKey != null && Clsid == other.Clsid && HardwareUrl == other.HardwareUrl && DeviceKey == other.DeviceKey;

    /// <summary>A driver version as Windows writes it: four 16-bit parts (32.0.21030.2001).</summary>
    public static string Version(ulong v) =>
        string.Create(CultureInfo.InvariantCulture, $"{v >> 48}.{(v >> 32) & 0xFFFF}.{(v >> 16) & 0xFFFF}.{v & 0xFFFF}");

    public override string ToString() =>
        $"CLSID {Clsid?.ToString() ?? "none"}, URL {HardwareUrl ?? "none"}, device {DeviceKey ?? "none"}, driver {DriverVersion ?? "none"}";
}

/// <summary>
/// H.264 from Media Foundation's hardware encoder MFTs, whichever the graphics chip provides, through one implementation
/// with no vendor branch (the vendor id is read for the log only). A probe, once per run, walks the hardware entries
/// Windows lists, in its order: an entry without a hardware URL is skipped unactivated, a transform that isn't
/// asynchronous is shut down unconfigured, and the first that passes a short test encode is trusted: three frames at
/// <see cref="ProbeWidth"/> × <see cref="ProbeHeight"/>, one at a time, each output's time its input's, the first an IDR
/// with one in-band SPS and PPS the stream can send whose SPS describes that size; the forced third teaches whether
/// forcing works (<see cref="ChangesLive"/>). Trust binds to that activation's whole identity (<see cref="MfIdentity"/>);
/// a start on anything else probes it first. A session (<see cref="MfSession"/>) runs on two threads of its own, so
/// <see cref="IH264Session.Submit"/> only converts and queues. Everything native goes through two seams
/// (<see cref="IMfCatalog"/>, <see cref="IMfEncoderPort"/>), so all of it runs over a fake on any OS.
/// </summary>
internal sealed class MediaFoundationH264 : IH264Backend
{
    /// <summary>How long the probe's walk over the entries may take (each attempt's teardown waited for within <see cref="Quarantine.Bound"/> beyond it).</summary>
    public static readonly TimeSpan ProbeBound = TimeSpan.FromSeconds(3);

    /// <summary>How long a start may take, a probe of the entry it activates included.</summary>
    public static readonly TimeSpan StartBound = TimeSpan.FromSeconds(2);

    /// <summary>How long a probe waits for each frame's output, with no further input.</summary>
    public static readonly TimeSpan ProbeFrameWait = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a teardown waits for the MFT lock to flush; past it, it shuts the transform down without flushing.</summary>
    public static readonly TimeSpan FlushLockWait = TimeSpan.FromMilliseconds(250);

    /// <summary>The probe's size: both sides even, neither a multiple of 16, so an encoder that can't crop fails it.</summary>
    public const int ProbeWidth = 322;

    public const int ProbeHeight = 194;

    public const int ProbeFrames = 3;

    /// <summary>Stream changes a session renegotiates; the next one is a failure.</summary>
    public const int MaxStreamChanges = 2;

    /// <summary>What the catalog is asked for: hardware encoders, in Windows' order.</summary>
    public const uint EnumFlags = Mf.EnumHardware | Mf.EnumSortAndFilter;

    private readonly IMfCatalog _catalog;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private MfIdentity? _trusted;
    private bool _forces;

    public MediaFoundationH264(IMfCatalog catalog, ILogger logger, TimeProvider? time = null)
    {
        _catalog = catalog;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public string Name => "Media Foundation";

    public H264Kind Kind => H264Kind.Hardware;

    /// <summary>Whether the probe found a forced keyframe comes out an IDR; false once a session's forced frame came out a delta.</summary>
    public bool ChangesLive
    {
        get
        {
            lock (_lock)
                return _forces;
        }
    }

    /// <summary>A hardware session that doesn't shut down within the bound is a stuck driver: none opens beside it in that stream.</summary>
    public bool GivesUpWhenTeardownHangs => true;

    /// <summary>The identity the last passing probe recorded (tests, logs); null before any.</summary>
    internal MfIdentity? Trusted
    {
        get
        {
            lock (_lock)
                return _trusted;
        }
    }

    internal ILogger Logger => _logger;

    internal TimeProvider Time => _time;

    internal IMfCatalog Catalog => _catalog;

    /// <summary>A session's forced frame came out a delta: keyframe requests restart sessions for the rest of the run.</summary>
    internal void ForcingIgnored()
    {
        lock (_lock)
        {
            if (!_forces)
                return;
            _forces = false;
        }

        _logger.LogWarning("Media Foundation: the encoder ignored a forced keyframe; a keyframe request restarts the session from now on");
    }

    // ---- the probe ----

    public H264Probe Probe() => Probe(CancellationToken.None, _ => { });

    /// <summary>
    /// The walk over the hardware entries, on a thread of its own. <paramref name="stopping"/> ends it at once with the
    /// attempt it runs torn down; an attempt whose teardown passes <see cref="Quarantine.Bound"/> is handed to
    /// <paramref name="abandoned"/> and ends the walk, so nothing is activated beside it.
    /// </summary>
    public H264Probe Probe(CancellationToken stopping, Action<Task> abandoned)
    {
        var walk = RunOnThread("Media Foundation probe", () => Walk(stopping, abandoned));
        // The walk bounds its own waits and ends at once at a stop, its attempt torn down; this one only stops waiting
        // for a native call outside an attempt (the listing) that never returns.
        if (Ended(walk, ProbeBound + Quarantine.Bound + TimeSpan.FromSeconds(1)) && walk.IsCompletedSuccessfully)
            return walk.Result;
        _logger.LogWarning("Media Foundation's probe didn't end within its bound; the preview's video is images");
        return H264Probe.Unavailable;
    }

    private H264Probe Walk(CancellationToken stopping, Action<Task> abandoned)
    {
        var started = _time.GetTimestamp();
        if (Enter("probe", out var enterBusy) is { } enterProblem)
        {
            _logger.LogWarning("Media Foundation's probe couldn't start: {Problem}", enterProblem);
            return enterBusy ? H264Probe.Busy : H264Probe.Unavailable;
        }

        IReadOnlyList<IMfEntry> entries = [];
        try
        {
            if (List(out entries, out var listBusy) is { } listProblem)
            {
                if (listBusy)
                {
                    _logger.LogInformation("Media Foundation: {Problem}; asked again later", listProblem);
                    return H264Probe.Busy;
                }

                _logger.LogInformation("No working hardware H.264 encoder: {Problem}; the preview's video is images", listProblem);
                return H264Probe.Unavailable;
            }

            var deadline = started + Ticks(ProbeBound);
            var tried = new List<string>();
            var busy = false;
            foreach (var entry in entries)
            {
                if (stopping.IsCancellationRequested)
                {
                    _logger.LogInformation("Media Foundation's probe stopped with the engine");
                    return H264Probe.Unavailable;
                }

                if (_time.GetTimestamp() >= deadline)
                {
                    tried.Add(Describe(entry) + ": not tried within the probe's bound");
                    continue;
                }

                var left = false;
                var attempt = ProbeEntry(entry, deadline, stopping, teardown =>
                {
                    left = true;
                    abandoned(teardown);
                });
                if (attempt.Passed)
                {
                    lock (_lock)
                    {
                        _trusted = attempt.Identity;
                        _forces = attempt.Forces;
                    }

                    _logger.LogInformation(
                        "Media Foundation: {Entry} passed the probe in {Seconds} s; forced keyframes {Forces}; identity {Identity}{Tried}",
                        Describe(entry), Seconds(_time.GetElapsedTime(started)), attempt.Forces ? "work" : "don't work", attempt.Identity,
                        tried.Count > 0 ? " (after " + string.Join("; ", tried) + ")" : "");
                    return H264Probe.Available;
                }

                busy |= attempt.Busy;
                tried.Add($"{Describe(entry)}: {attempt.Reason}");
                if (left || stopping.IsCancellationRequested)
                {
                    _logger.LogWarning("Media Foundation's probe ended: {Entry} {Reason}; the preview's video is images", Describe(entry), attempt.Reason);
                    return H264Probe.Unavailable;
                }

                _logger.LogInformation("Media Foundation: {Entry} {Reason}; trying the next", Describe(entry), attempt.Reason);
            }

            if (busy)
            {
                _logger.LogInformation("Media Foundation: no hardware H.264 encoder is free now (tried {Tried}); asked again later", Tried(tried));
                return H264Probe.Busy;
            }

            _logger.LogInformation("No working hardware H.264 encoder (tried {Tried}); the preview's video is images", Tried(tried));
            return H264Probe.Unavailable;
        }
        finally
        {
            Dispose(entries);
            _catalog.LeaveThread();
        }
    }

    internal sealed record Attempt(bool Passed, bool Busy, string Reason, MfIdentity? Identity, bool Forces);

    private static Attempt Failing(string reason, bool busy = false) => new(false, busy, reason, null, false);

    /// <summary>
    /// One entry's test encode, on a session of its own: three frames, each submitted once the previous one's output
    /// came back. Its teardown is waited for within <see cref="Quarantine.Bound"/>; one that doesn't end fails the
    /// entry and goes to <paramref name="leftBehind"/>.
    /// </summary>
    private Attempt ProbeEntry(IMfEntry entry, long deadline, CancellationToken ct, Action<Task> leftBehind)
    {
        if (entry.GetString(Mf.HardwareUrl) == null)
            return Failing("has no hardware URL; not activated");

        var outcomes = Channel.CreateUnbounded<H264Outcome>();
        var session = new MfSession(this, new H264Config(ProbeWidth, ProbeHeight, 30, VideoStream.HardwareBitrate), o => outcomes.Writer.TryWrite(o), probing: true);
        session.Launch(s =>
        {
            if (s.TryActivate(entry, out var busy) is { } problem)
                throw new H264StartException(busy ? H264StartProblem.Busy : H264StartProblem.Fault, problem);
        });

        Attempt attempt;
        try
        {
            attempt = Drive(session, outcomes.Reader, deadline, ct);
        }
        catch (Exception ex)
        {
            attempt = Failing("failed its probe: " + ex.Message);
        }

        var teardown = session.Abort();
        if (!Ended(teardown, Quarantine.Bound))
        {
            leftBehind(teardown);
            return Failing($"didn't tear down within {(int)Quarantine.Bound.TotalMilliseconds} ms");
        }

        return attempt.Passed ? attempt with { Identity = session.Identity } : attempt;
    }

    private Attempt Drive(MfSession session, ChannelReader<H264Outcome> outcomes, long deadline, CancellationToken ct)
    {
        if (!Ended(session.Ready, Remaining(deadline), ct))
            return Failing(ct.IsCancellationRequested ? "was stopped" : "didn't start within the probe's bound");
        if (session.Ready.Exception?.InnerException is H264StartException refused)
            return Failing(refused.Message, refused.Problem == H264StartProblem.Busy);
        if (!session.Ready.IsCompletedSuccessfully)
            return Failing("didn't start");

        var forces = false;
        var pixels = new byte[ProbeWidth * ProbeHeight * 4];
        // The third frame is forced; one that comes out an IDR teaches forcing only when the force was taken (an encoder
        // that doesn't implement it may still make an IDR of its own accord).
        for (var i = 0; i < ProbeFrames; i++)
        {
            // A different grey each frame, so each is an encode of its own.
            Array.Fill(pixels, (byte)(64 + 48 * i));
            if (session.Submit(pixels, ProbeWidth, ProbeHeight, i, i == ProbeFrames - 1, out var refusal) != H264Submit.Accepted)
                return Failing($"refused frame {i + 1}: {refusal}");

            // One at a time: the next frame goes in only once this one's output is back, with no further input.
            var read = outcomes.ReadAsync(ct).AsTask();
            var wait = Remaining(deadline) < ProbeFrameWait ? Remaining(deadline) : ProbeFrameWait;
            if (!Ended(read, wait, ct) || !read.IsCompletedSuccessfully)
                return Failing(ct.IsCancellationRequested ? "was stopped" : $"gave no output for frame {i + 1} within {(int)wait.TotalMilliseconds} ms");

            var outcome = read.Result;
            if (outcome.Kind != H264OutcomeKind.Encoded || outcome.FrameId != i)
                return Failing(outcome.Problem ?? $"frame {i + 1} came back {outcome.Kind}");
            if (i == 0 && Unusable(outcome) is { } why)
                return Failing(why);
            if (i == ProbeFrames - 1)
                forces = outcome.Keyframe && !session.ForceUnsupported;
        }

        return new Attempt(true, false, "passed", null, forces);
    }

    /// <summary>Why the probe's first output isn't a keyframe with an avcC the stream can send; null when it is.</summary>
    private static string? Unusable(H264Outcome outcome)
    {
        if (!outcome.Keyframe || outcome.Avcc == null)
            return "its first frame isn't a keyframe with its parameter sets";
        H264Sps.RewriteForStream(outcome.Avcc, out var problem);
        return problem == null ? null : "its avcC: " + problem;
    }

    // ---- a start ----

    /// <summary>
    /// A session, its setup on its own thread within <see cref="StartBound"/>. Any failure, timed out or not, hands back
    /// the session's one teardown task (<see cref="H264StartException.Teardown"/>); a setup still running when the start
    /// gives up never publishes the session.
    /// </summary>
    public IH264Session Start(H264Config config, Action<H264Outcome> output)
    {
        if (config.Width < 1 || config.Height < 1)
            throw new H264StartException(H264StartProblem.Unsupported, $"a {config.Width}x{config.Height} frame");

        var session = new MfSession(this, config, output, probing: false);
        session.Launch(Choose);
        H264StartException? refusal = null;
        if (!Ended(session.Ready, StartBound))
            refusal = new H264StartException(H264StartProblem.Fault, $"Media Foundation's start took over {Seconds(StartBound)} s");
        else if (session.Ready.Exception?.InnerException is H264StartException refused)
            refusal = refused;
        else if (!session.Ready.IsCompletedSuccessfully)
            refusal = new H264StartException(H264StartProblem.Fault, "Media Foundation's start failed");
        if (refusal == null)
            return session;

        // Retired as the start gives up: a setup still inside a native call goes no further once it returns. The teardown
        // also waits for every probe the setup left behind, collected once the setup has ended (a probe still inside its
        // teardown wait when the start gives up registers its teardown later).
        refusal.Teardown = session.TeardownWithProbes(session.Abort());
        throw refusal;
    }

    /// <summary>How many times a start probes one entry whose activation's identity keeps changing after its probe.</summary>
    public const int ProbesPerEntry = 2;

    /// <summary>
    /// The start's choice, on the session's thread: the trusted entry first (its CLSID, hardware URL and device key),
    /// then each hardware entry in Windows' order. An activation whose whole identity is the trusted one is used; any
    /// other is shut down, its entry probed, and on a pass trusted and activated again, that activation used only when
    /// its whole identity is the one probed (else it is shut down and probed in turn).
    /// </summary>
    private void Choose(MfSession session)
    {
        var deadline = _time.GetTimestamp() + Ticks(StartBound);
        if (List(out var entries, out var listBusy) is { } listProblem)
            throw new H264StartException(listBusy ? H264StartProblem.Busy : H264StartProblem.Fault, listProblem);
        try
        {
            MfIdentity? trusted;
            lock (_lock)
                trusted = _trusted;

            var busy = false;
            var tried = new List<string>();
            foreach (var entry in Ordered(entries, trusted))
            {
                session.CheckAlive();
                if (entry.GetString(Mf.HardwareUrl) == null)
                {
                    tried.Add(Describe(entry) + ": no hardware URL");
                    continue;
                }

                if (session.TryActivate(entry, out var activationBusy) is { } problem)
                {
                    busy |= activationBusy;
                    tried.Add($"{Describe(entry)}: {problem}");
                    continue;
                }

                if (trusted is { Complete: true } && session.Identity == trusted)
                    return;

                // Not the trusted activation, or not all of it known: probed before it is used, and so is every
                // activation after a pass whose identity isn't the one probed (a driver updated in between).
                for (var probes = 1; ; probes++)
                {
                    session.DropPort();
                    session.CheckAlive();
                    var left = false;
                    var attempt = ProbeEntry(entry, deadline, session.Token, teardown =>
                    {
                        left = true;
                        session.LeftBehind.Add(teardown);
                    });
                    session.CheckAlive();
                    if (left)
                        throw new H264StartException(H264StartProblem.Fault, $"{Describe(entry)} {attempt.Reason} in a start's probe");
                    if (!attempt.Passed)
                    {
                        busy |= attempt.Busy;
                        tried.Add($"{Describe(entry)}: {attempt.Reason}");
                        break;
                    }

                    // This start's own pass: only it binds the trust, and forcing can only be turned off here.
                    lock (_lock)
                    {
                        _trusted = attempt.Identity;
                        if (!attempt.Forces)
                            _forces = false;
                    }

                    _logger.LogInformation("Media Foundation: {Entry} passed a start's probe; identity {Identity}", Describe(entry), attempt.Identity);
                    if (session.TryActivate(entry, out activationBusy) is { } again)
                    {
                        busy |= activationBusy;
                        tried.Add($"{Describe(entry)}: {again}");
                        break;
                    }

                    if (session.Identity == attempt.Identity)
                        return;

                    _logger.LogInformation("Media Foundation: {Entry} activated as {Identity} after its probe of {Probed}; not used unprobed",
                        Describe(entry), session.Identity, attempt.Identity);
                    if (probes == ProbesPerEntry)
                    {
                        session.DropPort();
                        tried.Add($"{Describe(entry)}: its identity changed after each of {ProbesPerEntry} probes");
                        break;
                    }
                }
            }

            throw new H264StartException(busy ? H264StartProblem.Busy : H264StartProblem.Fault,
                (busy ? "no hardware H.264 encoder is free now" : "no hardware H.264 encoder passed") + $" (tried {Tried(tried)})");
        }
        finally
        {
            Dispose(entries);
        }
    }

    /// <summary>The trusted entry first (its CLSID, hardware URL and device key), then the rest in Windows' order.</summary>
    private static IEnumerable<IMfEntry> Ordered(IReadOnlyList<IMfEntry> entries, MfIdentity? trusted)
    {
        var first = trusted == null ? null : entries.FirstOrDefault(e => trusted.SameEntry(IdentityOf(e)));
        if (first != null)
            yield return first;
        foreach (var entry in entries)
        {
            if (entry != first)
                yield return entry;
        }
    }

    /// <summary>An entry's identity as far as the entry gives it (the driver version is the activated transform's).</summary>
    internal static MfIdentity IdentityOf(IMfEntry entry) =>
        new(entry.GetGuid(Mf.TransformClsid), entry.GetString(Mf.HardwareUrl),
            entry.GetString(Mf.DeviceInstance) ?? (entry.GetUInt64(Mf.AdapterLuid) is { } luid ? $"LUID {luid:X16}" : null), null);

    // ---- helpers ----

    /// <summary>
    /// The catalog's entries, after Media Foundation's startup; null, or what went wrong (<paramref name="busy"/>: out of
    /// memory at either, transient).
    /// </summary>
    private string? List(out IReadOnlyList<IMfEntry> entries, out bool busy)
    {
        entries = [];
        busy = false;
        try
        {
            var hr = _catalog.Startup();
            if (hr < 0)
            {
                busy = hr == Mf.EOutOfMemory;
                return $"Media Foundation didn't start ({Mf.Hr(hr)})";
            }

            hr = _catalog.Enumerate(EnumFlags, out entries);
            busy = hr == Mf.EOutOfMemory;
            return hr < 0 ? $"Media Foundation couldn't list its encoders ({Mf.Hr(hr)})" : null;
        }
        catch (DllNotFoundException)
        {
            return "this Windows has no Media Foundation (an N edition without the Media Feature Pack)";
        }
    }

    /// <summary>A thread's COM init; null, or what went wrong (<paramref name="busy"/>: out of memory, transient).</summary>
    internal string? Enter(string role, out bool busy)
    {
        busy = false;
        try
        {
            var hr = _catalog.EnterThread(role);
            busy = hr == Mf.EOutOfMemory;
            return hr < 0 ? $"COM didn't start on the {role} thread ({Mf.Hr(hr)})" : null;
        }
        catch (DllNotFoundException)
        {
            return "this Windows has no Media Foundation (an N edition without the Media Feature Pack)";
        }
    }

    /// <summary>For the log: the entry's friendly name and vendor id (the only place the vendor is read).</summary>
    internal static string Describe(IMfEntry entry) =>
        $"{entry.GetString(Mf.FriendlyName) ?? "an unnamed encoder"} ({entry.GetString(Mf.HardwareVendorId) ?? "no vendor id"})";

    private static string Tried(List<string> tried) => tried.Count == 0 ? "none listed" : string.Join("; ", tried);

    private static void Dispose(IReadOnlyList<IMfEntry> entries)
    {
        foreach (var entry in entries)
            entry.Dispose();
    }

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture);

    internal long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _time.TimestampFrequency);

    private TimeSpan Remaining(long deadline)
    {
        var left = _time.GetElapsedTime(_time.GetTimestamp(), deadline);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>Whether <paramref name="task"/> ended (completed, failed or cancelled) within <paramref name="bound"/> on the backend's clock, before <paramref name="ct"/>.</summary>
    internal bool Ended(Task task, TimeSpan bound, CancellationToken ct = default)
    {
        if (task.IsCompleted)
            return true;
        if (bound <= TimeSpan.Zero || ct.IsCancellationRequested)
            return false;
        try
        {
            task.WaitAsync(bound, _time, ct).Wait(CancellationToken.None);
        }
        catch (AggregateException)
        {
        }

        return task.IsCompleted;
    }

    /// <summary>Runs <paramref name="work"/> on a background thread of its own, so a blocking native call never holds a pool thread.</summary>
    internal static Task<T> RunOnThread<T>(string name, Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try
            {
                done.TrySetResult(work());
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = name,
        }.Start();
        return done.Task;
    }
}

/// <summary>
/// One Media Foundation encoder session: the session's thread sets it up (the start's choice or the probe's activation,
/// the settings, the output type then the input type, the input layout, streaming), then feeds it; the event thread reads
/// the encoder's events and outputs. <see cref="Submit"/> converts RGBA to NV12 in the negotiated layout and queues it,
/// never calling Media Foundation; a sample goes in only with a <c>NeedInput</c> credit, under the MFT lock, the abort
/// mark checked before every native input call. Outputs pair with frames by sample time; the first must be an IDR with
/// its parameter sets, every new SPS must describe the session's size, and a forced frame that comes out a delta is a
/// failure. <see cref="Abort"/> reports every outstanding frame aborted, wakes both threads and returns; its one teardown
/// (flush if the lock is free, shut down, join both threads, release) runs on a thread of its own and is never a drain.
/// </summary>
internal sealed class MfSession : IH264Session
{
    /// <summary>The session was aborted during its setup: the setup goes no further.</summary>
    private sealed class Aborted : Exception;

    private readonly record struct Outstanding(long Id, long Time, bool Forced);

    private readonly record struct Queued(byte[] Nv12, long Time, bool Forced);

    private readonly MediaFoundationH264 _backend;
    private readonly IMfCatalog _catalog;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly H264Config _config;
    private readonly Action<H264Outcome> _output;
    private readonly bool _probing;
    private readonly long _duration;
    private readonly long _started;

    // _state guards the fields below it; _report orders outcomes (each reported under it, so none follows Abort's).
    private readonly Lock _state = new();
    private readonly Lock _report = new();
    private readonly Queue<Outstanding> _outstanding = new();
    private readonly Queue<Queued> _queue = new();
    private IMfEncoderPort? _port;
    private bool _activated;
    private bool _streaming;
    private Thread? _thread;
    private Thread? _events;
    private int _credits;
    private bool _aborted;
    private string? _broken;
    private long _lastTime = -1;

    // The MFT lock: ProcessInput, ProcessOutput and ICodecAPI, never two at once.
    private readonly SemaphoreSlim _mft = new(1, 1);

    // A sample queued or a credit given.
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _setupLeft = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _teardown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<byte[]> _pool = new();
    private readonly AnnexB _annexB = new();
    private readonly HashSet<uint> _unknownEvents = [];
    private Nv12Layout _layout;
    private int _inputSize;
    private int _inputAlignment = 1;
    private MfOutputAllocation _allocation;
    private int _changes;
    private bool _cleanPointLogged;
    private long _progress;
    private bool _forceUnsupported;

    public MfSession(MediaFoundationH264 backend, H264Config config, Action<H264Outcome> output, bool probing)
    {
        _backend = backend;
        _catalog = backend.Catalog;
        _logger = backend.Logger;
        _time = backend.Time;
        _config = config;
        _output = output;
        _probing = probing;
        Width = (config.Width + 1) & ~1;
        Height = (config.Height + 1) & ~1;
        _duration = TimeSpan.TicksPerSecond / Math.Max(config.Fps, 1);
        _started = _time.GetTimestamp();
    }

    /// <summary>The session's even size, the frame's padded.</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>The activation's identity (its entry's CLSID, URL and device key, its transform's driver version), once activated.</summary>
    public MfIdentity? Identity { get; private set; }

    /// <summary>Completes once the event thread is up, after the whole setup; fails with the start's refusal.</summary>
    public Task Ready => _ready.Task;

    /// <summary>Cancelled by <see cref="Abort"/>: every wait of the session, a start's probes included, observes it.</summary>
    internal CancellationToken Token => _cts.Token;

    /// <summary>Teardowns of a start's probes that didn't end within their bound (the start fails with them).</summary>
    internal ConcurrentBag<Task> LeftBehind { get; } = [];

    /// <summary>A probe's force the encoder doesn't implement (E_NOTIMPL): the frame went in unforced, and forcing is off.</summary>
    internal bool ForceUnsupported => Volatile.Read(ref _forceUnsupported);

    /// <summary>
    /// A failed start's one teardown: <paramref name="own"/> (this session's), and every probe's teardown the setup left
    /// behind, collected only once the setup has ended, since a probe inside its teardown wait registers it later.
    /// </summary>
    internal async Task TeardownWithProbes(Task own)
    {
        await _setupLeft.Task.ConfigureAwait(false);
        await Task.WhenAll([own, .. LeftBehind]).ConfigureAwait(false);
    }

    public IReadOnlyList<Task> Released => [];

    public int InFlight
    {
        get
        {
            lock (_state)
                return _outstanding.Count;
        }
    }

    public long Progress => Interlocked.Read(ref _progress);

    /// <summary>Always false: Media Foundation's types are fixed while it streams; the frame rate is a hint, and sample times carry the timing.</summary>
    public bool TrySetFrameRate(int fps) => false;

    private bool IsAborted
    {
        get
        {
            lock (_state)
                return _aborted;
        }
    }

    /// <summary>Starts the session's thread: <paramref name="choose"/> leaves it an activated port (<see cref="TryActivate"/>), then the setup.</summary>
    public void Launch(Action<MfSession> choose)
    {
        var thread = new Thread(() => Run(choose))
        {
            IsBackground = true,
            Name = _probing ? "Media Foundation probe session" : "Media Foundation session",
        };
        lock (_state)
            _thread = thread;
        thread.Start();
    }

    /// <summary>The setup goes no further once the session is aborted (a start that gave up).</summary>
    internal void CheckAlive()
    {
        if (IsAborted)
            throw new Aborted();
    }

    // ---- setup, on the session's thread ----

    private void Run(Action<MfSession> choose)
    {
        var entered = false;
        var live = false;
        try
        {
            if (_backend.Enter("session", out var busy) is { } problem)
                throw new H264StartException(busy ? H264StartProblem.Busy : H264StartProblem.Fault, problem);
            entered = true;
            choose(this);
            CheckAlive();
            if (_port == null)
                throw new H264StartException(H264StartProblem.Fault, "no encoder was activated");
            Configure(_port);
            live = StartEvents();
        }
        catch (Aborted)
        {
        }
        catch (H264StartException ex)
        {
            _ready.TrySetException(ex);
        }
        catch (Exception ex)
        {
            _ready.TrySetException(new H264StartException(H264StartProblem.Fault, "Media Foundation's setup failed: " + ex.Message));
        }
        finally
        {
            _setupLeft.TrySetResult();
        }

        try
        {
            if (live)
                Feed(_port!);
        }
        catch (Exception ex)
        {
            Fail("feeding the encoder failed: " + ex.Message);
        }
        finally
        {
            if (entered)
                _catalog.LeaveThread();
        }
    }

    /// <summary>
    /// Activates <paramref name="entry"/> as this session's port and reads its identity; null, or why it isn't used (the
    /// port is then shut down and released). A transform that isn't asynchronous is never configured. <paramref name="busy"/>:
    /// the failure was out of memory, transient.
    /// </summary>
    internal string? TryActivate(IMfEntry entry, out bool busy)
    {
        busy = false;
        CheckAlive();
        var identity = MediaFoundationH264.IdentityOf(entry);
        var port = entry.CreatePort();
        lock (_state)
        {
            _port = port;
            _activated = false;
        }

        var hr = port.Activate();
        if (hr >= 0)
        {
            lock (_state)
                _activated = true;
        }

        // A slow activation that returns after the start gave up goes no further: the teardown shuts it down.
        CheckAlive();
        if (hr < 0)
        {
            DropPort();
            busy = hr == Mf.EOutOfMemory;
            return $"failed to activate ({Mf.Hr(hr)})";
        }

        hr = port.GetUInt32(Mf.TransformAsync, out var isAsync);
        if (hr < 0 || isAsync != 1)
        {
            DropPort();
            busy = hr == Mf.EOutOfMemory;
            return busy ? $"its MF_TRANSFORM_ASYNC can't be read ({Mf.Hr(hr)})" : "isn't asynchronous; not used";
        }

        CheckAlive();
        Identity = identity with { DriverVersion = DriverVersionOf(port) };
        return null;
    }

    /// <summary>
    /// The transform's driver version as text: read as a UINT64 (what the spike saw), else as the string mfapi.h declares
    /// it; null when it is neither.
    /// </summary>
    private static string? DriverVersionOf(IMfEncoderPort port) =>
        port.GetUInt64(Mf.DriverVersion, out var version) >= 0 ? MfIdentity.Version(version)
        : port.GetString(Mf.DriverVersion, out var text) >= 0 && !string.IsNullOrEmpty(text) ? text
        : null;

    /// <summary>Shuts down and releases the session's port during the setup (an activation not used).</summary>
    internal void DropPort()
    {
        IMfEncoderPort? port;
        bool activated;
        lock (_state)
        {
            (port, activated) = (_port, _activated);
            _port = null;
            _activated = false;
        }

        if (port == null)
            return;
        if (activated)
            port.Shutdown();
        port.Release();
    }

    /// <summary>A native step of the setup, only while the session isn't aborted.</summary>
    private int Call(Func<int> step)
    {
        CheckAlive();
        return step();
    }

    /// <summary>
    /// A setup stage's refusal. <c>E_OUTOFMEMORY</c> is Busy at every stage, before the stage's own class: the encoder's
    /// memory is taken now, which another session's end can free.
    /// </summary>
    internal static H264StartException Refused(int hr, H264StartProblem otherwise, string what) =>
        new(hr == Mf.EOutOfMemory ? H264StartProblem.Busy : otherwise, $"{what} ({Mf.Hr(hr)})");

    private void Configure(IMfEncoderPort port)
    {
        var hr = Call(() => port.SetUInt32(Mf.TransformAsyncUnlock, 1));
        if (hr < 0)
            throw Refused(hr, H264StartProblem.Fault, "it refused MF_TRANSFORM_ASYNC_UNLOCK");

        // Hints: logged when refused (out of memory is Busy, as at every stage).
        var refused = new List<string>();
        void Hint(Func<int> set, string name)
        {
            var result = Call(set);
            if (result == Mf.EOutOfMemory)
                throw Refused(result, H264StartProblem.Busy, $"it refused {name}");
            if (result < 0)
                refused.Add($"{name} ({Mf.Hr(result)})");
        }

        Hint(() => port.SetUInt32(Mf.LowLatency, 1), "MF_LOW_LATENCY");
        Hint(() => port.SetCodecValue(Mf.CodecRateControlMode, Mf.RateControlCbr), "CBR");
        Hint(() => port.SetCodecValue(Mf.CodecMeanBitRate, (uint)Math.Min(_config.Bitrate, uint.MaxValue)), "the mean bit rate");
        Hint(() => port.SetCodecValue(Mf.CodecGopSize, 600), "GOP 600");
        Hint(() => port.SetCodecValue(Mf.CodecBFrames, 0), "B-frames 0");

        // Mandatory: low-latency mode (no frame held back), read back where the encoder answers.
        hr = Call(() => port.SetCodecValue(Mf.CodecLowLatencyMode, 1));
        if (hr < 0)
            throw Refused(hr, H264StartProblem.Fault, "it refused low-latency mode");
        uint lowLatency = 1;
        hr = Call(() => port.GetCodecValue(Mf.CodecLowLatencyMode, out lowLatency));
        // An encoder that doesn't answer GetValue for it (E_NOTIMPL, or any failure but out of memory) isn't failed for
        // that: the probe's one-at-a-time frames prove the behaviour.
        if (hr == Mf.EOutOfMemory)
            throw Refused(hr, H264StartProblem.Busy, "its low-latency mode can't be read back");
        if (hr >= 0 && lowLatency == 0)
            throw new H264StartException(H264StartProblem.Fault, "its low-latency mode reads back off");

        // The output type, then the input type; their BT.709 colour attributes are hints (§2.3).
        var output = new MfVideoType(Mf.H264, Width, Height)
        {
            FrameRate = _config.Fps,
            Bitrate = (uint)Math.Min(_config.Bitrate, uint.MaxValue),
            Profile = Mf.ProfileBaseline,
            InterlaceMode = Mf.InterlaceProgressive,
        };
        SetType(port, output, "output", refused);
        MfVideoType? current = null;
        hr = Call(() => port.GetOutputCurrentType(out current));
        if (hr < 0 || current == null)
            throw Refused(hr, H264StartProblem.Fault, "its output type can't be read back");
        if (current.Profile != Mf.ProfileBaseline)
            throw new H264StartException(H264StartProblem.Fault, $"its output type reads back profile {current.Profile?.ToString(CultureInfo.InvariantCulture) ?? "none"}, not Baseline");

        var input = new MfVideoType(Mf.Nv12, Width, Height)
        {
            FrameRate = _config.Fps,
            DefaultStride = Width,
            InterlaceMode = Mf.InterlaceProgressive,
        };
        SetType(port, input, "input", refused);
        if (refused.Count > 0)
            _logger.LogInformation("Media Foundation refused hints, running without them: {Hints}", string.Join(", ", refused));

        ReadLayout(port);
        MfStreamInfo outputInfo = default;
        hr = Call(() => port.GetOutputStreamInfo(out outputInfo));
        if (hr < 0)
            throw Refused(hr, H264StartProblem.Fault, "its output stream info can't be read");
        _allocation = MfOutputAllocation.Of(outputInfo);

        hr = Call(() => port.ProcessMessage(Mf.MessageNotifyBeginStreaming));
        if (hr < 0)
            throw Refused(hr, hr == Mf.HwMftFailedStartStreaming ? H264StartProblem.Busy : H264StartProblem.Fault, "it refused to begin streaming");
        lock (_state)
            _streaming = true;
        hr = Call(() => port.ProcessMessage(Mf.MessageNotifyStartOfStream));
        if (hr < 0)
            throw Refused(hr, H264StartProblem.Fault, "it refused the start of the stream");
    }

    /// <summary>
    /// Sets <paramref name="type"/> with the BT.709 colour attributes; a type refused with them is set again without them,
    /// its format, size, profile, rate and stride kept, and the dropped colour logged as a refused hint. A type refused
    /// without them too is refused by the second answer's class (<see cref="TypeClass"/>).
    /// </summary>
    private void SetType(IMfEncoderPort port, MfVideoType type, string which, List<string> refused)
    {
        var coloured = type with
        {
            Matrix = Mf.MatrixBt709,
            Primaries = Mf.PrimariesBt709,
            Transfer = Mf.TransferBt709,
            NominalRange = Mf.NominalRange16To235,
        };
        var setter = which == "output" ? (Func<MfVideoType, int>)port.SetOutputType : port.SetInputType;
        var hr = Call(() => setter(coloured));
        if (hr >= 0)
            return;
        if (hr == Mf.EOutOfMemory)
            throw Refused(hr, H264StartProblem.Busy, $"it refused the {which} type {coloured}");

        var colourHr = hr;
        hr = Call(() => setter(type));
        if (hr < 0)
            throw Refused(hr, TypeClass(hr), $"it refused the {which} type {type}");
        refused.Add($"BT.709 colour on the {which} type ({Mf.Hr(colourHr)})");
    }

    /// <summary>
    /// The input layout the encoder negotiated, read once both types are set: the row pitch from the input type's
    /// default stride (absent: the width), the chroma plane at pitch × height, the buffer's alignment and minimum size
    /// from the input stream info. One this session can't write is Unsupported, with its reason.
    /// </summary>
    private void ReadLayout(IMfEncoderPort port)
    {
        MfInputLayout layout = default;
        var hr = Call(() => port.GetInputCurrentLayout(out layout));
        if (hr < 0)
            throw Refused(hr, H264StartProblem.Fault, "its input type can't be read back");
        MfStreamInfo info = default;
        hr = Call(() => port.GetInputStreamInfo(out info));
        if (hr < 0)
            throw Refused(hr, H264StartProblem.Fault, "its input stream info can't be read");

        var pitch = layout.DefaultStride ?? Width;
        string? problem = null;
        Nv12Layout nv12 = default;
        if (layout.Width != Width || layout.Height != Height)
            problem = $"a frame of {layout.Width}x{layout.Height} on its input type";
        else if (pitch < 0)
            problem = $"stride {pitch} (bottom-up)";
        else if (pitch < Width)
            problem = $"stride {pitch}, below the width {Width}";
        else if (info.Alignment > 1 && (info.Alignment & (info.Alignment - 1)) != 0)
            problem = $"alignment {info.Alignment}, not a power of two";
        else
        {
            try
            {
                nv12 = new Nv12Layout(Width, Height, pitch);
                if (layout.SampleSize is { } sampleSize && sampleSize < nv12.Size)
                    problem = $"a sample size of {sampleSize}, below the layout's {nv12.Size}";
            }
            catch (ArgumentException ex)
            {
                problem = ex.Message;
            }
        }

        if (problem != null)
            throw new H264StartException(H264StartProblem.Unsupported, $"it asks for an input layout this engine doesn't write: {problem}");

        _layout = nv12;
        _inputSize = Math.Max(nv12.Size, info.Size);
        _inputAlignment = Math.Max(info.Alignment, 1);
        _logger.LogDebug("Media Foundation's input layout at {Width}x{Height}: pitch {Pitch}, {Size} bytes in buffers of {Buffer} aligned to {Alignment}, stream flags 0x{Flags:X}",
            Width, Height, pitch, nv12.Size, _inputSize, _inputAlignment, info.Flags);
    }

    /// <summary>A start's class for a refused type: one the encoder can't take at this size is Unsupported (out of memory: <see cref="Refused"/>).</summary>
    internal static H264StartProblem TypeClass(int hr) => hr switch
    {
        Mf.InvalidMediaType or Mf.TransformTypeNotSet or Mf.UnsupportedD3DType or Mf.EInvalidArg => H264StartProblem.Unsupported,
        _ => H264StartProblem.Fault,
    };

    /// <summary>Starts the event thread, unless the session was aborted (a start that gave up never gets one).</summary>
    private bool StartEvents()
    {
        var thread = new Thread(Events) { IsBackground = true, Name = "Media Foundation events" };
        lock (_state)
        {
            if (_aborted)
                return false;
            _events = thread;
            thread.Start();
        }

        return true;
    }

    // ---- input, on the session's thread ----

    /// <summary>Waits for a queued sample and a credit (one wait, which the abort's cancellation ends) and feeds it.</summary>
    private void Feed(IMfEncoderPort port)
    {
        while (TryNext(out var item))
        {
            string? problem;
            try
            {
                problem = Input(port, item);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                Return(item.Nv12);
            }

            if (problem != null)
                Fail(problem);
        }
    }

    private bool TryNext(out Queued item)
    {
        while (true)
        {
            lock (_state)
            {
                if (_aborted)
                {
                    item = default;
                    return false;
                }

                if (_queue.Count > 0 && _credits > 0)
                {
                    item = _queue.Dequeue();
                    _credits--;
                    return true;
                }
            }

            try
            {
                _signal.Wait(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                item = default;
                return false;
            }
        }
    }

    /// <summary>One sample in, under the MFT lock, the abort mark checked before each native input call.</summary>
    private string? Input(IMfEncoderPort port, Queued item)
    {
        _mft.Wait(_cts.Token);
        try
        {
            if (IsAborted)
                return null;
            int hr;
            if (item.Forced)
            {
                hr = port.SetCodecValue(Mf.CodecForceKeyFrame, 1);
                if (hr == Mf.ENotImpl && _probing)
                {
                    // A capability, not a fault: the probe's frame goes in unforced, and forcing is off.
                    Volatile.Write(ref _forceUnsupported, true);
                    _logger.LogInformation("Media Foundation's encoder doesn't implement forced keyframes ({Hr}); a keyframe request restarts the session", Mf.Hr(hr));
                }
                else if (hr < 0)
                    return $"forcing a keyframe failed ({Mf.Hr(hr)})";
                if (IsAborted)
                    return null;
            }

            hr = port.CreateInputBuffer(_inputSize, _inputAlignment, out var buffer);
            if (hr < 0 || buffer == null)
                return $"no input buffer ({Mf.Hr(hr)})";
            using (buffer)
            {
                hr = buffer.Write(item.Nv12.AsSpan(0, _layout.Size));
                if (hr < 0)
                    return $"the input buffer couldn't be written ({Mf.Hr(hr)})";
                if (IsAborted)
                    return null;
                hr = port.ProcessInput(buffer, item.Time, _duration);
                return hr < 0 ? $"ProcessInput failed ({Mf.Hr(hr)})" : null;
            }
        }
        finally
        {
            _mft.Release();
        }
    }

    // ---- events and output, on the event thread ----

    private void Events()
    {
        if (_backend.Enter("events", out var busy) is { } problem)
        {
            _ready.TrySetException(new H264StartException(busy ? H264StartProblem.Busy : H264StartProblem.Fault, problem));
            return;
        }

        try
        {
            _ready.TrySetResult();
            EventLoop(_port!);
        }
        catch (Exception ex)
        {
            Fail("reading the encoder's events failed: " + ex.Message);
        }
        finally
        {
            _catalog.LeaveThread();
        }
    }

    private void EventLoop(IMfEncoderPort port)
    {
        while (true)
        {
            var hr = port.NextEvent(out var e);
            if (hr == Mf.Shutdown)
                return;
            if (hr < 0)
            {
                Fail($"GetEvent failed ({Mf.Hr(hr)})");
                return;
            }

            if (IsAborted)
                continue;
            // A failing status fails the session whatever the event's type, before any dispatch by type.
            if (e.Status < 0 || e.Type == Mf.EventError)
            {
                Fail($"the encoder reported an error (event {e.Type}, {Mf.Hr(e.Status)})");
                continue;
            }

            switch (e.Type)
            {
                case Mf.EventNeedInput:
                    lock (_state)
                        _credits++;
                    _signal.Release();
                    break;
                case Mf.EventHaveOutput:
                    Output(port);
                    break;
                default:
                    if (_unknownEvents.Add(e.Type))
                        _logger.LogInformation("Media Foundation sent event {Type} ({Status}); ignored", e.Type, Mf.Hr(e.Status));
                    break;
            }
        }
    }

    /// <summary>
    /// <c>ProcessOutput</c> under the MFT lock, into the session's output allocation; a stream change is renegotiated
    /// (the allocation rebuilt) before it runs again.
    /// </summary>
    private void Output(IMfEncoderPort port)
    {
        MfOutput? output = null;
        var hr = 0;
        string? problem = null;
        try
        {
            _mft.Wait(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (IsAborted)
                return;
            hr = port.ProcessOutput(_allocation, out output);
            // An abort ends the renegotiation: nothing more reaches the transform, whose shutdown may already have run.
            while (hr == Mf.TransformStreamChange && problem == null && !IsAborted)
            {
                if (++_changes > MediaFoundationH264.MaxStreamChanges)
                {
                    problem = $"a stream change, {_changes} in one session";
                    break;
                }

                problem = Renegotiate(port);
                if (problem == null && !IsAborted)
                    hr = port.ProcessOutput(_allocation, out output);
            }
        }
        finally
        {
            _mft.Release();
        }

        if (problem != null)
            Fail(problem);
        else if (IsAborted || hr == Mf.TransformNeedMoreInput || (hr == Mf.SFalse && output == null))
            return; // nothing yet (no sample this call: events only): the output follows on a later HaveOutput
        else if (hr < 0 || output == null)
            Fail($"ProcessOutput failed ({Mf.Hr(hr)})");
        else
            Pair(output);
    }

    /// <summary>
    /// A stream change, as Media Foundation specifies: the encoder's first available output type, only when it is H.264
    /// Baseline at the session's size (its sequence header is never read); then the output stream info again, and the
    /// output allocation rebuilt from it. Null, or why the change is refused. An abort is rechecked between the calls: the
    /// renegotiation stops there (null), and the caller's loop ends.
    /// </summary>
    private string? Renegotiate(IMfEncoderPort port)
    {
        var hr = port.GetOutputAvailableType(0, out var type);
        if (IsAborted)
            return null;
        if (hr < 0 || type == null)
            return $"a stream change with no output type offered ({Mf.Hr(hr)})";
        if (type.Subtype != Mf.H264 || (type.Profile is { } profile && profile != Mf.ProfileBaseline) || type.Width != Width || type.Height != Height)
            return $"a stream change to {type}, not H.264 Baseline at {Width}x{Height}";
        hr = port.SetOutputType(type);
        if (IsAborted)
            return null;
        if (hr < 0)
            return $"a stream change's output type was refused ({Mf.Hr(hr)})";
        hr = port.GetOutputStreamInfo(out var info);
        if (IsAborted)
            return null;
        if (hr < 0)
            return $"the output stream info after a stream change can't be read ({Mf.Hr(hr)})";
        _allocation = MfOutputAllocation.Of(info);
        _logger.LogInformation("Media Foundation renegotiated a stream change to {Type}; output samples {Samples}", type,
            _allocation.EncoderProvides ? "the encoder's" : $"ours of {_allocation.Size} bytes aligned to {_allocation.Alignment}");
        return null;
    }

    /// <summary>An output, the outcome of the oldest outstanding frame when its sample time is that frame's.</summary>
    private void Pair(MfOutput output)
    {
        lock (_report)
        {
            Outstanding head;
            string? problem = null;
            lock (_state)
            {
                if (_aborted || _broken != null)
                    return;
                if (!_outstanding.TryPeek(out head))
                    problem = "an output for no frame it was given";
                else if (output.SampleTime is not { } time)
                    problem = "an output with no sample time";
                else if (time != head.Time)
                    problem = $"an output at {time} where frame {head.Id}'s time {head.Time} was due";

                if (problem != null)
                {
                    _broken = problem;
                    if (!_outstanding.TryDequeue(out head))
                        return;
                }
                else
                {
                    _outstanding.Dequeue();
                }
            }

            H264Outcome outcome;
            if (problem != null)
                outcome = new H264Outcome(head.Id, H264OutcomeKind.Failed, Problem: problem);
            else
            {
                try
                {
                    outcome = Build(head, output);
                }
                catch (Exception ex)
                {
                    outcome = new H264Outcome(head.Id, H264OutcomeKind.Failed, Problem: "reading the encoded frame failed: " + ex.Message);
                }
            }

            if (outcome.Kind == H264OutcomeKind.Failed)
            {
                lock (_state)
                    _broken ??= outcome.Problem;
            }

            Report(outcome);
        }
    }

    /// <summary>The frame's access unit: AVCC with the in-band parameter sets only, its new SPS checked against the session's size.</summary>
    private H264Outcome Build(Outstanding head, MfOutput output)
    {
        var unit = _annexB.Normalise(output.Data);
        if (unit.Problem != null)
            return new H264Outcome(head.Id, H264OutcomeKind.Failed, Problem: unit.Problem);
        if (unit.NewSps is { } sps)
        {
            var size = H264Sps.PictureSize(sps);
            if (size != (Width, Height))
                return new H264Outcome(head.Id, H264OutcomeKind.Failed,
                    Problem: "the encoder's picture isn't the frame's size: " + (size is { } s ? $"{s.Width}x{s.Height} for {Width}x{Height}" : "its SPS can't be read"));
        }

        if (head.Forced && !unit.Keyframe && !_probing)
        {
            _backend.ForcingIgnored();
            return new H264Outcome(head.Id, H264OutcomeKind.Failed, Problem: "the encoder ignored a forced keyframe");
        }

        if (output.CleanPoint is { } clean && clean != unit.Keyframe && !_cleanPointLogged)
        {
            _cleanPointLogged = true;
            _logger.LogInformation("Media Foundation's clean-point flag says {Clean} for a frame that {Is} an IDR", clean, unit.Keyframe ? "is" : "isn't");
        }

        return new H264Outcome(head.Id, H264OutcomeKind.Encoded, unit.Data, unit.Keyframe, unit.Keyframe ? unit.Avcc : null);
    }

    /// <summary>The session can't go on: the oldest outstanding frame fails (once), and later frames are refused.</summary>
    private void Fail(string why)
    {
        lock (_report)
        {
            Outstanding head;
            lock (_state)
            {
                if (_aborted || _broken != null)
                    return;
                _broken = why;
                if (!_outstanding.TryDequeue(out head))
                    return;
            }

            Report(new H264Outcome(head.Id, H264OutcomeKind.Failed, Problem: why));
        }
    }

    /// <summary>The outcome barrier: the stream's callback never unwinds into the session's threads.</summary>
    private void Report(H264Outcome outcome)
    {
        try
        {
            _output(outcome);
        }
        catch (Exception)
        {
            // The stream's own failure; nothing of it reaches the encoder's threads.
        }
        finally
        {
            Interlocked.Increment(ref _progress);
        }
    }

    // ---- submissions, on the stream's loop ----

    /// <summary>Converts the frame to NV12 in the negotiated layout and queues it with its sample time; never calls Media Foundation.</summary>
    public H264Submit Submit(byte[] pixels, int width, int height, long frameId, bool forceKeyframe, out string? problem)
    {
        problem = null;
        if (width != _config.Width || height != _config.Height)
        {
            problem = $"a {width}x{height} frame for a {_config.Width}x{_config.Height} session";
            return H264Submit.Refused;
        }

        if (Refusal() is { } refusal)
        {
            problem = refusal;
            return H264Submit.Refused;
        }

        var nv12 = Rent();
        try
        {
            Nv12.Write(pixels, width, height, nv12, _layout);
        }
        catch (ArgumentException ex)
        {
            _pool.Enqueue(nv12);
            problem = ex.Message;
            return H264Submit.Refused;
        }

        // The forced keyframe only where the probe proved it (a probe always forces its third frame).
        var force = forceKeyframe && (_probing || _backend.ChangesLive);
        lock (_state)
        {
            problem = _aborted ? "the session was aborted" : _broken;
            if (problem == null)
            {
                // 100 ns units since the session started, strictly increasing.
                var time = _time.GetElapsedTime(_started).Ticks;
                if (time <= _lastTime)
                    time = _lastTime + 1;
                _lastTime = time;
                _outstanding.Enqueue(new Outstanding(frameId, time, force));
                _queue.Enqueue(new Queued(nv12, time, force));
            }
        }

        if (problem != null)
        {
            Return(nv12);
            return H264Submit.Refused;
        }

        _signal.Release();
        return H264Submit.Accepted;
    }

    private string? Refusal()
    {
        lock (_state)
            return _aborted ? "the session was aborted" : _broken ?? (_layout.Width == 0 ? "the session hasn't started" : null);
    }

    private byte[] Rent()
    {
        while (_pool.TryDequeue(out var buffer))
        {
            if (buffer.Length == _layout.Size)
                return buffer;
        }

        return new byte[_layout.Size];
    }

    private void Return(byte[] buffer)
    {
        if (_pool.Count <= VideoStream.MaxInFlight)
            _pool.Enqueue(buffer);
    }

    // ---- abort and teardown ----

    /// <summary>
    /// Ends the session without draining it: every outstanding frame reported aborted now, both threads' waits woken,
    /// and nothing reported after this returns. The one teardown runs on a thread of its own: it waits for a setup still
    /// inside a native call, flushes if the MFT lock is free within <see cref="MediaFoundationH264.FlushLockWait"/>, shuts
    /// the transform down (which ends a blocked <c>GetEvent</c>), joins both threads and releases the port; the task ends
    /// only when all of that has. Idempotent.
    /// </summary>
    public Task Abort()
    {
        List<Outstanding> aborted;
        lock (_report)
        {
            lock (_state)
            {
                if (_aborted)
                    return _teardown.Task;
                _aborted = true;
                aborted = [.. _outstanding];
                _outstanding.Clear();
                while (_queue.TryDequeue(out var queued))
                    Return(queued.Nv12);
            }

            foreach (var frame in aborted)
                Report(new H264Outcome(frame.Id, H264OutcomeKind.Aborted));
        }

        _cts.Cancel();
        new Thread(() =>
        {
            try
            {
                Teardown();
                _teardown.TrySetResult();
            }
            catch (Exception ex)
            {
                _teardown.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Media Foundation teardown",
        }.Start();
        return _teardown.Task;
    }

    private void Teardown()
    {
        var entered = _backend.Enter("teardown", out _) == null;
        try
        {
            // A setup still inside a native call (a slow activation) goes no further once it returns.
            _setupLeft.Task.Wait(CancellationToken.None);
            IMfEncoderPort? port;
            bool activated, streaming;
            Thread? thread, events;
            lock (_state)
                (port, activated, streaming, thread, events) = (_port, _activated, _streaming, _thread, _events);

            if (port != null && streaming)
                Flush(port);
            if (port != null && activated)
                port.Shutdown();
            thread?.Join();
            events?.Join();
            if (port != null)
            {
                port.Release();
                lock (_state)
                {
                    _port = null;
                    _activated = false;
                }
            }
        }
        finally
        {
            if (entered)
                _catalog.LeaveThread();
        }
    }

    /// <summary><c>MFT_MESSAGE_COMMAND_FLUSH</c> when the MFT lock is free within its wait; otherwise none (a call holds it, which the shutdown ends).</summary>
    private void Flush(IMfEncoderPort port)
    {
        using var wait = new CancellationTokenSource(MediaFoundationH264.FlushLockWait, _time);
        try
        {
            _mft.Wait(wait.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Media Foundation's session was shut down without a flush: a call held the encoder");
            return;
        }

        try
        {
            port.ProcessMessage(Mf.MessageCommandFlush);
        }
        finally
        {
            _mft.Release();
        }
    }
}
