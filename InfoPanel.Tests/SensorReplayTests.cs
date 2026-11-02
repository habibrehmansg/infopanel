using System.Net.Http.Json;
using System.Text.Json;
using InfoPanel.Hosting;
using InfoPanel.Sensors;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>The scrubbed HWiNFO frames recorded on a real PC (<c>InfoPanel.Core.Tests/Fixtures/hwinfo</c>), a recording <c>--replay-sensors</c> plays.</summary>
internal static class HwInfoFixture
{
    public static string Folder { get; } = Find(Path.Combine("InfoPanel.Core.Tests", "Fixtures", "hwinfo"));

    private static string Find(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException($"{relative} not found above {AppContext.BaseDirectory}");
    }
}

/// <summary>The PC's Libre answers recorded without its driver (<c>InfoPanel.Core.Tests/Fixtures/libre</c>), a recording <c>--replay-sensors</c> plays.</summary>
internal static class LibreFixture
{
    public static string Folder { get; } = Path.Combine(Path.GetDirectoryName(HwInfoFixture.Folder)!, "libre");
}

/// <summary>
/// Recording and replaying sensors from the command line: <c>--replay-sensors</c> plays a recording in place of the
/// platform's sources (the session says mock), refused with <c>--mock</c>; <c>--record-sensors</c> and its options.
/// </summary>
public sealed class SensorReplayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EngineOptions? Load(string[] args, out IReadOnlyList<string> errors)
    {
        var parsed = EngineCommandLine.Parse(args);
        Assert.Null(parsed.Error);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(parsed.Settings).Build();
        return EngineOptions.Load(configuration, out errors);
    }

    [Fact]
    public void ReplaySensors_TakesARecordingFolder_AndSaysTheSensorsArentReal()
    {
        var options = Load(["--replay-sensors", HwInfoFixture.Folder], out var errors);
        Assert.Empty(errors);
        Assert.Equal(Path.GetFullPath(HwInfoFixture.Folder), options!.ReplaySensors);
        Assert.False(options.Mock);
        Assert.True(options.SensorsNotReal);
        Assert.False(Load([], out _)!.SensorsNotReal);
        Assert.Contains("--replay-sensors", EngineCommandLine.Usage);
    }

    [Theory]
    [InlineData("--mock")]
    [InlineData("--mock-seed")]
    public void ReplaySensors_WithMock_IsRefused(string mock)
    {
        string[] args = mock == "--mock" ? ["--replay-sensors", HwInfoFixture.Folder, "--mock"] : ["--replay-sensors", HwInfoFixture.Folder, "--mock-seed", "3"];
        Assert.Null(Load(args, out var errors));
        Assert.Contains(errors, e => e.Contains("can't be used with --mock", StringComparison.Ordinal));
    }

    [Fact]
    public void ReplaySensors_OfAFolderWithNoRecording_IsRefused()
    {
        var empty = Path.Combine(Path.GetTempPath(), "infopanel-no-recording-" + Guid.NewGuid().ToString("N"));
        Assert.Null(Load(["--replay-sensors", empty], out var errors));
        Assert.Contains(errors, e => e.StartsWith("--replay-sensors", StringComparison.Ordinal) && e.Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void RecordSensors_Options_NeedEachOther_AndAreChecked()
    {
        Assert.Null(Load(["--record-seconds", "10"], out var errors));
        Assert.Contains("--record-seconds needs --record-sensors.", errors);
        Assert.Null(Load(["--scrub"], out errors));
        Assert.Contains("--scrub needs --record-sensors.", errors);

        var folder = Path.Combine(Path.GetTempPath(), "infopanel-record-" + Guid.NewGuid().ToString("N"));
        Assert.Null(Load(["--record-sensors", folder, "--record-seconds", "0"], out errors));
        Assert.Contains(errors, e => e.Contains("--record-seconds must be a number from 1 to 3600", StringComparison.Ordinal));

        var options = Load(["--record-sensors", folder, "--record-seconds", "60", "--scrub"], out errors);
        if (OperatingSystem.IsWindows())
        {
            Assert.Empty(errors);
            Assert.Equal((Path.GetFullPath(folder), 60, true), (options!.RecordSensors, options.RecordSeconds, options.Scrub));
            Assert.Null(Load(["--record-sensors", folder, "--mock"], out errors));
            Assert.Contains(errors, e => e.Contains("can't be used with --mock", StringComparison.Ordinal));
        }
        else
        {
            Assert.Null(options);
            Assert.Contains(errors, e => e.Contains("only Windows", StringComparison.Ordinal));
        }

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task AReplayingEngine_ListsTheRecordedHWiNFOSensors_LiveAndCycling_AndTheSessionSaysMock()
    {
        using var engine = new EngineFactory();
        using var factory = engine.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Engine:MockSeed", "");
            builder.UseSetting("Engine:ReplaySensors", HwInfoFixture.Folder);
        });
        using var api = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        api.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", EngineFactory.Token);

        var session = await api.GetFromJsonAsync<JsonElement>("/api/session", Ct);
        Assert.True(session.GetProperty("mock").GetBoolean());

        var sensors = await api.GetFromJsonAsync<JsonElement>("/api/sensors", Ct);
        var hwinfo = sensors.EnumerateArray().Where(s => s.GetProperty("source").GetString() == "hwinfo").ToList();
        Assert.True(hwinfo.Count > 100, $"{hwinfo.Count} HWiNFO sensors");
        Assert.All(hwinfo, s => Assert.StartsWith("hwinfo:-1:", s.GetProperty("key").GetString()));
        Assert.All(hwinfo, s => Assert.Equal("live", s.GetProperty("status").GetString()));
        Assert.Single(factory.Services.GetServices<ISensorSource>().Where(s => s.Id == "hwinfo"));

        // The recording plays round after round: within a few seconds some reading changes.
        var first = await Values(api);
        var changed = false;
        for (var i = 0; i < 40 && !changed; i++)
        {
            await Task.Delay(250, Ct);
            var next = await Values(api);
            changed = next.Any(v => first.TryGetValue(v.Key, out var was) && was != v.Value);
        }

        Assert.True(changed, "no reading changed while the recording played");

        // A key the recording doesn't have reads unavailable, not simulated.
        var missing = await api.GetFromJsonAsync<JsonElement>("/api/sensors/values", Ct);
        Assert.DoesNotContain(missing.EnumerateArray(), v => v.GetProperty("key").GetString() == "hwinfo:-1:1:0:1");
    }

    private static async Task<Dictionary<string, double>> Values(HttpClient api)
    {
        var values = await api.GetFromJsonAsync<JsonElement>("/api/sensors/values", Ct);
        return values.EnumerateArray().Where(v => v.GetProperty("key").GetString()!.StartsWith("hwinfo:", StringComparison.Ordinal))
            .ToDictionary(v => v.GetProperty("key").GetString()!, v => v.GetProperty("now").GetDouble());
    }

    [Fact]
    public async Task TheEngine_StartedWithReplaySensorsAndMock_ExitsWith2()
    {
        var root = Path.Combine(Path.GetTempPath(), "infopanel-replay-mock-" + Guid.NewGuid().ToString("N"));
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "InfoPanel.exe" : "InfoPanel"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "--data-dir", Path.Combine(root, "data"), "--v1-dir", Path.Combine(root, "v1"), "--port", EngineRuntime.FindFreePort().ToString(), "--no-browser", "--no-windows", "--no-tray", "--no-plugins", "--offline", "--mock", "--replay-sensors", HwInfoFixture.Folder })
            start.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var errors = process.StandardError.ReadToEndAsync(Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }

        var said = await output + await errors;
        Assert.True(process.ExitCode == 2, $"exit {process.ExitCode}: {said}");
        Assert.Contains("--replay-sensors", said);
        Assert.False(Directory.Exists(Path.Combine(root, "data")));
    }
}
