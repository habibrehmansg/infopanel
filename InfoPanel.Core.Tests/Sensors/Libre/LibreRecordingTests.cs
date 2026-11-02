using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.SensorHelper;
using InfoPanel.SensorHelper;
using InfoPanel.Sensors;
using InfoPanel.Sensors.HwInfo;
using InfoPanel.Sensors.Libre;
using InfoPanel.Sensors.Replay;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.Libre;

/// <summary>
/// <c>--record-sensors</c> records the helper's answers the Libre source took, round by round (<see cref="LibreRecorder"/>),
/// beside HWiNFO's in one manifest; <c>--replay-sensors</c> plays them as a helper (<see cref="LibreReplay"/>): the same
/// keys, values in a loop, its state, and the settings applied as on the PC. The committed fixture is the PC's own
/// answers (no PawnIO installed), its adapters' GUIDs made up.
/// </summary>
public sealed class LibreRecordingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "infopanel-libre-recording-" + Guid.NewGuid().ToString("N"));

    public static string Fixture { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures", "libre");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>Records <paramref name="tree"/> for an open and <paramref name="values"/>.Length rounds, CPU Total reading each in turn.</summary>
    private LibreRecorder Record(FakeTree tree, float[] values, bool scrub = false, HwInfoScrubber? scrubber = null)
    {
        var time = new TimerTimeProvider(DateTimeOffset.UnixEpoch);
        var recorder = new LibreRecorder(_root, 1000, scrub, LibreSource.Interval, time.GetUtcNow(), time: time, scrubber: scrubber);
        using var rig = new LibreRig(t => new TreeLauncher(tree, t), recorder);
        rig.Open();
        var at = tree.Sensors.FindIndex(s => s.Identifier == "/amdcpu/0/load/0");
        foreach (var value in values)
        {
            tree.Sensors[at] = tree.Sensors[at] with { Value = value };
            rig.Poll(LibreSource.Interval);
        }

        return recorder;
    }

    [Fact]
    public void ARecording_ReplaysTheHelpersKeys_AndItsValuesInALoop()
    {
        var recorder = Record(FakeTree.Pc(), [20, 30, 40]);
        Assert.True(recorder.Finished); // the source stopped: its rounds are in the manifest
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, SensorRecording.ManifestName)))!;
        Assert.Equal(4, manifest["libre"]!["rounds"]!.GetValue<int>());
        Assert.Equal(2000, manifest["libre"]!["intervalMs"]!.GetValue<int>());
        Assert.Equal(["libre"], manifest["sources"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(4, Directory.GetFiles(Path.Combine(_root, "libre"), "*.frames.gz").Length);
        Assert.Null(SensorRecording.Check(_root));

        var replay = LibreReplay.Load(_root);
        Assert.Equal(4, replay.Samples);
        using var rig = new LibreRig(_ => replay);
        rig.Open();
        var values = new List<double?> { rig.Now("/amdcpu/0/load/0") };
        for (var i = 0; i < 5; i++)
        {
            rig.Poll(LibreSource.Interval);
            values.Add(rig.Now("/amdcpu/0/load/0"));
        }

        Assert.Equal(["libre:/lpc/nct6798d/0/voltage/0", "libre:/lpc/nct6798d/0/fan/1", "libre:/amdcpu/0/load/0", "libre:/amdcpu/0/load/1",
            "libre:/amdcpu/0/temperature/2", "libre:/gpu-nvidia/0/temperature/0", "libre:/nvme/0/data/24", "libre:/nic/{4D36E972-E325-11CE}/throughput/7"], rig.Keys);
        Assert.Equal([9.75, 20, 30, 40, 9.75, 20], values);
        Assert.Equal("ASUS PRIME B650", rig.Source.GetSensors()[1].Group);
        Assert.Equal(SensorStatus.Unavailable, rig.Status("/lpc/nct6798d/0/voltage/0")); // recorded null stays a failed read
        Assert.Equal(SourceState.Running, rig.State);
    }

    [Fact]
    public void AReplay_TakesTheSettings_StorageOffDropsTheDrives_AndOffSaysOff()
    {
        Record(FakeTree.Pc(), [20, 30]);
        using var rig = new LibreRig(_ => LibreReplay.Load(_root));
        rig.Open();
        Assert.Equal(SensorStatus.Live, rig.Status("/nvme/0/data/24"));

        rig.Settings = rig.Settings with { Storage = false };
        rig.Poll(LibreSource.Interval);
        rig.Poll(LibreSource.Interval);
        // Turned off on purpose: the drives' entries unlisted at once.
        Assert.DoesNotContain("libre:/nvme/0/data/24", rig.Keys);
        Assert.Equal(SensorStatus.Unavailable, rig.Status("/nvme/0/data/24"));
        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/load/0"));

        rig.Settings = rig.Settings with { Enabled = false };
        rig.Poll(LibreSource.Interval);
        Assert.Equal((SourceStateKind.Off, "disabled"), (rig.State.Kind, rig.State.Reason?.Code));
        rig.Settings = rig.Settings with { Enabled = true, Storage = true };
        rig.Poll(LibreSource.Interval);
        rig.Poll(LibreSource.Interval);
        Assert.Equal(SourceState.Running, rig.State);
        Assert.Equal(SensorStatus.Live, rig.Status("/nvme/0/data/24"));
    }

    [Fact]
    public void AScrubbedRecording_ScrubsTheNames_AndKeepsTheIdentifiers()
    {
        Record(FakeTree.Pc(), [20], scrub: true, scrubber: new HwInfoScrubber("PRIME", null));
        using var rig = new LibreRig(_ => LibreReplay.Load(_root));
        rig.Open();

        Assert.Contains("libre:/lpc/nct6798d/0/fan/1", rig.Keys);
        Assert.Equal("ASUS THISPC B650", rig.Source.GetSensors().Single(s => s.Key.ToString() == "libre:/lpc/nct6798d/0/fan/1").Group);
        Assert.True(JsonNode.Parse(File.ReadAllText(Path.Combine(_root, SensorRecording.ManifestName)))!["scrubbed"]!.GetValue<bool>());
    }

    [Fact]
    public void TheManifest_KeepsHWiNFOsPartAndLibres_WhicheverWritesFirst()
    {
        SensorRecording.WriteLibreManifest(_root, LibreSource.Interval, true, DateTimeOffset.UnixEpoch);
        SensorRecording.WriteManifest(_root, TimeSpan.FromMilliseconds(500), true, DateTimeOffset.UnixEpoch);
        SensorRecording.WriteLibreManifest(_root, LibreSource.Interval, true, DateTimeOffset.UnixEpoch, rounds: 30, ended: DateTimeOffset.UnixEpoch.AddMinutes(1));
        SensorRecording.WriteManifest(_root, TimeSpan.FromMilliseconds(500), true, DateTimeOffset.UnixEpoch, rounds: 120, ended: DateTimeOffset.UnixEpoch.AddMinutes(1));

        Assert.Equal(120, SensorRecording.RoundsOf(_root));
        Assert.Equal(30, SensorRecording.LibreRoundsOf(_root));
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, SensorRecording.ManifestName)))!;
        Assert.Equal(["hwinfo", "libre"], manifest["sources"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(500, manifest["intervalMs"]!.GetValue<int>());
        Assert.Equal(SensorRecording.Format, manifest["format"]!.GetValue<string>());
    }

    [Fact]
    public void TheFixture_IsThePCsLibreWithoutItsDriver_Limited()
    {
        using var rig = new LibreRig(_ => LibreReplay.Load(Fixture));
        rig.Open();
        rig.Poll(LibreSource.Interval);

        Assert.Equal((SourceStateKind.Limited, "noDriver"), (rig.State.Kind, rig.State.Reason?.Code));
        Assert.Equal(199, rig.Keys.Count);
        Assert.Equal("libre:/amdcpu/0/load/0", rig.Keys[0]);
        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/load/0"));
        Assert.Equal(("AMD Ryzen 7 8845HS w/ Radeon 780M Graphics", "CPU Total", "%"),
            (rig.Source.GetSensors()[0].Group, rig.Source.GetSensors()[0].Name, rig.Source.GetSensors()[0].Unit));
        Assert.Contains(rig.Source.GetSensors(), s => s.Unit == "B/s");
        Assert.Contains(rig.Source.GetSensors(), s => s.Group == "CT1000P3PSSD8");
    }

    [Fact]
    public void ALibreOnlyRecording_ReplaysHWiNFOAsNotRunning()
    {
        using var hwinfo = SensorReplay.CreateHwInfo(Fixture);
        hwinfo.Open();

        Assert.Equal((SourceStateKind.Unavailable, HwInfoSource.NotRunning.Code), (hwinfo.State.Kind, hwinfo.State.Reason?.Code));
        Assert.Empty(hwinfo.GetSensors());
    }

    private static readonly HelloAnswer Hello = new(HelperProtocol.Version, "2.0.0-test", true, "unknown");

    private static CatalogueAnswer CatalogueOf(long version, params string[] sensors) => new(version,
        [new HelperHardware("/amdcpu/0", "CPU", "Cpu", null)],
        [.. sensors.Select((id, i) => new HelperSensor(id, "Sensor " + i, "Load", i, "%", "/amdcpu/0"))]);

    private static SampleAnswer SampleOf(long version, params float?[] values) => new(version, values, values, values);

    /// <summary>The frames of a round's file, as the recorder writes them: each a 4-byte little-endian length, then its JSON.</summary>
    private static byte[] FramesOf(params HelperAnswer[] answers)
    {
        using var frames = new MemoryStream();
        foreach (var answer in answers)
        {
            var json = HelperCodec.EncodeAnswer(answer);
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)json.Length);
            frames.Write(length);
            frames.Write(json);
        }

        return frames.ToArray();
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.Fastest))
            gzip.Write(bytes);
        return memory.ToArray();
    }

    /// <summary>Writes round <paramref name="round"/>'s file with <paramref name="answers"/>.</summary>
    private void WriteRound(int round, params HelperAnswer[] answers)
    {
        Directory.CreateDirectory(Path.Combine(_root, "libre"));
        File.WriteAllBytes(Path.Combine(_root, "libre", SensorRecording.LibreFileName(round)), Gzip(FramesOf(answers)));
    }

    [Fact]
    public void ACatalogueChange_IsReplayed_ItsSensorsAppear()
    {
        // The PC: catalogue A, a sample under it; then a sample under B (the engine skipped it and fetched B); then B's.
        WriteRound(0, Hello, CatalogueOf(1, "/amdcpu/0/load/0"), SampleOf(1, 10f));
        WriteRound(1, SampleOf(2, 11f, 50f), CatalogueOf(2, "/amdcpu/0/load/0", "/amdcpu/0/load/1"));
        WriteRound(2, SampleOf(2, 12f, 60f));
        SensorRecording.WriteLibreManifest(_root, LibreSource.Interval, false, DateTimeOffset.UnixEpoch, rounds: 3);

        using var rig = new LibreRig(_ => LibreReplay.Load(_root));
        rig.Open();
        var seen = new List<double?>();
        for (var i = 0; i < 6; i++)
        {
            rig.Poll(LibreSource.Interval);
            seen.Add(rig.Now("/amdcpu/0/load/1"));
        }

        // B's sensor is listed and read each time the loop comes to B's sample (one round each change for the fetch).
        Assert.Contains("libre:/amdcpu/0/load/1", rig.Keys);
        Assert.Contains(60, seen);
    }

    [Fact]
    public void RecordedOutages_AreReplayed_AsFailedReads_TrailingOnesToo()
    {
        // A good sample, two rounds with nothing answered, a good one, then nothing for the last two rounds.
        WriteRound(0, Hello, CatalogueOf(1, "/amdcpu/0/load/0"), SampleOf(1, 10f));
        WriteRound(3, SampleOf(1, 13f));
        SensorRecording.WriteLibreManifest(_root, LibreSource.Interval, false, DateTimeOffset.UnixEpoch, rounds: 6);

        var replay = LibreReplay.Load(_root);
        Assert.Equal(6, replay.Samples);
        using var rig = new LibreRig(_ => replay);
        rig.Open();
        var seen = new List<(double?, SensorStatus)> { (rig.Now("/amdcpu/0/load/0"), rig.Status("/amdcpu/0/load/0")) };
        for (var i = 0; i < 6; i++)
        {
            rig.Poll(LibreSource.Interval);
            seen.Add((rig.Now("/amdcpu/0/load/0"), rig.Status("/amdcpu/0/load/0")));
        }

        Assert.Equal([(10, SensorStatus.Live), (10, SensorStatus.Stale), (10, SensorStatus.Stale), (13, SensorStatus.Live),
            (13, SensorStatus.Stale), (13, SensorStatus.Stale), (10, SensorStatus.Live)], seen);
        Assert.Equal(SourceState.Running, rig.State);
    }

    [Fact]
    public void ARecordingTheEngineStoppedWhileWritingARound_KeepsItsCompleteRounds()
    {
        // The engine was killed while it wrote round 2: its file is cut short, and the manifest has no rounds.
        WriteRound(0, Hello, CatalogueOf(1, "/amdcpu/0/load/0"), SampleOf(1, 10f));
        WriteRound(1, SampleOf(1, 11f));
        var frames = FramesOf(SampleOf(1, 12f), SampleOf(1, 12f));
        File.WriteAllBytes(Path.Combine(_root, "libre", SensorRecording.LibreFileName(2)), Gzip(frames[..(frames.Length - 7)]));
        SensorRecording.WriteLibreManifest(_root, LibreSource.Interval, false, DateTimeOffset.UnixEpoch);

        var replay = LibreReplay.Load(_root);
        Assert.Equal(2, replay.Samples);

        // A finished recording (its rounds in the manifest) with a file cut short is refused, as before.
        SensorRecording.WriteLibreManifest(_root, LibreSource.Interval, false, DateTimeOffset.UnixEpoch, rounds: 3);
        Assert.Throws<InvalidDataException>(() => LibreReplay.Load(_root));

        // The gzip itself cut short (its end and part of its data missing): the same.
        var gzip = Gzip(frames);
        File.WriteAllBytes(Path.Combine(_root, "libre", SensorRecording.LibreFileName(2)), gzip[..(gzip.Length / 2)]);
        SensorRecording.WriteLibreManifest(_root, LibreSource.Interval, false, DateTimeOffset.UnixEpoch);
        Assert.Equal(2, LibreReplay.Load(_root).Samples);
    }

    [Fact]
    public void AnUnfinishedRecordingsLastFile_ThatIsWholeButRefused_OrUnreadable_FailsTheLoad()
    {
        WriteRound(0, Hello, CatalogueOf(1, "/amdcpu/0/load/0"), SampleOf(1, 10f));
        WriteRound(1, SampleOf(1, 11f));
        SensorRecording.WriteLibreManifest(_root, LibreSource.Interval, false, DateTimeOffset.UnixEpoch); // no rounds: unfinished
        var last = Path.Combine(_root, "libre", SensorRecording.LibreFileName(2));

        // Whole, but an answer the protocol refuses: corrupt, not cut short.
        var json = "{\"type\":\"nonsense\"}"u8.ToArray();
        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)json.Length);
        json.CopyTo(frame, 4);
        File.WriteAllBytes(last, Gzip(frame));
        Assert.Contains("refuses", Assert.Throws<InvalidDataException>(() => LibreReplay.Load(_root)).Message);

        // A frame no answer can have (empty): corrupt.
        File.WriteAllBytes(last, Gzip([.. FramesOf(SampleOf(1, 12f)), 0, 0, 0, 0]));
        Assert.Throws<InvalidDataException>(() => LibreReplay.Load(_root));

        // Not a gzip at all.
        File.WriteAllBytes(last, [.. Enumerable.Repeat((byte)0x5a, 64)]);
        Assert.Throws<InvalidDataException>(() => LibreReplay.Load(_root));

        // A folder under its name: it can't be read, which isn't a cut either.
        File.Delete(last);
        Directory.CreateDirectory(last);
        Assert.Throws<InvalidDataException>(() => LibreReplay.Load(_root));
    }

    [Fact]
    public void ARestartsRoundWithoutASample_PlaysAsItsOwnRound_TheNextValueOnTime()
    {
        // The PC: a good sample; the helper lost; its restart (hello, catalogue B, no sample); B's first value.
        WriteRound(0, Hello, CatalogueOf(1, "/amdcpu/0/load/0"), SampleOf(1, 10f));
        WriteRound(2, Hello, CatalogueOf(2, "/amdcpu/0/load/0", "/amdcpu/0/load/1"));
        WriteRound(3, SampleOf(2, 13f, 70f));
        SensorRecording.WriteLibreManifest(_root, LibreSource.Interval, false, DateTimeOffset.UnixEpoch, rounds: 4);

        using var rig = new LibreRig(_ => LibreReplay.Load(_root));
        rig.Open(); // round 0
        var seen = new List<(double?, double?)>();
        for (var i = 1; i < 4; i++)
        {
            rig.Poll(LibreSource.Interval);
            seen.Add((rig.Now("/amdcpu/0/load/0"), rig.Now("/amdcpu/0/load/1")));
        }

        // Round 2's catalogue is fetched in its own round (its readings skipped), and round 3's values land in round 3.
        Assert.Equal([(10, null), (10, null), (13, 70)], seen);
        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/load/1"));
    }

    [Fact]
    public void ARound_IsWrittenUnderATemporaryName_ThenPublished_NeverTakingAnotherFilesName()
    {
        var time = new TimerTimeProvider(DateTimeOffset.UnixEpoch);
        var recorder = new LibreRecorder(_root, 1000, false, LibreSource.Interval, time.GetUtcNow(), time: time);
        recorder.Answered(HelperCodec.EncodeAnswer(Hello));
        recorder.RoundEnded();
        Assert.Equal([SensorRecording.LibreFileName(0)], Directory.GetFiles(Path.Combine(_root, "libre"), "*.frames.gz*").Select(Path.GetFileName));

        // Round 1's name is taken by a file the recorder didn't write: it is left as it is, and the recording ends.
        var theirs = Path.Combine(_root, "libre", SensorRecording.LibreFileName(1));
        File.WriteAllText(theirs, "someone else's");
        recorder.Answered(HelperCodec.EncodeAnswer(SampleOf(1, 10f)));
        recorder.RoundEnded();
        Assert.Equal("someone else's", File.ReadAllText(theirs));
        Assert.True(recorder.Finished);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "libre"), "*.frames.gz*").Length);
    }

    [Fact]
    public void ASecondRecorder_OnTheSameFolder_RecordsNothing_AndDeletesNothing()
    {
        var time = new TimerTimeProvider(DateTimeOffset.UnixEpoch);
        var first = new LibreRecorder(_root, 1000, false, LibreSource.Interval, time.GetUtcNow(), time: time);
        // Another engine passed the folder's check before either wrote a frame.
        var second = new LibreRecorder(_root, 1000, false, LibreSource.Interval, time.GetUtcNow(), time: time);
        first.Answered(HelperCodec.EncodeAnswer(Hello));
        first.RoundEnded();
        var frame = Path.Combine(_root, "libre", SensorRecording.LibreFileName(0));
        var bytes = File.ReadAllBytes(frame);

        second.Answered(HelperCodec.EncodeAnswer(Hello));
        second.RoundEnded();
        second.Stopped();
        Assert.Equal(bytes, File.ReadAllBytes(frame));
        Assert.True(second.Finished);
        Assert.Equal(0, second.Rounds);

        first.Stopped();
        Assert.Equal(1, SensorRecording.LibreRoundsOf(_root));
    }

    [Fact]
    public void AFolderWithoutLibresFrames_IsNoLibreRecording()
    {
        Directory.CreateDirectory(Path.Combine(_root, "libre"));
        File.WriteAllText(Path.Combine(_root, "libre", "notes.txt"), "x");

        Assert.False(SensorRecording.HoldsLibre(_root));
        Assert.Contains("no hwinfo/*.bin.gz or libre/*.frames.gz", SensorRecording.Check(_root));
        Assert.Throws<InvalidDataException>(() => LibreReplay.Load(_root));
    }
}
