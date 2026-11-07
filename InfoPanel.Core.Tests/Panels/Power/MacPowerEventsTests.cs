using System.Diagnostics;
using InfoPanel.Panels.Power;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Power;

/// <summary>
/// The macOS power source: on a Mac it registers for IOKit's power notifications on a run-loop thread of its own and
/// unregisters, the thread ended, when disposed; its messages (here delivered by the test, as IOKit's callback does):
/// will-sleep allowed only once the handler finished or at 3 s, an idle sleep's question allowed at once, powered-on
/// told as a wake.
/// </summary>
public sealed class MacPowerEventsTests
{
    private static readonly PowerHandlers Nothing = new(() => Task.FromResult(true), () => { });

    [Fact]
    public void On_a_mac_it_registers_on_a_thread_of_its_own_and_unregisters_when_disposed()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "IOKit is macOS's.");
        var source = new MacPowerEvents(Serilog.Core.Logger.None);

        Assert.True(source.Start(Nothing));
        var thread = source.RunLoopThread;
        Assert.NotNull(thread);
        Assert.True(thread.IsAlive);
        Assert.NotEqual(Environment.CurrentManagedThreadId, thread.ManagedThreadId);
        Assert.True(source.Registered);

        source.Dispose();

        Assert.False(thread.IsAlive);
        Assert.False(source.Registered);
        source.Dispose(); // twice: nothing
    }

    [Fact]
    public async Task Will_sleep_is_allowed_only_once_the_handler_has_finished()
    {
        var allowed = new List<(IntPtr Id, TimeSpan At)>();
        var watch = Stopwatch.StartNew();
        var finishedAt = TimeSpan.Zero;
        var source = new MacPowerEvents(Serilog.Core.Logger.None, id => allowed.Add((id, watch.Elapsed)), new PowerHandlers(async () =>
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
            finishedAt = watch.Elapsed;
            return true;
        }, () => { }));

        await Task.Run(() => source.OnMessage(MacPowerEvents.SystemWillSleep, 42), TestContext.Current.CancellationToken);

        var (id, at) = Assert.Single(allowed);
        Assert.Equal(42, id);
        Assert.True(finishedAt > TimeSpan.Zero && at >= finishedAt, $"allowed at {at}, the handler finished at {finishedAt}");
    }

    [Fact]
    public async Task Will_sleep_is_allowed_at_3_s_when_the_handler_hangs()
    {
        var allowed = new List<(IntPtr Id, TimeSpan At)>();
        var watch = Stopwatch.StartNew();
        var never = new TaskCompletionSource<bool>();
        var source = new MacPowerEvents(Serilog.Core.Logger.None, id => allowed.Add((id, watch.Elapsed)), new PowerHandlers(() => never.Task, () => { }));

        await Task.Run(() => source.OnMessage(MacPowerEvents.SystemWillSleep, 7), TestContext.Current.CancellationToken);

        var (id, at) = Assert.Single(allowed);
        Assert.Equal(7, id);
        Assert.InRange(at, TimeSpan.FromSeconds(2.9), TimeSpan.FromSeconds(5)); // 3 s, written out: not the constant under test
    }

    [Fact]
    public async Task Will_sleep_is_allowed_at_3_s_when_the_handler_blocks_before_returning_its_task()
    {
        var allowed = new List<(IntPtr Id, TimeSpan At)>();
        var watch = Stopwatch.StartNew();
        using var held = new ManualResetEventSlim(false);
        var source = new MacPowerEvents(Serilog.Core.Logger.None, id => allowed.Add((id, watch.Elapsed)), new PowerHandlers(() =>
        {
            held.Wait(TimeSpan.FromSeconds(10)); // synchronous work that blocks, before any task is returned
            return Task.FromResult(true);
        }, () => { }));

        try
        {
            await Task.Run(() => source.OnMessage(MacPowerEvents.SystemWillSleep, 9), TestContext.Current.CancellationToken);
        }
        finally
        {
            held.Set();
        }

        var (id, at) = Assert.Single(allowed);
        Assert.Equal(9, id);
        Assert.InRange(at, TimeSpan.FromSeconds(2.9), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Dispose_after_the_thread_left_its_run_loop_never_stops_that_freed_loop()
    {
        var stopped = new List<IntPtr>();
        var source = new MacPowerEvents(Serilog.Core.Logger.None, _ => { }, Nothing, stopped.Add);
        source.LoopStarted(0x1234);

        source.LoopExiting(); // the thread left first (its run loop is freed when it ends)
        source.Dispose();

        Assert.Empty(stopped);
    }

    [Fact]
    public async Task A_running_loop_is_stopped_once_and_the_thread_cannot_leave_while_its_stop_is_under_way()
    {
        var stopped = new List<IntPtr>();
        using var inStop = new ManualResetEventSlim(false);
        using var finishStop = new ManualResetEventSlim(false);
        var source = new MacPowerEvents(Serilog.Core.Logger.None, _ => { }, Nothing, loop =>
        {
            stopped.Add(loop);
            inStop.Set();
            finishStop.Wait(TimeSpan.FromSeconds(10));
        });
        source.LoopStarted(0x1234);

        var dispose = Task.Run(source.Dispose, TestContext.Current.CancellationToken);
        Assert.True(inStop.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var exiting = Task.Run(source.LoopExiting, TestContext.Current.CancellationToken);
        var leftDuringStop = await Task.WhenAny(exiting, Task.Delay(300, TestContext.Current.CancellationToken)) == exiting;
        finishStop.Set();
        await Task.WhenAll(dispose, exiting);

        Assert.False(leftDuringStop, "the thread left its run loop while the stop was using it");
        Assert.Equal([(IntPtr)0x1234], stopped);
    }

    [Fact]
    public void An_idle_sleeps_question_is_allowed_at_once_and_powered_on_is_a_wake()
    {
        var allowed = new List<IntPtr>();
        var (suspends, wakes) = (0, 0);
        var source = new MacPowerEvents(Serilog.Core.Logger.None, allowed.Add, new PowerHandlers(() =>
        {
            suspends++;
            return Task.FromResult(true);
        }, () => wakes++));

        source.OnMessage(MacPowerEvents.CanSystemSleep, 3);
        source.OnMessage(MacPowerEvents.SystemWillPowerOn, 0);
        source.OnMessage(MacPowerEvents.SystemHasPoweredOn, 0);

        Assert.Equal([(IntPtr)3], allowed);
        Assert.Equal((0, 1), (suspends, wakes));
    }
}
