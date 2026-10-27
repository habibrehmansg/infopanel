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
            ["denied"] = "permission-denied",
        }, sensors);
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
}
