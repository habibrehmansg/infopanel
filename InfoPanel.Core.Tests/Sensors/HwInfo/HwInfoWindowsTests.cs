using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Sensors;
using InfoPanel.Sensors.HwInfo;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.HwInfo;

/// <summary>
/// The HWiNFO reader on real Windows mappings, opt-in (<c>INFOPANEL_WINDOWS_TESTS=1</c> on Windows; skipped
/// elsewhere). The first tests make their own <c>Local\InfoPanelTest-{guid}</c> mappings, named as HWiNFO names its
/// remotes (<c>…_REMOTE_1</c>, <c>_2</c> …), so they run on any Windows, without HWiNFO. The last two read the real
/// HWiNFO (<c>Global\HWiNFO_SENS_SM2</c>, skipped when it isn't running), the second only unelevated: that is the
/// proof that the engine reads HWiNFO as the normal user. What they read goes to the test output and
/// <c>TestResults/hwinfo-real.txt</c>.
/// </summary>
public sealed class HwInfoWindowsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool Enabled => OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("INFOPANEL_WINDOWS_TESTS") == "1";

    private const string Skip = "Reads real Windows mappings: set INFOPANEL_WINDOWS_TESTS=1 on Windows to run it.";

    private static HwInfoImageBuilder Pc(string? host = null, long pollTime = 1_793_508_823)
    {
        var prefix = host == null ? "" : $"[{host}] ";
        var b = new HwInfoImageBuilder { PollTime = pollTime };
        b.AddSensor(0xF0000300, 0, prefix + "CPU [#0]: AMD Ryzen 7 8845HS");
        b.AddSensor(0xF0000301, 1, prefix + "Memory Timings");
        b.AddReading(0, 0x1000000, "CPU (Tctl/Tdie)", "°C", 52.125, 33, 90.75, 40.5);
        b.AddReading(1, 0x7000000, "Memory Clock", "MHz", 2800, 2795, 2805, 2800.5);
        b.Slack = 4096;
        return b;
    }

    [Fact]
    public void TestMappings_LocalAndRemotes_AreReadThroughTheWindowsReader_UpToTheFirstGap()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (OperatingSystem.IsWindows())
            LocalAndRemotes();
    }

    [Fact]
    public void TestMappings_ARemoteDyingAlone_AndOneProbedAgainAfterFiveRounds()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (OperatingSystem.IsWindows())
            RemoteDiesAndReturns();
    }

    [Fact]
    public void RealHWiNFO_IsRead_EveryReadingUnder1xKeys()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (OperatingSystem.IsWindows())
            ReadRealHwInfo(requireUnelevated: false);
    }

    [Fact]
    public void RealHWiNFO_OpensAsTheNormalUser()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (OperatingSystem.IsWindows())
        {
            Assert.SkipWhen(IsElevated(), "This process is elevated: run it unelevated (a limited scheduled task) to prove the normal user reads HWiNFO.");
            ReadRealHwInfo(requireUnelevated: true);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void LocalAndRemotes()
    {
        var name = @"Local\InfoPanelTest-" + Guid.NewGuid().ToString("N");
        using var local = Create(name, Pc().Build());
        using var r1 = Create(name + "_REMOTE_1", Pc("PC1").Build());
        using var r2 = Create(name + "_REMOTE_2", Pc("PC2").Build());
        using var r4 = Create(name + "_REMOTE_4", Pc("PC4").Build());
        using var source = HwInfoSource.Create(new WindowsHwInfoMemoryOpener(), localName: name, remotePrefix: name + "_REMOTE_");

        source.Open();

        Assert.Equal(
            [
                ("hwinfo:-1:4026532608:0:16777216", "CPU [#0]: AMD Ryzen 7 8845HS", "CPU (Tctl/Tdie)", "°C"),
                ("hwinfo:-1:4026532609:1:117440512", "Memory Timings", "Memory Clock", "MHz"),
                ("hwinfo:1:4026532608:0:16777216", "[PC1] CPU [#0]: AMD Ryzen 7 8845HS", "CPU (Tctl/Tdie)", "°C"),
                ("hwinfo:1:4026532609:1:117440512", "[PC1] Memory Timings", "Memory Clock", "MHz"),
                ("hwinfo:2:4026532608:0:16777216", "[PC2] CPU [#0]: AMD Ryzen 7 8845HS", "CPU (Tctl/Tdie)", "°C"),
                ("hwinfo:2:4026532609:1:117440512", "[PC2] Memory Timings", "Memory Clock", "MHz"),
            ],
            source.GetSensors().Select(s => (s.Key.ToString(), s.Group, s.Name, s.Unit)));
        Assert.True(source.TryRead(SensorKey.HwInfo(2, 0xF0000300, 0, 0x1000000), out var reading));
        Assert.Equal((52.125, 33, 90.75, 40.5), (reading.ValueNow, reading.ValueMin, reading.ValueMax, reading.ValueAvg));
        Assert.Equal(SensorStatus.Live, source.GetStatus(SensorKey.HwInfo(-1, 0xF0000300, 0, 0x1000000)));
    }

    [SupportedOSPlatform("windows")]
    private static void RemoteDiesAndReturns()
    {
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 11, 1, 4, 53, 43, TimeSpan.Zero));
        var name = @"Local\InfoPanelTest-" + Guid.NewGuid().ToString("N");
        var pc = Pc();
        using var local = Create(name, pc.Build());
        var r1 = Create(name + "_REMOTE_1", Pc("PC1").Build());
        using var r2 = Create(name + "_REMOTE_2", Pc("PC2").Build());
        using var source = HwInfoSource.Create(new WindowsHwInfoMemoryOpener(), time, localName: name, remotePrefix: name + "_REMOTE_");
        source.Open();
        Assert.Equal(6, source.GetSensors().Count);

        void Round()
        {
            time.Advance(HwInfoSource.Interval);
            pc.PollTime++;
            Write(local, pc.Build());
            source.Poll();
        }

        // Remote 1 goes (HWiNFO writes DEAD and closes it): only its keys fail.
        Write(r1, Pc("PC1").BuildDead());
        r1.Dispose();
        Round();
        Assert.Equal(SensorStatus.Stale, source.GetStatus(SensorKey.HwInfo(1, 0xF0000300, 0, 0x1000000)));
        Assert.Equal(SensorStatus.Live, source.GetStatus(SensorKey.HwInfo(-1, 0xF0000300, 0, 0x1000000)));
        Assert.Equal(SensorStatus.Live, source.GetStatus(SensorKey.HwInfo(2, 0xF0000300, 0, 0x1000000)));

        // A remote 1 again: found at the next probe (every fifth round: rounds 0 and 1 were the start's, 2 the one
        // above), with remote 3 past the gap now reachable too.
        using var again = Create(name + "_REMOTE_1", Pc("PC1").Build());
        using var r3 = Create(name + "_REMOTE_3", Pc("PC3").Build());
        for (var i = 0; i < 2; i++)
            Round();
        Assert.DoesNotContain(source.GetSensors(), s => s.Key.ToString().StartsWith("hwinfo:3:", StringComparison.Ordinal));
        Round();
        Assert.Contains(source.GetSensors(), s => s.Key.ToString().StartsWith("hwinfo:3:", StringComparison.Ordinal));
        Round();
        Assert.Equal(SensorStatus.Live, source.GetStatus(SensorKey.HwInfo(1, 0xF0000300, 0, 0x1000000)));
        Assert.Equal(SensorStatus.Live, source.GetStatus(SensorKey.HwInfo(3, 0xF0000300, 0, 0x1000000)));
    }

    [SupportedOSPlatform("windows")]
    private static void ReadRealHwInfo(bool requireUnelevated)
    {
        IHwInfoMemory? probe;
        try
        {
            probe = new WindowsHwInfoMemoryOpener().TryOpen(HwInfoSampler.LocalName);
        }
        catch (UnauthorizedAccessException ex)
        {
            Assert.Fail($"HWiNFO's mapping exists but this user may not read it: {ex.Message}");
            return;
        }

        Assert.SkipWhen(probe == null, "HWiNFO isn't running with Shared Memory Support on.");
        probe!.Dispose();

        using var source = HwInfoSource.CreateWindows();
        source.Open();
        Thread.Sleep(600);
        source.Poll();

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"elevated: {IsElevated()}; user: {WindowsIdentity.GetCurrent().Name}; {source.GetSensors().Count} readings");
        CheckKeys(source, new WindowsHwInfoMemoryOpener(), HwInfoSampler.LocalName, HwInfoSampler.RemotePrefix, report);
        if (requireUnelevated)
            Assert.False(IsElevated());

        var text = report.ToString();
        TestContext.Current.TestOutputHelper?.WriteLine(text);
        var directory = Path.Combine(TestPaths.ProjectDirectory, "TestResults");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, requireUnelevated ? "hwinfo-real-unelevated.txt" : "hwinfo-real.txt"), text);
    }

    /// <summary>
    /// The checks on a source that read real mappings: every key well-formed, live and read, each once; the local
    /// mapping's keys there, and the keys of the local mapping and of each connected remote exactly those its own
    /// mapping lists (<c>{remote}:{sensor id}:{instance}:{reading id}</c>, read from it here).
    /// </summary>
    internal static void CheckKeys(PolledSensorSource source, IHwInfoMemoryOpener opener, string localName, string remotePrefix, StringBuilder report)
    {
        var sensors = source.GetSensors();
        Assert.NotEmpty(sensors);
        var byRemote = sensors.GroupBy(s => int.Parse(s.Key.Id.Split(':')[0], CultureInfo.InvariantCulture)).ToDictionary(g => g.Key, g => g.Select(s => s.Key.Id).ToHashSet());
        Assert.True(byRemote.ContainsKey(-1), "no key of the local mapping");
        foreach (var (remote, keys) in byRemote)
        {
            Assert.True(remote is -1 or >= 1, $"remote index {remote}");
            using var memory = opener.TryOpen(remote < 0 ? localName : remotePrefix + remote.ToString(CultureInfo.InvariantCulture));
            Assert.True(memory != null, $"remote {remote}'s mapping isn't there");
            var image = new byte[(int)Math.Min(memory!.Length, HwInfoLayout.MaxImageBytes)];
            memory.Read(0, image);
            Assert.Equal(HwInfoImageState.Ok, HwInfoLayout.ReadHeader(image, image.Length, out var h, out _));
            var snapshot = HwInfoLayout.Read(image, h);
            var listed = snapshot.Readings.Select(r => string.Create(CultureInfo.InvariantCulture,
                $"{remote}:{snapshot.Sensors[(int)r.SensorIndex].Id}:{snapshot.Sensors[(int)r.SensorIndex].Instance}:{r.Id}")).ToHashSet();
            Assert.True(listed.SetEquals(keys), $"remote {remote}: {keys.Count} keys, its mapping lists {listed.Count}");
        }

        foreach (var sensor in sensors)
        {
            var parts = sensor.Key.Id.Split(':');
            Assert.Equal(4, parts.Length);
            Assert.True(SensorHub.IsWellFormed(sensor.Key), sensor.Key.ToString());
            var status = source.GetStatus(sensor.Key);
            Assert.Equal(SensorStatus.Live, status);
            Assert.True(source.TryRead(sensor.Key, out var r));
            report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{sensor.Key,-40} {r.ValueNow,12:0.###} {r.ValueMin,12:0.###} {r.ValueMax,12:0.###} {r.ValueAvg,12:0.###} {sensor.Unit,-6} {sensor.Group} / {sensor.Name}"));
        }

        Assert.Equal(sensors.Count, sensors.Select(s => s.Key).Distinct().Count());
    }

    [Fact]
    public void TheRealMappingChecks_TakeAConnectedRemote_CheckingItsKeysAgainstItsOwnMapping()
    {
        // Runs anywhere: the checks the real-HWiNFO tests make, on fake mappings with a remote connected.
        var memory = new FakeHwInfoMemory();
        memory.Set(HwInfoSampler.LocalName, Pc().Build());
        memory.Set(HwInfoSampler.RemotePrefix + "1", Pc("PC1").Build());
        using var source = HwInfoSource.Create(memory);
        source.Open();
        Assert.Contains(source.GetSensors(), s => s.Key.Id.StartsWith("1:", StringComparison.Ordinal));

        CheckKeys(source, memory, HwInfoSampler.LocalName, HwInfoSampler.RemotePrefix, new StringBuilder());

        // A remote's keys that its own mapping doesn't list fail the checks.
        memory.Set(HwInfoSampler.RemotePrefix + "1", new HwInfoImageBuilder().AddSensor(1, 0, "[PC1] Other").AddReading(0, 1, "Other", "V", 1).Build());
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => CheckKeys(source, memory, HwInfoSampler.LocalName, HwInfoSampler.RemotePrefix, new StringBuilder()));
    }

    /// <summary>Whether this process runs with an administrator's full token (UAC elevated).</summary>
    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [SupportedOSPlatform("windows")]
    private static MemoryMappedFile Create(string name, byte[] bytes)
    {
        var file = MemoryMappedFile.CreateNew(name, bytes.Length + 4096L, MemoryMappedFileAccess.ReadWrite);
        Write(file, bytes);
        return file;
    }

    [SupportedOSPlatform("windows")]
    private static void Write(MemoryMappedFile file, byte[] bytes)
    {
        using var view = file.CreateViewAccessor(0, bytes.Length, MemoryMappedFileAccess.Write);
        view.WriteArray(0, bytes, 0, bytes.Length);
    }
}
