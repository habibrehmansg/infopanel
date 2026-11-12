using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Testing;

/// <summary>
/// One panel for the conformance suite: a driver, the device it is offered (as the engine offers it: the device is one
/// of the family's own panels, scripted so the driver's probe takes it and its start runs), and the kit's fake device
/// behind it, whose trace the suite reads. <see cref="Fake"/> null: the driver reaches no device through the seam (the
/// virtual panel), and only what the suite can see from the driver's answers is checked.
/// </summary>
public sealed record ConformanceSetup(IPanelDriver Driver, PanelCandidate Device, FakePanelDevice? Fake = null)
{
    /// <summary>
    /// The writes the family's probe makes, its queries: each write the probe makes must pass it (<see cref="Conformance.ProbeWrites"/>
    /// makes one from the exact queries). A family whose queries could pass for frames (or parts of one) supplies it from
    /// its protocol; without it the suite only checks that no write of the probe is a frame's size or more (its
    /// identity's width × height × 2 bytes).
    /// </summary>
    public Func<PanelTraceEntry, bool>? ProbeWrite { get; init; }
}

/// <summary>A rule a driver broke: the check that found it (one of <see cref="Conformance.Checks"/>) and what it saw.</summary>
public sealed record ConformanceFailure(string Check, string Message)
{
    public override string ToString() => $"{Check}: {Message}";
}

/// <summary>
/// The suite's settings: the brightness a session is opened at, the screen-off budget it gives a device that stops
/// answering and how much longer than it a whole <c>Close</c> may take, how far a call's timeout or a
/// <see cref="PanelWrite.DeviceTime"/> may be off what the fake device saw (the threads' scheduling, never a protocol's
/// margin; a call's start after the close budget has none), how slow it makes a frame's transfer to see that
/// <see cref="PanelWrite.DeviceTime"/> covers it, how long it watches for a write after a session ended, the deadline of
/// a probe's and a start's token, and the frames it writes (<see cref="ConformanceFrames.Of"/>: give your own for a
/// driver that reads its frames' contents).
/// </summary>
public sealed record ConformanceOptions
{
    public int Brightness { get; init; } = 50;

    public TimeSpan CloseBudget { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan CloseSlack { get; init; } = TimeSpan.FromMilliseconds(750);

    public TimeSpan Slack { get; init; } = TimeSpan.FromMilliseconds(50);

    public TimeSpan TransferDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan Quiet { get; init; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(10);

    public Func<PanelFrameFormat, int, int, byte[]> Frame { get; init; } = ConformanceFrames.Of;
}

/// <summary>
/// The conformance suite: the rules every panel driver keeps, checked against a driver over the kit's fakes and
/// answered as a list of failures (empty: it passes), so any test framework calls it:
/// <c>Assert.Empty(Conformance.Check(() => MySetup()))</c>. Each check starts from a new setup (the function is called
/// again: a new driver over a new fake device) and opens its own session, so one check's faults don't reach another's.
/// <list type="bullet">
/// <item><see cref="Probe"/>: the probe takes the family's panel and only asks: it lets the device go before it answers
/// (every handle it opened is closed) and draws nothing: each write passes the setup's <see cref="ConformanceSetup.ProbeWrite"/>,
/// or, without one, no write is a frame's size or more (the identity's width × height × 2 bytes).</item>
/// <item><see cref="Start"/>: a start whose token is cancelled while its second call on the device (its first, for a
/// start of one call) is held there, the hold let go only once the token is cancelled, and a start whose token is
/// cancelled before it begins, each throw <see cref="OperationCanceledException"/>, answer no session and let the device
/// go (every handle they opened closed).</item>
/// <item><see cref="Formats"/>: the capabilities are whole (a size, a format at least and none twice, 1 to 60 frames a
/// second, the JPEG settings in range) and a frame of every declared format writes, reaching the device.</item>
/// <item><see cref="Brightness"/>: a brightness range runs low to high and <c>SetBrightness</c> takes every percentage
/// from 0 to 100 between frames; a panel without a range (a fixed light, or <see cref="PanelCapabilities.DimInSoftware"/>)
/// is never sent one. Either way a start at 0, the configured brightness and 100 starts (sampled: each start is a whole
/// session).</item>
/// <item><see cref="CloseBudget"/>: <c>Close(screenOff: true)</c> returns within its budget while the device doesn't
/// answer: as the fake device saw them, no call it makes starts once the budget is spent, and none has a timeout that runs
/// past it (within <see cref="ConformanceOptions.Slack"/>); with no budget left it makes no call.</item>
/// <item><see cref="Unplug"/>: a device unplugged during a frame's write (at its first transfer and at its last) answers
/// <see cref="PanelDeviceStatus.Gone"/>, the write stops there, and the screen-off then stops at its first Gone.</item>
/// <item><see cref="AfterClose"/>: <c>Close</c> has let every handle go when it returns, and nothing reaches the device
/// after it, watched for <see cref="ConformanceOptions.Quiet"/> before the session's dispose and again after it; no call
/// is made on a handle let go, and <c>Close(screenOff: false)</c> writes nothing.</item>
/// <item><see cref="DeviceTime"/>: a frame's <see cref="PanelWrite.DeviceTime"/> covers its transfer (a transfer made
/// slow shows in it) and is no longer than the time the frame's calls on the device took, from the first's entry to the
/// last's return (within <see cref="ConformanceOptions.Slack"/>): the driver's own work before or after is outside it.</item>
/// <item><see cref="Overlays"/>: each overlay is a whole PNG of the panel's size (its signature, IHDR first with fields a
/// PNG has, its image data in IDATs one after the other, a palette before them where the colour type needs one, IEND
/// last, every chunk's CRC; ancillary and animation chunks allowed), at 0, 90, 180 or 270 degrees.</item>
/// <item><see cref="Crop"/>: a crop lies inside the frame.</item>
/// </list>
/// The checks that read the device's calls (part of probe, formats and close-budget; all of start, unplug,
/// after-close's device part and device-time's slow transfer) need <see cref="ConformanceSetup.Fake"/>; without it they
/// check what the driver's answers show. A setup or a driver that throws is a failure of the check it threw in, never an
/// exception out of the suite.
/// </summary>
public static class Conformance
{
    public const string Probe = "probe";
    public const string Start = "start";
    public const string Formats = "formats";
    public const string Brightness = "brightness";
    public const string CloseBudget = "close-budget";
    public const string Unplug = "unplug";
    public const string AfterClose = "after-close";
    public const string DeviceTime = "device-time";
    public const string Overlays = "overlays";
    public const string Crop = "crop";

    /// <summary>The checks, in the order they run.</summary>
    public static IReadOnlyList<string> Checks { get; } = [Probe, Start, Formats, Brightness, CloseBudget, Unplug, AfterClose, DeviceTime, Overlays, Crop];

    /// <summary>A <see cref="ConformanceSetup.ProbeWrite"/> that takes exactly <paramref name="writes"/>, the family's probe queries, and nothing else.</summary>
    public static Func<PanelTraceEntry, bool> ProbeWrites(params byte[][] writes) =>
        entry => writes.Any(w => w.AsSpan().SequenceEqual(entry.Data));

    /// <summary>The time a session that isn't under test is given to close: the engine's own bound for a screen-off.</summary>
    private static readonly TimeSpan CloseBound = TimeSpan.FromSeconds(3);

    /// <summary>How much longer than the write itself a <see cref="PanelWrite.DeviceTime"/> may be: two clocks' rounding.</summary>
    private static readonly TimeSpan ClockSlack = TimeSpan.FromMilliseconds(5);

    /// <summary>Runs every check against fresh setups from <paramref name="setup"/>; the failures, none when the driver passes.</summary>
    public static IReadOnlyList<ConformanceFailure> Check(Func<ConformanceSetup> setup, ConformanceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(setup);
        var run = new Run(setup, options ?? new ConformanceOptions());
        run.Each(Probe, run.CheckProbe);
        run.Each(Start, run.CheckStart);
        run.Each(Formats, run.CheckFormats);
        run.Each(Brightness, run.CheckBrightness);
        run.Each(CloseBudget, run.CheckCloseBudget);
        run.Each(Unplug, run.CheckUnplug);
        run.Each(AfterClose, run.CheckAfterClose);
        run.Each(DeviceTime, run.CheckDeviceTime);
        run.Each(Overlays, run.CheckOverlays);
        run.Each(Crop, run.CheckCrop);
        return run.Failures;
    }

    /// <summary>A started panel: its setup, the identity its probe answered and its open session.</summary>
    private sealed class Started(ConformanceSetup setup, PanelIdentity identity, IPanelSession session)
    {
        public ConformanceSetup Setup { get; } = setup;

        public PanelIdentity Identity { get; } = identity;

        public IPanelSession Session { get; } = session;

        public FakePanelDevice? Fake => Setup.Fake;

        public long Frames { get; set; }
    }

    private sealed class Run(Func<ConformanceSetup> setup, ConformanceOptions options)
    {
        private string _check = "";

        public List<ConformanceFailure> Failures { get; } = [];

        public void Each(string check, Action body)
        {
            _check = check;
            try
            {
                body();
            }
            catch (Exception ex)
            {
                Fail($"threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void Fail(string message) => Failures.Add(new ConformanceFailure(_check, message));

        private ConformanceSetup NewSetup() => setup() ?? throw new InvalidOperationException("the setup answered null");

        // The probe, then the start at `brightness`: the started panel, or null with the failure added.
        private Started? Start(int? brightness = null) => Start(NewSetup(), brightness);

        private Started? Start(ConformanceSetup made, int? brightness)
        {
            using var deadline = new CancellationTokenSource(options.Deadline);
            var probe = made.Driver.Probe(made.Device, deadline.Token);
            if (probe is not { Mine: true, Identity: { } identity, Status: PanelDeviceStatus.Ok })
            {
                Fail($"the probe didn't take the panel the setup made ({Describe(probe)})");
                return null;
            }
            var at = brightness ?? options.Brightness;
            var open = made.Driver.Open(made.Device, identity, new PanelOpenOptions(at), deadline.Token);
            if (open is not { Session: { } session, Status: PanelDeviceStatus.Ok })
            {
                Fail($"the panel didn't start at brightness {at} ({open?.Status}: {open?.Message})");
                open?.Session?.Dispose();
                return null;
            }
            return new Started(made, identity, session);
        }

        // The engine's end of a session: the screen-off within the bound, then the dispose.
        private static void End(Started started)
        {
            try
            {
                started.Session.Close(screenOff: true, CloseBound);
            }
            finally
            {
                started.Session.Dispose();
            }
        }

        // The size of the frames the session is sent: its crop's when it has one.
        private static (int Width, int Height) SentSize(PanelCapabilities caps) =>
            caps.Crop is { } crop ? (crop.Width, crop.Height) : (caps.Width, caps.Height);

        private PanelWrite Write(Started started, PanelFrameFormat format, out TimeSpan took)
        {
            var (width, height) = SentSize(started.Session.Capabilities);
            var bytes = options.Frame(format, width, height);
            using var deadline = new CancellationTokenSource(options.Deadline);
            var clock = Stopwatch.StartNew();
            var write = started.Session.WriteFrame(new PanelFrameView(bytes, format, ++started.Frames, repeat: false), deadline.Token);
            took = clock.Elapsed;
            return write ?? throw new InvalidOperationException("WriteFrame answered null");
        }

        private PanelWrite Write(Started started, out TimeSpan took) => Write(started, started.Session.Capabilities.Formats[0], out took);

        public void CheckProbe()
        {
            var made = NewSetup();
            var before = made.Fake?.Trace.Entries.Count ?? 0;
            using var deadline = new CancellationTokenSource(options.Deadline);
            var probe = made.Driver.Probe(made.Device, deadline.Token);
            if (probe is not { Mine: true, Identity: { } identity, Status: PanelDeviceStatus.Ok })
                Fail($"the probe didn't take the panel the setup made ({Describe(probe)})");
            else if (identity.Width < 1 || identity.Height < 1 || string.IsNullOrEmpty(identity.Model))
                Fail($"the probe's identity has no model or no size ({identity})");
            if (made.Fake is not { } fake)
                return;
            var calls = fake.Trace.Entries.Skip(before).ToList();
            foreach (var open in OpenHandles(calls))
                Fail($"the probe left {open.Target} (handle h{open.Handle}) open: a probe lets the device go before it answers");
            var writes = calls.Where(IsWrite).ToList();
            if (made.ProbeWrite is { } query)
            {
                foreach (var write in writes.Where(w => !query(w)))
                    Fail($"the probe wrote {Name(write)}, which isn't one of the family's queries (the setup's ProbeWrite): a probe only asks and never draws");
            }
            else if (probe?.Identity is { Width: > 0, Height: > 0 } size)
            {
                // Each write on its own: a query may be long, and several queries may add up past a frame.
                var frame = (long)size.Width * size.Height * 2;
                foreach (var write in writes.Where(w => w.Data.Length >= frame))
                    Fail($"the probe wrote {Name(write)}, a {size.Width} × {size.Height} frame's worth or more ({frame} bytes at 2 a pixel): a probe only asks and never draws");
            }
        }

        // The probe of a new setup with a fake device: the setup and the identity, or null with the failure added.
        private (ConformanceSetup Made, FakePanelDevice Fake, PanelIdentity Identity)? Probed()
        {
            var made = NewSetup();
            if (made.Fake is not { } fake)
                return null;
            using var deadline = new CancellationTokenSource(options.Deadline);
            var probe = made.Driver.Probe(made.Device, deadline.Token);
            if (probe is not { Mine: true, Identity: { } identity, Status: PanelDeviceStatus.Ok })
            {
                Fail($"the probe didn't take the panel the setup made ({Describe(probe)})");
                return null;
            }
            return (made, fake, identity);
        }

        public void CheckStart()
        {
            if (NewSetup().Fake == null)
                return; // no device to hold: what a start does with its token can't be seen

            // How many calls a start makes, on a session of its own.
            if (Probed() is not (var counted, var device, var found))
                return;
            var calls = device.Calls;
            using (var deadline = new CancellationTokenSource(options.Deadline))
            {
                var first = counted.Driver.Open(counted.Device, found, new PanelOpenOptions(options.Brightness), deadline.Token);
                if (first is not { Session: { } session, Status: PanelDeviceStatus.Ok })
                {
                    Fail($"the panel didn't start at brightness {options.Brightness} ({first?.Status}: {first?.Message})");
                    first?.Session?.Dispose();
                    return;
                }
                calls = device.Calls - calls;
                End(new Started(counted, found, session));
            }

            // Cancelled while its second call (its first, when it makes one) is held on the device: the hold ends only
            // after the token is cancelled, so the start is still under way when it is.
            if (calls > 0 && Probed() is (var made, var fake, var identity))
            {
                var nth = Math.Min(calls, 2);
                var before = fake.Trace.Entries.Count;
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var held = fake.Hold(fake.Calls + nth, release.Task);
                using var stop = new CancellationTokenSource();
                var open = Task.Run(() => made.Driver.Open(made.Device, identity, new PanelOpenOptions(options.Brightness), stop.Token));
                if (Task.WaitAny([open, held], options.Deadline) < 0)
                {
                    stop.Cancel();
                    release.TrySetResult();
                    Fail($"the start didn't end within {Ms(options.Deadline)}");
                    return;
                }
                if (held.IsCompleted)
                {
                    stop.Cancel();
                    release.TrySetResult();
                    Cancelled(open, made, identity, before, $"a start whose token was cancelled while its {(nth == 1 ? "first" : "second")} call was held on the device");
                }
                else if (open.Status == TaskStatus.RanToCompletion && open.Result?.Session is { } quick)
                {
                    End(new Started(made, identity, quick)); // this start made fewer calls than the one counted: nothing was held
                }
            }

            // Its token cancelled before it starts.
            if (Probed() is (var early, var earlyFake, var earlyIdentity))
            {
                var before = earlyFake.Trace.Entries.Count;
                using var stop = new CancellationTokenSource();
                stop.Cancel();
                var open = Task.Run(() => early.Driver.Open(early.Device, earlyIdentity, new PanelOpenOptions(options.Brightness), stop.Token));
                Cancelled(open, early, earlyIdentity, before, "a start whose token was cancelled before it began");
            }
        }

        // A cancelled start's end: it throws OperationCanceledException, answers no session, and every handle it opened
        // (from trace entry `before` on) is closed.
        private void Cancelled(Task<PanelOpen> open, ConformanceSetup made, PanelIdentity identity, int before, string start)
        {
            PanelOpen? answer = null;
            Exception? thrown = null;
            try
            {
                if (!open.Wait(options.Deadline))
                {
                    Fail($"{start} didn't end within {Ms(options.Deadline)}");
                    return;
                }
                answer = open.Result;
            }
            catch (AggregateException ex)
            {
                thrown = ex.InnerException;
            }
            if (answer?.Session is { } session)
            {
                Fail($"{start} answered a session: a cancelled start throws OperationCanceledException, the device let go");
                End(new Started(made, identity, session));
            }
            else if (thrown is not OperationCanceledException)
            {
                Fail(thrown != null
                    ? $"{start} threw {thrown.GetType().Name}: {thrown.Message}; a cancelled start throws OperationCanceledException"
                    : $"{start} answered {answer?.Status} ({answer?.Message}); a cancelled start throws OperationCanceledException");
            }
            foreach (var left in OpenHandles(made.Fake!.Trace.Entries.Skip(before).ToList()))
                Fail($"{start} left {left.Target} (handle h{left.Handle}) open: a cancelled start lets the device go");
        }

        public void CheckFormats()
        {
            if (Start() is not { } started)
                return;
            try
            {
                var caps = started.Session.Capabilities ?? throw new InvalidOperationException("Capabilities is null");
                if (caps.Width < 1 || caps.Height < 1)
                    Fail($"the capabilities have no size ({caps.Width} × {caps.Height})");
                if (caps.Formats is not { Count: > 0 } formats)
                {
                    Fail("the capabilities declare no format");
                    return;
                }
                if (formats.Distinct().Count() != formats.Count)
                    Fail($"the capabilities declare a format twice ({string.Join(", ", formats)})");
                foreach (var unknown in formats.Where(f => !Enum.IsDefined(f)))
                    Fail($"the capabilities declare a format the SDK doesn't have ({(int)unknown})");
                if (caps.MaxFps is < 1 or > 60)
                    Fail($"MaxFps is {caps.MaxFps}: 1 to 60");
                if (caps.MaxFrameBytes is < 1)
                    Fail($"MaxFrameBytes is {caps.MaxFrameBytes}: null or at least 1");
                if (caps.JpegQuality is < 1 or > 100 || caps.MinJpegQuality < 1 || caps.MinJpegQuality > caps.JpegQuality)
                    Fail($"the JPEG quality is {caps.JpegQuality} with a floor of {caps.MinJpegQuality}: 1 ≤ floor ≤ quality ≤ 100");
                if (caps.JpegQualityStep < 1)
                    Fail($"JpegQualityStep is {caps.JpegQualityStep}: at least 1");
                if (caps.KeepAliveEvery is { } every && every <= TimeSpan.Zero)
                    Fail($"KeepAliveEvery is {every}: null or longer than zero");
                if (caps.Width < 1 || caps.Height < 1 || (caps.Crop is { } crop && (crop.Width < 1 || crop.Height < 1)))
                    return; // no frame to write; the crop check says why when it is the crop

                foreach (var format in formats.Distinct().Where(f => Enum.IsDefined(f)))
                {
                    var calls = started.Fake?.Calls ?? 0;
                    var write = Write(started, format, out _);
                    if (write.Status != PanelDeviceStatus.Ok)
                        Fail($"a {format} frame answered {write.Status}: every declared format writes");
                    else if (started.Fake is { } fake && fake.Calls == calls)
                        Fail($"a {format} frame answered Ok and reached no device");
                }
            }
            finally
            {
                End(started);
            }
        }

        public void CheckBrightness()
        {
            if (Start() is not { } started)
                return;
            try
            {
                var caps = started.Session.Capabilities;
                if (caps.Brightness is { } range)
                {
                    if (range.Min > range.Max)
                        Fail($"the brightness range runs from {range.Min} down to {range.Max}");
                    // Every percentage the engine may send, between frames.
                    var refused = new List<string>();
                    for (var percent = 0; percent <= 100; percent++)
                    {
                        var status = started.Session.SetBrightness(percent);
                        if (status != PanelDeviceStatus.Ok)
                            refused.Add($"{percent} ({status})");
                    }
                    if (refused.Count > 0)
                        Fail($"SetBrightness refused {refused.Count} of the percentages 0 to 100: {string.Join(", ", refused.Take(10))}{(refused.Count > 10 ? " …" : "")}");
                    var write = Write(started, out _);
                    if (write.Status != PanelDeviceStatus.Ok)
                        Fail($"a frame after the brightness changes answered {write.Status}");
                }
                // No range (a fixed light, or dimmed in software by the engine): the suite, as the engine, never calls
                // SetBrightness, and what its start writes is its own.
            }
            finally
            {
                End(started);
            }

            // The start at the ends of the range, with or without one (sampled: each start is a whole session): it starts.
            foreach (var percent in new[] { 0, 100 })
            {
                if (Start(percent) is { } at)
                    End(at);
            }
        }

        public void CheckCloseBudget()
        {
            // A device that stops answering: every call from the screen-off's first on waits out its timeout.
            if (Start() is not { } started)
                return;
            try
            {
                var write = Write(started, out _);
                if (write.Status != PanelDeviceStatus.Ok)
                {
                    Fail($"a frame answered {write.Status} before the close");
                    return;
                }
                var next = (started.Fake?.Calls ?? 0) + 1;
                if (started.Fake is { } fake)
                {
                    for (var call = next; call < next + 64; call++)
                        _ = fake.Stall(call);
                }
                var began = Stopwatch.GetTimestamp();
                started.Session.Close(screenOff: true, options.CloseBudget);
                var took = Stopwatch.GetElapsedTime(began);
                if (took > options.CloseBudget + options.CloseSlack)
                    Fail($"Close(screenOff: true) given {Ms(options.CloseBudget)} took {Ms(took)}{(started.Fake != null ? " while the device didn't answer" : "")}: each write's timeout is what is left of the budget");
                // Each call as the device saw it: entered before the budget was spent (no slack: the suite's clock starts
                // before the driver's, so a call the driver makes with time left is entered before the budget's end on
                // it too), its timeout no longer than what was left (within the slack, for when the call is seen).
                foreach (var call in started.Fake?.Timings.Where(t => t.Call >= next) ?? [])
                {
                    var at = Stopwatch.GetElapsedTime(began, call.Started);
                    var left = options.CloseBudget - at;
                    if (at >= options.CloseBudget)
                        Fail($"Close(screenOff: true) given {Ms(options.CloseBudget)} started a call {Ms(at)} in, after the budget was spent: no write starts once it is");
                    else if (TimeSpan.FromMilliseconds(call.TimeoutMs) > left + options.Slack)
                        Fail($"Close(screenOff: true) given {Ms(options.CloseBudget)} started a call {Ms(at)} in with a timeout of {call.TimeoutMs} ms, past the {Ms(left)} left: each write's timeout is what is left of the budget");
                }
            }
            finally
            {
                started.Session.Dispose();
            }

            // A budget already spent: no write starts.
            if (Start() is not { } spent)
                return;
            try
            {
                var write = Write(spent, out _);
                if (write.Status != PanelDeviceStatus.Ok)
                {
                    Fail($"a frame answered {write.Status} before the close");
                    return;
                }
                var calls = spent.Fake?.Calls ?? 0;
                var clock = Stopwatch.StartNew();
                spent.Session.Close(screenOff: true, TimeSpan.Zero);
                var took = clock.Elapsed;
                if (took > options.CloseSlack)
                    Fail($"Close(screenOff: true) with no budget left took {Ms(took)}");
                if (spent.Fake is { } fake && fake.Calls > calls)
                    Fail($"Close(screenOff: true) with no budget left made {fake.Calls - calls} call(s): no write starts once the budget is spent");
            }
            finally
            {
                spent.Session.Dispose();
            }
        }

        public void CheckUnplug()
        {
            // How many calls a frame takes, on a session of its own.
            if (Start() is not { } counted)
                return;
            int perFrame;
            try
            {
                if (counted.Fake is not { } device)
                    return; // the device can't be unplugged: there is none
                var calls = device.Calls;
                var write = Write(counted, out _);
                if (write.Status != PanelDeviceStatus.Ok)
                {
                    Fail($"a frame answered {write.Status}");
                    return;
                }
                perFrame = device.Calls - calls;
            }
            finally
            {
                End(counted);
            }
            if (perFrame == 0)
            {
                Fail("a frame made no call on the device");
                return;
            }

            foreach (var at in perFrame == 1 ? new[] { 1 } : new[] { 1, perFrame })
            {
                if (Start() is not { } started)
                    return;
                try
                {
                    var fake = started.Fake!;
                    var before = fake.Calls;
                    fake.UnplugAt(before + at);
                    var write = Write(started, out _);
                    var where = perFrame == 1 ? "during a frame's write" : $"at call {at} of a frame's {perFrame}";
                    if (write.Status != PanelDeviceStatus.Gone)
                        Fail($"unplugged {where}, the write answered {write.Status}, not Gone");
                    if (fake.Calls - before > at)
                        Fail($"unplugged {where}, the write went on with {fake.Calls - before - at} more call(s) after the device was gone");
                    var closing = fake.Calls;
                    started.Session.Close(screenOff: true, CloseBound);
                    if (fake.Calls - closing > 1)
                        Fail($"unplugged {where}, Close(screenOff: true) then made {fake.Calls - closing} calls: the screen-off stops at its first Gone");
                }
                finally
                {
                    started.Session.Dispose();
                }
            }
        }

        public void CheckAfterClose()
        {
            // The screen-off, then the session watched before its dispose and after it: Close has let the device go, and
            // nothing reaches it once Close has returned.
            if (Start() is not { } started)
                return;
            var closed = -1;
            var held = new HashSet<int>();
            try
            {
                var write = Write(started, out _);
                if (write.Status != PanelDeviceStatus.Ok)
                    Fail($"a frame answered {write.Status} before the close");
                started.Session.Close(screenOff: true, CloseBound);
                closed = HeldAfterClose(started.Fake, held, "Close(screenOff: true)");
                Thread.Sleep(options.Quiet); // a timer or thread Close left running writes here, before any dispose
            }
            finally
            {
                started.Session.Dispose();
            }
            Thread.Sleep(options.Quiet);
            if (started.Fake is { } fake && closed >= 0)
            {
                var entries = fake.Trace.Entries;
                foreach (var late in entries.Skip(closed).Where(e => e.Op != PanelTraceOp.Close))
                    Fail($"{Name(late)} reached the device after Close returned");
                foreach (var open in OpenHandles(entries).Where(e => !held.Contains(e.Handle)))
                    Fail($"{open.Target} (handle h{open.Handle}) was never let go");
                if (fake.CallsAfterDispose > 0)
                    Fail($"{fake.CallsAfterDispose} call(s) were made on a handle already let go");
            }

            // No screen-off asked for (the device is gone): nothing is written, and the device is let go.
            if (Start() is not { } gone)
                return;
            var before = -1;
            try
            {
                var write = Write(gone, out _);
                if (write.Status != PanelDeviceStatus.Ok)
                    Fail($"a frame answered {write.Status} before the close");
                before = gone.Fake?.Trace.Entries.Count ?? 0;
                gone.Session.Close(screenOff: false, CloseBound);
                HeldAfterClose(gone.Fake, [], "Close(screenOff: false)");
                Thread.Sleep(options.Quiet);
            }
            finally
            {
                gone.Session.Dispose();
            }
            Thread.Sleep(options.Quiet);
            if (gone.Fake is { } device && before >= 0)
            {
                foreach (var written in device.Trace.Entries.Skip(before).Where(IsWrite))
                    Fail($"Close(screenOff: false) wrote ({Name(written)}): without the screen-off nothing is written");
            }
        }

        // Right after Close returned: every handle still open is a failure (added to `held`); the trace's length then.
        private int HeldAfterClose(FakePanelDevice? fake, HashSet<int> held, string close)
        {
            if (fake == null)
                return 0;
            var entries = fake.Trace.Entries;
            foreach (var open in OpenHandles(entries))
            {
                held.Add(open.Handle);
                Fail($"{open.Target} (handle h{open.Handle}) was still held when {close} returned: Close lets the device go");
            }
            return entries.Count;
        }

        public void CheckDeviceTime()
        {
            if (Start() is not { } started)
                return;
            try
            {
                var first = (started.Fake?.Calls ?? 0) + 1;
                var write = Write(started, out var took);
                if (write.Status != PanelDeviceStatus.Ok)
                {
                    Fail($"a frame answered {write.Status}");
                    return;
                }
                CheckWithin(write, took);
                if (started.Fake is not { } fake)
                    return;
                CheckTransfers(write, fake, first);
                var slowed = fake.Calls + 1;
                _ = fake.Delay(slowed, options.TransferDelay);
                write = Write(started, out took);
                if (write.Status != PanelDeviceStatus.Ok)
                {
                    Fail($"a frame whose transfer took {Ms(options.TransferDelay)} answered {write.Status}");
                    return;
                }
                var transfer = fake.Timings.FirstOrDefault(t => t.Call == slowed)?.Length ?? options.TransferDelay;
                if (write.DeviceTime + TimeSpan.FromMilliseconds(1) < transfer)
                    Fail($"a frame whose transfer took {Ms(transfer)} answered a DeviceTime of {Ms(write.DeviceTime)}: it covers the transfer");
                CheckWithin(write, took);
                CheckTransfers(write, fake, slowed);
            }
            finally
            {
                End(started);
            }
        }

        private void CheckWithin(PanelWrite write, TimeSpan took)
        {
            if (write.DeviceTime < TimeSpan.Zero)
                Fail($"DeviceTime is {Ms(write.DeviceTime)}");
            else if (write.DeviceTime > took + ClockSlack)
                Fail($"DeviceTime is {Ms(write.DeviceTime)}, longer than the whole WriteFrame ({Ms(took)}): it is the transfer's time alone");
        }

        // DeviceTime against the frame's calls from `from` on as the device saw them, from the first's entry to the last's return.
        private void CheckTransfers(PanelWrite write, FakePanelDevice fake, int from)
        {
            var calls = fake.Timings.Where(t => t.Call >= from).ToList();
            if (calls.Count == 0)
                return; // no call: formats says so
            var span = Stopwatch.GetElapsedTime(calls.Min(t => t.Started), calls.Max(t => t.Ended));
            if (write.DeviceTime > span + options.Slack)
                Fail($"DeviceTime is {Ms(write.DeviceTime)}, but the frame's {calls.Count} call(s) took {Ms(span)} on the device: it is the transfer's time alone, measured around the write, without the driver's own work");
        }

        public void CheckOverlays()
        {
            if (Start() is not { } started)
                return;
            try
            {
                var caps = started.Session.Capabilities;
                foreach (var (rotation, png) in caps.Overlays ?? new Dictionary<int, byte[]>())
                {
                    if (rotation is not (0 or 90 or 180 or 270))
                        Fail($"an overlay is for {rotation}°: 0, 90, 180 or 270");
                    if (PngProblem(png, out var width, out var height) is { } problem)
                        Fail($"the overlay for {rotation}° isn't a whole PNG: {problem}");
                    else if ((width, height) != (caps.Width, caps.Height))
                        Fail($"the overlay for {rotation}° is {width} × {height}; the panel is {caps.Width} × {caps.Height}");
                }
            }
            finally
            {
                End(started);
            }
        }

        public void CheckCrop()
        {
            if (Start() is not { } started)
                return;
            try
            {
                var caps = started.Session.Capabilities;
                if (caps.Crop is { } crop && (crop.X < 0 || crop.Y < 0 || crop.Width < 1 || crop.Height < 1
                    || (long)crop.X + crop.Width > caps.Width || (long)crop.Y + crop.Height > caps.Height))
                    Fail($"the crop {crop.Width} × {crop.Height} at ({crop.X}, {crop.Y}) isn't inside the {caps.Width} × {caps.Height} frame");
            }
            finally
            {
                End(started);
            }
        }
    }

    private static string Describe(PanelProbe? probe) =>
        probe == null ? "it answered null" : $"mine {probe.Mine}, {probe.Status}{(probe.Message is { Length: > 0 } m ? $": {m}" : "")}";

    private static bool IsWrite(PanelTraceEntry entry) =>
        entry.Op is PanelTraceOp.UsbWrite or PanelTraceOp.HidWrite or PanelTraceOp.HidSetFeature or PanelTraceOp.SerialWrite;

    // A call as the trace names it, without its bytes: "usbWrite ep01, 2 bytes".
    private static string Name(PanelTraceEntry entry) =>
        $"{entry.Op}{(entry.Target.Length > 0 ? " " + entry.Target : "")}, {entry.Data.Length} bytes";

    // The opens that went through and have no close after them, in order.
    private static IEnumerable<PanelTraceEntry> OpenHandles(IReadOnlyList<PanelTraceEntry> entries)
    {
        var closed = entries.Where(e => e.Op == PanelTraceOp.Close).Select(e => e.Handle).ToHashSet();
        return entries.Where(e => e.Op == PanelTraceOp.Open && e.Status == PanelDeviceStatus.Ok && !closed.Contains(e.Handle));
    }

    /// <summary>
    /// What keeps <paramref name="png"/> from being a whole PNG, read without an image library (null: nothing): its
    /// signature, then chunks each whole with its CRC, the first a 13-byte IHDR (its size, read out; a bit depth its
    /// colour type takes, compression and filter method 0, interlace 0 or 1), its image data in IDAT chunks one after the
    /// other, a palette (PLTE, its entries whole, at most 256) before them when the colour type is 3 and never with a
    /// greyscale one, and IEND, empty, last. Any other chunk (an ancillary one, an animated PNG's) may come between.
    /// </summary>
    private static string? PngProblem(byte[]? png, out int width, out int height)
    {
        width = height = 0;
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (png == null || png.Length < signature.Length || !png.AsSpan(0, signature.Length).SequenceEqual(signature))
            return "it has no PNG signature";
        var at = signature.Length;
        var chunks = 0;
        var data = false; // an IDAT seen
        var dataEnded = false; // another chunk after the IDATs
        var palette = false;
        byte colourType = 0;
        var ended = false;
        while (at < png.Length)
        {
            if (ended)
                return $"{png.Length - at} bytes follow its IEND chunk";
            if (png.Length - at < 12)
                return $"its chunk at byte {at} is cut short";
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
            if (length > (uint)(png.Length - at - 12))
                return $"its chunk at byte {at} is cut short";
            var size = (int)length;
            var type = Encoding.ASCII.GetString(png, at + 4, 4);
            if (Crc32(png.AsSpan(at + 4, 4 + size)) != BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + size)))
                return $"its {type} chunk at byte {at} has a wrong CRC";
            if (chunks == 0)
            {
                if (type != "IHDR" || size != 13)
                    return "its first chunk isn't a 13-byte IHDR";
                width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at + 8));
                height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at + 12));
                if (width < 1 || height < 1)
                    return $"its IHDR says {width} × {height}";
                var depth = png[at + 16];
                colourType = png[at + 17];
                ReadOnlySpan<byte> depths = colourType switch
                {
                    0 => [1, 2, 4, 8, 16], // greyscale
                    2 or 4 or 6 => [8, 16], // truecolour, greyscale with alpha, truecolour with alpha
                    3 => [1, 2, 4, 8], // indexed
                    _ => [],
                };
                if (depths.IsEmpty)
                    return $"its IHDR's colour type is {colourType}: 0, 2, 3, 4 or 6";
                if (!depths.Contains(depth))
                    return $"its IHDR's bit depth is {depth}, which colour type {colourType} doesn't take";
                if (png[at + 18] != 0)
                    return $"its IHDR's compression method is {png[at + 18]}: 0";
                if (png[at + 19] != 0)
                    return $"its IHDR's filter method is {png[at + 19]}: 0";
                if (png[at + 20] > 1)
                    return $"its IHDR's interlace method is {png[at + 20]}: 0 or 1";
            }
            else if (type == "IHDR")
                return "it has a second IHDR";
            if (type == "IDAT")
            {
                if (dataEnded)
                    return "its image data is split: its IDAT chunks aren't one after the other";
                data = true;
            }
            else if (data)
                dataEnded = true;
            if (type == "PLTE")
            {
                if (palette)
                    return "it has a second PLTE";
                if (data)
                    return "its PLTE comes after its image data";
                if (colourType is 0 or 4)
                    return $"its PLTE is in a greyscale image (colour type {colourType})";
                if (size == 0 || size % 3 != 0 || size > 3 * 256)
                    return $"its PLTE is {size} bytes: 1 to 256 entries of 3";
                palette = true;
            }
            if (type == "IDAT" && colourType == 3 && !palette)
                return "its image is indexed (colour type 3) and has no PLTE before its image data";
            if (type == "IEND")
            {
                if (size != 0)
                    return "its IEND chunk isn't empty";
                ended = true;
            }
            at += 12 + size;
            chunks++;
        }
        if (chunks == 0)
            return "it has no chunks";
        if (!data)
            return "it has no image data (IDAT)";
        return ended ? null : "it doesn't end with an IEND chunk";
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    // The CRC-32 a PNG chunk carries, over its type and data.
    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static string Ms(TimeSpan time) => $"{time.TotalMilliseconds:0} ms";
}
