using System.Diagnostics;
using System.Threading.Channels;
using InfoPanel.SensorHelper;
using Xunit;
using static InfoPanel.Core.Tests.SensorHelper.HelperTestKit;
using TimerTimeProvider = InfoPanel.Core.Tests.Sensors.TimerTimeProvider;

namespace InfoPanel.Core.Tests.SensorHelper;

/// <summary>
/// The helper's process ends, measured on a real child process: <c>InfoPanel.SensorHelper.TestHost</c>, the helper's
/// own lifetime and serve loop over a fake tree that hangs or fails on cue (the real helper runs on Windows only). The
/// engine is a <c>sleep</c> this test starts; the test itself is the engine's end of the pipe. The child must end when
/// the pipe closes and when the engine dies, while the tree hangs inside a call (never closing it under that call); a
/// close that hangs is cut at the 2 s bound, and the engine's end cuts it at once; a thread holding the console's lock
/// doesn't hold the exit; a process that took the engine's id
/// is never served; a computer that fails to open ends it. Each time is written to the test's output. That a hung close
/// is never cut before its bound is shown on the helper's own clock, its lifetime run in this process on a manual one.
/// </summary>
public sealed class HelperChildProcessTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>How long a child may take past its bound before the test calls it hung (a loaded machine is slow to schedule).</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// How long past a child's own exit this process may take to see it, where the test times the child on its own clock:
    /// a sanity limit (the time under test is the child's).
    /// </summary>
    private static readonly TimeSpan ExitNotice = TimeSpan.FromSeconds(30);

    private static string HostPath => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "InfoPanel.SensorHelper.TestHost.exe" : "InfoPanel.SensorHelper.TestHost");

    /// <summary>The stand-in helper as a child process, its output read line by line; killed at the end if it still runs.</summary>
    private sealed class Child : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        private readonly List<string> _seen = [];

        public Child(string behaviour, Process engine, long? engineStart = null, TimeSpan? cleanupBound = null)
        {
            Pipe = PipeName();
            var info = new ProcessStartInfo(HostPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true, // nothing reads it; the child must not take the test's input
            };
            info.ArgumentList.Add(behaviour);
            info.ArgumentList.Add("--engine-client"); // the test is the engine's end of the pipe
            info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (cleanupBound is { } bound)
            {
                info.ArgumentList.Add("--cleanup-ms");
                info.ArgumentList.Add(((int)bound.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            using (var parent = HelperParent.Open(engine.Id) ?? throw new InvalidOperationException("The engine isn't running."))
            {
                foreach (var argument in new HelperCommandLine(Pipe, engine.Id, engineStart ?? parent.StartTime).Arguments)
                    info.ArgumentList.Add(argument);
            }

            _process = new Process { StartInfo = info };
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is { } line)
                    _lines.Writer.TryWrite(line);
                else
                    _lines.Writer.TryComplete();
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is { } line)
                    TestContext.Current.TestOutputHelper?.WriteLine($"child stderr: {line}");
            };
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public string Pipe { get; }

        /// <summary>Every line the child wrote so far that a wait read.</summary>
        public IReadOnlyList<string> Seen => _seen;

        public async Task SaysAsync(string word)
        {
            using var patience = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
            patience.CancelAfter(Patience);
            while (await _lines.Reader.WaitToReadAsync(patience.Token))
            {
                while (_lines.Reader.TryRead(out var line))
                {
                    _seen.Add(line);
                    if (line == word)
                        return;
                }
            }

            Assert.Fail($"the child ended without saying '{word}' (it said: {string.Join(", ", _seen)})");
        }

        /// <summary>Waits for the child to exit, at most <paramref name="within"/> after <paramref name="since"/> started; its exit code and the time it took.</summary>
        public async Task<(int Code, TimeSpan After)> ExitsAsync(Stopwatch since, TimeSpan within)
        {
            using var patience = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
            patience.CancelAfter(within + TimeSpan.FromSeconds(10) - since.Elapsed);
            try
            {
                await _process.WaitForExitAsync(patience.Token);
            }
            catch (OperationCanceledException) when (!Cancel.IsCancellationRequested)
            {
                Assert.Fail($"the helper's process still runs {since.Elapsed.TotalMilliseconds:0} ms on (bound {within.TotalMilliseconds:0} ms)");
            }

            var after = since.Elapsed;
            while (_lines.Reader.TryRead(out var line))
                _seen.Add(line);
            Assert.True(after <= within, $"the helper's process ended {after.TotalMilliseconds:0} ms on; bound {within.TotalMilliseconds:0} ms");
            return (_process.ExitCode, after);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None);
            }

            _process.Dispose();
        }
    }

    private static void Log(string what, TimeSpan after) =>
        TestContext.Current.TestOutputHelper?.WriteLine($"helper child: {what}: ended {after.TotalMilliseconds:0} ms on");

    /// <summary>The engine, for a test of the stand-in as a child process: on Linux the test skips here, before it starts any process.</summary>
    private static Process Engine()
    {
        Assert.SkipWhen(OperatingSystem.IsLinux(), LinuxChildSkip);
        return LongRunningProcess();
    }

    private static void End(Process engine)
    {
        engine.Kill();
        engine.WaitForExit();
    }

    [Fact]
    public async Task WhenThePipeCloses_TheTreeIsClosed_AndTheProcessEnds()
    {
        using var engine = Engine();
        await using var child = new Child("serve", engine);
        var client = await Connect(child.Pipe);
        await client.HelloAsync(Cancel);
        await client.SampleAsync(Cancel);

        var since = Stopwatch.StartNew();
        await client.DisposeAsync();

        var (code, after) = await child.ExitsAsync(since, HelperProcess.CleanupBound);
        Log("pipe closed, tree idle", after);
        Assert.Equal(0, code);
        Assert.Contains("disposed", child.Seen);
        End(engine);
    }

    [Fact]
    public async Task WhenThePipeCloses_WhileTheTreeHangs_TheProcessEnds_WithoutClosingTheTree()
    {
        using var engine = Engine();
        await using var child = new Child("hang-update", engine);
        var client = await Connect(child.Pipe);
        await client.HelloAsync(Cancel);
        var sample = client.SampleAsync(Cancel);
        await child.SaysAsync("update-entered");

        var since = Stopwatch.StartNew();
        await client.DisposeAsync();

        var (code, after) = await child.ExitsAsync(since, HelperProcess.CleanupBound);
        Log("pipe closed, tree hung in a call", after);
        Assert.Equal(0, code);
        Assert.DoesNotContain("dispose-entered", child.Seen);
        await Assert.ThrowsAnyAsync<Exception>(() => sample);
        End(engine);
    }

    [Fact]
    public async Task WhenTheEngineDies_WhileTheTreeHangs_TheProcessEnds()
    {
        using var engine = Engine();
        await using var child = new Child("hang-update", engine);
        await using var client = await Connect(child.Pipe);
        await client.HelloAsync(Cancel);
        _ = client.SampleAsync(Cancel);
        await child.SaysAsync("update-entered");

        var since = Stopwatch.StartNew();
        End(engine);

        var (code, after) = await child.ExitsAsync(since, HelperProcess.CleanupBound);
        Log("engine died, tree hung in a call", after);
        Assert.Equal(0, code);
        Assert.DoesNotContain("dispose-entered", child.Seen);
    }

    [Fact]
    public async Task WhenTheEngineDies_BeforeItConnects_TheProcessEnds()
    {
        using var engine = Engine();
        await using var child = new Child("serve", engine);
        await child.SaysAsync("tree-created");

        var since = Stopwatch.StartNew();
        End(engine);

        var (code, after) = await child.ExitsAsync(since, HelperProcess.CleanupBound);
        Log("engine died before connecting", after);
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task WhenAThreadHoldsTheConsole_ThePipeClosing_StillEndsTheProcess()
    {
        using var engine = Engine();
        await using var child = new Child("hold-console", engine);
        var client = await Connect(child.Pipe);
        await client.HelloAsync(Cancel);
        await client.SampleAsync(Cancel);
        await child.SaysAsync("console-held");

        var since = Stopwatch.StartNew();
        await client.DisposeAsync();

        var (code, after) = await child.ExitsAsync(since, HelperProcess.CleanupBound);
        Log("pipe closed, another thread holding the console", after);
        Assert.Equal(0, code);
        Assert.Contains("disposed", child.Seen);
        End(engine);
    }

    [Fact]
    public async Task ACloseThatHangs_IsCutAtTheBound_ThenTheProcessEnds()
    {
        using var engine = Engine();
        await using var child = new Child("hang-dispose", engine);
        var client = await Connect(child.Pipe);
        await client.HelloAsync(Cancel);
        await client.CatalogueAsync(Cancel);

        var since = Stopwatch.StartNew();
        await client.DisposeAsync();

        // The cut is bounded above on the child's own clock, from the close's start to its exit: the end seen here also
        // holds the child's teardown and this process's notice of it, which a loaded machine delays by seconds (.NET
        // handles a child's end only while no program launch of this process holds its start lock, and launches can
        // stall). Seen from here, the end only has to come, within a sanity limit. The bound below is this process's
        // own: its clock starts before the pipe closes, so it can't read a cut as early; the child's can, as its clock
        // starts inside the close, which its thread may enter late, after the bound's wait began. A late notice can hide
        // an early cut from that bound: that the cut never comes before the bound is shown on the helper's own clock
        // (ACloseThatHangs_IsNeverCutBeforeTheBound_OnTheHelpersClock).
        var (code, after) = await child.ExitsAsync(since, HelperProcess.CleanupBound + ExitNotice);
        var cut = TimeSpan.FromMilliseconds(int.Parse(Assert.Single(child.Seen, l => l.StartsWith("exit-after-dispose-ms ", StringComparison.Ordinal))["exit-after-dispose-ms ".Length..], System.Globalization.CultureInfo.InvariantCulture));
        Log("pipe closed, the tree's close hung", after);
        Log("the close cut, on the child's clock", cut);
        Assert.Equal(0, code);
        Assert.Contains("dispose-entered", child.Seen);
        Assert.DoesNotContain("disposed", child.Seen);
        Assert.True(cut <= HelperProcess.CleanupBound + Slack, $"cut {cut.TotalMilliseconds:0} ms into the close: past the bound");
        Assert.True(after >= HelperProcess.CleanupBound - TimeSpan.FromMilliseconds(100), $"ended {after.TotalMilliseconds:0} ms on: before the bound, so the close wasn't given its time");
        End(engine);
    }

    /// <summary>The engine as the helper's lifetime sees it when run in this process: always the process it was told of, never ending.</summary>
    private sealed class StandInEngine : IHelperParent
    {
        public const int Pid = 4242;

        public long StartTime => 639_000_000_000_000_000;

        public Task Exited => EngineRunsOn;

        public void Dispose()
        {
        }
    }

    /// <summary>A manual clock (<see cref="TimerTimeProvider"/>) that counts the timers set on it and those that fired.</summary>
    private sealed class CountingTime(TimerTimeProvider inner) : TimeProvider
    {
        public int Set;
        public int Fired;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override long GetTimestamp() => inner.GetTimestamp();

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(s =>
            {
                Interlocked.Increment(ref Fired);
                callback(s);
            }, state, dueTime, period);
            Interlocked.Increment(ref Set); // once its due time is set, so a test that saw it can move the clock past it
            return timer;
        }

        public void Advance(TimeSpan by) => inner.Advance(by);
    }

    /// <summary>
    /// The cut of <see cref="ACloseThatHangs_IsCutAtTheBound_ThenTheProcessEnds"/>, never before the bound: the helper's
    /// lifetime runs in this process on a manual clock, where no late thread or late notice of an exit can blur the
    /// time. Up to the bound's last tick the close is still given its time; at the bound it is cut, and the process
    /// exits. The bound is the one the helper documents, not read from it.
    /// </summary>
    [Fact]
    public async Task ACloseThatHangs_IsNeverCutBeforeTheBound_OnTheHelpersClock()
    {
        var bound = TimeSpan.FromSeconds(2);
        var time = new CountingTime(new TimerTimeProvider(DateTimeOffset.UnixEpoch));
        var tree = FakeTree.Pc();
        tree.DisposeGo.Reset();
        var engine = new StandInEngine();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipe = PipeName();
        var options = new HelperProcessOptions
        {
            OpenParent = pid => pid == StandInEngine.Pid ? engine : null,
            ClientProcessId = _ => StandInEngine.Pid, // the test is the client, and on Windows its real id isn't the stand-in's
            Time = time,
            Exit = code => exited.TrySetResult(code),
            Error = TextWriter.Null,
        };
        var run = HelperProcess.RunAsync([.. new HelperCommandLine(pipe, StandInEngine.Pid, engine.StartTime).Arguments], () => tree, "2.0.0-test", options);
        var client = await Connect(pipe);
        await client.HelloAsync(Cancel);

        await client.DisposeAsync();
        Assert.True(tree.DisposeEntered.Wait(Patience, Cancel), "the helper closes the tree");
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref time.Set) > 0, Patience), "the helper sets its bound on its own clock");
        Assert.Equal(1, Volatile.Read(ref time.Set));

        // The clock's timers fire inside Advance: none having fired, nothing cut the close.
        time.Advance(bound - TimeSpan.FromTicks(1));
        Assert.Equal(0, Volatile.Read(ref time.Fired));
        Assert.False(exited.Task.IsCompleted, "the helper exited before the bound");

        time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(1, Volatile.Read(ref time.Fired));
        Assert.Equal(0, await exited.Task.WaitAsync(Patience, Cancel));
        Assert.Equal(0, await run.WaitAsync(Patience, Cancel));
        Assert.False(tree.Disposed);
        tree.DisposeGo.Set();
    }

    [Fact]
    public async Task WhenTheEngineDies_DuringACloseThatHangs_TheProcessEndsAtOnce()
    {
        using var engine = Engine();
        await using var child = new Child("hang-dispose", engine, cleanupBound: TimeSpan.FromMinutes(1));
        var client = await Connect(child.Pipe);
        await client.HelloAsync(Cancel);
        await client.CatalogueAsync(Cancel);
        await client.DisposeAsync();
        await child.SaysAsync("dispose-entered");

        var since = Stopwatch.StartNew();
        End(engine);

        var (code, after) = await child.ExitsAsync(since, HelperProcess.CleanupBound);
        Log("engine died while the tree's close hung (bound 60 s)", after);
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task AProcessThatTookTheEnginesId_IsNeverServed_AndTheHelperEndsAtOnce()
    {
        using var engine = Engine();
        long start;
        using (var parent = HelperParent.Open(engine.Id)!)
            start = parent.StartTime;
        var since = Stopwatch.StartNew();
        await using var child = new Child("serve", engine, engineStart: start + 1);

        var (code, after) = await child.ExitsAsync(since, TimeSpan.FromSeconds(10)); // from its start: a .NET process starting
        Log("parent's start time differs", after);
        Assert.Equal(0, code);
        Assert.DoesNotContain("tree-created", child.Seen);
        End(engine);
    }

    [Fact]
    public async Task AComputerThatFailsToOpen_EndsTheProcess_With4()
    {
        using var engine = Engine();
        await using var child = new Child("fail-open", engine);
        await using var client = await Connect(child.Pipe);
        await client.HelloAsync(Cancel);

        var since = Stopwatch.StartNew();
        Assert.Equal(ErrorAnswer.TreeFailed, (await Assert.ThrowsAsync<HelperErrorException>(() => client.CatalogueAsync(Cancel))).Code);

        var (code, after) = await child.ExitsAsync(since, HelperProcess.CleanupBound);
        Log("computer failed to open", after);
        Assert.Equal(4, code);
        End(engine);
    }
}
