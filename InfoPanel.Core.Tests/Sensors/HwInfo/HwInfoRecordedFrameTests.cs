using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Models;
using InfoPanel.Sensors;
using InfoPanel.Core.Tests.Sensors.Replay;
using InfoPanel.Sensors.HwInfo;
using InfoPanel.Sensors.Replay;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.HwInfo;

/// <summary>
/// The HWiNFO source on three frames recorded from a real PC (HWiNFO 8.54, 15 sensors, 276 readings), scrubbed of PC
/// names and drive serial numbers (<c>Fixtures/hwinfo/</c>, a recording <c>--replay-sensors</c> plays too). The
/// expected keys are worked out by this test's own reading of the frame with 1.x's struct (<c>HWHash.cs</c>), not by
/// <see cref="HwInfoLayout"/>.
/// </summary>
public class HwInfoRecordedFrameTests
{
    private static readonly string Folder = Path.Combine(TestPaths.ProjectDirectory, "Fixtures", "hwinfo");

    private static byte[] Frame(int i)
    {
        using var file = File.OpenRead(Path.Combine(Folder, "hwinfo", SensorRecording.FileName(i, -1)));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var memory = new MemoryStream();
        gzip.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>1.x's key rule, read with 1.x's offsets: (remote, header[reading.Index].ID, .Instance, reading.ID).</summary>
    private static List<(string Key, string Group, string Name, double Value, double Min, double Max, double Avg)> OneX(byte[] b, int remote = -1)
    {
        uint U(int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
        double D(int o) => BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(o));
        string S(int o) => Encoding.Latin1.GetString(b, o, 128).Split('\0')[0];
        uint so = U(20), ss = U(24), sn = U(28), ro = U(32), rs = U(36), rn = U(40);
        var list = new List<(string, string, string, double, double, double, double)>();
        for (var i = 0; i < rn; i++)
        {
            var o = (int)(ro + rs * i);
            var index = U(o + 4);
            var header = (int)(so + ss * index);
            list.Add(($"hwinfo:{remote}:{U(header)}:{U(header + 4)}:{U(o + 8)}", S(header + 8), S(o + 12), D(o + 284), D(o + 292), D(o + 300), D(o + 308)));
        }

        return list;
    }

    private sealed class Rig
    {
        public readonly FakeHwInfoMemory Memory = new();
        public readonly TimerTimeProvider Time = new(new DateTimeOffset(2026, 11, 1, 5, 24, 26, TimeSpan.Zero));
        public readonly PolledSensorSource Source;

        public Rig() => Source = HwInfoSource.Create(Memory, Time);

        public void Poll(int rounds = 1)
        {
            for (var i = 0; i < rounds; i++)
            {
                Time.Advance(HwInfoSource.Interval);
                Source.Poll();
            }
        }
    }

    [Fact]
    public void TheFrames_AreHWiNFO854s_Layout()
    {
        var frame = Frame(0);
        Assert.Equal(HwInfoImageState.Ok, HwInfoLayout.ReadHeader(frame, frame.Length, out var h, out _));
        Assert.Equal((2u, 1u, 48u, 392u, 15u, 5928u, 460u, 276u, 2000u),
            (h.Version, h.Revision, h.SensorOffset, h.SensorSize, h.SensorCount, h.ReadingOffset, h.ReadingSize, h.ReadingCount, h.PollingPeriodMs));
        Assert.Equal(frame.Length, h.End);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EveryReadingsKey_Is1xsTupleRule_WithNameGroupAndHWiNFOsStatistics_InHWiNFOsOrder(int i)
    {
        var frame = Frame(i);
        var rig = new Rig();
        rig.Memory.Set(HwInfoSampler.LocalName, frame);
        rig.Source.Open();

        var expected = OneX(frame);
        var sensors = rig.Source.GetSensors();
        Assert.Equal(276, expected.Count);
        Assert.Equal(expected.Select(e => e.Key), sensors.Select(s => s.Key.ToString()));
        Assert.Equal(expected.Select(e => (e.Group, e.Name)), sensors.Select(s => (s.Group, s.Name)));
        foreach (var e in expected)
        {
            Assert.True(rig.Source.TryRead(Key(e.Key), out var r), e.Key);
            Assert.Equal((e.Value, e.Min, e.Max, e.Avg), (r.ValueNow, r.ValueMin, r.ValueMax, r.ValueAvg));
        }

        Assert.Equal(15, sensors.Select(s => s.Group).Distinct().Count());
        Assert.Equal("hwinfo:-1:4026532609:0:134217728", sensors[0].Key.ToString());
    }

    private static SensorKey Key(string text)
    {
        Assert.True(SensorKey.TryParse(text, out var key));
        return key;
    }

    /// <summary>Three readings by hand, as the PC's own listing of them showed (sensor, label, unit).</summary>
    [Theory]
    [InlineData("hwinfo:-1:4026533121:0:16777216", "CPU [#0]: AMD Ryzen 7 8845HS: Enhanced", "CPU (Tctl/Tdie)", "°C", 87.75, 33.0, 90.875)]
    [InlineData("hwinfo:-1:4026532608:0:117440529", "CPU [#0]: AMD Ryzen 7 8845HS", "Total CPU Usage", "%", 13.486247575258021, 0.7025103125, 99.99951666666666)]
    [InlineData("hwinfo:-1:4026532609:1:100663296", "Memory Timings", "Memory Clock", "MHz", 2779.7974004882813, 1062.484713623047, 2794.9602437011717)]
    public void ThreeReadings_ByHand(string key, string group, string name, string unit, double now, double min, double max)
    {
        var rig = new Rig();
        rig.Memory.Set(HwInfoSampler.LocalName, Frame(0));
        rig.Source.Open();

        var sensor = rig.Source.GetSensors().Single(s => s.Key.ToString() == key);
        Assert.Equal((group, name, unit), (sensor.Group, sensor.Name, sensor.Unit));
        Assert.True(rig.Source.TryRead(sensor.Key, out var r));
        Assert.Equal((now, min, max, unit), (r.ValueNow, r.ValueMin, r.ValueMax, r.Unit));
    }

    [Fact]
    public void Units_AreHWiNFOs_UTF8OnesRead()
    {
        var rig = new Rig();
        rig.Memory.Set(HwInfoSampler.LocalName, Frame(0));
        rig.Source.Open();

        var units = rig.Source.GetSensors().GroupBy(s => s.Unit).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(24, units["°C"]);
        Assert.Equal(83, units["%"]);
        Assert.Equal(37, units["MHz"]);
        Assert.Equal(15, units["Yes/No"]);
        Assert.Equal(2, units[""]);
    }

    [Fact]
    public void EveryCommittedFrame_IsScrubbed_EveryStringFieldClean_AndEveryOtherByteZero()
    {
        var files = Directory.GetFiles(Path.Combine(Folder, "hwinfo"), "*.bin.gz");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            using var stream = File.OpenRead(file);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var memory = new MemoryStream();
            gzip.CopyTo(memory);
            ScrubCheck.AssertScrubbed(memory.ToArray(), Path.GetFileName(file));
        }

        // The drive's serial number was replaced, not dropped.
        Assert.Contains(ScrubCheck.Strings(Frame(0)), s => s.Text.Contains("(SERIAL1)", StringComparison.Ordinal));
    }

    [Fact]
    public void Frames_PlayedInTurn_MoveTheValues_AndKeepTheCatalogue()
    {
        var rig = new Rig();
        rig.Memory.Set(HwInfoSampler.LocalName, Frame(0));
        rig.Source.Open();
        var cpu = Key("hwinfo:-1:4026533121:0:16777216");
        var keys = rig.Source.GetSensors().Select(s => s.Key).ToList();

        var seen = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            rig.Memory.Set(HwInfoSampler.LocalName, Frame(i));
            rig.Poll();
            Assert.True(rig.Source.TryRead(cpu, out var r));
            seen.Add(r.ValueNow);
            Assert.Equal(SensorStatus.Live, rig.Source.GetStatus(cpu));
        }

        Assert.Equal([87.75, 52.0, 39.5], seen);
        Assert.Equal(keys, rig.Source.GetSensors().Select(s => s.Key));
    }

    [Fact]
    public void ARealFrame_Frozen_Dead_AppearingLate_AndAsARemote()
    {
        var rig = new Rig();
        rig.Source.Open();
        rig.Poll(2);
        Assert.Empty(rig.Source.GetSensors());

        // HWiNFO starts after the engine.
        rig.Memory.Set(HwInfoSampler.LocalName, Frame(0));
        rig.Memory.Set(HwInfoSampler.RemotePrefix + "1", Frame(1));
        rig.Poll(3);
        Assert.Equal(276 * 2, rig.Source.GetSensors().Count);
        var local = Key("hwinfo:-1:4026533121:0:16777216");
        var remote = Key("hwinfo:1:4026533121:0:16777216");
        Assert.True(rig.Source.TryRead(remote, out var r));
        Assert.Equal(52.0, r.ValueNow);

        // Its poll time stops (period 2000 ms: frozen after 6 s), then it says DEAD.
        rig.Poll(12);
        Assert.Equal(SensorStatus.Stale, rig.Source.GetStatus(local));
        var dead = Frame(0);
        BinaryPrimitives.WriteUInt32LittleEndian(dead, HwInfoLayout.SignatureDead);
        rig.Memory.Set(HwInfoSampler.LocalName, dead);
        rig.Poll(70);
        Assert.DoesNotContain(rig.Source.GetSensors(), s => s.Key.Id.StartsWith("-1:", StringComparison.Ordinal));
        Assert.Equal(SensorStatus.Unavailable, rig.Source.GetStatus(local));
    }
}
