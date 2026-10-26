using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using InfoPanel.Sensors;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Sensors.MacOS;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors;

/// <summary>
/// The real macOS sensors on the machine running the tests: values must be in plausible ranges and statuses must
/// make sense. Reads hardware and its values depend on the machine's load, so it runs only on macOS with
/// <c>INFOPANEL_SENSOR_TESTS=1</c>; skipped otherwise. Writes what it read to the test output and
/// <c>TestResults/mac-sensors.txt</c>.
/// </summary>
public sealed class MacSensorSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool Enabled => OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("INFOPANEL_SENSOR_TESTS") == "1";

    [Fact]
    public async Task RealSensors_ReadPlausibleValues()
    {
        Assert.SkipUnless(Enabled, "Reads real hardware sensors: set INFOPANEL_SENSOR_TESTS=1 on macOS to run it.");
        if (OperatingSystem.IsMacOS())
            await ReadAndCheck();
    }

    [SupportedOSPlatform("macos")]
    private static async Task ReadAndCheck()
    {
        using var source = MacSensorSource.Create();
        await source.StartAsync(Ct);
        try
        {
            // Rates need two polls, the SMC is read every other poll: wait for the fourth.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (source.Polls < 4 && DateTime.UtcNow < deadline)
                await Task.Delay(100, Ct);
            Assert.True(source.Polls >= 4, "the source polls once a second");

            var sensors = source.GetSensors();
            var hub = new SensorHub([source]);
            var report = new StringBuilder();
            var values = new Dictionary<string, double>();
            foreach (var sensor in sensors)
            {
                var status = hub.GetStatus(sensor.Key);
                var id = sensor.Key.Id[MacSensorSource.KeyPrefix.Length..];
                var read = hub.TryRead(sensor.Key, out var reading);
                Assert.Equal(status is SensorStatus.Live or SensorStatus.Stale, read);
                if (read)
                    values[id] = reading.ValueNow;
                report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{sensor.Key,-50} {StatusText(status),-18} {(read ? reading.ValueNow.ToString("0.##", CultureInfo.InvariantCulture) : "-"),10} {sensor.Unit,-5} {sensor.Group} / {sensor.Name}"));
            }

            var text = report.ToString();
            TestContext.Current.TestOutputHelper?.WriteLine(text);
            var directory = Path.Combine(TestPaths.ProjectDirectory, "TestResults");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "mac-sensors.txt"), text);

            // The public baseline is on every Mac and must be live.
            foreach (var id in new[] { "cpu/load", "memory/used", "memory/available", "memory/load", "network/download", "network/upload" })
                Assert.True(values.ContainsKey(id), $"{id} has a reading");

            Assert.InRange(values["cpu/load"], 0, 100);
            Assert.InRange(values["memory/load"], 1, 100);
            Assert.InRange(values["memory/used"], 100, 16 * 1024 * 1024);
            Assert.InRange(values["network/download"], 0, 10_000_000);
            Assert.Equal(Environment.ProcessorCount, sensors.Count(s => s.Key.Id.EndsWith("/load", StringComparison.Ordinal) && s.Key.Id.Contains("/cpu/core-", StringComparison.Ordinal)));
            foreach (var (id, value) in values)
            {
                // Percentages (cpu/load-e, gpu/renderer-load...; not network/download).
                if ((id.Contains("load", StringComparison.Ordinal) && !id.StartsWith("network/", StringComparison.Ordinal))
                    || id is "memory/pressure" or "battery/level")
                    Assert.InRange(value, 0, 100);
                if (id.Contains("temperature", StringComparison.Ordinal))
                    Assert.InRange(value, 10, 125);
                if (id.StartsWith("fans/", StringComparison.Ordinal))
                    Assert.InRange(value, 0, 20_000);
                if (id.Contains("frequency", StringComparison.Ordinal))
                    Assert.True(value == 0 || value is > 300 and < 6000, $"{id} = {value} MHz");
                if (id.StartsWith("disk/", StringComparison.Ordinal) || id.StartsWith("network/", StringComparison.Ordinal) || id.Contains("power", StringComparison.Ordinal))
                    Assert.True(value >= 0, $"{id} = {value}");
            }

            if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64)
            {
                // Apple Silicon: the E/P split, cluster frequencies, GPU load and the CPU temperature (E-cores are never
                // gated, so the CPU average always has a plausible key).
                foreach (var id in new[] { "cpu/load-e", "cpu/load-p", "cpu/frequency-e", "cpu/frequency-p", "gpu/load", "cpu/temperature" })
                    Assert.True(values.ContainsKey(id), $"{id} has a reading");
            }
        }
        finally
        {
            await source.StopAsync(Ct);
        }
    }

    private static string StatusText(SensorStatus status) =>
        System.Text.Json.JsonSerializer.Serialize(status).Trim('"');
}
