using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InfoPanel.Hosting;
using InfoPanel.Models;
using InfoPanel.Sensors;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>
/// Start-up waits (up to <see cref="SensorServiceOptions.StartupWait"/>) for every source's discovery before the host
/// serves, so a client that reads <c>/api/sensors</c> once at start-up sees the full catalogue; a source that takes
/// longer doesn't hold start-up past the bound.
/// </summary>
public sealed class SensorStartupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One sensor under <c>/test.slow/</c>, listed only once its start has waited <c>discovery</c>.</summary>
    private sealed class SlowSource(TimeSpan discovery) : ISensorSource
    {
        private static readonly SensorInfo[] Sensors =
            [new(SensorKey.Plugin("/test.slow/value"), "test.slow", "Slow", "value", "u")];

        private volatile SensorInfo[] _sensors = [];

        public bool StartCancelled { get; private set; }

        public string Id => "test.slow";

        public bool Handles(SensorKey key) => key.Type == Enums.SensorType.Plugin && key.Id.StartsWith("/test.slow/", StringComparison.Ordinal);

        public IReadOnlyList<SensorInfo> GetSensors() => _sensors;

        public bool TryRead(SensorKey key, out SensorReading reading)
        {
            reading = new SensorReading(1, 1, 1, 1, "u");
            return _sensors.Length > 0 && Handles(key);
        }

        public async Task StartAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(discovery, ct);
            }
            catch (OperationCanceledException)
            {
                StartCancelled = true;
                throw;
            }

            _sensors = Sensors;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static WebApplicationFactory<Program> Engine(EngineFactory engine, SlowSource source, TimeSpan startupWait) =>
        engine.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ISensorSource>(source);
            services.AddSingleton(new SensorServiceOptions(startupWait));
        }));

    private static async Task<List<string>> SlowSensorKeys(HttpClient client)
    {
        var sensors = await client.GetFromJsonAsync<JsonElement>("/api/sensors", Ct);
        return sensors.EnumerateArray().Where(s => s.GetProperty("source").GetString() == "test.slow")
            .Select(s => s.GetProperty("key").GetString()!).ToList();
    }

    [Fact]
    public async Task Sensors_AreCompleteOnceTheServerAnswers_WhenDiscoveryFinishesWithinTheBound()
    {
        using var engine = new EngineFactory();
        var source = new SlowSource(TimeSpan.FromMilliseconds(500));
        using var factory = Engine(engine, source, TimeSpan.FromSeconds(30));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", EngineFactory.Token);

        Assert.Equal(["plugin:/test.slow/value"], await SlowSensorKeys(client));
    }

    [Fact]
    public async Task StartupCompletes_WhenDiscoveryOutlastsTheBound()
    {
        using var engine = new EngineFactory();
        var source = new SlowSource(Timeout.InfiniteTimeSpan);
        var factory = Engine(engine, source, TimeSpan.FromMilliseconds(200));
        try
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", EngineFactory.Token);

            using var health = await client.GetAsync("/healthz", Ct);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            // The rest of the catalogue is served; the source still discovering lists nothing yet.
            Assert.Empty(await SlowSensorKeys(client));
            var all = await client.GetFromJsonAsync<JsonElement>("/api/sensors", Ct);
            Assert.Contains(all.EnumerateArray(), s => s.GetProperty("source").GetString() == "infopanel.mock");
        }
        finally
        {
            factory.Dispose();
        }

        // Shutdown cancels the start still in progress.
        Assert.True(source.StartCancelled);
    }
}
