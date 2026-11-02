using System.IO.Compression;
using System.Text;
using System.Text.Json;
using InfoPanel.Core.Tests.Sensors.HwInfo;
using InfoPanel.Sensors;
using InfoPanel.Sensors.HwInfo;
using InfoPanel.Sensors.Replay;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.Replay;

/// <summary>Recording what the HWiNFO source reads, scrubbing it, and replaying it anywhere.</summary>
public sealed class SensorRecordingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "infopanel-recording-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static HwInfoImageBuilder Pc(long pollTime, double cpu, string? host = null)
    {
        var prefix = host == null ? "" : $"[{host}] ";
        var b = new HwInfoImageBuilder { PollTime = pollTime };
        b.Sensors.Add(new HwInfoImageBuilder.Sensor(0xF0000300, 0, prefix + "CPU [#0]: AMD Ryzen 7 8845HS", "My renamed CPU on DESKTOP-OWNER"));
        b.AddSensor(0xF0000500, 0, prefix + "S.M.A.R.T.: CT1000P3PSSD8 (0000FAKE1234) [C:]");
        b.AddSensor(0xF0000501, 0, prefix + "AZW SER8 (ITE IT8613E)");
        b.Readings.Add(new HwInfoImageBuilder.Reading(0, 0x1000000, "CPU (Tctl/Tdie)", "°C", cpu, 33, 90, 40, UserName: "Tctl of DESKTOP-OWNER"));
        b.AddReading(1, 0x1000000, "Drive Temperature", "°C", 30);
        b.AddReading(2, 0x2000000, "CPU Fan", "RPM", 1200);
        return b;
    }

    private static readonly TimeSpan Interval = HwInfoSource.Interval;

    private (PolledSensorSource Source, FakeHwInfoMemory Memory, TimerTimeProvider Time, HwInfoRecorder Recorder) Recording(int rounds, bool scrub)
    {
        var memory = new FakeHwInfoMemory();
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 11, 1, 4, 53, 43, TimeSpan.Zero));
        var recorder = new HwInfoRecorder(_root, rounds, scrub, Interval, time.GetUtcNow());
        var source = HwInfoSource.Create(memory, time, observer: recorder);
        return (source, memory, time, recorder);
    }

    private static byte[] Gunzip(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var memory = new MemoryStream();
        gzip.CopyTo(memory);
        return memory.ToArray();
    }

    private string[] Files() =>
        Directory.GetFiles(Path.Combine(_root, "hwinfo")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    [Fact]
    public void TheRecorder_KeepsEachMappingsCopy_EachRound_ForItsRounds_ThenStops()
    {
        var (source, memory, time, recorder) = Recording(rounds: 3, scrub: false);
        var local = Pc(1000, 50);
        memory.Set(HwInfoSampler.LocalName, local.Build());
        memory.Set(HwInfoSampler.RemotePrefix + "1", Pc(1000, 60, "OFFICE-PC").Build());
        source.Open();
        memory.Remove(HwInfoSampler.RemotePrefix + "1");
        memory.Set(HwInfoSampler.LocalName, local.BuildDead());
        time.Advance(Interval);
        source.Poll();
        time.Advance(Interval);
        source.Poll();

        Assert.True(recorder.Finished);
        Assert.Equal(3, recorder.Rounds);
        // Round 0 (discovery) and 1 (the first poll) read both; round 2 found the local one DEAD and the remote's view
        // too (it keeps its last bytes, HWiNFO's DEAD not written there); round 3 isn't recorded.
        Assert.Equal(["000000.local.bin.gz", "000000.remote-1.bin.gz", "000001.local.bin.gz", "000001.remote-1.bin.gz", "000002.local.bin.gz", "000002.remote-1.bin.gz", "recorder.claim"], Files());
        Assert.Equal(local.Build(), Gunzip(Path.Combine(_root, "hwinfo", "000000.local.bin.gz")));
        Assert.Equal(local.BuildDead()[..4], Gunzip(Path.Combine(_root, "hwinfo", "000002.local.bin.gz")));

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "recording.json")));
        Assert.Equal("infopanel.sensor-recording/1", manifest.RootElement.GetProperty("format").GetString());
        Assert.Equal(500, manifest.RootElement.GetProperty("intervalMs").GetInt32());
        Assert.False(manifest.RootElement.GetProperty("scrubbed").GetBoolean());
    }

    [Fact]
    public void Scrubbing_ReplacesHostNamesSerialsAndThisPCsName_InEveryNameField_AndLeavesTheRest()
    {
        var scrubber = new HwInfoScrubber("desktop-owner");
        var first = Pc(1000, 50, "OFFICE-PC").Build();
        var second = Pc(1000, 50, "LAB-RIG").Build();
        var again = Pc(1002, 51, "OFFICE-PC").Build();
        var original = (byte[])first.Clone();
        scrubber.Scrub(first);
        scrubber.Scrub(second);
        scrubber.Scrub(again);

        Assert.Equal(HwInfoImageState.Ok, HwInfoLayout.ReadHeader(first, first.Length, out var h, out _));
        var read = HwInfoLayout.Read(first, h);
        Assert.Equal(["[PC1] CPU [#0]: AMD Ryzen 7 8845HS", "[PC1] S.M.A.R.T.: CT1000P3PSSD8 (SERIAL1) [C:]", "[PC1] AZW SER8 (ITE IT8613E)"], read.Sensors.Select(s => s.Name));
        Assert.Equal("[PC2] CPU [#0]: AMD Ryzen 7 8845HS", Read(second).Sensors[0].Name);
        Assert.Equal("[PC1] CPU [#0]: AMD Ryzen 7 8845HS", Read(again).Sensors[0].Name);

        // Nothing of the names is left anywhere in the bytes, in either encoding.
        foreach (var image in new[] { first, second, again })
        {
            foreach (var secret in new[] { "OFFICE-PC", "LAB-RIG", "0000FAKE1234", "DESKTOP-OWNER", "renamed" })
            {
                Assert.DoesNotContain(secret, Encoding.Latin1.GetString(image), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(secret, Encoding.UTF8.GetString(image), StringComparison.OrdinalIgnoreCase);
            }
        }

        // Ids, values and units are as they were: only name bytes changed.
        Assert.Equal(HwInfoLayout.Read(original, h).Readings.Select(r => (r.SensorIndex, r.Id, r.Unit, r.Value, r.Min, r.Max, r.Avg)),
            read.Readings.Select(r => (r.SensorIndex, r.Id, r.Unit, r.Value, r.Min, r.Max, r.Avg)));
        Assert.Equal(original.AsSpan(0, 48).ToArray(), first.AsSpan(0, 48).ToArray());
        Assert.Equal("CPU [#0]: THISPC", scrubber.ScrubName("CPU [#0]: DESKTOP-OWNER"));
        Assert.Equal("Drive (SN 12)", scrubber.ScrubName("Drive (SN 12)"));
        Assert.Equal("Board (ABCDEFGH)", scrubber.ScrubName("Board (ABCDEFGH)"));
    }

    private static HwInfoSnapshot Read(byte[] image)
    {
        Assert.Equal(HwInfoImageState.Ok, HwInfoLayout.ReadHeader(image, image.Length, out var h, out _));
        return HwInfoLayout.Read(image, h);
    }

    [Fact]
    public void ARecording_RecordedScrubbed_HoldsNoNameItWasGiven()
    {
        var (source, memory, _, _) = Recording(rounds: 2, scrub: true);
        memory.Set(HwInfoSampler.LocalName, Pc(1000, 50).Build());
        memory.Set(HwInfoSampler.RemotePrefix + "1", Pc(1000, 60, "OFFICE-PC").Build());
        source.Open();

        foreach (var file in Directory.GetFiles(Path.Combine(_root, "hwinfo")))
        {
            var text = Encoding.Latin1.GetString(Gunzip(file));
            Assert.DoesNotContain("OFFICE-PC", text, StringComparison.Ordinal);
            Assert.DoesNotContain("0000FAKE1234", text, StringComparison.Ordinal);
            Assert.DoesNotContain("renamed", text, StringComparison.Ordinal);
        }

        Assert.True(JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "recording.json"))).RootElement.GetProperty("scrubbed").GetBoolean());
    }

    [Fact]
    public void AReplay_ReadsWhatTheSourceRead_RoundAfterRound_Looping()
    {
        // Record four rounds of a PC whose CPU climbs, with a remote there for the first two only.
        var (source, memory, time, _) = Recording(rounds: 4, scrub: false);
        memory.Set(HwInfoSampler.LocalName, Pc(1000, 50).Build());
        memory.Set(HwInfoSampler.RemotePrefix + "1", Pc(1000, 70, "PC1").Build());
        source.Open();
        var cpu = SensorKey.HwInfo(-1, 0xF0000300, 0, 0x1000000);
        var remoteCpu = SensorKey.HwInfo(1, 0xF0000300, 0, 0x1000000);
        for (var round = 2; round < 4; round++)
        {
            memory.Set(HwInfoSampler.RemotePrefix + "1", Pc(1000, 70, "PC1").BuildDead());
            memory.Set(HwInfoSampler.LocalName, Pc(1000 + round, 50 + round).Build());
            time.Advance(Interval);
            source.Poll();
        }

        var replayTime = new TimerTimeProvider(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var replay = HwInfoReplay.Load(_root);
        Assert.Equal(4, replay.Rounds);
        var replayed = HwInfoSource.Create(replay, replayTime, observer: replay);
        replayed.Open();

        // Keys as recorded; the remote listed while its rounds last.
        Assert.Equal(["hwinfo:-1:4026532608:0:16777216", "hwinfo:-1:4026533120:0:16777216", "hwinfo:-1:4026533121:0:33554432",
            "hwinfo:1:4026532608:0:16777216", "hwinfo:1:4026533120:0:16777216", "hwinfo:1:4026533121:0:33554432"],
            replayed.GetSensors().Select(s => s.Key.ToString()));
        Assert.True(replayed.TryRead(remoteCpu, out var r));
        Assert.Equal(70, r.ValueNow);

        var values = new List<double>();
        for (var i = 0; i < 8; i++)
        {
            Assert.True(replayed.TryRead(cpu, out var reading));
            values.Add(reading.ValueNow);
            replayTime.Advance(Interval);
            replayed.Poll();
        }

        // Open read rounds 0 and 1; then 2, 3, and around again.
        Assert.Equal([50, 52, 53, 50, 50, 52, 53, 50], values);
        Assert.Equal(SensorStatus.Stale, replayed.GetStatus(remoteCpu));
    }

    [Fact]
    public void AFolderWithoutARecording_IsRefused_SayingWhy()
    {
        Assert.Contains("does not exist", SensorRecording.Check(_root));
        Directory.CreateDirectory(Path.Combine(_root, "hwinfo"));
        File.WriteAllText(Path.Combine(_root, "hwinfo", "notes.txt"), "x");
        Assert.Contains("no hwinfo/*.bin.gz", SensorRecording.Check(_root));
        Assert.Throws<InvalidDataException>(() => HwInfoReplay.Load(_root));
    }

    [Fact]
    public void AReplay_MappingMissingFromARound_IsGone_AndAnOpenViewOfItReadsDead()
    {
        var folder = Path.Combine(_root, "hwinfo");
        Directory.CreateDirectory(folder);
        void Write(int round, int remote, byte[] bytes)
        {
            using var file = File.Create(Path.Combine(folder, SensorRecording.FileName(round, remote)));
            using var gzip = new GZipStream(file, CompressionLevel.Fastest);
            gzip.Write(bytes);
        }

        var local = Pc(1000, 50).Build();
        var remote = Pc(1000, 70, "PC1").Build();
        Write(0, -1, local);
        Write(0, 1, remote);
        Write(1, -1, local);

        var replay = HwInfoReplay.Load(_root);
        using var view = replay.TryOpen(HwInfoSampler.RemotePrefix + "1");
        Assert.NotNull(view);
        Assert.Equal(remote.Length, view.Length);
        Assert.Null(replay.TryOpen(HwInfoSampler.RemotePrefix + "2"));
        Assert.Null(replay.TryOpen("Global\\Something else"));

        replay.RoundEnded();
        Assert.Null(replay.TryOpen(HwInfoSampler.RemotePrefix + "1"));
        Assert.Equal(4, view.Length);
        var head = new byte[4];
        view.Read(0, head);
        Assert.Equal(HwInfoImageState.Dead, HwInfoLayout.ReadHeader(head, 4, out _, out _));

        replay.RoundEnded();
        Assert.Equal(0, replay.Round);
        Assert.Equal(remote.Length, view.Length);
    }

    [Fact]
    public void Scrubbing_CatchesSerialsAddressesAndNames_ByPattern()
    {
        var scrubber = new HwInfoScrubber("DESKTOP-OWNER", "jdoe");
        Assert.Equal("Drive: SSD (SERIAL1)", scrubber.ScrubName("Drive: SSD (AB12CD34)"));
        Assert.Equal("Drive (SN SERIAL2)", scrubber.ScrubName("Drive (SN 123456)"));
        Assert.Equal("Disk S/N: SERIAL3", scrubber.ScrubName("Disk S/N: X1Y2Z3W4"));
        Assert.Equal("Board Serial Number SERIAL4", scrubber.ScrubName("Board Serial Number 99887766"));
        Assert.Equal("Drive: SSD (SERIAL1) again", scrubber.ScrubName("Drive: SSD (AB12CD34) again"));
        Assert.Equal("NIC IP1 / IP2", scrubber.ScrubName("NIC 192.168.1.20 / fe80::1c2d:3e4f:5a6b:7c8d"));
        Assert.Equal("NIC MAC1 MAC1 MAC2", scrubber.ScrubName("NIC 00:1A:2B:3C:4D:5E 00-1a-2b-3c-4d-5e 10:20:30:40:50:60"));
        Assert.Equal("THISPC of THISUSER", scrubber.ScrubName("desktop-owner of JDoe"));
        Assert.Equal("[PC1] CPU on PC1", scrubber.ScrubName("[OFFICE-PC] CPU on office-pc"));
        // Not identifying: model names, short or letter-only tokens, ordinary labels.
        foreach (var plain in new[] { "S.M.A.R.T.: CT1000P3PSSD8 [C:]", "Drive (SN 12)", "Board (ABCDEFGH)", "AZW SER8 (ITE IT8613E)", "CPU (Tctl/Tdie)", "Core 0 T0 Effective Clock", "Memory Clock", "°C" })
            Assert.Equal(plain, scrubber.ScrubName(plain));
    }

    [Fact]
    public void Scrubbing_AnImage_RunsNamesAndUnits_InBothEncodings_ThroughTheScrubber_AndBlanksTheUsersNames()
    {
        var b = Pc(1000, 50, "OFFICE-PC");
        b.Readings.Add(new HwInfoImageBuilder.Reading(2, 0x3000000, "Link 10.0.0.7", "(SN 0000FAKE9876)", 1, 1, 1, 1));
        var image = b.Build();
        Assert.True(new HwInfoScrubber("desktop-owner", "jdoe").Scrub(image));

        ScrubCheck.AssertScrubbed(image, "the image");
        var read = Read(image);
        Assert.Equal("Link IP1", read.Readings[3].Name);
        // The unit field is 16 bytes, so its serial is cut; what is left of it is an alias.
        Assert.StartsWith("(SN SERIAL", read.Readings[3].Unit, StringComparison.Ordinal);
        Assert.StartsWith("(SN SERIAL", HwInfoLayout.Windows1252(HwInfoLayout.ReadingElement(image, Header(image), 3).Slice(268, 16)), StringComparison.Ordinal);
        foreach (var secret in new[] { "renamed", "DESKTOP-OWNER", "OFFICE-PC", "0000FAKE", "10.0.0.7" })
        {
            Assert.DoesNotContain(secret, Encoding.Latin1.GetString(image), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(image), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static HwInfoHeader Header(byte[] image)
    {
        Assert.Equal(HwInfoImageState.Ok, HwInfoLayout.ReadHeader(image, image.Length, out var h, out _));
        return h;
    }

    [Theory]
    [InlineData(264, 316, 0, 0, 0)]
    [InlineData(300, 444, 8, 16, 0)]
    [InlineData(391, 459, 0, 4, 0)]
    [InlineData(392, 460, 12, 0, 0)]
    [InlineData(400, 480, 4, 20, 0)]
    public void Scrubbing_KeepsOnlyTheRetainedFields_AtAnyElementSize_ZeroingTheUsersNamesExtensionsAndGaps(int sensorSize, int readingSize, int gapAfterHeader, int gapBetween, int tail)
    {
        var image = ScrubCheck.OddImage(sensorSize, readingSize, gapAfterHeader, gapBetween, tail, "DESKTOP-OWNER");
        Assert.True(new HwInfoScrubber("desktop-owner").Scrub(image));

        ScrubCheck.AssertScrubbed(image, $"sizes {sensorSize}/{readingSize}");
        var read = Read(image);
        Assert.Equal(["Drive 0 on THISPC (SERIAL1)", "Drive 1 on THISPC (SERIAL2)"], read.Sensors.Select(s => s.Name));
        Assert.Equal([("Temperature 0", "C", 40d), ("Temperature 1", "C", 41d)], read.Readings.Select(r => (r.Name, r.Unit, r.Value)));
        Assert.DoesNotContain((byte)'Z', image);
    }

    [Fact]
    public void AnImageThatDoesntRead_IsReportedAndNeverWritten_AndDeadKeepsOnlyItsSignature()
    {
        var scrubber = new HwInfoScrubber("desktop-owner");
        var whole = Pc(1000, 50, "OFFICE-PC").Build();
        var torn = whole[..^100];
        Assert.False(scrubber.Scrub(torn));
        var unknown = (byte[])whole.Clone();
        unknown[0] = (byte)'X';
        Assert.False(scrubber.Scrub(unknown));
        var dead = Pc(1000, 50, "OFFICE-PC").BuildDead();
        Assert.True(scrubber.Scrub(dead));
        Assert.All(dead[4..], b => Assert.Equal(0, b));

        // The recorder, scrubbing, keeps no file of a frame that doesn't read.
        var recorder = new HwInfoRecorder(_root, 3, scrub: true, Interval, DateTimeOffset.UnixEpoch);
        recorder.Copied(-1, torn);
        recorder.Copied(1, whole);
        recorder.RoundEnded();
        Assert.Equal(["000000.remote-1.bin.gz", "recorder.claim"], Files());
        ScrubCheck.AssertScrubbed(Gunzip(Path.Combine(_root, "hwinfo", "000000.remote-1.bin.gz")), "remote 1");
    }

    [Fact]
    public void AReplay_ReadsOnlyRegularFiles_NeverWaitingOnAFifo()
    {
        Assert.SkipUnless(Fifo.Supported, "Windows has no FIFOs in folders.");
        var folder = Path.Combine(_root, "hwinfo");
        Directory.CreateDirectory(folder);
        var fifo = Path.Combine(folder, SensorRecording.FileName(0, -1));
        Fifo.Create(fifo);

        var error = Fifo.Within(5, () => Assert.Throws<InvalidDataException>(() => HwInfoReplay.Load(_root)), fifo);
        Assert.Contains("000000.local.bin.gz", error.Message, StringComparison.Ordinal);
    }

    private void WriteFrame(int round, int remote, byte[] bytes)
    {
        var folder = Path.Combine(_root, "hwinfo");
        Directory.CreateDirectory(folder);
        using var file = File.Create(Path.Combine(folder, SensorRecording.FileName(round, remote)));
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        gzip.Write(bytes);
    }

    [Fact]
    public void AReplay_CountsFilesFramesAndMappingsPerRound_BeforeItReadsThem()
    {
        var limits = new SensorRecordingLimits(MaxRounds: 100, MaxFrames: 4, MaxMappingsPerRound: 2, MaxBytes: 1 << 20);
        var image = Pc(1000, 50).Build();
        for (var round = 0; round < 4; round++)
            WriteFrame(round, -1, image);
        Assert.Equal(4, HwInfoReplay.Load(_root, limits: limits).Rounds);

        // Empty frames cost no bytes, but count as frames.
        WriteFrame(4, -1, []);
        Assert.Contains("frames", Assert.Throws<InvalidDataException>(() => HwInfoReplay.Load(_root, limits: limits)).Message, StringComparison.Ordinal);
        File.Delete(Path.Combine(_root, "hwinfo", SensorRecording.FileName(4, -1)));

        // Three mappings in one round.
        WriteFrame(0, 1, image);
        WriteFrame(0, 2, []);
        var many = new SensorRecordingLimits(100, 100, 2, 1 << 20);
        Assert.Contains("mappings", Assert.Throws<InvalidDataException>(() => HwInfoReplay.Load(_root, limits: many)).Message, StringComparison.Ordinal);
        File.Delete(Path.Combine(_root, "hwinfo", SensorRecording.FileName(0, 2)));
        File.Delete(Path.Combine(_root, "hwinfo", SensorRecording.FileName(0, 1)));

        // Anything else in the folder counts towards the listing's budget too.
        for (var i = 0; i < 8; i++)
            File.WriteAllText(Path.Combine(_root, "hwinfo", $"note-{i}.txt"), "x");
        Assert.Contains("files", Assert.Throws<InvalidDataException>(() => HwInfoReplay.Load(_root, limits: limits)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARecording_KeepsItsRoundCount_SoTrailingRoundsWithNoMappingAreReplayed()
    {
        var (source, memory, time, recorder) = Recording(rounds: 8, scrub: false);
        var local = Pc(1000, 50);
        memory.Set(HwInfoSampler.LocalName, local.Build());
        source.Open();
        memory.Set(HwInfoSampler.LocalName, local.BuildDead());
        time.Advance(Interval);
        source.Poll();
        memory.Remove(HwInfoSampler.LocalName);
        for (var i = 0; i < 5; i++)
        {
            time.Advance(Interval);
            source.Poll();
        }

        Assert.True(recorder.Finished);
        using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "recording.json"))))
            Assert.Equal(8, manifest.RootElement.GetProperty("rounds").GetInt32());
        Assert.Equal(["000000.local.bin.gz", "000001.local.bin.gz", "000002.local.bin.gz", "recorder.claim"], Files());

        // Replayed: live for two rounds, then HWiNFO is gone for six, then around again.
        var replay = HwInfoReplay.Load(_root);
        Assert.Equal(8, replay.Rounds);
        var replayTime = new TimerTimeProvider(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var replayed = HwInfoSource.Create(replay, replayTime, observer: replay);
        replayed.Open();
        var cpu = SensorKey.HwInfo(-1, 0xF0000300, 0, 0x1000000);
        var statuses = new List<SensorStatus>();
        for (var i = 0; i < 8; i++)
        {
            replayTime.Advance(Interval);
            replayed.Poll();
            statuses.Add(replayed.GetStatus(cpu));
        }

        Assert.Equal([SensorStatus.Stale, SensorStatus.Stale, SensorStatus.Stale, SensorStatus.Stale, SensorStatus.Stale, SensorStatus.Stale, SensorStatus.Stale, SensorStatus.Live], statuses);
    }

    [Fact]
    public void TheRecorder_StopsCleanlyAtTheReplaysBudget_AndTheRecordingReplays()
    {
        var image = Pc(1000, 50).Build();
        var remote = Pc(1000, 60, "OFFICE-PC").Build();
        var limits = new SensorRecordingLimits(MaxRounds: 100, MaxFrames: 100, MaxMappingsPerRound: 4, MaxBytes: (image.Length + remote.Length) * 2 + 10);
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 11, 1, 4, 53, 43, TimeSpan.Zero));
        var memory = new FakeHwInfoMemory();
        var recorder = new HwInfoRecorder(_root, 50, scrub: false, Interval, time.GetUtcNow(), time: time, limits: limits);
        var source = HwInfoSource.Create(memory, time, observer: recorder);
        memory.Set(HwInfoSampler.LocalName, image);
        memory.Set(HwInfoSampler.RemotePrefix + "1", remote);
        source.Open();
        for (var i = 0; i < 4; i++)
        {
            time.Advance(Interval);
            source.Poll();
        }

        Assert.True(recorder.Finished);
        Assert.Equal(2, recorder.Rounds);
        Assert.Equal(["000000.local.bin.gz", "000000.remote-1.bin.gz", "000001.local.bin.gz", "000001.remote-1.bin.gz", "recorder.claim"], Files());
        using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "recording.json"))))
            Assert.Equal(2, manifest.RootElement.GetProperty("rounds").GetInt32());
        Assert.Equal(2, HwInfoReplay.Load(_root, limits: limits).Rounds);

        // So does one with more mappings in a round than a replay takes.
        var other = Path.Combine(_root, "other");
        var narrow = new HwInfoRecorder(other, 50, scrub: false, Interval, time.GetUtcNow(), limits: new SensorRecordingLimits(100, 100, 1, 1 << 20));
        narrow.Copied(-1, image);
        narrow.RoundEnded();
        narrow.Copied(-1, image);
        narrow.Copied(1, remote);
        narrow.RoundEnded();
        Assert.True(narrow.Finished);
        Assert.Equal(1, narrow.Rounds);
        Assert.Equal(1, HwInfoReplay.Load(other).Rounds);
    }

    [Fact]
    public void RecordSeconds_IsElapsedTime_NotARoundCount()
    {
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 11, 1, 4, 53, 43, TimeSpan.Zero));
        var memory = new FakeHwInfoMemory();
        var recorder = new HwInfoRecorder(_root, SensorRecording.MaxRounds, scrub: false, Interval, time.GetUtcNow(), duration: TimeSpan.FromSeconds(1), time: time);
        var source = HwInfoSource.Create(memory, time, observer: recorder);
        memory.Set(HwInfoSampler.LocalName, Pc(1000, 50).Build());

        // Discovery and the first poll take two rounds at once: no time has passed.
        source.Open();
        Assert.False(recorder.Finished);
        time.Advance(Interval);
        source.Poll();
        Assert.False(recorder.Finished);
        time.Advance(Interval);
        source.Poll();
        Assert.True(recorder.Finished);
        Assert.Equal(4, recorder.Rounds);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "recording.json")));
        Assert.Equal(4, manifest.RootElement.GetProperty("rounds").GetInt32());
    }

    [Fact]
    public void AStop_DuringAnOutage_WritesTheRoundsRecordedSoFar_SoTheOutageIsReplayed()
    {
        var (source, memory, time, recorder) = Recording(rounds: 1000, scrub: false);
        var local = Pc(1000, 50);
        memory.Set(HwInfoSampler.LocalName, local.Build());
        source.Open();
        memory.Set(HwInfoSampler.LocalName, local.BuildDead());
        time.Advance(Interval);
        source.Poll();
        memory.Remove(HwInfoSampler.LocalName);
        for (var i = 0; i < 4; i++)
        {
            time.Advance(Interval);
            source.Poll();
        }

        // The engine stops (its sources stop, their samplers close) long before the recording's end.
        source.Dispose();
        Assert.True(recorder.Finished);
        Assert.Equal(7, recorder.Rounds);
        using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "recording.json"))))
        {
            Assert.Equal(7, manifest.RootElement.GetProperty("rounds").GetInt32());
            Assert.True(manifest.RootElement.TryGetProperty("ended", out _));
        }

        Assert.Equal(["000000.local.bin.gz", "000001.local.bin.gz", "000002.local.bin.gz", "recorder.claim"], Files());
        Assert.Equal(7, HwInfoReplay.Load(_root).Rounds);
    }

    [Fact]
    public void ARecordingThatCantWriteItsManifest_StopsAlone_AndReadingsReorderedThatRound_KeepTheirValues()
    {
        var (source, memory, time, recorder) = Recording(rounds: 3, scrub: false);
        var pc = Pc(1000, 50);
        memory.Set(HwInfoSampler.LocalName, pc.Build());
        source.Open();
        var drive = SensorKey.HwInfo(-1, 0xF0000500, 0, 0x1000000);
        var fan = SensorKey.HwInfo(-1, 0xF0000501, 0, 0x2000000);
        var manifest = Path.Combine(_root, "recording.json");
        // A folder where the manifest goes: no user can write it there, an administrator or root neither (a read-only
        // file is no failure to them).
        File.Delete(manifest);
        Directory.CreateDirectory(manifest);

        // The recording's last round: two readings swap places, and its manifest can't be written.
        (pc.Readings[1], pc.Readings[2]) = (pc.Readings[2], pc.Readings[1]);
        for (var i = 0; i < 3; i++)
        {
            pc.PollTime++;
            memory.Set(HwInfoSampler.LocalName, pc.Build());
            time.Advance(Interval);
            source.Poll();
            Assert.True(recorder.Finished);
            Assert.True(source.TryRead(drive, out var d));
            Assert.Equal(30, d.ValueNow);
            Assert.True(source.TryRead(fan, out var f));
            Assert.Equal(1200, f.ValueNow);
            Assert.Equal(SensorStatus.Live, source.GetStatus(fan));
        }

        Assert.Equal(3, recorder.Rounds);
        // The recording ended, and writing its rounds into the manifest failed: no manifest file was written (a write
        // that succeeded leaves one, with the rounds), and the folder in its place is still there, empty.
        Assert.False(File.Exists(manifest), "The manifest was written: its write didn't fail.");
        Assert.True(Directory.Exists(manifest));
        Assert.Empty(Directory.EnumerateFileSystemEntries(manifest));
    }

    [Fact]
    public void ASecondRecorder_OnTheSameFolder_RecordsNothing_TakesNothingOver_AndDeletesNothing()
    {
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 11, 1, 4, 53, 43, TimeSpan.Zero));
        var first = new HwInfoRecorder(_root, 1000, scrub: false, Interval, time.GetUtcNow(), time: time);
        // Another engine passed the folder's check before either wrote a frame, and its rounds come first.
        var second = new HwInfoRecorder(_root, 1000, scrub: false, Interval, time.GetUtcNow(), time: time);
        var theirs = Pc(1000, 70).Build();
        second.Copied(-1, theirs);
        second.RoundEnded();

        var mine = Pc(1000, 50).Build();
        var next = Pc(1001, 51).Build();
        first.Copied(-1, mine);
        first.RoundEnded();
        first.Copied(-1, next);
        first.RoundEnded();
        second.Copied(-1, theirs);
        second.RoundEnded();
        second.Stopped();

        // The first recorder's frames are its own, none taken or ended by the second's, which recorded nothing.
        Assert.Equal(mine, Gunzip(Path.Combine(_root, "hwinfo", "000000.local.bin.gz")));
        Assert.Equal(next, Gunzip(Path.Combine(_root, "hwinfo", "000001.local.bin.gz")));
        Assert.Equal(["000000.local.bin.gz", "000001.local.bin.gz", "recorder.claim"], Files());
        Assert.False(first.Finished);
        Assert.Equal(2, first.Rounds);
        Assert.True(second.Finished);
        Assert.Equal(0, second.Rounds);

        // The manifest is the first's alone, and the recording replays with its claim in the folder.
        first.Stopped();
        Assert.Equal(2, SensorRecording.RoundsOf(_root));
        Assert.Equal(2, HwInfoReplay.Load(_root).Rounds);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void AFolderThatCantBeClaimed_GivesAFinishedRecorder_AndNeverThrows()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes");
        // A hwinfo/ folder that can be read but not written to: creating the claim is denied (UnauthorizedAccessException).
        var folder = Path.Combine(_root, "hwinfo");
        Directory.CreateDirectory(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var canWrite = false;
            try
            {
                File.WriteAllText(Path.Combine(folder, "probe"), "x");
                canWrite = true;
                File.Delete(Path.Combine(folder, "probe"));
            }
            catch (UnauthorizedAccessException)
            {
            }
            Assert.SkipWhen(canWrite, "Running with rights that ignore the folder's mode");

            var recorder = new HwInfoRecorder(_root, 1000, scrub: false, Interval, DateTimeOffset.UnixEpoch);
            recorder.Copied(-1, Pc(1000, 50).Build());
            recorder.RoundEnded();
            recorder.Stopped();

            Assert.True(recorder.Finished);
            Assert.Equal(0, recorder.Rounds);
            Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void ARecordingFolderThatRefusesItsManifest_GivesAFinishedRecorder_AndNeverThrows()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes");
        // hwinfo/ can be written (the claim succeeds), the recording's own folder can't: its manifest is denied.
        var folder = Path.Combine(_root, "hwinfo");
        Directory.CreateDirectory(folder);
        File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var canWrite = false;
            try
            {
                File.WriteAllText(Path.Combine(_root, "probe"), "x");
                canWrite = true;
                File.Delete(Path.Combine(_root, "probe"));
            }
            catch (UnauthorizedAccessException)
            {
            }
            Assert.SkipWhen(canWrite, "Running with rights that ignore the folder's mode");

            var recorder = new HwInfoRecorder(_root, 1000, scrub: false, Interval, DateTimeOffset.UnixEpoch);
            recorder.Copied(-1, Pc(1000, 50).Build());
            recorder.RoundEnded();
            recorder.Stopped();

            Assert.True(recorder.Finished);
            Assert.Equal(0, recorder.Rounds);
            Assert.Equal([SensorRecording.ClaimName], Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName));
            Assert.False(File.Exists(Path.Combine(_root, SensorRecording.ManifestName)));
        }
        finally
        {
            File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void AFrameNameAnotherFileHolds_EndsTheRecording_AndOnlyTheRoundsOwnFilesAreTakenBack()
    {
        var recorder = new HwInfoRecorder(_root, 1000, scrub: false, Interval, DateTimeOffset.UnixEpoch);
        recorder.Copied(-1, Pc(1000, 50).Build());
        recorder.RoundEnded();

        // Round 1's remote file is there already, not the recorder's: it is left as it is, and so is round 0.
        var stray = Path.Combine(_root, "hwinfo", SensorRecording.FileName(1, 1));
        File.WriteAllText(stray, "someone else's");
        recorder.Copied(-1, Pc(1001, 51).Build());
        recorder.Copied(1, Pc(1001, 61, "OFFICE-PC").Build());
        recorder.RoundEnded();

        Assert.True(recorder.Finished);
        Assert.Equal(1, recorder.Rounds);
        Assert.Equal("someone else's", File.ReadAllText(stray));
        Assert.Equal(["000000.local.bin.gz", "000001.remote-1.bin.gz", "recorder.claim"], Files());
        Assert.Equal(1, SensorRecording.RoundsOf(_root));
    }

    [Fact]
    public void AReplay_LoadsARecording_WithOrWithoutItsClaim()
    {
        var recorder = new HwInfoRecorder(_root, 2, scrub: false, Interval, DateTimeOffset.UnixEpoch);
        recorder.Copied(-1, Pc(1000, 50).Build());
        recorder.RoundEnded();
        recorder.Copied(-1, Pc(1001, 51).Build());
        recorder.RoundEnded();
        Assert.True(recorder.Finished);
        var claim = Path.Combine(_root, "hwinfo", "recorder.claim");
        Assert.True(File.Exists(claim));
        Assert.True(SensorRecording.HoldsHwInfo(_root));
        Assert.Null(SensorRecording.Check(_root));
        Assert.Equal(2, HwInfoReplay.Load(_root).Rounds);

        // A recording made before the claim, or by hand, has none.
        File.Delete(claim);
        Assert.Equal(2, HwInfoReplay.Load(_root).Rounds);
    }

    [Fact]
    public void AReplay_RefusesMoreDeclaredRoundsThanItTakes()
    {
        var limits = new SensorRecordingLimits(MaxRounds: 10, MaxFrames: 100, MaxMappingsPerRound: 2, MaxBytes: 1 << 20);
        WriteFrame(0, -1, Pc(1000, 50).Build());
        SensorRecording.WriteManifest(_root, Interval, scrubbed: false, DateTimeOffset.UnixEpoch, rounds: 11);
        var error = Assert.Throws<InvalidDataException>(() => HwInfoReplay.Load(_root, limits: limits));
        Assert.Contains("11 rounds, over 10", error.Message, StringComparison.Ordinal);

        SensorRecording.WriteManifest(_root, Interval, scrubbed: false, DateTimeOffset.UnixEpoch, rounds: 10);
        Assert.Equal(10, HwInfoReplay.Load(_root, limits: limits).Rounds);
    }

    [Fact]
    public void AReplay_RefusesAFrameBeyondTheDeclaredRounds()
    {
        var image = Pc(1000, 50).Build();
        WriteFrame(0, -1, image);
        WriteFrame(2, 1, image);
        SensorRecording.WriteManifest(_root, Interval, scrubbed: false, DateTimeOffset.UnixEpoch, rounds: 2);
        var error = Assert.Throws<InvalidDataException>(() => HwInfoReplay.Load(_root));
        Assert.Contains("'000002.remote-1.bin.gz' is past the recording's 2 rounds", error.Message, StringComparison.Ordinal);

        SensorRecording.WriteManifest(_root, Interval, scrubbed: false, DateTimeOffset.UnixEpoch, rounds: 3);
        Assert.Equal(3, HwInfoReplay.Load(_root).Rounds);
    }

    /// <summary>An image whose only strings are these, with no user names: otherwise clean, as a scrubbed one is.</summary>
    private static byte[] Clean(string sensorName, string label = "Drive Temperature", string unit = "°C")
    {
        var b = new HwInfoImageBuilder();
        b.Sensors.Add(new HwInfoImageBuilder.Sensor(0xF0000500, 0, sensorName, UserName: ""));
        b.Readings.Add(new HwInfoImageBuilder.Reading(0, 0x1000000, label, unit, 30, 25, 40, 31, UserName: ""));
        return b.Build();
    }

    [Fact]
    public void Scrubbing_CatchesASerialCutOffByTheFieldsEnd_InThe128ByteNamesAndThe16ByteUnits()
    {
        var scrubber = new HwInfoScrubber();
        // Each field filled to its end, the serial's closing parenthesis cut off (no NUL in any of them).
        var name = new string('N', 117) + "(AB12CD34EF";
        var label = new string('L', 117) + "(CD34EF56GH";
        Assert.Equal(128, name.Length);
        var image = Clean(name, label, "(AB12CD34EF56GH78)");
        var e = HwInfoLayout.ReadingElement(image, Header(image), 0);
        Assert.Equal("(AB12CD34EF56GH7", HwInfoLayout.Windows1252(e.Slice(268, 16)));
        Assert.Equal("(AB12CD34EF56GH7", HwInfoLayout.Utf8(e.Slice(444, 16)));

        Assert.True(scrubber.Scrub(image));
        ScrubCheck.AssertScrubbed(image, "the image");
        var read = Read(image);
        Assert.Equal(new string('N', 117) + "(SERIAL1", read.Sensors[0].Name);
        Assert.Equal(new string('L', 117) + "(SERIAL2", read.Readings[0].Name);
        e = HwInfoLayout.ReadingElement(image, Header(image), 0);
        Assert.Equal("(SERIAL3", HwInfoLayout.Windows1252(e.Slice(268, 16)));
        Assert.Equal("(SERIAL3", HwInfoLayout.Utf8(e.Slice(444, 16)));
        foreach (var secret in new[] { "AB12CD34", "CD34EF56" })
        {
            Assert.DoesNotContain(secret, Encoding.Latin1.GetString(image), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(image), StringComparison.Ordinal);
        }

        // Any string that ends inside a serial-like run is taken to be cut there; a letter-only or short run isn't one.
        Assert.Equal("Drive (SERIAL4", scrubber.ScrubName("Drive (X9Y8Z7W6"));
        Assert.Equal("Board (ABCDEFGH", scrubber.ScrubName("Board (ABCDEFGH"));
        Assert.Equal("Drive (SN 12", scrubber.ScrubName("Drive (SN 12"));
    }

    private static string? Failure(Action check)
    {
        try
        {
            check();
            return null;
        }
        catch (Xunit.Sdk.XunitException ex)
        {
            return ex.Message;
        }
    }

    [Fact]
    public void TheFixtureCheck_RefusesASerialCutOffByTheFieldsEnd_InA128ByteName_AndIn16ByteUnits()
    {
        Assert.Contains("serial-like 'AB12CD34EF'", ScrubCheck.Problems(new string('N', 117) + "(AB12CD34EF"));
        Assert.Contains("serial-like 'AB12CD34EF56GH7'", ScrubCheck.Problems("(AB12CD34EF56GH7"));

        // The same image passes with each string closed, so each failure below is that string's.
        Assert.Null(Failure(() => ScrubCheck.AssertScrubbed(Clean("Drive (SERIAL1)", unit: "(SERIAL2)"), "clean")));
        var name = Failure(() => ScrubCheck.AssertScrubbed(Clean(new string('N', 117) + "(AB12CD34EF"), "the name"));
        Assert.Contains("sensor 0 name", name, StringComparison.Ordinal);
        Assert.Contains("serial-like 'AB12CD34EF'", name, StringComparison.Ordinal);
        var unit = Failure(() => ScrubCheck.AssertScrubbed(Clean("Drive", unit: "(AB12CD34EF56GH78)"), "the unit"));
        Assert.Contains("reading 0 unit", unit, StringComparison.Ordinal);
        Assert.Contains("serial-like 'AB12CD34EF56GH7'", unit, StringComparison.Ordinal);

        // The UTF-8 unit alone (its Windows-1252 twin clean).
        var b = new HwInfoImageBuilder();
        b.Sensors.Add(new HwInfoImageBuilder.Sensor(0xF0000500, 0, "Drive", UserName: ""));
        b.Readings.Add(new HwInfoImageBuilder.Reading(0, 0x1000000, "Drive Temperature", "(AB12CD34EF56GH78)", 30, 25, 40, 31, UserName: "")
        {
            UnitBytes1252 = HwInfoImageBuilder.Raw(16, "C"),
        });
        var utf8 = Failure(() => ScrubCheck.AssertScrubbed(b.Build(), "the UTF-8 unit"));
        Assert.Contains("reading 0 UTF-8 unit", utf8, StringComparison.Ordinal);
        Assert.Contains("serial-like 'AB12CD34EF56GH7'", utf8, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFixtureCheck_RefusesMachineNamesUserNamesAndRemoteNames()
    {
        // Codex's case: an otherwise clean image naming a PC and its user, though the scrubber was given both.
        var failure = Failure(() => ScrubCheck.AssertScrubbed(Clean("CPU on DESKTOP-OWNER by jdoe"), "the image", ["jdoe"]));
        Assert.Contains("the machine name 'DESKTOP-OWNER'", failure, StringComparison.Ordinal);
        Assert.Contains("the name 'jdoe'", failure, StringComparison.Ordinal);

        // Windows' default machine names, whatever their case.
        foreach (var machine in new[] { "DESKTOP-AB12CDE", "LAPTOP-9XY8ZW7Q", "WIN-ABCDEF12345", "desktop-ab12cde" })
            Assert.Contains($"the machine name '{machine}'", ScrubCheck.Problems($"CPU on {machine}"));

        // A bracketed host other than the aliases.
        Assert.Contains("the host 'OFFICE-PC'", ScrubCheck.Problems("[OFFICE-PC] CPU [#0]: AMD Ryzen 7 8845HS"));
        Assert.Contains("the host 'SERIAL1'", ScrubCheck.Problems("[SERIAL1] CPU"));

        // A name the test passes in, bracketed or not, whole words, any case.
        Assert.Contains("the name 'OFFICE-PC'", ScrubCheck.Problems("CPU on office-pc", ["OFFICE-PC"]));
        Assert.Contains("the name 'jdoe'", ScrubCheck.Problems("Drive of JDOE", ["jdoe"]));

        // What a scrub leaves passes.
        foreach (var clean in new[] { "[PC1] CPU on PC1", "[PC2] CPU", "[THISPC] CPU", "CPU on THISPC by THISUSER", "CPU [#0]: AMD Ryzen 7 8845HS", "Windows Hello", "jdoe2 jdoex" })
            Assert.Empty(ScrubCheck.Problems(clean, ["jdoe", "OFFICE-PC"]));
    }

    [Fact]
    public void FileNames_SayTheRoundAndTheMapping()
    {
        Assert.Equal("000007.local.bin.gz", SensorRecording.FileName(7, -1));
        Assert.Equal("000123.remote-2.bin.gz", SensorRecording.FileName(123, 2));
        Assert.True(SensorRecording.TryParseFileName("000123.remote-2.bin.gz", out var round, out var remote));
        Assert.Equal((123, 2), (round, remote));
        Assert.True(SensorRecording.TryParseFileName("000000.local.bin.gz", out _, out remote));
        Assert.Equal(-1, remote);
        Assert.False(SensorRecording.TryParseFileName("../000000.local.bin.gz", out _, out _));
        Assert.False(SensorRecording.TryParseFileName("000000.remote-.bin.gz", out _, out _));
    }
}
