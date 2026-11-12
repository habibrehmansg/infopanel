using System.Diagnostics;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Testing;

/// <summary>
/// A fake device's slow call (<see cref="FakePanelDevice.Delay"/>): the call takes the time it is given and then goes
/// through, as a transfer the device is slow to take; one slower than its timeout times out, and an unplug ends it.
/// </summary>
public sealed class FakePanelDeviceDelayTests
{
    private static readonly PanelCandidate Panel = new(0x1209, 0x000c, 1, 2, "1-2");

    private static (FakePanelDevice Device, IPanelUsb Usb) Opened()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Panel);
        Assert.Equal(PanelDeviceStatus.Ok, devices.OpenUsb(Panel, 0, 1000, out var usb));
        return (device, usb!);
    }

    [Fact]
    public void A_delayed_write_takes_its_time_then_goes_through()
    {
        var (device, usb) = Opened();
        device.Delay(device.Calls + 1, TimeSpan.FromMilliseconds(120));

        var clock = Stopwatch.StartNew();
        var status = usb.BulkWrite(0x01, [1, 2, 3], 1000);

        Assert.Equal(PanelDeviceStatus.Ok, status);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(120), $"took {clock.Elapsed.TotalMilliseconds} ms");
        Assert.Equal(new PanelTraceEntry(PanelTraceOp.UsbWrite, "ep01", [1, 2, 3]) { Handle = 1 }, device.Trace.Entries[^1]);
    }

    [Fact]
    public void Only_the_call_named_is_delayed()
    {
        var (device, usb) = Opened();
        var waiting = device.Delay(device.Calls + 2, TimeSpan.FromMilliseconds(400));

        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkWrite(0x01, [1], 1000));
        var waitedAfterFirst = waiting.IsCompleted;
        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkWrite(0x01, [2], 1000));

        // The hook's own state, not a clock's upper bound: the first call never waited, the second did, its whole delay.
        Assert.False(waitedAfterFirst, "the first call waited");
        Assert.True(waiting.IsCompleted, "the second call didn't wait");
        var second = device.Timings[^1];
        Assert.Equal(device.Calls, second.Call);
        Assert.True(second.Length >= TimeSpan.FromMilliseconds(400), $"the second took {second.Length.TotalMilliseconds} ms");
    }

    [Fact]
    public void A_delay_past_the_timeout_times_out()
    {
        var (device, usb) = Opened();
        device.Delay(device.Calls + 1, TimeSpan.FromSeconds(30));

        var clock = Stopwatch.StartNew();
        var status = usb.BulkWrite(0x01, [1], 100);

        Assert.Equal(PanelDeviceStatus.Timeout, status);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed.TotalMilliseconds} ms");
    }

    [Fact]
    public void A_calls_timing_runs_from_its_entry_to_its_return_its_trace_callbacks_included()
    {
        var (device, usb) = Opened();
        device.Trace.Added += entry =>
        {
            if (entry.Op == PanelTraceOp.UsbWrite)
                Thread.Sleep(100); // a slow callback on the write's own line
        };

        var entered = Stopwatch.GetTimestamp();
        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkWrite(0x01, [1, 2, 3], 1000));
        var returned = Stopwatch.GetTimestamp();

        var write = device.Timings[^1];
        Assert.True(write.Length >= TimeSpan.FromMilliseconds(100), $"the write's timing is {write.Length.TotalMilliseconds} ms");
        Assert.InRange(write.Started, entered, write.Ended);
        Assert.InRange(write.Ended, write.Started, returned);
    }

    [Fact]
    public async Task A_held_call_waits_for_its_release_whatever_its_timeout_then_goes_through()
    {
        var (device, usb) = Opened();
        var release = new TaskCompletionSource();
        var held = device.Hold(device.Calls + 1, release.Task);

        var write = Task.Run(() => usb.BulkWrite(0x01, [1], 1));
        await held.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken); // past its 1 ms timeout: still held
        Assert.False(write.IsCompleted, "the held write ended before its release");
        release.SetResult();

        Assert.Equal(PanelDeviceStatus.Ok, await write.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_held_call_ends_on_its_token()
    {
        var (device, usb) = Opened();
        var held = device.Hold(device.Calls + 1, new TaskCompletionSource().Task);
        using var stop = new CancellationTokenSource();

        var write = Task.Run(() => usb.BulkWrite(0x01, [1], 1000, cancellationToken: stop.Token));
        await held.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_scripted_unplug_at_a_later_call_ends_a_held_call_with_Gone()
    {
        // The device is unplugged at the call after the held one (UnplugAt): the held call ends as at Unplug, with Gone,
        // instead of waiting for a release that never comes.
        var (device, usb) = Opened();
        var held = device.Hold(device.Calls + 1, new TaskCompletionSource().Task);
        device.UnplugAt(device.Calls + 2);

        var write = Task.Run(() => usb.BulkWrite(0x01, [1], 1000));
        await held.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(PanelDeviceStatus.Gone, usb.BulkWrite(0x01, [2], 1000));

        Assert.Equal(PanelDeviceStatus.Gone, await write.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unplug_ends_a_delayed_call_with_Gone()
    {
        var (device, usb) = Opened();
        var waiting = device.Delay(device.Calls + 1, TimeSpan.FromSeconds(30));

        var write = Task.Run(() => usb.BulkWrite(0x01, [1], 60_000));
        await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        device.Unplug();

        Assert.Equal(PanelDeviceStatus.Gone, await write.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }
}
