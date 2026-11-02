using System.Diagnostics;
using InfoPanel.Core.Tests.SensorHelper;
using InfoPanel.SensorHelper;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Libre;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.Libre;

/// <summary>
/// The Libre source over a real child process: the helper's stand-in (<c>InfoPanel.SensorHelper.TestHost</c>, the
/// helper's own lifetime and serve loop over a small tree that can hang or fail to open), started by the engine's
/// launcher as the helper is, with the engine's process id and start time. It serves (so the start time it was given is
/// the engine's own), and its process is gone once the source stops, once a hung request is given up, and after a
/// failed open, which is restarted with backoff.
/// </summary>
public sealed class LibreHelperProcessTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// How long a start may take to reach the stand-in's read: a sanity limit, not a timing (the giving up runs on the
    /// rig's manual clock). A start is a child process's exec, which the machine's exec checks and other tests' starts
    /// and exits can hold for seconds, then the hello and the catalogue, whose completions run on this process's pool,
    /// which a full run can starve for longer than <see cref="HelperTestKit.Patience"/>.
    /// </summary>
    private static readonly TimeSpan StartPatience = TimeSpan.FromSeconds(60);

    private static string HostPath => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "InfoPanel.SensorHelper.TestHost.exe" : "InfoPanel.SensorHelper.TestHost");

    private static LibreRig ProcessRig(string behaviour, out LibreHelperProcess launcher)
    {
        LibreHelperProcess? made = null;
        var rig = new LibreRig((time, log) => made = new LibreHelperProcess(HostPath, [behaviour], time, log));
        launcher = made!;
        return rig;
    }

    private static bool Runs(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public void TheHelper_ServesTheEngineThatStartedIt_AndIsGoneOnceTheSourceStops()
    {
        var rig = ProcessRig("serve", out var launcher);
        try
        {
            rig.Open();

            // A helper told another start time ends with 0 before it serves: this one served.
            Assert.Equal(SourceState.Running, rig.State);
            Assert.Equal(["libre:/amdcpu/0/load/0"], rig.Keys);
            Assert.Equal(12.5, rig.Now("/amdcpu/0/load/0"));
            Assert.True(Runs(launcher.LastProcessId));
        }
        finally
        {
            rig.Dispose();
        }

        // The stop closed the pipe and waited for the process to end: none is left behind.
        Assert.False(Runs(launcher.LastProcessId), "the helper's process outlived the source");
    }

    [Fact]
    public void AFailedOpen_EndsTheHelperWithFour_AndIsRestartedTwoSecondsOn()
    {
        using var rig = ProcessRig("fail-open", out var launcher);
        rig.Settings = rig.Settings with { Storage = false }; // the start configures: the computer's open fails
        rig.Open();

        Assert.Equal((SourceStateKind.Unavailable, "helperFailed"), (rig.State.Kind, rig.State.Reason?.Code));
        var first = launcher.LastProcessId;
        Assert.False(Runs(first));
        rig.Poll(TimeSpan.FromSeconds(1.5));
        Assert.Equal(first, launcher.LastProcessId);
        rig.Poll(TimeSpan.FromSeconds(0.5));
        Assert.NotEqual(first, launcher.LastProcessId);
        Assert.False(Runs(launcher.LastProcessId));
    }

    [Fact]
    public async Task AHelperThatHangs_IsGivenUpWhenItsRequestTimesOut_AndItsProcessEnds()
    {
        using var rig = ProcessRig("hang-update", out var launcher);
        var open = Task.Run(rig.Open, Cancel);
        // The stand-in says so on its output, which the launcher logs: its tree is inside a read that never returns.
        var patience = Stopwatch.StartNew();
        while (!HasSaid(rig, "update-entered") && patience.Elapsed < StartPatience)
            await Task.Delay(20, Cancel);
        Assert.True(HasSaid(rig, "update-entered"), $"the stand-in didn't say update-entered within {patience.Elapsed.TotalSeconds:0.0} s (the open {open.Status}); logged:\n{Logged(rig)}");
        Assert.False(open.IsCompleted);

        rig.Time.Advance(HelperProtocol.RequestTimeout);
        await open.WaitAsync(HelperTestKit.Patience, Cancel);

        Assert.Equal((SourceStateKind.Unavailable, "helperFailed"), (rig.State.Kind, rig.State.Reason?.Code));
        Assert.False(Runs(launcher.LastProcessId), "the hung helper's process outlived its giving up");
    }

    private static string Logged(LibreRig rig)
    {
        lock (rig.Log)
            return string.Join("\n", rig.Log.Select(e => $"{e.Timestamp:HH:mm:ss.fff} {e.Level} {e.RenderMessage()}{(e.Exception is { } x ? $" ({x.GetType().Name}: {x.Message})" : "")}"));
    }

    private static bool HasSaid(LibreRig rig, string word)
    {
        lock (rig.Log)
            return rig.Log.Any(e => e.Properties.TryGetValue("Line", out var line) && line.ToString() == "\"" + word + "\"");
    }
}
