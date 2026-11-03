using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using InfoPanel.Sensors.Libre;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>
/// The PawnIO driver over the API: <c>driver</c> on the Libre source's entry only, valid against the schema; and
/// <c>POST /api/sensors/sources/libre/driver</c>, the session token's: 202 with the driver installing, 409 while an
/// install runs or without a bundled installer (a mock engine, a replay), 403 for an integration token. The engines here
/// replay the Libre recording; the install's launcher is a fake (nothing is ever run).
/// </summary>
public sealed class LibreDriverEndpointTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "infopanel-pawnio-api-" + Guid.NewGuid().ToString("N"));
    private readonly string _installer;
    private readonly string _sha256;
    private readonly FakeLauncher _launcher = new();

    public LibreDriverEndpointTests()
    {
        Directory.CreateDirectory(_folder);
        _installer = Path.Combine(_folder, "PawnIO_setup.exe");
        var bytes = "MZ a stand-in for the installer"u8.ToArray();
        File.WriteAllBytes(_installer, bytes);
        _sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static HttpClient Client(WebApplicationFactory<Program> engine, string? token = EngineFactory.Token)
    {
        var client = engine.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        if (token != null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>An engine replaying the Libre recording, its bundled installer this test's file, run by the fake launcher.</summary>
    private WebApplicationFactory<Program> Replaying(EngineFactory engine, bool withInstaller = true) => engine.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Engine:MockSeed", "");
        builder.UseSetting("Engine:ReplaySensors", LibreFixture.Folder);
        if (withInstaller)
            builder.ConfigureTestServices(services => services.AddSingleton(sp =>
                new PawnIoInstaller(_installer, _launcher, sp.GetRequiredService<LibreSource>().DriverChanged, supported: true, sha256: _sha256)));
    });

    private static async Task<List<JsonElement>> Sources(HttpClient client)
    {
        var body = await client.GetFromJsonAsync<JsonElement>("/api/sensors/sources", Ct);
        var list = body.EnumerateArray().ToList();
        Assert.All(list, s => Assert.True(ReadEndpointTests.Valid(s, "sensorSource")));
        return list;
    }

    private static async Task<JsonElement> Driver(HttpClient client) =>
        (await Sources(client)).Single(s => s.GetProperty("id").GetString() == "libre").GetProperty("driver");

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Install(HttpClient client)
    {
        using var response = await client.PostAsync("/api/sensors/sources/libre/driver", null, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    [Fact]
    public async Task OnlyLibresEntry_HasTheDriver_AsTheRecordedHelperSaidIt_WithTheBundledInstaller()
    {
        using var engine = new EngineFactory();
        using var replaying = Replaying(engine);
        using var client = Client(replaying);

        var sources = await Sources(client);
        Assert.All(sources.Where(s => s.GetProperty("id").GetString() != "libre"), s => Assert.False(s.TryGetProperty("driver", out _)));
        var driver = await Driver(client);
        Assert.Equal(["state", "version", "installer", "lastInstall"], driver.EnumerateObject().Select(p => p.Name));
        Assert.Equal("missing", driver.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, driver.GetProperty("version").ValueKind);
        Assert.Equal("2.2.0", driver.GetProperty("installer").GetString());
        Assert.Equal(JsonValueKind.Null, driver.GetProperty("lastInstall").ValueKind);
    }

    [Fact]
    public async Task AReplayWithoutAnInstaller_SaysNone_AndTheInstallIs409()
    {
        using var engine = new EngineFactory();
        using var replaying = Replaying(engine, withInstaller: false);
        using var client = Client(replaying);

        Assert.Equal(JsonValueKind.Null, (await Driver(client)).GetProperty("installer").ValueKind);
        var (status, body) = await Install(client);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.True(ReadEndpointTests.Valid(body, "problem"));
        Assert.Empty(_launcher.Starts);
    }

    [Fact]
    public async Task AMockEngine_HasNoLibre_AndTheInstallIs409()
    {
        using var engine = new EngineFactory();
        using var client = Client(engine);

        Assert.DoesNotContain(await Sources(client), s => s.TryGetProperty("driver", out _));
        Assert.Equal(HttpStatusCode.Conflict, (await Install(client)).Status);
    }

    [Fact]
    public async Task AnInstall_Is202Installing_ThenOneAtATime_ThenItsOutcome()
    {
        using var engine = new EngineFactory();
        using var replaying = Replaying(engine);
        using var client = Client(replaying);

        var (status, body) = await Install(client);
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.True(ReadEndpointTests.Valid(body, "libreDriver"));
        Assert.Equal("installing", body.GetProperty("state").GetString());
        Assert.Equal("2.2.0", body.GetProperty("installer").GetString());
        var process = await _launcher.Launched;
        Assert.Equal((_installer, "-install -silent", "runas"), (_launcher.Starts[0].FileName, _launcher.Starts[0].Arguments, _launcher.Starts[0].Verb));

        Assert.Equal("installing", (await Driver(client)).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Install(client)).Status);
        Assert.Single(_launcher.Starts);

        process.Exit(PawnIoInstaller.RebootRequired);
        JsonElement driver = default;
        for (var i = 0; i < 100; i++)
        {
            driver = await Driver(client);
            if (driver.GetProperty("state").GetString() != "installing")
                break;
            await Task.Delay(50, Ct);
        }

        Assert.Equal("missing", driver.GetProperty("state").GetString());
        Assert.Equal("restartNeeded", driver.GetProperty("lastInstall").GetProperty("outcome").GetString());
        Assert.False(driver.GetProperty("lastInstall").TryGetProperty("code", out _));
    }

    [Fact]
    public async Task AnIntegrationToken_Is403_EvenWithEdit_AndNothingRuns()
    {
        using var engine = new EngineFactory();
        using var replaying = Replaying(engine);
        using var session = Client(replaying);
        using var created = await session.PostAsJsonAsync("/api/tokens", new { name = "driver", scopes = new[] { "read", "edit" } }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var token = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString();
        using var integration = Client(replaying, token);

        var (status, body) = await Install(integration);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.True(ReadEndpointTests.Valid(body, "scopeProblem"));
        Assert.Empty(_launcher.Starts);
        // It reads the driver like any other token.
        Assert.Equal("missing", (await Driver(integration)).GetProperty("state").GetString());
    }

    /// <summary>Records each start and hands back a process the test ends; never starts anything.</summary>
    private sealed class FakeLauncher : IPawnIoLauncher
    {
        private readonly TaskCompletionSource<FakeProcess> _launched = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<ProcessStartInfo> Starts { get; } = [];

        public Task<FakeProcess> Launched => _launched.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);

        public IPawnIoProcess? Start(ProcessStartInfo start)
        {
            Starts.Add(start);
            var process = new FakeProcess();
            _launched.TrySetResult(process);
            return process;
        }
    }

    private sealed class FakeProcess : IPawnIoProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Exit(int code) => _exit.TrySetResult(code);

        public Task WaitForExitAsync(CancellationToken cancel) => _exit.Task.WaitAsync(cancel);

        public int ExitCode => _exit.Task.Result;

        public void Dispose()
        {
        }
    }
}
