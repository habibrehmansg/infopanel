namespace InfoPanel.Preview.Video;

/// <summary>Where a backend encodes: the frame header's <c>encoder</c> (<c>hardware</c> or <c>software</c>).</summary>
internal enum H264Kind
{
    Hardware,
    Software,
}

/// <summary>
/// Why a backend refused to start a session. Only <see cref="Fault"/> counts against the backend: a backend's capacity
/// depends on the configuration, so a size it can't take, or no session free right now, says nothing about its health.
/// </summary>
internal enum H264StartProblem
{
    /// <summary>This size or rate is beyond the backend: cached for that backend and size for the run.</summary>
    Unsupported,

    /// <summary>No session free now: the same backend is tried again after a backoff.</summary>
    Busy,

    /// <summary>Anything else: an unknown status, or a refused mandatory setting (a guarantee the stream rests on).</summary>
    Fault,
}

/// <summary>What a backend's probe found.</summary>
internal enum H264Probe
{
    /// <summary>It works here.</summary>
    Available,

    /// <summary>It works here, but no session is free now: transient, asked again after a backoff, never cached.</summary>
    Busy,

    /// <summary>It can't work here (none, unsupported, or a fault): cached for the run.</summary>
    Unavailable,
}

/// <summary>A refused start, with what kind of refusal it was.</summary>
internal sealed class H264StartException(H264StartProblem problem, string message) : Exception(message)
{
    public H264StartProblem Problem { get; } = problem;

    /// <summary>
    /// The teardown of what the start had created, running off the caller's thread (null: nothing to tear down). It may
    /// never end: the caller bounds its wait and counts one that doesn't end as a quarantined session.
    /// </summary>
    public Task? Teardown { get; set; }
}

/// <summary>A session's configuration: the frame's size (the session pads odd sides), the expected frame rate and the bit rate.</summary>
internal sealed record H264Config(int Width, int Height, int Fps, long Bitrate);

internal enum H264OutcomeKind
{
    /// <summary>The frame's access unit: AVCC (4-byte NAL lengths), no parameter sets in band; on a keyframe, the avcC.</summary>
    Encoded,

    /// <summary>The encoder dropped the frame (no access unit); the session is fine.</summary>
    Dropped,

    /// <summary>The encoder failed the frame, or reading its output failed: a failure of the session.</summary>
    Failed,

    /// <summary>The session was aborted before the frame came back.</summary>
    Aborted,
}

/// <summary>The one outcome of one accepted submission.</summary>
internal readonly record struct H264Outcome(long FrameId, H264OutcomeKind Kind, byte[]? Data = null, bool Keyframe = false, byte[]? Avcc = null,
    string? Problem = null);

/// <summary>What <see cref="IH264Session.Submit"/> did with a frame.</summary>
internal enum H264Submit
{
    /// <summary>Taken: exactly one outcome follows (possibly before <c>Submit</c> returns).</summary>
    Accepted,

    /// <summary>Not taken, and no outcome follows; the session is fine (the frame is skipped).</summary>
    Skipped,

    /// <summary>Not taken, and no outcome follows: a failure of the session.</summary>
    Refused,
}

/// <summary>
/// An H.264 encoder: hardware or software, found on this computer or not. The settings a stream's guarantees rest on are
/// mandatory (Baseline profile, no frame reordering, hardware for a hardware backend, the BT.709 matrix): a backend that
/// can't establish one refuses the start as a <see cref="H264StartProblem.Fault"/>. Hints (real time, the expected frame
/// rate, the keyframe interval, the bit rate) it can't set are logged and the session runs.
/// </summary>
internal interface IH264Backend
{
    /// <summary>For the log: "VideoToolbox".</summary>
    string Name { get; }

    H264Kind Kind { get; }

    /// <summary>
    /// Whether a session takes a forced keyframe and a new expected frame rate while it runs (VideoToolbox's does).
    /// A backend whose sessions can't (an ffmpeg process: its arguments are fixed when it starts, and its first frame is
    /// its only keyframe) has its session replaced for them instead, which isn't a failure.
    /// </summary>
    bool ChangesLive => true;

    /// <summary>
    /// Whether a session whose teardown doesn't end within <see cref="Quarantine.Bound"/> gives the backend up for the
    /// stream, instead of another session starting beside it: an ffmpeg process whose output reader hasn't ended. A
    /// VideoToolbox session's is quarantined and the stream goes on with a new one.
    /// </summary>
    bool GivesUpWhenTeardownHangs => false;

    /// <summary>
    /// Whether the backend works here (a tiny session), with the refusal's class: <see cref="H264Probe.Busy"/> when no
    /// session is free now (asked again after a backoff), <see cref="H264Probe.Unavailable"/> when it can't work here.
    /// Runs off the receive and render threads, and is bounded by the backend.
    /// </summary>
    H264Probe Probe();

    /// <summary>
    /// <see cref="Probe()"/> as the run's chain runs it: <paramref name="stopping"/> is the engine's stop, which ends the
    /// probe at once (unavailable) with whatever it started ended, and <paramref name="abandoned"/> is handed the teardown
    /// of a session the probe started and stopped waiting for at <see cref="Quarantine.Bound"/>, which fails the probe and
    /// counts as a quarantined session. A backend whose probe starts no process and waits for nothing it started
    /// needn't implement it.
    /// </summary>
    H264Probe Probe(CancellationToken stopping, Action<Task> abandoned) => Probe();

    /// <summary>
    /// The engine's stop, after its probes and streams have ended: kills every process the backend started that is still
    /// running (a quarantined or abandoned session's included) and waits at most <paramref name="bound"/> for them to be
    /// collected; nothing starts after it. A backend that starts no process needn't implement it.
    /// </summary>
    Task ReapAsync(TimeSpan bound, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Starts a session. <paramref name="output"/> is called with exactly one outcome per accepted submission, in
    /// submission order, on any thread; it must not block. Throws <see cref="H264StartException"/> when it can't.
    /// </summary>
    IH264Session Start(H264Config config, Action<H264Outcome> output);
}

/// <summary>One encoder session: frames in, access units out through the start's output callback.</summary>
internal interface IH264Session
{
    /// <summary>Submits a frame of packed RGBA pixels (premultiplied; its alpha is ignored, so transparency shows black).</summary>
    H264Submit Submit(byte[] pixels, int width, int height, long frameId, bool forceKeyframe, out string? problem);

    /// <summary>Accepted submissions whose outcome hasn't been reported yet.</summary>
    int InFlight { get; }

    /// <summary>Outcomes reported so far (the stall watchdog's progress).</summary>
    long Progress { get; }

    /// <summary>
    /// The teardowns of sessions the start created and let go before this one (a retry in another pixel format), running
    /// off the caller's thread; empty when there were none. Each may never end: the caller watches them at once, as a
    /// refused start's (<see cref="H264StartException.Teardown"/>), not when this session ends.
    /// </summary>
    IReadOnlyList<Task> Released { get; }

    /// <summary>Asks the live session for another expected frame rate; false when it refused.</summary>
    bool TrySetFrameRate(int fps);

    /// <summary>
    /// Ends the session without draining it: no outcome for any outstanding submission is reported after this returns
    /// except as <see cref="H264OutcomeKind.Aborted"/>, and the teardown runs off the caller's thread. The task ends when
    /// the teardown has; a session that never tears down never ends it (the caller bounds the wait). Idempotent.
    /// </summary>
    Task Abort();
}
