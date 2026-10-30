using InfoPanel.Preview.Video;

namespace InfoPanel.Tests.Preview.Video;

/// <summary>
/// A scriptable H.264 backend for the stream's tests and for test engines (so the contract tour runs anywhere): each
/// start makes a <see cref="FakeH264Session"/>, which by default "encodes" every frame at once, its access unit the
/// frame id and the pixels' first four bytes, a keyframe carrying a Baseline avcC.
/// </summary>
internal sealed class FakeH264Backend(string name = "Fake", H264Kind kind = H264Kind.Hardware) : IH264Backend
{
    private readonly Lock _lock = new();
    private readonly List<FakeH264Session> _sessions = [];
    private int _probes;

    public string Name { get; } = name;

    public H264Kind Kind { get; } = kind;

    public bool Available { get; set; } = true;

    /// <summary>What each probe answers (null: <see cref="Available"/>'s answer).</summary>
    public Func<H264Probe>? ProbeAnswer { get; set; }

    /// <summary>Refuses a start with this problem (null: starts).</summary>
    public Func<H264Config, H264StartProblem?> Refuse { get; set; } = _ => null;

    /// <summary>The teardown a refused start hands back (what it had created; null: nothing).</summary>
    public Task? RefusalTeardown { get; set; }

    /// <summary>Set up each new session before it is returned.</summary>
    public Action<FakeH264Session> Configure { get; set; } = _ => { };

    public int Probes => Volatile.Read(ref _probes);

    public IReadOnlyList<FakeH264Session> Sessions
    {
        get
        {
            lock (_lock)
                return [.. _sessions];
        }
    }

    public FakeH264Session Last => Sessions[^1];

    public int Starts { get; private set; }

    public H264Probe Probe()
    {
        Interlocked.Increment(ref _probes);
        return ProbeAnswer?.Invoke() ?? (Available ? H264Probe.Available : H264Probe.Unavailable);
    }

    public IH264Session Start(H264Config config, Action<H264Outcome> output)
    {
        lock (_lock)
            Starts++;
        if (Refuse(config) is { } problem)
            throw new H264StartException(problem, $"fake refusal ({problem})") { Teardown = RefusalTeardown };
        var session = new FakeH264Session(config, output);
        Configure(session);
        lock (_lock)
            _sessions.Add(session);
        return session;
    }

    /// <summary>A Baseline avcC (a VideoToolbox session's, 1600×960, level 4.0).</summary>
    public static readonly byte[] BaselineAvcc = Convert.FromBase64String("AUIAKP/hAA4nQgAoq0AyA801AQEBAgEABCjOPIA=");

    /// <summary>A Main-profile avcC that reorders (x264 with B-frames).</summary>
    public static readonly byte[] MainAvcc = Convert.FromBase64String("AU1AHv/hABhnTUAe7KBQF/y4CIAAAAMAgAAAHgeLFssBAAVo6+PLIA==");
}

internal sealed class FakeH264Session(H264Config config, Action<H264Outcome> output) : IH264Session
{
    private readonly Lock _lock = new();
    private readonly List<(long Id, bool Key, byte[] Pixels)> _held = [];
    private readonly List<(long Id, bool Force)> _submitted = [];
    private readonly TaskCompletionSource _teardown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _inFlight;
    private long _progress;
    private bool _first = true;
    private int _aborted;

    public H264Config Config { get; } = config;

    /// <summary>Submissions stay in the encoder until <see cref="Release"/>.</summary>
    public bool Hold { get; set; }

    /// <summary>The next outcomes are lost: the frame stays in flight and nothing comes.</summary>
    public int Lose;

    /// <summary>The next outcomes are failures (as reading the encoded buffer failing).</summary>
    public int Fail;

    /// <summary>The next outcomes are drops.</summary>
    public int Drop;

    /// <summary>The next submissions are refused (an encoder error).</summary>
    public int RefuseSubmit;

    /// <summary>The next submissions are skipped (a buffer it couldn't lock).</summary>
    public int SkipSubmit;

    /// <summary>The next released pair of held frames comes out of order.</summary>
    public bool Swap;

    /// <summary>The next outcome names a frame it was never given.</summary>
    public bool Unknown;

    /// <summary>The teardown never ends.</summary>
    public bool Wedged { get; set; }

    /// <summary>The teardowns its start hands back of sessions it let go first (none by default).</summary>
    public IReadOnlyList<Task> Released { get; set; } = [];

    /// <summary>A frame rate it refuses.</summary>
    public int RefusedFps { get; set; } = -1;

    /// <summary>The avcC its keyframes carry.</summary>
    public byte[]? Avcc { get; set; } = FakeH264Backend.BaselineAvcc;

    public List<int> FrameRates { get; } = [];

    public bool Aborted => Volatile.Read(ref _aborted) != 0;

    public IReadOnlyList<(long Id, bool Force)> Submitted
    {
        get
        {
            lock (_lock)
                return [.. _submitted];
        }
    }

    public int InFlight => Volatile.Read(ref _inFlight);

    public long Progress => Interlocked.Read(ref _progress);

    public H264Submit Submit(byte[] pixels, int width, int height, long frameId, bool forceKeyframe, out string? problem)
    {
        problem = null;
        if (Aborted)
            throw new InvalidOperationException("submitted to an aborted session");
        if (Take(ref RefuseSubmit))
        {
            problem = "fake refusal";
            return H264Submit.Refused;
        }

        if (Take(ref SkipSubmit))
            return H264Submit.Skipped;

        bool key;
        lock (_lock)
        {
            _submitted.Add((frameId, forceKeyframe));
            key = forceKeyframe || _first;
            _first = false;
            Interlocked.Increment(ref _inFlight);
            _held.Add((frameId, key, pixels[..Math.Min(4, pixels.Length)]));
        }

        if (!Hold)
            Release();
        return H264Submit.Accepted;
    }

    /// <summary>Emits up to <paramref name="count"/> held frames, in order (or swapped, once).</summary>
    public void Release(int count = int.MaxValue)
    {
        while (count-- > 0)
        {
            (long Id, bool Key, byte[] Pixels) next;
            lock (_lock)
            {
                if (_held.Count == 0 || Aborted)
                    return;
                var at = Swap && _held.Count > 1 ? 1 : 0;
                if (at == 1)
                    Swap = false;
                next = _held[at];
                _held.RemoveAt(at);
            }

            Emit(next.Id, next.Key, next.Pixels);
        }
    }

    public int Held
    {
        get
        {
            lock (_lock)
                return _held.Count;
        }
    }

    private void Emit(long id, bool key, byte[] pixels)
    {
        if (Take(ref Lose))
            return;

        H264Outcome outcome;
        if (Unknown)
        {
            Unknown = false;
            outcome = new H264Outcome(id + 1000, H264OutcomeKind.Encoded, [1, 2, 3], key, Avcc);
        }
        else if (Take(ref Fail))
            outcome = new H264Outcome(id, H264OutcomeKind.Failed, Problem: "fake: reading the encoded buffer failed");
        else if (Take(ref Drop))
            outcome = new H264Outcome(id, H264OutcomeKind.Dropped);
        else
            outcome = new H264Outcome(id, H264OutcomeKind.Encoded, [.. BitConverter.GetBytes(id), .. pixels], key, key ? Avcc : null);

        Interlocked.Decrement(ref _inFlight);
        try
        {
            output(outcome);
        }
        catch (Exception)
        {
            // As a native callback's barrier: never let it unwind into the encoder.
        }
        finally
        {
            Interlocked.Increment(ref _progress);
        }
    }

    public bool TrySetFrameRate(int fps)
    {
        lock (_lock)
            FrameRates.Add(fps);
        return fps != RefusedFps;
    }

    public Task Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0)
            return _teardown.Task;

        List<(long Id, bool Key, byte[] Pixels)> held;
        lock (_lock)
        {
            held = [.. _held];
            _held.Clear();
        }

        foreach (var frame in held)
        {
            Interlocked.Decrement(ref _inFlight);
            output(new H264Outcome(frame.Id, H264OutcomeKind.Aborted));
        }

        if (!Wedged)
            _teardown.TrySetResult();
        return _teardown.Task;
    }

    /// <summary>Lets a wedged teardown end.</summary>
    public void Unwedge() => _teardown.TrySetResult();

    private static bool Take(ref int count)
    {
        while (true)
        {
            var left = Volatile.Read(ref count);
            if (left <= 0)
                return false;
            if (Interlocked.CompareExchange(ref count, left - 1, left) == left)
                return true;
        }
    }
}
