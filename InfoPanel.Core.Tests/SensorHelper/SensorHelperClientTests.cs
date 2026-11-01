using System.Buffers.Binary;
using System.Text;
using InfoPanel.Core.Tests.Sensors;
using InfoPanel.SensorHelper;
using Xunit;
using static InfoPanel.Core.Tests.SensorHelper.HelperTestKit;

namespace InfoPanel.Core.Tests.SensorHelper;

/// <summary>
/// The engine's side of the protocol: against the real serve loop (the answers, the catalogue version, the recording
/// seam), against a helper that hangs (a sample given up after 10 s, on a manual clock), and against a raw pipe
/// answering what a broken or hostile helper might (another version, an unknown or mismatched answer, a frame over the
/// cap or cut short, JSON too deep, more sensors than the cap, a sample that doesn't fit its catalogue).
/// </summary>
public sealed class SensorHelperClientTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private sealed class Recorder : SensorHelperClient.IObserver
    {
        public readonly List<string> Answers = [];

        public void Answered(ReadOnlySpan<byte> json) => Answers.Add(Encoding.UTF8.GetString(json));
    }

    [Fact]
    public async Task EachAnswerItAccepts_ReachesTheObserver_AsItsJson()
    {
        var recorder = new Recorder();
        var (pipe, _) = StartHelper(FakeTree.Pc());
        await using var client = await Connect(pipe, observer: recorder);

        await client.HelloAsync(Cancel);
        var catalogue = await client.CatalogueAsync(Cancel);
        var sample = await client.SampleAsync(Cancel);

        Assert.Equal(3, recorder.Answers.Count);
        Assert.Equal(Encoding.UTF8.GetString(HelperCodec.EncodeAnswer(catalogue)), recorder.Answers[1]);
        Assert.Equal(Encoding.UTF8.GetString(HelperCodec.EncodeAnswer(sample.Answer)), recorder.Answers[2]);
        Assert.Equal(catalogue.Version, client.CatalogueVersion);
        Assert.Equal(catalogue.Sensors.Count, client.CatalogueCount);
    }

    [Fact]
    public async Task ASample_IsGivenUpAfterTenSeconds_AndTheClientCloses()
    {
        var time = new TimerTimeProvider(DateTimeOffset.UnixEpoch);
        var tree = FakeTree.Pc();
        var (pipe, _) = StartHelper(tree);
        await using var client = await Connect(pipe, time);
        await client.HelloAsync(Cancel);
        await client.CatalogueAsync(Cancel);

        tree.UpdateEntered.Reset();
        tree.Go.Reset(); // the tree hangs, as LibreHardwareMonitor's native calls now and then do
        var sample = client.SampleAsync(Cancel);
        Assert.True(tree.UpdateEntered.Wait(Patience, Cancel));

        time.Advance(HelperProtocol.SampleTimeout - TimeSpan.FromTicks(1));
        await Task.Delay(200, Cancel);
        Assert.False(sample.IsCompleted);

        time.Advance(TimeSpan.FromTicks(1));
        await Task.WhenAny(sample, Task.Delay(Patience, Cancel)); // the clock moved; no real 10 s pass
        Assert.True(sample.IsFaulted, "the sample wasn't given up");
        Assert.IsType<TimeoutException>(sample.Exception!.InnerException);
        Assert.Equal(TimeSpan.FromSeconds(10), HelperProtocol.SampleTimeout);
        Assert.True(client.Closed);
        Assert.Equal("closed", (await Assert.ThrowsAsync<HelperProtocolException>(() => client.SampleAsync(Cancel))).Code);
        tree.Go.Set();
    }

    [Fact]
    public async Task ASampleQueuedBehindASlowRequest_IsGivenUpTenSecondsAfterItWasAsked_UnsentAndWithTheClientOpen()
    {
        var time = new TimerTimeProvider(DateTimeOffset.UnixEpoch);
        var (server, client) = await RawHelper(time);
        await using var _ = server;
        await using var __ = client;
        var configure = client.ConfigureAsync(false, 30, Cancel); // opening the computer takes seconds
        Assert.Equal("""{"type":"configure","storage":false,"storageInterval":30}""", await Receive(server));
        var sample = client.SampleAsync(Cancel);

        time.Advance(HelperProtocol.SampleTimeout - TimeSpan.FromTicks(1));
        await Task.Delay(200, Cancel);
        Assert.False(sample.IsCompleted);

        time.Advance(TimeSpan.FromTicks(1));
        await Task.WhenAny(sample, Task.Delay(Patience, Cancel));
        Assert.True(sample.IsFaulted, "the queued sample wasn't given up");
        var busy = Assert.IsType<HelperBusyException>(sample.Exception!.InnerException);
        Assert.IsAssignableFrom<TimeoutException>(busy);
        Assert.False(client.Closed); // nothing was sent for it: the pipe is still in step

        await Send(server, """{"type":"configure","storage":false,"storageInterval":30}""");
        Assert.Equal(new ConfigureAnswer(false, 30), await configure);
        var next = client.SampleAsync(Cancel);
        Assert.Equal("""{"type":"sample"}""", await Receive(server)); // the next frame is the new sample's: the given-up one never went
        await Send(server, """{"type":"sample","version":1,"value":[],"min":[],"max":[]}""");
        Assert.True((await next).CatalogueChanged);
    }

    [Fact]
    public async Task ASampleThatWaitedForAnother_HasOnlyWhatIsLeftOfItsTenSeconds_ThenTheClientCloses()
    {
        var time = new TimerTimeProvider(DateTimeOffset.UnixEpoch);
        var (server, client) = await RawHelper(time);
        await using var _ = server;
        await using var __ = client;
        var configure = client.ConfigureAsync(false, 30, Cancel);
        await Receive(server);
        var sample = client.SampleAsync(Cancel);

        time.Advance(TimeSpan.FromSeconds(6));
        await Send(server, """{"type":"configure","storage":false,"storageInterval":30}""");
        await configure;
        Assert.Equal("""{"type":"sample"}""", await Receive(server)); // sent, and never answered

        time.Advance(TimeSpan.FromSeconds(4) - TimeSpan.FromTicks(1));
        await Task.Delay(200, Cancel);
        Assert.False(sample.IsCompleted);

        time.Advance(TimeSpan.FromTicks(1));
        await Task.WhenAny(sample, Task.Delay(Patience, Cancel));
        Assert.True(sample.IsFaulted, "the sample got a fresh ten seconds after the gate");
        var timeout = Assert.IsType<TimeoutException>(sample.Exception!.InnerException);
        Assert.IsNotType<HelperBusyException>(timeout);
        Assert.True(client.Closed); // the answer may still come: the pipe is no longer in step
    }

    private sealed class Probe(Action<string> answered) : SensorHelperClient.IObserver
    {
        public void Answered(ReadOnlySpan<byte> json) => answered(Encoding.UTF8.GetString(json));
    }

    [Fact]
    public async Task TheCataloguesVersionAndCount_AreTheClientsBeforeAnotherRequestCanStart()
    {
        // The observer runs inside the exchange, before the next request may take its turn: what it sees of the
        // client's catalogue state is what a request waiting for that turn would see first.
        SensorHelperClient? client = null;
        var seen = new List<(long Version, int Count)>();
        var (server, raw) = await RawHelper(observer: new Probe(json =>
        {
            if (json.StartsWith("""{"type":"catalogue""", StringComparison.Ordinal))
                seen.Add((client!.CatalogueVersion, client.CatalogueCount));
        }));
        client = raw;
        await using var _ = server;
        await using var __ = client;

        var catalogue = client.CatalogueAsync(Cancel);
        await Receive(server);
        await Send(server, Catalogue(4, 2));
        await catalogue;

        Assert.Equal([(4L, 2)], seen);
    }

    [Fact]
    public async Task SamplesRacingCatalogueRefreshes_AreEachReadAgainstTheCatalogueBeforeThem()
    {
        var (server, client) = await RawHelper();
        await using var _ = server;
        await using var __ = client;
        var helper = Task.Run(async () =>
        {
            // Each catalogue is a new version with one more sensor; a sample answers under the version now.
            var version = 0;
            while (await Receive(server) is { } request)
            {
                if (request.Contains("catalogue", StringComparison.Ordinal))
                    await Send(server, Catalogue(++version, version));
                else
                    await Send(server, $$"""{"type":"sample","version":{{Math.Max(version, 1)}},"value":[{{string.Join(",", Enumerable.Repeat("1", version))}}],"min":[{{string.Join(",", Enumerable.Repeat("1", version))}}],"max":[{{string.Join(",", Enumerable.Repeat("1", version))}}]}""");
            }
        }, Cancel);
        await client.CatalogueAsync(Cancel);

        for (var round = 0; round < 200; round++)
        {
            var refresh = client.CatalogueAsync(Cancel);
            var samples = Enumerable.Range(0, 3).Select(_ => client.SampleAsync(Cancel)).ToList();
            await refresh;
            foreach (var sample in samples)
                Assert.False((await sample).CatalogueChanged); // the helper's version moves only with a catalogue the client took
        }

        Assert.False(client.Closed);
        await client.DisposeAsync();
        await helper.WaitAsync(Patience, Cancel);
    }

    /// <summary>A catalogue under <paramref name="version"/> listing <paramref name="count"/> sensors on one CPU.</summary>
    private static string Catalogue(long version, int count) =>
        $$"""{"type":"catalogue","version":{{version}},"hardware":[{"id":"/a","name":"","type":"Cpu","parent":null}],"sensors":[{{string.Join(",", Enumerable.Range(0, count).Select(i => $$"""{"id":"/a/{{i}}","name":"","type":"Load","index":{{i}},"unit":"%","hw":"/a"}"""))}}]}""";

    [Fact]
    public async Task ASampleUnderAnotherCatalogueVersion_SaysTheCatalogueChanged()
    {
        var (server, client) = await RawHelper();
        await using var _ = server;
        await using var __ = client;

        var catalogue = client.CatalogueAsync(Cancel);
        await Receive(server);
        await Send(server, """{"type":"catalogue","version":4,"hardware":[{"id":"/a","name":"","type":"Cpu","parent":null}],"sensors":[{"id":"/a/0","name":"","type":"Load","index":0,"unit":"%","hw":"/a"}]}""");
        await catalogue;

        var sample = client.SampleAsync(Cancel);
        await Receive(server);
        await Send(server, """{"type":"sample","version":5,"value":[1,2],"min":[1,2],"max":[1,2]}""");

        Assert.True((await sample).CatalogueChanged);
        Assert.Equal(4, client.CatalogueVersion);
    }

    public static TheoryData<string, string, string> BadAnswers => new()
    {
        // request, answer, the code the client refuses it with
        { "hello", """{"type":"hello","protocol":2,"helper":"9.0.0","elevated":false,"driver":"unknown"}""", "protocol" },
        { "hello", """{"type":"error","code":"protocol","message":"The helper speaks protocol 2."}""", "protocol" },
        { "hello", """{"type":"catalogue","version":1,"hardware":[],"sensors":[]}""", "unexpected" },
        { "sample", """{"type":"configure","storage":true,"storageInterval":30}""", "unexpected" },
        { "hello", """{"type":"setFan","value":100}""", "malformed" },
        { "hello", """{"type":"hello","protocol":1,"helper":"2.0.0","elevated":false,"driver":"unknown","run":"cmd"}""", "malformed" },
        { "hello", Nested(HelperProtocol.MaxDepth + 1, "hello"), "tooDeep" },
        { "sample", """{"type":"sample","version":1,"value":[1],"min":[1],"max":[1]}""", "malformed" }, // version 1 has no sensors here
    };

    [Theory]
    [MemberData(nameof(BadAnswers))]
    public async Task AnAnswerItRefuses_ClosesTheClient(string request, string answer, string code)
    {
        var (server, client) = await RawHelper();
        await using var _ = server;
        await using var __ = client;
        if (request == "sample")
        {
            var catalogue = client.CatalogueAsync(Cancel);
            await Receive(server);
            await Send(server, """{"type":"catalogue","version":1,"hardware":[],"sensors":[]}""");
            await catalogue;
        }

        Task call = request == "hello" ? client.HelloAsync(Cancel) : client.SampleAsync(Cancel);
        await Receive(server);
        await Send(server, answer);

        var refused = await Assert.ThrowsAnyAsync<HelperProtocolException>(() => call);
        Assert.Equal(code, refused.Code);
        Assert.True(client.Closed);
    }

    [Fact]
    public async Task AnAnswerOverTheCap_IsRefused_UnreadBeyondItsLength()
    {
        var (server, client) = await RawHelper();
        await using var _ = server;
        await using var __ = client;
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, HelperProtocol.MaxFrameBytes + 1);

        var call = client.HelloAsync(Cancel);
        await Receive(server);
        await server.WriteAsync(header, Cancel);
        await server.FlushAsync(Cancel);

        Assert.Equal("tooLarge", (await Assert.ThrowsAnyAsync<HelperProtocolException>(() => call)).Code);
        Assert.True(client.Closed);
    }

    [Fact]
    public async Task AnAnswerCutShort_OrNone_IsRefused()
    {
        var (server, client) = await RawHelper();
        await using var __ = client;
        var frame = Frame("""{"type":"hello","protocol":1,"helper":"2.0.0","elevated":false,"driver":"unknown"}""");

        var call = client.HelloAsync(Cancel);
        await Receive(server);
        await server.WriteAsync(frame.AsMemory(0, 10), Cancel);
        await server.FlushAsync(Cancel);
        await server.DisposeAsync();

        Assert.Equal("truncated", (await Assert.ThrowsAnyAsync<HelperProtocolException>(() => call)).Code);

        var (server2, client2) = await RawHelper();
        await using var ___ = client2;
        var call2 = client2.HelloAsync(Cancel);
        await Receive(server2);
        await server2.DisposeAsync();

        Assert.Equal("closed", (await Assert.ThrowsAnyAsync<HelperProtocolException>(() => call2)).Code);
        Assert.True(client2.Closed);
    }

    [Fact]
    public async Task MoreSensorsThanTheCap_AreRefused()
    {
        var (server, client) = await RawHelper();
        await using var _ = server;
        await using var __ = client;
        var values = string.Join(",", Enumerable.Repeat("null", HelperProtocol.MaxSensors + 1));

        var call = client.SampleAsync(Cancel);
        await Receive(server);
        await Send(server, $$"""{"type":"sample","version":1,"value":[{{values}}],"min":[{{values}}],"max":[{{values}}]}""");

        Assert.Equal("tooLarge", (await Assert.ThrowsAnyAsync<HelperProtocolException>(() => call)).Code);
    }

    [Fact]
    public async Task ATreeFailure_LeavesTheClientOpen()
    {
        var (server, client) = await RawHelper();
        await using var _ = server;
        await using var __ = client;

        var call = client.SampleAsync(Cancel);
        await Receive(server);
        await Send(server, """{"type":"error","code":"treeFailed","message":"LibreHardwareMonitor threw."}""");

        Assert.Equal(ErrorAnswer.TreeFailed, (await Assert.ThrowsAsync<HelperErrorException>(() => call)).Code);
        Assert.False(client.Closed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public async Task AConfigureOutOfRange_IsNeverSent(int interval)
    {
        var (server, client) = await RawHelper();
        await using var _ = server;
        await using var __ = client;

        await Assert.ThrowsAnyAsync<HelperProtocolException>(() => client.ConfigureAsync(true, interval, Cancel));

        var hello = client.HelloAsync(Cancel);
        Assert.Equal("""{"type":"hello","protocol":1}""", await Receive(server)); // the first frame the helper sees
        await Send(server, """{"type":"hello","protocol":1,"helper":"2.0.0","elevated":false,"driver":"unknown"}""");
        await hello;
    }
}
