using InfoPanel.SensorHelper;
using Xunit;
using static InfoPanel.Core.Tests.SensorHelper.HelperTestKit;

namespace InfoPanel.Core.Tests.SensorHelper;

/// <summary>
/// The helper's serve lifetime: it stops when the engine closes its pipe (while the tree hangs too, its call left
/// behind and the tree not closed under it), when the engine breaks the one-request-at-a-time rule, and when the
/// engine's process ends (a real process here) before connecting, while connected, or while the tree hangs; a client
/// that isn't the engine's process is dropped unserved and the engine served after it; a retired
/// server calls the tree no more; a name that could be a path is refused. The process around it:
/// <c>HelperProcessTests</c> and <c>HelperChildProcessTests</c>.
/// </summary>
public sealed class HelperHostTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheHelperStops_WhenTheEngineClosesItsPipe()
    {
        var tree = FakeTree.Pc();
        var (pipe, run) = StartHelper(tree);
        var client = await Connect(pipe);
        await client.HelloAsync(Cancel);
        await client.SampleAsync(Cancel);
        Assert.False(run.IsCompleted);

        await client.DisposeAsync();

        Assert.Equal(HelperExit.PipeClosed, await run.WaitAsync(Patience, Cancel));
    }

    [Fact]
    public async Task TheHelperStops_WhenTheEngineClosesItsPipe_WhileTheTreeHangs()
    {
        var tree = FakeTree.Pc();
        var server = new HelperServer(tree, "2.0.0-test");
        var pipe = PipeName();
        var run = HelperHost.RunAsync(pipe, server, EngineRunsOn, cancel: Cancel);
        var client = await Connect(pipe);
        await client.HelloAsync(Cancel);
        tree.Go.Reset();
        tree.UpdateEntered.Reset();
        var sample = client.SampleAsync(Cancel);
        Assert.True(tree.UpdateEntered.Wait(Patience, Cancel));

        await client.DisposeAsync(); // the engine gives the helper up, as after its 10 s

        Assert.Equal(HelperExit.PipeClosed, await run.WaitAsync(Patience, Cancel));
        Assert.False(server.Retire()); // a call is still inside the tree: it must not be closed under it
        await Assert.ThrowsAnyAsync<Exception>(() => sample);
        tree.Go.Set();
    }

    [Fact]
    public async Task AFrameSentBeforeTheAnswer_IsRefused_AndTheHelperStops()
    {
        var tree = FakeTree.Pc();
        var (pipe, run) = StartHelper(tree);
        await using var raw = await RawClient(pipe);
        await Send(raw, """{"type":"hello","protocol":1}""");
        await Receive(raw);
        tree.Go.Reset();
        tree.UpdateEntered.Reset();
        await Send(raw, """{"type":"sample"}""");
        Assert.True(tree.UpdateEntered.Wait(Patience, Cancel));

        await Send(raw, """{"type":"sample"}"""); // one request at a time: a second before the first's answer breaks it

        Assert.Equal(HelperExit.Refused, await run.WaitAsync(Patience, Cancel));
        Assert.Null(await Receive(raw));
        tree.Go.Set();
    }

    [Fact]
    public async Task AServerIdleWhenRetired_CallsTheTreeNoMore()
    {
        var tree = FakeTree.Pc();
        var server = new HelperServer(tree, "2.0.0-test");
        var pipe = PipeName();
        var run = HelperHost.RunAsync(pipe, server, EngineRunsOn, cancel: Cancel);
        await using var raw = await RawClient(pipe);
        await Send(raw, """{"type":"hello","protocol":1}""");
        await Receive(raw);
        await Send(raw, """{"type":"catalogue"}""");
        await Receive(raw);
        var updates = tree.Updates;

        Assert.True(server.Retire());
        await Send(raw, """{"type":"sample"}""");

        Assert.StartsWith("""{"type":"error","code":"treeFailed",""", await Receive(raw));
        Assert.Equal(updates, tree.Updates);
        _ = run;
    }

    [Fact]
    public async Task AClientThatIsNotTheEngine_IsDroppedUnserved_AndTheEngineIsServedAfterIt()
    {
        var tree = FakeTree.Pc();
        var clients = new Queue<int?>([999, null, 4242]); // another process, one whose id can't be read, the engine
        using var asked = new SemaphoreSlim(0);
        var pipe = PipeName();
        var run = HelperHost.RunAsync(pipe, new HelperServer(tree, "2.0.0-test"), EngineRunsOn, HelperHost.ClientIs(4242, _ =>
        {
            var pid = clients.Dequeue();
            asked.Release();
            return pid;
        }), Cancel);

        foreach (var _ in new[] { "another process", "unreadable" })
        {
            await using var intruder = await RawClient(pipe);
            Assert.True(await asked.WaitAsync(Patience, Cancel));
            await Task.Delay(50, Cancel); // most often the helper has dropped it by now, before anything is written
            await IsDroppedUnanswered(intruder);
        }

        await using var engine = await Connect(pipe);
        Assert.Equal(HelperProtocol.Version, (await engine.HelloAsync(Cancel)).Protocol);
        await engine.SampleAsync(Cancel);
        Assert.Empty(clients);
        Assert.Equal(1, tree.Updates); // nothing reached the tree for the others
        Assert.False(run.IsCompleted);
    }

    /// <summary>
    /// A client the helper drops unserved sends hello: the pipe may already be broken when it writes, else its read meets
    /// the pipe's end; either way nothing is answered.
    /// </summary>
    private static async Task IsDroppedUnanswered(Stream intruder)
    {
        try
        {
            await Send(intruder, """{"type":"hello","protocol":1}""");
        }
        catch (IOException)
        {
            return; // dropped before the write
        }

        string? answer;
        try
        {
            answer = await Receive(intruder);
        }
        catch (IOException)
        {
            return; // dropped, the read met a broken pipe
        }

        Assert.Null(answer); // dropped, the read met its end
    }

    [Fact]
    public async Task TheHelperStops_WhenTheEnginesProcessEnds_WhileConnected()
    {
        using var engine = LongRunningProcess();
        var (pipe, run) = StartHelper(FakeTree.Pc(), Watch(engine));
        await using var client = await Connect(pipe);
        await client.HelloAsync(Cancel);
        Assert.False(run.IsCompleted);

        engine.Kill();

        Assert.Equal(HelperExit.EngineExited, await run.WaitAsync(Patience, Cancel));
    }

    [Fact]
    public async Task TheHelperStops_WhenTheEnginesProcessEnds_BeforeItConnects()
    {
        using var engine = LongRunningProcess();
        var (_, run) = StartHelper(FakeTree.Pc(), Watch(engine));
        await Task.Delay(100, Cancel);
        Assert.False(run.IsCompleted);

        engine.Kill();

        Assert.Equal(HelperExit.EngineExited, await run.WaitAsync(Patience, Cancel));
    }

    [Fact]
    public async Task TheHelperStops_WhenTheEnginesProcessEnds_WhileTheTreeHangs()
    {
        using var engine = LongRunningProcess();
        var tree = FakeTree.Pc();
        var (pipe, run) = StartHelper(tree, Watch(engine));
        await using var client = await Connect(pipe);
        await client.HelloAsync(Cancel);
        tree.Go.Reset();
        tree.UpdateEntered.Reset();
        _ = client.SampleAsync(Cancel);
        Assert.True(tree.UpdateEntered.Wait(Patience, Cancel));

        engine.Kill();

        Assert.Equal(HelperExit.EngineExited, await run.WaitAsync(Patience, Cancel));
        tree.Go.Set();
    }

    [Fact]
    public async Task AnEngineThatIsAlreadyGone_EndsTheWatchAtOnce()
    {
        using var engine = LongRunningProcess();
        var pid = engine.Id;
        engine.Kill();
        await engine.WaitForExitAsync(Cancel);

        Assert.Null(HelperParent.Open(pid)); // nothing to watch: the helper ends at once
    }

    [Fact]
    public async Task TheWatch_StillFires_AfterCollections_WhenOnlyItsTaskIsKept()
    {
        using var engine = LongRunningProcess();
        var (exited, owner) = OpenKeepingOnlyTheTask(engine.Id);
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        if (OperatingSystem.IsWindows())
            Assert.True(owner.IsAlive, "the process handle's owner was collected while its wait was registered");

        // New handles and waits after the collections: a closed handle's value is taken again, and the thread pool's
        // wait thread waits anew on the handles it holds.
        var others = Enumerable.Range(0, 8).Select(_ => new ManualResetEvent(false)).ToArray();
        var waits = others.Select(e => ThreadPool.RegisterWaitForSingleObject(e, static (_, _) => { }, null, Timeout.Infinite, executeOnlyOnce: true)).ToArray();
        try
        {
            Assert.False(exited.IsCompleted);

            engine.Kill();

            await exited.WaitAsync(Patience, Cancel);
        }
        finally
        {
            foreach (var wait in waits)
                wait.Unregister(null);
            foreach (var other in others)
                other.Dispose();
        }
    }

    /// <summary>The watch's task and a weak reference to the parent: nothing else holds the parent.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (Task Exited, WeakReference Owner) OpenKeepingOnlyTheTask(int pid)
    {
        var parent = HelperParent.Open(pid) ?? throw new InvalidOperationException("The engine isn't running.");
        return (parent.Exited, new WeakReference(parent));
    }

    [Fact]
    public async Task APipeNameThatCouldBeAPath_IsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => HelperHost.RunAsync("../engine", FakeTree.Pc(), "2.0.0", EngineRunsOn, Cancel));
    }
}
