using InfoPanel.Shell;
using InfoPanel.Shell.Windows;
using Xunit;

namespace InfoPanel.Tests.Shell.Windows;

/// <summary>
/// What the system tells the notification-area icon's logic, on any OS through <see cref="FakeTrayNative"/>: the session
/// ending (sign-out, shutdown) stops the engine without asking and holds the end, bounded, until the host has stopped;
/// a display scale change redraws the icon at its new size.
/// </summary>
public sealed class WinTraySystemTests
{
    private const uint TaskbarCreated = 0xC0DE;

    private readonly FakeTrayNative _native = new();
    private readonly List<string> _invoked = [];

    private WinTray Shown()
    {
        var tray = new WinTray(_native, new WinStartup(new FakeRegistry(), "\"InfoPanel.exe\""), TaskbarCreated, () => 0);
        tray.Show(WinShellText.Tooltip("2.0.0"), () => ShellMenu.Build(new ShellState("2.0.0")), id =>
        {
            _invoked.Add(id);
            return Task.CompletedTask;
        });
        return tray;
    }

    [Fact]
    public void TheSessionEnding_StopsTheEngine_WithoutAsking_ThenHoldsTheEndUntilTheHostHasStopped_Bounded()
    {
        var tray = Shown();
        var ending = 0;
        tray.SessionEnding += () => ending++;
        var endingAtHold = -1;
        _native.OnHold = () => endingAtHold = ending;

        Assert.Equal((nint)1, tray.OnMessage(WinTray.WM_QUERYENDSESSION, 0, 0));
        Assert.Equal(0, ending);
        Assert.Empty(_native.Holds);

        Assert.Equal((nint)0, tray.OnMessage(WinTray.WM_ENDSESSION, 1, 0));

        Assert.Equal(1, ending);
        Assert.Equal(1, endingAtHold);
        Assert.Equal(WinTray.SessionEndWait, Assert.Single(_native.Holds));
        Assert.InRange(WinTray.SessionEndWait, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        Assert.Empty(_native.Prompts);
        Assert.Empty(_invoked);
    }

    [Fact]
    public void ASessionEndThatWasCancelled_ChangesNothing()
    {
        var tray = Shown();
        var ending = 0;
        tray.SessionEnding += () => ending++;

        Assert.Equal((nint)0, tray.OnMessage(WinTray.WM_ENDSESSION, 0, 0));

        Assert.Equal(0, ending);
        Assert.Empty(_native.Holds);
        Assert.True(tray.Present);
    }

    [Fact]
    public void TheSessionEnding_WhileTheEngineIsAlreadyStopping_StillHoldsTheEnd()
    {
        var tray = Shown();
        tray.Remove();

        Assert.Equal((nint)1, tray.OnMessage(WinTray.WM_QUERYENDSESSION, 0, 0));
        Assert.Equal((nint)0, tray.OnMessage(WinTray.WM_ENDSESSION, 1, 0));

        Assert.Single(_native.Holds);
    }

    [Fact]
    public void ASecondSessionEnd_DispatchedByTheHold_StopsTheEngineOnce_AndHoldsOnlyToTheFirstsDeadline()
    {
        // The real hold over a queue whose first message, 3 s in, is Windows' WM_ENDSESSION again (dispatched to the
        // icon's window, so the hold is entered again from inside itself); then nothing comes and the engine never stops.
        var messages = new ScriptedMessages();
        var tray = Shown(messages);
        var ending = 0;
        tray.SessionEnding += () => ending++;
        messages.Script.Enqueue(() =>
        {
            messages.Now = 3_000;
            Assert.Equal((nint)0, tray.OnMessage(WinTray.WM_ENDSESSION, 1, 0));
            return WinMessageTaken.Dispatched;
        });

        Assert.Equal((nint)0, tray.OnMessage(WinTray.WM_ENDSESSION, 1, 0));

        Assert.Equal(1, ending);
        Assert.Equal((long)WinTray.SessionEndWait.TotalMilliseconds, messages.Now);
        Assert.Equal([WinTray.SessionEndWait, TimeSpan.FromSeconds(1)], _native.Holds);
    }

    [Fact]
    public void AQueueThatNeverEmpties_DoesntHoldTheSessionsEndPastItsDeadline()
    {
        // Every take finds another message, 1 ms after the last: the hold must stop draining at the deadline.
        var messages = new ScriptedMessages { Endless = true };
        var tray = Shown(messages);

        Assert.Equal((nint)0, tray.OnMessage(WinTray.WM_ENDSESSION, 1, 0));

        Assert.Equal((long)WinTray.SessionEndWait.TotalMilliseconds, messages.Now);
        Assert.Empty(messages.Waits);
    }

    [Fact]
    public void TimePassingBeforeTheHoldStarts_DoesntMoveItsDeadline()
    {
        // The hold starts 1.5 s after the end set its deadline (the thread descheduled before it); the engine never
        // stops: the end is held to the deadline the end set, not 1.5 s past it.
        var messages = new ScriptedMessages();
        var tray = Shown(messages);
        var hold = _native.OnHold;
        _native.OnHold = () =>
        {
            messages.Now += 1_500;
            hold();
        };

        Assert.Equal((nint)0, tray.OnMessage(WinTray.WM_ENDSESSION, 1, 0));

        Assert.Equal((long)WinTray.SessionEndWait.TotalMilliseconds, messages.Now);
    }

    [Fact]
    public void AMessageQueuedBehindASentSessionEnd_IsntDispatchedOnceItsHoldHasReachedTheDeadline()
    {
        // 3 s in, Windows sends WM_ENDSESSION again: it runs inside the hold's look at the queue, and its own hold waits
        // out the deadline. A message posted as that wait ends (one whose handler would take 2 s) must stay queued for the
        // loop: dispatched after the deadline, it would hold the session's end past it.
        var messages = new ScriptedMessages();
        var tray = Shown(messages);
        var dispatchedAt = new List<long>();
        messages.SendAt(3_000, () => Assert.Equal((nint)0, tray.OnMessage(WinTray.WM_ENDSESSION, 1, 0)));
        messages.PostAt(4_000, () =>
        {
            dispatchedAt.Add(messages.Now);
            messages.Now += 2_000;
            return WinMessageTaken.Dispatched;
        });

        Assert.Equal((nint)0, tray.OnMessage(WinTray.WM_ENDSESSION, 1, 0));

        Assert.Empty(dispatchedAt);
        Assert.Single(messages.Script);
        Assert.Equal((long)WinTray.SessionEndWait.TotalMilliseconds, messages.Now);
        Assert.Equal([WinTray.SessionEndWait, TimeSpan.FromSeconds(1)], _native.Holds);
    }

    /// <summary>The tray, shown, on <paramref name="messages"/>' clock, its session end held by the real hold over them.</summary>
    private WinTray Shown(ScriptedMessages messages)
    {
        var tray = new WinTray(_native, new WinStartup(new FakeRegistry(), "\"InfoPanel.exe\""), TaskbarCreated, () => messages.Now);
        tray.Show(WinShellText.Tooltip("2.0.0"), () => ShellMenu.Build(new ShellState("2.0.0")), _ => Task.CompletedTask);
        _native.OnHold = () => WinMessageHold.Run(messages, () => messages.Now, _native.Deadlines[^1]);
        return tray;
    }

    /// <summary>
    /// The UI thread's queue for <see cref="WinMessageHold"/>, on a clock of its own: each peek and each take runs the sent
    /// messages first, as Windows does inside every look at the queue; a take then runs the next queued one (none once they're used up, or one
    /// more each millisecond when <see cref="Endless"/>); a wait lets the time pass until it ends or a message arrives
    /// (<see cref="SendAt"/>, <see cref="PostAt"/>). A hold that keeps draining far past any deadline fails instead of
    /// running for ever.
    /// </summary>
    private sealed class ScriptedMessages : IWinMessageSource
    {
        private const int MostTakes = 100_000;
        private readonly Queue<Action> _sent = new();
        private readonly List<(long At, Action? Sent, Func<WinMessageTaken>? Posted)> _arrivals = [];
        private int _takes;
        private int _peeks;

        public long Now { get; set; }

        /// <summary>The queued (posted) messages, in order; each returns what its dispatch did.</summary>
        public Queue<Func<WinMessageTaken>> Script { get; } = new();

        public bool Endless { get; init; }

        public List<long> Waits { get; } = [];

        /// <summary>Sends <paramref name="handler"/> to the thread at <paramref name="at"/> ms (it runs inside a look).</summary>
        public void SendAt(long at, Action handler) => _arrivals.Add((at, handler, null));

        /// <summary>Posts <paramref name="message"/> to the queue at <paramref name="at"/> ms.</summary>
        public void PostAt(long at, Func<WinMessageTaken> message) => _arrivals.Add((at, null, message));

        public bool Peek()
        {
            if (++_peeks > MostTakes)
                throw new InvalidOperationException($"The hold looked {MostTakes} times without stopping (now {Now} ms)");
            RunSent();
            return Script.Count > 0 || Endless;
        }

        public WinMessageTaken Take()
        {
            if (++_takes > MostTakes)
                throw new InvalidOperationException($"The hold took {MostTakes} messages without stopping (now {Now} ms)");
            RunSent();
            if (Script.TryDequeue(out var next))
                return next();
            if (!Endless)
                return WinMessageTaken.None;
            Now++;
            return WinMessageTaken.Dispatched;
        }

        public void Wait(long milliseconds)
        {
            Assert.InRange(milliseconds, 1, long.MaxValue);
            Waits.Add(milliseconds);
            var until = Now + milliseconds;
            Now = Math.Max(Now, _arrivals.Where(a => a.At <= until).Select(a => a.At).DefaultIfEmpty(until).Min());
            Arrive();
        }

        /// <summary>Runs every sent message that has arrived, as a look at the queue does before it returns one.</summary>
        private void RunSent()
        {
            Arrive();
            while (_sent.TryDequeue(out var handler))
                handler();
        }

        private void Arrive()
        {
            foreach (var arrival in _arrivals.Where(a => a.At <= Now).OrderBy(a => a.At).ToList())
            {
                _arrivals.Remove(arrival);
                if (arrival.Sent is { } handler)
                    _sent.Enqueue(handler);
                else
                    Script.Enqueue(arrival.Posted!);
            }
        }
    }

    [Theory]
    [InlineData(WinTray.WM_SETTINGCHANGE)]
    [InlineData(WinTray.WM_DISPLAYCHANGE)]
    [InlineData(WinTray.WM_DPICHANGED)]
    public void AScaleChange_RedrawsTheIconAtItsNewSize_AndAMessageThatChangesNothing_DoesNot(uint message)
    {
        _native.Pixels = 16;
        var tray = Shown();

        tray.OnMessage(message, 0, 0);
        Assert.Empty(_native.Modifies);

        _native.Pixels = 32;
        Assert.Null(tray.OnMessage(message, 0, 0));
        Assert.Equal([true], _native.Modifies);

        tray.OnMessage(message, 0, 0);
        Assert.Single(_native.Modifies);
    }

    [Fact]
    public void TheIconAddedAgain_TakesTheSizeThen_SoALaterScaleChangeIsMeasuredFromIt()
    {
        _native.Pixels = 16;
        var tray = Shown();

        _native.Pixels = 24;
        tray.OnMessage(TaskbarCreated, 0, 0);
        tray.OnMessage(WinTray.WM_SETTINGCHANGE, 0, 0);

        Assert.Equal(2, _native.Adds.Count);
        Assert.Empty(_native.Modifies);
    }
}
