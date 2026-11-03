using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InfoPanel.SensorHelper;
using InfoPanel.Sensors.Libre;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>
/// "Read all sensors" over the API: <c>elevation</c> on the Libre source's entry only, valid against the schema; and
/// <c>POST</c> and <c>DELETE /api/sensors/sources/libre/elevation</c>, the session token's: 202 setting up, 409 for a
/// second change meanwhile and with nothing to set up (a replay, the helper outside Program Files, the engine
/// elevated), 403 for an integration token, every outcome as <c>lastChange</c>, and a new sensor helper after
/// <c>on</c> and after removal only. The engines replay the Libre recording; Windows and the shell are fakes (nothing is
/// ever run).
/// </summary>
public sealed class LibreElevationEndpointTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private const string Installed = @"C:\Program Files\InfoPanel\sensor-helper\InfoPanel.SensorHelper.exe";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "infopanel-elevation-api-" + Guid.NewGuid().ToString("N"));
    private readonly string _helper;
    private readonly FakeWindows _windows = new();
    private readonly FakeShell _shell = new();
    private int _helperChanges;

    public LibreElevationEndpointTests()
    {
        Directory.CreateDirectory(_folder);
        _helper = Path.Combine(_folder, "InfoPanel.SensorHelper.exe");
        File.WriteAllBytes(_helper, "MZ a stand-in for the helper"u8.ToArray());
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static HttpClient Client(WebApplicationFactory<Program> engine, string? token = EngineFactory.Token)
    {
        var client = engine.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        if (token != null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>An engine replaying the Libre recording; with <paramref name="windows"/>, its elevation on this test's fake Windows, helper and shell.</summary>
    private WebApplicationFactory<Program> Replaying(EngineFactory engine, bool windows = true) => engine.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Engine:MockSeed", "");
        builder.UseSetting("Engine:ReplaySensors", LibreFixture.Folder);
        if (windows)
            builder.ConfigureTestServices(services => services.AddSingleton(sp =>
            {
                var libre = sp.GetRequiredService<LibreSource>();
                return new LibreElevation(_helper, _windows, _shell, () =>
                {
                    Interlocked.Increment(ref _helperChanges);
                    libre.HelperChanged();
                });
            }));
    });

    private static async Task<List<JsonElement>> Sources(HttpClient client)
    {
        var body = await client.GetFromJsonAsync<JsonElement>("/api/sensors/sources", Ct);
        var list = body.EnumerateArray().ToList();
        Assert.All(list, s => Assert.True(ReadEndpointTests.Valid(s, "sensorSource")));
        return list;
    }

    private static async Task<JsonElement> Elevation(HttpClient client) =>
        (await Sources(client)).Single(s => s.GetProperty("id").GetString() == "libre").GetProperty("elevation");

    private static async Task<JsonElement> ElevationWhen(HttpClient client, Func<JsonElement, bool> ready)
    {
        for (var i = 0; i < 300; i++)
        {
            var elevation = await Elevation(client);
            if (ready(elevation))
                return elevation;
            await Task.Delay(50, Ct);
        }

        throw new Xunit.Sdk.XunitException("The elevation never got there: " + (await Elevation(client)));
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Change(HttpClient client, bool on)
    {
        using var response = on
            ? await client.PostAsync("/api/sensors/sources/libre/elevation", null, Ct)
            : await client.DeleteAsync("/api/sensors/sources/libre/elevation", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    [Fact]
    public async Task AReplay_SaysUnavailable_NotWindows_OnLibresEntryOnly_AndBothChangesAre409()
    {
        using var engine = new EngineFactory();
        using var replaying = Replaying(engine, windows: false);
        using var client = Client(replaying);

        var sources = await Sources(client);
        Assert.All(sources.Where(s => s.GetProperty("id").GetString() != "libre"), s => Assert.False(s.TryGetProperty("elevation", out _)));
        var elevation = await Elevation(client);
        Assert.Equal(["state", "reason", "lastChange"], elevation.EnumerateObject().Select(p => p.Name));
        Assert.Equal("unavailable", elevation.GetProperty("state").GetString());
        Assert.Equal("notWindows", elevation.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, elevation.GetProperty("lastChange").ValueKind);

        foreach (var on in new[] { true, false })
        {
            var (status, body) = await Change(client, on);
            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.True(ReadEndpointTests.Valid(body, "problem"));
        }

        Assert.Empty(_shell.Starts);
    }

    [Fact]
    public async Task AMockEngine_HasNoLibre_AndAChangeIs409()
    {
        using var engine = new EngineFactory();
        using var client = Client(engine);

        Assert.DoesNotContain(await Sources(client), s => s.TryGetProperty("elevation", out _));
        Assert.Equal(HttpStatusCode.Conflict, (await Change(client, on: true)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Change(client, on: false)).Status);
    }

    [Fact]
    public async Task OutsideProgramFiles_ItIsUnavailable_NotInstalled_AndAChangeIs409_WithNoPrompt()
    {
        _windows.Final = @"\\?\C:\Users\me\Downloads\InfoPanel\sensor-helper\InfoPanel.SensorHelper.exe";
        using var engine = new EngineFactory();
        using var replaying = Replaying(engine);
        using var client = Client(replaying);

        var elevation = await Elevation(client);
        Assert.Equal(("unavailable", "notInstalled"), (elevation.GetProperty("state").GetString(), elevation.GetProperty("reason").GetString()));
        var (status, body) = await Change(client, on: true);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.True(ReadEndpointTests.Valid(body, "problem"));
        Assert.Empty(_shell.Starts);
    }

    [Fact]
    public async Task WithTheEngineElevated_ItIsUnavailable_EngineElevated()
    {
        _windows.EngineElevated = true;
        using var engine = new EngineFactory();
        using var replaying = Replaying(engine);
        using var client = Client(replaying);

        var elevation = await Elevation(client);
        Assert.Equal(("unavailable", "engineElevated"), (elevation.GetProperty("state").GetString(), elevation.GetProperty("reason").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, (await Change(client, on: true)).Status);
        Assert.Empty(_shell.Starts);
    }

    [Fact]
    public async Task SettingUp_Is202_ThenOneAtATime_ThenOn_AndANewHelperStarts()
    {
        using var engine = new EngineFactory();
        using var replaying = Replaying(engine);
        using var client = Client(replaying);
        Assert.Equal("off", (await Elevation(client)).GetProperty("state").GetString());
        Assert.False((await Elevation(client)).TryGetProperty("reason", out _));

        var (status, body) = await Change(client, on: true);
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.True(ReadEndpointTests.Valid(body, "libreElevation"));
        Assert.Equal("settingUp", body.GetProperty("state").GetString());
        var start = await _shell.Started(1);
        Assert.Equal((@"C:\WINDOWS\system32\cmd.exe", "runas", true), (start.FileName, start.Verb, start.UseShellExecute));
        Assert.EndsWith("& \"" + Installed + "\" --register --user " + Sid + "\"", start.Arguments); // through the command processor (LibreElevationTests pins its line)

        Assert.Equal("settingUp", (await Elevation(client)).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Change(client, on: true)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Change(client, on: false)).Status);
        Assert.Single(_shell.Starts);
        Assert.Equal(0, _helperChanges);

        _windows.Task = (Installed, HelperTask.ServeArguments(Sid));
        _shell.Exit(0);
        var elevation = await ElevationWhen(client, e => e.GetProperty("state").GetString() != "settingUp");
        Assert.Equal("on", elevation.GetProperty("state").GetString());
        Assert.Equal("on", elevation.GetProperty("lastChange").GetProperty("outcome").GetString());
        Assert.False(elevation.GetProperty("lastChange").TryGetProperty("code", out _));
        Assert.Equal(1, _helperChanges);
    }

    [Fact]
    public async Task OnWithTheHelperFallenBack_SaysWhy_UnderElevation()
    {
        _windows.Task = (Installed, HelperTask.ServeArguments(Sid));
        using var engine = new EngineFactory();
        using var replaying = Replaying(engine);
        using var client = Client(replaying);
        Assert.False((await Elevation(client)).TryGetProperty("fallback", out _));

        replaying.Services.GetRequiredService<LibreElevation>().HelperFellBack(new LibreHelperFallback(LibreHelperChooser.NotServing, "nothing served its pipe within 10 s"));
        var elevation = await Elevation(client);

        Assert.Equal("on", elevation.GetProperty("state").GetString());
        Assert.Equal("notServing", elevation.GetProperty("fallback").GetProperty("code").GetString());
        Assert.Equal("nothing served its pipe within 10 s", elevation.GetProperty("fallback").GetProperty("message").GetString());
    }

    [Fact]
    public async Task TurningOff_Is202_RunsUnregister_ThenOff_AndANewHelperStarts()
    {
        _windows.Task = (Installed, HelperTask.ServeArguments(Sid));
        using var engine = new EngineFactory();
        using var replaying = Replaying(engine);
        using var client = Client(replaying);
        Assert.Equal("on", (await Elevation(client)).GetProperty("state").GetString());

        var (status, body) = await Change(client, on: false);
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal("settingUp", body.GetProperty("state").GetString());
        var start = await _shell.Started(1);
        Assert.Equal((@"C:\WINDOWS\system32\cmd.exe", "runas"), (start.FileName, start.Verb));
        Assert.EndsWith("& \"" + Installed + "\" --unregister --user " + Sid + "\"", start.Arguments);

        _windows.Task = null;
        _shell.Exit(0);
        var elevation = await ElevationWhen(client, e => e.GetProperty("state").GetString() != "settingUp");
        Assert.Equal("off", elevation.GetProperty("state").GetString());
        Assert.Equal("off", elevation.GetProperty("lastChange").GetProperty("outcome").GetString());
        Assert.Equal(1, _helperChanges);
    }

    [Fact]
    public async Task ADeclinedPrompt_IsDeclined_NoNewHelper()
    {
        _shell.Refusal = () => new Win32Exception(1223);
        using var engine = new EngineFactory();
        using var replaying = Replaying(engine);
        using var client = Client(replaying);

        Assert.Equal(HttpStatusCode.Accepted, (await Change(client, on: true)).Status);
        var elevation = await ElevationWhen(client, e => e.GetProperty("lastChange").ValueKind != JsonValueKind.Null);

        Assert.Equal("off", elevation.GetProperty("state").GetString());
        Assert.Equal("declined", elevation.GetProperty("lastChange").GetProperty("outcome").GetString());
        Assert.Equal(0, _helperChanges);
    }

    [Fact]
    public async Task AFailedSetUp_IsFailed_WithItsCode_NoNewHelper()
    {
        using var engine = new EngineFactory();
        using var replaying = Replaying(engine);
        using var client = Client(replaying);

        Assert.Equal(HttpStatusCode.Accepted, (await Change(client, on: true)).Status);
        await _shell.Started(1);
        _shell.Exit(HelperTask.ExitSchedulerFailed);
        var elevation = await ElevationWhen(client, e => e.GetProperty("lastChange").ValueKind != JsonValueKind.Null);

        Assert.Equal("off", elevation.GetProperty("state").GetString());
        Assert.Equal("failed", elevation.GetProperty("lastChange").GetProperty("outcome").GetString());
        Assert.Equal(13, elevation.GetProperty("lastChange").GetProperty("code").GetInt32());
        Assert.Equal(0, _helperChanges);
    }

    [Fact]
    public async Task AnIntegrationToken_Is403_ForBoth_EvenWithEdit_AndNothingRuns()
    {
        using var engine = new EngineFactory();
        using var replaying = Replaying(engine);
        using var session = Client(replaying);
        using var created = await session.PostAsJsonAsync("/api/tokens", new { name = "elevation", scopes = new[] { "read", "edit" } }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var token = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString();
        using var integration = Client(replaying, token);

        foreach (var on in new[] { true, false })
        {
            var (status, body) = await Change(integration, on);
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.True(ReadEndpointTests.Valid(body, "scopeProblem"));
        }

        Assert.Empty(_shell.Starts);
        // It reads the elevation like any other token.
        Assert.Equal("off", (await Elevation(integration)).GetProperty("state").GetString());
    }

    private sealed class FakeWindows : ILibreElevationSystem
    {
        public bool EngineElevated { get; set; }

        public string? UserSid => Sid;

        public string? ProgramFiles => @"C:\Program Files";

        public string Final { get; set; } = @"\\?\" + Installed;

        public (string Path, string Arguments)? Task { get; set; }

        public string FinalPathOf(FileStream file) => Final;

        public (string Path, string Arguments)? TaskAction(string sid) => sid == Sid ? Task : null;

        public void RunTask(string sid) => throw new IOException("No task runs in these tests.");

        public string? PipeOwner(NamedPipeClientStream pipe) => null;

        public string SystemDirectory => @"C:\WINDOWS\system32";
    }

    private sealed class FakeShell : InfoPanel.Sensors.Libre.IPawnIoLauncher
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<ProcessStartInfo> Starts { get; } = [];

        public Func<Exception>? Refusal { get; set; }

        public IPawnIoProcess? Start(ProcessStartInfo start)
        {
            if (Refusal?.Invoke() is { } refusal)
                throw refusal;
            lock (Starts)
                Starts.Add(start);
            _started.TrySetResult();
            return new Process(_exit.Task);
        }

        public async Task<ProcessStartInfo> Started(int count)
        {
            await _started.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            lock (Starts)
                return Starts[count - 1];
        }

        public void Exit(int code) => _exit.TrySetResult(code);

        private sealed class Process(Task<int> exit) : IPawnIoProcess
        {
            public Task WaitForExitAsync(CancellationToken cancel) => exit.WaitAsync(cancel);

            public int ExitCode => exit.Result;

            public void Dispose()
            {
            }
        }
    }
}
