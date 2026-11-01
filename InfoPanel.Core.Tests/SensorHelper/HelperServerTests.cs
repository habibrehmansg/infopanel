using InfoPanel.SensorHelper;
using Xunit;
using static InfoPanel.Core.Tests.SensorHelper.HelperTestKit;

namespace InfoPanel.Core.Tests.SensorHelper;

/// <summary>
/// The helper's serve loop over a fake tree, through a real named pipe: <c>hello</c>, <c>catalogue</c> (identifiers
/// exactly as the tree gives them, 1.4.x's order, the units), <c>sample</c> (null kept as null), <c>configure</c>
/// reaching the tree and a new catalogue version after it; what it refuses (anything before <c>hello</c>, another
/// version, an unknown message, a frame over the cap or cut short, JSON too deep) and that it then closes the pipe; a
/// failing read answered without closing; a computer that fails to open answered, then the pipe closed and the helper
/// retired; and never more sensors than the cap.
/// </summary>
public sealed class HelperServerTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Hello_AnswersTheProtocol_TheHelpersVersion_ElevationAndTheDriver()
    {
        var (pipe, run) = StartHelper(FakeTree.Pc());
        await using (var client = await Connect(pipe))
        {
            var hello = await client.HelloAsync(Cancel);

            Assert.Equal(new HelloAnswer(HelperProtocol.Version, "2.0.0-test", false, "unknown"), hello);
        }

        Assert.Equal(HelperExit.PipeClosed, await run.WaitAsync(Patience, Cancel));
    }

    [Fact]
    public async Task TheCatalogue_CopiesEachIdentifierExactly_In1xsOrder_WithItsUnit()
    {
        var tree = FakeTree.Pc();
        // Identifiers no rule rebuilds: LibreHardwareMonitor's own forms, a GUID, spaces and letters past ASCII.
        tree.Hardware.Add(new("/hdd/0 (Ä)", "Disk Ä", "Storage", 7, null));
        tree.Sensors.Add(new("/hdd/0 (Ä)/temperature/0/", "Température", "Temperature", 0, "/hdd/0 (Ä)", 30f, 30f, 30f));
        var (pipe, _) = StartHelper(tree);
        await using var client = await Connect(pipe);
        await client.HelloAsync(Cancel);

        var catalogue = await client.CatalogueAsync(Cancel);

        Assert.Equal(1, catalogue.Version);
        Assert.Equal(
            [
                // By hardware type (Motherboard 0, SuperIO 1, Cpu 2, GpuNvidia 4, Storage 7, Network 8), then index;
                // the tree's own order among equals.
                "/lpc/nct6798d/0/voltage/0",
                "/lpc/nct6798d/0/fan/1",
                "/amdcpu/0/load/0",
                "/amdcpu/0/load/1",
                "/amdcpu/0/temperature/2",
                "/gpu-nvidia/0/temperature/0",
                "/hdd/0 (Ä)/temperature/0/",
                "/nvme/0/data/24",
                "/nic/{4D36E972-E325-11CE}/throughput/7",
            ],
            catalogue.Sensors.Select(s => s.Id));
        Assert.Equal(new HelperSensor("/lpc/nct6798d/0/fan/1", "Fan #2", "Fan", 1, "RPM", "/lpc/nct6798d/0"), catalogue.Sensors[1]);
        Assert.Equal(new HelperSensor("/hdd/0 (Ä)/temperature/0/", "Température", "Temperature", 0, "°C", "/hdd/0 (Ä)"), catalogue.Sensors[6]);
        Assert.Equal("B/s", catalogue.Sensors[8].Unit);
        Assert.Equal("GB", catalogue.Sensors[7].Unit);

        Assert.Equal(tree.Hardware.Select(h => new HelperHardware(h.Identifier, h.Name, h.Type, h.Parent)), catalogue.Hardware);
        Assert.Equal("/motherboard", catalogue.Hardware.Single(h => h.Id == "/lpc/nct6798d/0").Parent);
    }

    [Fact]
    public void ATreeWhoseParentsGoRoundInACycle_IsServedWithTheCycleCut()
    {
        var tree = new TreeSnapshot(
            [
                new("/x", "", "Cpu", 2, "/a"),
                new("/a", "", "Cpu", 2, "/b"),
                new("/b", "", "Cpu", 2, "/c"),
                new("/c", "", "Cpu", 2, "/a"),
                new("/d", "", "Cpu", 2, "/e"),
                new("/e", "", "Cpu", 2, "/d"),
            ],
            []);

        var built = HelperServer.BuildCatalogue(tree);

        // Each cycle loses the one parent that closed it on the walk from the first piece listed; the rest stay.
        Assert.Equal([("/x", "/a"), ("/a", "/b"), ("/b", "/c"), ("/c", null), ("/d", "/e"), ("/e", null)], built.Hardware.Select(h => (h.Id, h.Parent)));
        HelperCodec.EncodeAnswer(new CatalogueAnswer(1, built.Hardware, built.Sensors));
    }

    [Fact]
    public async Task ASample_KeepsNullAsNull_AndAValueThatIsNoNumberAsNull_InTheCataloguesOrder()
    {
        var (pipe, _) = StartHelper(FakeTree.Pc());
        await using var client = await Connect(pipe);
        await client.HelloAsync(Cancel);
        var catalogue = await client.CatalogueAsync(Cancel);

        var sample = await client.SampleAsync(Cancel);

        Assert.False(sample.CatalogueChanged);
        Assert.Equal(catalogue.Version, sample.Answer.Version);
        var at = catalogue.Sensors.Select((s, i) => (s.Id, i)).ToDictionary(p => p.Id, p => p.i);
        var vcore = at["/lpc/nct6798d/0/voltage/0"];
        Assert.Null(sample.Answer.Value[vcore]);
        Assert.Null(sample.Answer.Min[vcore]);
        Assert.Null(sample.Answer.Max[vcore]);
        var tctl = at["/amdcpu/0/temperature/2"];
        Assert.Null(sample.Answer.Value[tctl]); // NaN
        Assert.Equal(35f, sample.Answer.Min[tctl]);
        Assert.Null(sample.Answer.Max[tctl]); // infinity
        var gpu = at["/gpu-nvidia/0/temperature/0"];
        Assert.Equal((51.5f, 40f, 72.25f), (sample.Answer.Value[gpu], sample.Answer.Min[gpu], sample.Answer.Max[gpu]));
        Assert.Equal(1536f, sample.Answer.Value[at["/nic/{4D36E972-E325-11CE}/throughput/7"]]); // bytes per second, unchanged
    }

    [Fact]
    public async Task Configure_ReachesTheTree_AndTheNextSampleNamesANewCatalogue()
    {
        var tree = FakeTree.Pc();
        var (pipe, _) = StartHelper(tree);
        await using var client = await Connect(pipe);
        await client.HelloAsync(Cancel);
        var first = await client.CatalogueAsync(Cancel);
        Assert.Equal([(HelperProtocol.DefaultStorage, HelperProtocol.DefaultStorageInterval)], tree.Configured); // opened at the defaults until told

        Assert.Equal(new ConfigureAnswer(false, 120), await client.ConfigureAsync(false, 120, Cancel));
        Assert.Equal((false, 120), tree.Configured[^1]);

        var sample = await client.SampleAsync(Cancel);
        Assert.True(sample.CatalogueChanged);
        Assert.Equal(first.Version + 1, sample.Answer.Version);
        var second = await client.CatalogueAsync(Cancel);
        Assert.Equal(sample.Answer.Version, second.Version);
        Assert.DoesNotContain(second.Sensors, s => s.Hw == "/nvme/0");
        Assert.Equal(second.Sensors.Count, sample.Answer.Value.Count);

        Assert.Equal(new ConfigureAnswer(true, 5), await client.ConfigureAsync(true, 5, Cancel));
        Assert.Equal((true, 5), tree.Configured[^1]);
        var third = await client.CatalogueAsync(Cancel);
        Assert.Equal(second.Version + 1, third.Version);
        Assert.Contains(third.Sensors, s => s.Hw == "/nvme/0");

        // The same list again keeps its version.
        Assert.False((await client.SampleAsync(Cancel)).CatalogueChanged);
        Assert.Equal(third.Version, (await client.CatalogueAsync(Cancel)).Version);
    }

    [Theory]
    [InlineData(true, 120)] // only the interval changes
    [InlineData(true, HelperProtocol.DefaultStorageInterval)] // nothing changes
    public async Task EveryReopen_NamesANewCatalogue_EvenWithTheSameSensors(bool storage, int interval)
    {
        var tree = FakeTree.Pc();
        var (pipe, _) = StartHelper(tree);
        await using var client = await Connect(pipe);
        await client.HelloAsync(Cancel);
        var first = await client.CatalogueAsync(Cancel);

        await client.ConfigureAsync(storage, interval, Cancel);

        var sample = await client.SampleAsync(Cancel);
        Assert.True(sample.CatalogueChanged);
        Assert.Equal(first.Version + 1, sample.Answer.Version);
        var second = await client.CatalogueAsync(Cancel);
        Assert.Equal(first.Version + 1, second.Version);
        Assert.Equal(first.Sensors, second.Sensors);
        Assert.Equal(first.Hardware, second.Hardware);
        Assert.False((await client.SampleAsync(Cancel)).CatalogueChanged);
    }

    [Fact]
    public async Task ATreeThatFails_IsAnsweredWithAnError_AndThePipeStaysOpen()
    {
        var tree = FakeTree.Pc();
        var (pipe, _) = StartHelper(tree);
        await using var client = await Connect(pipe);
        await client.HelloAsync(Cancel);
        await client.CatalogueAsync(Cancel);

        tree.Throw = true;
        var error = await Assert.ThrowsAsync<HelperErrorException>(() => client.SampleAsync(Cancel));
        Assert.Equal(ErrorAnswer.TreeFailed, error.Code);

        tree.Throw = false;
        Assert.False((await client.SampleAsync(Cancel)).CatalogueChanged);
    }

    [Theory]
    [InlineData("""{"type":"configure","storage":true,"storageInterval":30}""")]
    [InlineData("""{"type":"catalogue"}""")] // the first read opens the computer
    [InlineData("""{"type":"sample"}""")]
    public async Task AComputerThatFailsToOpen_IsAnsweredTreeFailed_ThenTheHelperStops_WithoutASecondOpen(string request)
    {
        var tree = FakeTree.Pc();
        tree.ThrowOnConfigure = true;
        var (pipe, run) = StartHelper(tree);
        await using var raw = await RawClient(pipe);
        await Send(raw, """{"type":"hello","protocol":1}""");
        await Receive(raw);

        await Send(raw, request);

        Assert.StartsWith("""{"type":"error","code":"treeFailed",""", await Receive(raw));
        Assert.Null(await Receive(raw));
        Assert.Equal(HelperExit.TreeFailed, await run.WaitAsync(Patience, Cancel));
        Assert.Single(tree.Configured); // never a second computer in one process
        Assert.Equal(0, tree.Updates);
    }

    [Fact]
    public async Task MoreSensorsThanTheCap_AreNeverSent()
    {
        var tree = new FakeTree();
        tree.Hardware.Add(new("h", "", "", 2, null));
        for (var i = 0; i <= HelperProtocol.MaxSensors; i++)
            tree.Sensors.Add(new($"{i}", "", "", 0, "h", 1f, 1f, 1f)); // short enough to fit a frame: the sensor cap refuses it
        var (pipe, _) = StartHelper(tree);
        await using var raw = await RawClient(pipe);
        await Send(raw, """{"type":"hello","protocol":1}""");
        await Receive(raw);

        await Send(raw, """{"type":"catalogue"}""");
        Assert.StartsWith("""{"type":"error","code":"tooLarge",""", await Receive(raw));
        await Send(raw, """{"type":"sample"}""");
        Assert.StartsWith("""{"type":"error","code":"tooLarge",""", await Receive(raw));
    }

    public static TheoryData<string, string> Refusals => new()
    {
        { """{"type":"catalogue"}""", "unexpected" }, // before hello
        { """{"type":"sample"}""", "unexpected" },
        { """{"type":"configure","storage":false,"storageInterval":30}""", "unexpected" },
        { """{"type":"hello","protocol":2}""", "protocol" }, // another version
        { """{"type":"hello","protocol":0}""", "protocol" },
        { """{"type":"setFan","value":100}""", "malformed" }, // no such message
        { """{"type":"hello","protocol":1,"path":"C:\\x"}""", "malformed" },
        { Nested(HelperProtocol.MaxDepth + 1, "hello"), "tooDeep" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task WhatTheHelperRefuses_IsAnsweredWithAnError_ThenThePipeCloses(string request, string code)
    {
        var tree = FakeTree.Pc();
        var (pipe, run) = StartHelper(tree);
        await using var raw = await RawClient(pipe);

        await Send(raw, request);

        Assert.StartsWith($$"""{"type":"error","code":"{{code}}",""", await Receive(raw));
        Assert.Null(await Receive(raw));
        Assert.Equal(HelperExit.Refused, await run.WaitAsync(Patience, Cancel));
        Assert.Equal(0, tree.Updates);
        Assert.Empty(tree.Configured);
    }

    [Theory]
    [InlineData("""{"type":"hello","protocol":1}""", "unexpected")] // hello twice
    [InlineData("""{"type":"configure","storage":true,"storageInterval":0}""", "malformed")]
    [InlineData("""{"type":"configure","storage":true,"storageInterval":301}""", "malformed")]
    [InlineData("""{"type":"error","code":"x","message":"y"}""", "malformed")]
    public async Task AfterHello_ItStillRefuses_ThenThePipeCloses(string request, string code)
    {
        var tree = FakeTree.Pc();
        var (pipe, run) = StartHelper(tree);
        await using var raw = await RawClient(pipe);
        await Send(raw, """{"type":"hello","protocol":1}""");
        Assert.StartsWith("""{"type":"hello","protocol":1,""", await Receive(raw));

        await Send(raw, request);

        Assert.StartsWith($$"""{"type":"error","code":"{{code}}",""", await Receive(raw));
        Assert.Null(await Receive(raw));
        Assert.Equal(HelperExit.Refused, await run.WaitAsync(Patience, Cancel));
        Assert.Empty(tree.Configured);
    }

    [Fact]
    public async Task AFrameOverTheCap_ClosesThePipe_UnreadAndUnanswered()
    {
        var (pipe, run) = StartHelper(FakeTree.Pc());
        await using var raw = await RawClient(pipe);
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header, HelperProtocol.MaxFrameBytes + 1);

        await raw.WriteAsync(header, Cancel);
        await raw.FlushAsync(Cancel);

        Assert.Null(await Receive(raw));
        Assert.Equal(HelperExit.Refused, await run.WaitAsync(Patience, Cancel));
    }

    [Fact]
    public async Task AFrameCutShort_ClosesThePipe()
    {
        var (pipe, run) = StartHelper(FakeTree.Pc());
        var raw = await RawClient(pipe);
        var frame = Frame("""{"type":"hello","protocol":1}""");

        await raw.WriteAsync(frame.AsMemory(0, frame.Length - 3), Cancel);
        await raw.FlushAsync(Cancel);
        await raw.DisposeAsync();

        Assert.Equal(HelperExit.Refused, await run.WaitAsync(Patience, Cancel));
    }
}
