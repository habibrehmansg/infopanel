using System.Diagnostics;
using InfoPanel.SensorHelper;
using Xunit;
using static InfoPanel.Core.Tests.SensorHelper.HelperTestKit;

namespace InfoPanel.Core.Tests.SensorHelper;

/// <summary>
/// The helper's process lifetime (<see cref="HelperProcess"/>) in this process, through its seams: the engine is the
/// process it was told of only when the start time matches (a gone or reused id ends the helper before anything opens),
/// and only that process is served on the pipe (the client's process id, which Windows tells and a seam stands in for);
/// when serving ends it closes the tree only when no call is inside it, within the clean-up bound, the engine still
/// watched, then exits with its code. The same lifetime as a real child process: <c>HelperChildProcessTests</c>.
/// </summary>
public sealed class HelperProcessTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private const int EnginePid = 4242;
    private const long EngineStart = 639_000_000_000_000_000;

    private sealed class FakeParent(long startTime) : IHelperParent
    {
        public readonly TaskCompletionSource Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long StartTime => startTime;

        public Task Exited => Ended.Task;

        public void Dispose()
        {
        }
    }

    /// <summary>A run of the helper's lifetime on a new pipe, with what it exited with and how long after <see cref="Mark"/>.</summary>
    private sealed class Run
    {
        public readonly string Pipe = PipeName();
        public readonly TaskCompletionSource<int> Exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly Stopwatch Since = new();
        public int TreesCreated;
        public TimeSpan ExitedAfter;
        public Task<int> Task = null!;

        public void Mark() => Since.Restart();

        /// <summary>
        /// The process id the pipe's client is said to have: the engine's it was told of, unless a test says otherwise
        /// (the test itself is the client, and on Windows its real id would never be the stand-in engine's).
        /// </summary>
        public Func<System.IO.Pipes.NamedPipeServerStream, int?> ClientProcessId = _ => EnginePid;

        public HelperProcessOptions Options(IHelperParent? parent, TimeSpan? cleanupBound = null) => new()
        {
            OpenParent = pid => pid == EnginePid ? parent : null,
            ClientProcessId = ClientProcessId,
            CleanupBound = cleanupBound ?? HelperProcess.CleanupBound,
            Exit = code =>
            {
                ExitedAfter = Since.Elapsed;
                Exit.TrySetResult(code);
            },
            Error = TextWriter.Null,
        };

        public string[] Args(long start = EngineStart) => [.. new HelperCommandLine(Pipe, EnginePid, start).Arguments];

        public void Start(ISensorTree tree, IHelperParent? parent, long start = EngineStart, TimeSpan? cleanupBound = null) =>
            Task = HelperProcess.RunAsync(Args(start), () =>
            {
                Interlocked.Increment(ref TreesCreated);
                return tree;
            }, "2.0.0-test", Options(parent, cleanupBound));
    }

    [Fact]
    public async Task TheEngineItWasToldOf_IsServed_AndAtTheEndTheTreeIsClosed_ThenTheProcessExits()
    {
        var tree = FakeTree.Pc();
        var run = new Run();
        run.Start(tree, new FakeParent(EngineStart));
        var client = await Connect(run.Pipe);
        await client.HelloAsync(Cancel);
        await client.SampleAsync(Cancel);

        await client.DisposeAsync();

        Assert.Equal(0, await run.Exit.Task.WaitAsync(Patience, Cancel));
        Assert.Equal(0, await run.Task.WaitAsync(Patience, Cancel));
        Assert.True(tree.Disposed);
    }

    [Fact]
    public async Task AProcessThatTookTheEnginesId_EndsTheHelperAtOnce_BeforeAnythingOpens()
    {
        var run = new Run();
        run.Start(FakeTree.Pc(), new FakeParent(EngineStart + 1)); // another start time: not the engine that started it

        Assert.Equal(0, await run.Task.WaitAsync(Patience, Cancel));
        Assert.Equal(0, run.TreesCreated);
        Assert.False(run.Exit.Task.IsCompleted);
    }

    [Fact]
    public async Task AnEngineThatIsGone_EndsTheHelperAtOnce_BeforeAnythingOpens()
    {
        var run = new Run();
        run.Start(FakeTree.Pc(), parent: null);

        Assert.Equal(0, await run.Task.WaitAsync(Patience, Cancel));
        Assert.Equal(0, run.TreesCreated);
    }

    [Fact]
    public async Task ACommandLineItDoesNotTake_Exits2()
    {
        Assert.Equal(2, await HelperProcess.RunAsync(["--pipe", "p"], () => throw new InvalidOperationException(), "2.0.0-test", new HelperProcessOptions { Error = TextWriter.Null }).WaitAsync(Patience, Cancel));
    }

    [Fact]
    public async Task OnlyTheEnginesProcess_IsServedOnThePipe()
    {
        var tree = FakeTree.Pc();
        var parent = new FakeParent(EngineStart);
        var run = new Run { ClientProcessId = _ => EnginePid + 1 }; // whoever connects isn't the engine
        run.Start(tree, parent);

        await using (var client = await Connect(run.Pipe))
            Assert.Equal("closed", (await Assert.ThrowsAnyAsync<HelperProtocolException>(() => client.HelloAsync(Cancel))).Code);
        Assert.False(run.Task.IsCompleted);
        Assert.Equal(0, tree.Updates);

        parent.Ended.SetResult();
        Assert.Equal(0, await run.Exit.Task.WaitAsync(Patience, Cancel));

        var served = new Run { ClientProcessId = _ => EnginePid };
        served.Start(FakeTree.Pc(), new FakeParent(EngineStart));
        var engine = await Connect(served.Pipe);
        await engine.HelloAsync(Cancel);
        await engine.DisposeAsync();
        Assert.Equal(0, await served.Exit.Task.WaitAsync(Patience, Cancel));
    }

    [Fact]
    public async Task ATreeACallIsStillInside_IsNeverClosed_AndTheProcessExitsAtOnce()
    {
        var tree = FakeTree.Pc();
        var run = new Run();
        run.Start(tree, new FakeParent(EngineStart), cleanupBound: TimeSpan.FromMinutes(1));
        var client = await Connect(run.Pipe);
        await client.HelloAsync(Cancel);
        tree.Go.Reset();
        tree.UpdateEntered.Reset();
        _ = client.SampleAsync(Cancel);
        Assert.True(tree.UpdateEntered.Wait(Patience, Cancel));

        run.Mark();
        await client.DisposeAsync();

        Assert.Equal(0, await run.Exit.Task.WaitAsync(Patience, Cancel));
        Assert.False(tree.DisposeEntered.IsSet);
        Assert.True(run.ExitedAfter < TimeSpan.FromSeconds(5), $"exited {run.ExitedAfter} after the pipe closed");
        tree.Go.Set();
    }

    [Fact]
    public async Task ACloseThatHangs_IsCutAtTheCleanUpBound()
    {
        var tree = FakeTree.Pc();
        tree.DisposeGo.Reset();
        var run = new Run();
        var bound = TimeSpan.FromMilliseconds(500);
        run.Start(tree, new FakeParent(EngineStart), cleanupBound: bound);
        var client = await Connect(run.Pipe);
        await client.HelloAsync(Cancel);

        run.Mark();
        await client.DisposeAsync();

        Assert.Equal(0, await run.Exit.Task.WaitAsync(Patience, Cancel));
        Assert.True(tree.DisposeEntered.IsSet);
        Assert.InRange(run.ExitedAfter, bound - TimeSpan.FromMilliseconds(50), bound + TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(2), HelperProcess.CleanupBound);
        tree.DisposeGo.Set();
    }

    [Fact]
    public async Task TheEnginesEnd_EndsAHungCleanUpAtOnce()
    {
        var tree = FakeTree.Pc();
        tree.DisposeGo.Reset();
        var parent = new FakeParent(EngineStart);
        var run = new Run();
        run.Start(tree, parent, cleanupBound: TimeSpan.FromMinutes(1));
        var client = await Connect(run.Pipe);
        await client.HelloAsync(Cancel);
        await client.DisposeAsync();
        Assert.True(tree.DisposeEntered.Wait(Patience, Cancel));

        run.Mark();
        parent.Ended.SetResult();

        Assert.Equal(0, await run.Exit.Task.WaitAsync(Patience, Cancel));
        Assert.True(run.ExitedAfter < TimeSpan.FromSeconds(5), $"exited {run.ExitedAfter} after the engine ended");
        tree.DisposeGo.Set();
    }

    [Fact]
    public async Task TheEnginesEnd_EndsTheHelper_WithoutClosingTheTree()
    {
        var tree = FakeTree.Pc();
        var parent = new FakeParent(EngineStart);
        var run = new Run();
        run.Start(tree, parent);
        var client = await Connect(run.Pipe);
        await client.HelloAsync(Cancel);

        parent.Ended.SetResult();

        Assert.Equal(0, await run.Exit.Task.WaitAsync(Patience, Cancel));
        Assert.False(tree.DisposeEntered.IsSet); // the process's end frees what the computer holds
        await client.DisposeAsync();
    }

    [Fact]
    public async Task AComputerThatFailsToOpen_EndsTheHelper_With4()
    {
        var tree = FakeTree.Pc();
        tree.ThrowOnConfigure = true;
        var run = new Run();
        run.Start(tree, new FakeParent(EngineStart));
        await using var client = await Connect(run.Pipe);
        await client.HelloAsync(Cancel);

        Assert.Equal(ErrorAnswer.TreeFailed, (await Assert.ThrowsAsync<HelperErrorException>(() => client.CatalogueAsync(Cancel))).Code);

        Assert.Equal(4, await run.Exit.Task.WaitAsync(Patience, Cancel));
        Assert.Single(tree.Configured);
    }

    [Fact]
    public async Task WhatTheHelperRefuses_EndsItWith3()
    {
        var run = new Run();
        run.Start(FakeTree.Pc(), new FakeParent(EngineStart));
        await using var raw = await RawClient(run.Pipe);

        await Send(raw, """{"type":"sample"}"""); // before hello

        // Read as the engine does: on Windows the pipe holds no answer unread, so the helper's write waits for the read.
        Assert.StartsWith("""{"type":"error","code":"unexpected",""", await Receive(raw));
        Assert.Equal(3, await run.Exit.Task.WaitAsync(Patience, Cancel));
    }
}
