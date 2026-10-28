using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InfoPanel.Models;
using InfoPanel.Sensors;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>
/// The sensor status in the API: <c>/api/sensors</c> lists every sensor with its status, <c>/api/sensors/values</c>
/// only those with a reading (live or stale), and an item's <c>meta.sensor.status</c> follows the hub.
/// </summary>
public sealed class SensorStatusEndpointTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Four sensors, one per source status, under <c>/test.status/</c>.</summary>
    private sealed class StatusSource : ISensorSource, ISensorStatusSource
    {
        private static readonly (string Id, SensorStatus Status)[] Sensors =
        [
            ("live", SensorStatus.Live),
            ("stale", SensorStatus.Stale),
            ("unavailable", SensorStatus.Unavailable),
            ("denied", SensorStatus.PermissionDenied),
        ];

        public string Id => "test.status";

        public bool Handles(SensorKey key) => key.Type == Enums.SensorType.Plugin && key.Id.StartsWith("/test.status/", StringComparison.Ordinal);

        public IReadOnlyList<SensorInfo> GetSensors() =>
            Sensors.Select(s => new SensorInfo(SensorKey.Plugin("/test.status/" + s.Id), Id, "Status", s.Id, "u")).ToList();

        public SensorStatus GetStatus(SensorKey key) =>
            Sensors.FirstOrDefault(s => "/test.status/" + s.Id == key.Id) is { Id: not null } s ? s.Status : SensorStatus.Unavailable;

        public bool TryRead(SensorKey key, out SensorReading reading)
        {
            reading = new SensorReading(1, 3, 2, 2, "u");
            return GetStatus(key) is SensorStatus.Live or SensorStatus.Stale;
        }

        public SensorState ReadState(SensorKey key) =>
            TryRead(key, out var reading) ? new SensorState(GetStatus(key), SensorKind.Number, reading) : SensorState.Without(GetStatus(key));

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// One sensor that is stale with 10 W until the first call into the source, then live with 20 W: a poll landing
    /// between two calls. A caller that reads the value and the status in separate calls pairs 10 with live.
    /// </summary>
    private sealed class PollingSource : ISensorSource, ISensorStatusSource
    {
        private static readonly SensorKey Key = SensorKey.Plugin("/test.poll/a");
        private int _calls;

        public string Id => "test.poll";

        public bool Handles(SensorKey key) => key == Key;

        public IReadOnlyList<SensorInfo> GetSensors() => [new SensorInfo(Key, Id, "Poll", "a", "W")];

        private SensorState Current() =>
            _calls == 0
                ? new SensorState(SensorStatus.Stale, SensorKind.Number, new SensorReading(10, 10, 10, 10, "W"))
                : new SensorState(SensorStatus.Live, SensorKind.Number, new SensorReading(20, 20, 20, 20, "W"));

        public SensorState ReadState(SensorKey key)
        {
            var state = Current();
            _calls++;
            return state;
        }

        public bool TryRead(SensorKey key, out SensorReading reading)
        {
            var state = ReadState(key);
            reading = state.Reading;
            return state.HasReading;
        }

        public SensorStatus GetStatus(SensorKey key) => ReadState(key).Status;

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private readonly EngineFactory _base = new();
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;

    public SensorStatusEndpointTests()
    {
        _factory = _base.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ISensorSource>(new StatusSource())));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _base.Dispose();
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", EngineFactory.Token);
        return client;
    }

    private async Task<List<JsonElement>> GetArray(string path)
    {
        using var client = Client();
        var json = await client.GetFromJsonAsync<JsonElement>(path, Ct);
        return json.EnumerateArray().ToList();
    }

    [Fact]
    public async Task Sensors_CarryEveryStatus()
    {
        var sensors = (await GetArray("/api/sensors")).Where(s => s.GetProperty("source").GetString() == "test.status")
            .ToDictionary(s => s.GetProperty("name").GetString()!, s => s.GetProperty("status").GetString());

        Assert.Equal(new Dictionary<string, string?>
        {
            ["live"] = "live",
            ["stale"] = "stale",
            ["unavailable"] = "unavailable",
            ["denied"] = "denied",
        }, sensors);
    }

    [Fact]
    public void Scenes_SeeTheStatuses_AsBindingsMapThem()
    {
        var inputs = _factory.Services.GetRequiredService<InfoPanel.Scenes.SceneRuntimeOptions>().Inputs;

        Assert.Equal(InfoPanel.Scenes.SceneInputStatus.Live, inputs.Read("plugin:/test.status/live", "number").Status);
        Assert.Equal(InfoPanel.Scenes.SceneInputStatus.Stale, inputs.Read("plugin:/test.status/stale", "number").Status);
        Assert.Equal(InfoPanel.Scenes.SceneInputStatus.Unavailable, inputs.Read("plugin:/test.status/unavailable", "number").Status);
        Assert.Equal(InfoPanel.Scenes.SceneInputStatus.Denied, inputs.Read("plugin:/test.status/denied", "number").Status);
    }

    [Fact]
    public async Task Values_ListOnlySensorsWithAReading_WithTheirStatus()
    {
        var values = (await GetArray("/api/sensors/values")).Where(v => v.GetProperty("key").GetString()!.StartsWith("plugin:/test.status/", StringComparison.Ordinal))
            .ToDictionary(v => v.GetProperty("key").GetString()!, v => v.GetProperty("status").GetString());

        Assert.Equal(new Dictionary<string, string?>
        {
            ["plugin:/test.status/live"] = "live",
            ["plugin:/test.status/stale"] = "stale",
        }, values);
    }

    [Fact]
    public async Task Values_ReadAValueAndItsStatusInOneCall_SoAPollBetweenThemCannotMixThem()
    {
        using var factory = _base.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ISensorSource>(new PollingSource())));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", EngineFactory.Token);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/sensors/values", Ct);
        var value = json.EnumerateArray().Single(v => v.GetProperty("key").GetString() == "plugin:/test.poll/a");

        // The state the one call returned: the stale 10, not the live status the poll made after it.
        Assert.Equal(10, value.GetProperty("now").GetDouble());
        Assert.Equal("stale", value.GetProperty("status").GetString());
    }
}
