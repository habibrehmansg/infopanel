using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InfoPanel.Core.Tests.Panels.Usb;
using InfoPanel.Documents;
using InfoPanel.Hosting;
using InfoPanel.Output;
using InfoPanel.Panels;
using InfoPanel.Panels.BeadaPanel;
using InfoPanel.Panels.Usb;
using InfoPanel.Preview;
using InfoPanel.Spec;
using InfoPanel.Tests.Preview;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoPanel.Tests.Panels;

/// <summary>
/// The USB panel devices' API (spec/docs/api.md "Devices", the <c>devices</c> feature) over the fake USB backend:
/// <c>GET /api/devices</c> (every token's; <c>supported</c> false with each reason), <c>PATCH</c> and <c>DELETE
/// /api/devices/{id}</c> (the session token's; 200, 204, 404, 409 while <c>devices.json</c> can't be held, 422), changes
/// applied live and saved to <c>devices.json</c>, and the <c>devices</c> socket message.
/// </summary>
public sealed class DeviceEndpointTests
{
    private const string Serial = "934072005c";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>
    /// An engine with panels on, over <paramref name="backend"/> (or the libusb candidates given), <c>devices.json</c>
    /// written by <paramref name="storeWriter"/> when given.
    /// </summary>
    private static WebApplicationFactory<Program> WithPanels(EngineFactory engine, FakeUsbBackend? backend, Func<IReadOnlyList<string>>? libraries = null,
        Action<string, byte[], Action>? storeWriter = null) =>
        engine.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Engine:NoPanels", "false");
            builder.ConfigureTestServices(services => services.AddSingleton(backend != null
                ? new PanelServiceOptions { Backend = () => backend, Runner = new PanelRunnerOptions { KeepAlive = Timeout.InfiniteTimeSpan }, StoreWriter = storeWriter }
                : new PanelServiceOptions { LibraryCandidates = libraries! }));
        });

    /// <summary>
    /// Writes <c>devices.json</c> as the engine does; once armed, its next write fails after the file is replaced, as a
    /// flush of its folder that fails does.
    /// </summary>
    private sealed class FlushFault
    {
        private int _armed;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public bool Fired => Volatile.Read(ref _armed) == 0;

        public void Write(string path, byte[] bytes, Action replaced) => AtomicFile.Write(path, bytes, null, () =>
        {
            replaced();
            if (Interlocked.Exchange(ref _armed, 0) == 1)
                throw new IOException("The folder couldn't be flushed.");
        });
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, string token = EngineFactory.Token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static async Task<string> ReadToken(HttpClient session)
    {
        using var created = await session.PostAsJsonAsync("/api/tokens", new { name = "devices", scopes = new[] { "read", "edit" } }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString()!;
    }

    private static FakeUsbDevice Panel(FakeUsbBackend backend, byte address = 4, string port = "0-1", string serial = Serial) =>
        backend.Add(new FakeUsbDevice(new UsbDeviceId(BeadaPanelProtocol.VendorId, BeadaPanelProtocol.ProductId, 0, address, port))
        {
            Reply = PanelReplies.Info(2, 5, 21, 1920, 480, serial),
        });

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpClient client, HttpMethod method, string path, string? json = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = json == null ? null : Body(json) };
        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    private static async Task<JsonElement> List(HttpClient client)
    {
        var (status, body) = await Send(client, HttpMethod.Get, "/api/devices");
        Assert.Equal(HttpStatusCode.OK, status);
        return body;
    }

    /// <summary>The first device listed once <paramref name="wanted"/> holds for it (15 s at most).</summary>
    private static async Task<JsonElement> Until(HttpClient client, Func<JsonElement, bool> wanted, int count = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var devices = (await List(client)).GetProperty("devices");
            if (devices.GetArrayLength() == count && wanted(devices[count - 1]))
                return devices[count - 1];
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"never got there: {devices}");
            await Task.Delay(20, Ct);
        }
    }

    private static async Task<string> ProfileNamed(HttpClient client, string name)
    {
        var profiles = await client.GetFromJsonAsync<JsonElement>("/api/profiles", Ct);
        return profiles.EnumerateArray().First(p => p.GetProperty("name").GetString() == name).GetProperty("id").GetString()!;
    }

    /// <summary>A new empty profile, which can be shown at once.</summary>
    private static async Task<string> AnotherProfile(HttpClient client)
    {
        var id = Guid.NewGuid().ToString("D");
        using var created = await client.PostAsync("/api/profiles", Body($$"""{"id":"{{id}}","name":"Another","width":480,"height":120}"""), Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return id;
    }

    [Fact]
    public async Task Devices_is_a_feature_of_the_session()
    {
        await using var engine = new EngineFactory();
        using var api = engine.CreateApiClient();
        var session = await api.GetFromJsonAsync<JsonElement>("/api/session", Ct);
        Assert.Contains("devices", session.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
    }

    [Fact]
    public async Task Under_no_panels_none_are_supported_off_and_changes_find_none()
    {
        await using var engine = new EngineFactory();
        using var api = engine.CreateApiClient();

        var list = await List(api);
        Assert.False(list.GetProperty("supported").GetBoolean());
        Assert.Equal("off", list.GetProperty("reason").GetString());
        Assert.Equal(0, list.GetProperty("devices").GetArrayLength());
        Assert.Equal(0, list.GetProperty("version").GetInt64());

        const string id = "00000000-0000-4000-8000-000000000001";
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Patch, $"/api/devices/{id}", """{"settings":{"brightness":50}}""")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Delete, $"/api/devices/{id}")).Status);
    }

    [Fact]
    public async Task Without_libusb_none_are_supported_library()
    {
        await using var engine = new EngineFactory();
        var missing = Path.Combine(engine.Root, "no-libusb", "libusb-1.0.dylib");
        await using var factory = WithPanels(engine, null, () => [missing]);
        using var api = Client(factory);

        var list = await List(api);
        Assert.False(list.GetProperty("supported").GetBoolean());
        Assert.Equal("library", list.GetProperty("reason").GetString());
        Assert.Equal(0, list.GetProperty("devices").GetArrayLength());
    }

    [Fact]
    public async Task A_panels_file_it_cant_hold_answers_store_refuses_changes_with_409_and_is_never_rewritten()
    {
        await using var engine = new EngineFactory();
        Directory.CreateDirectory(engine.DataDir);
        var file = Path.Combine(engine.DataDir, "devices.json");
        const string unheld = """{"format": "infopanel.devices/1", "devices": [{"id": "not a uuid"}]}""";
        File.WriteAllText(file, unheld);
        var backend = new FakeUsbBackend();
        Panel(backend);
        await using var factory = WithPanels(engine, backend);
        using var api = Client(factory);

        var list = await List(api);
        Assert.False(list.GetProperty("supported").GetBoolean());
        Assert.Equal("store", list.GetProperty("reason").GetString());
        Assert.Equal(0, list.GetProperty("devices").GetArrayLength());

        const string id = "00000000-0000-4000-8000-000000000001";
        var patched = await Send(api, HttpMethod.Patch, $"/api/devices/{id}", """{"settings":{"enabled":true}}""");
        Assert.Equal(HttpStatusCode.Conflict, patched.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(api, HttpMethod.Delete, $"/api/devices/{id}")).Status);
        Assert.Equal(unheld, File.ReadAllText(file));
    }

    [Fact]
    public async Task A_found_panel_is_listed_off_with_its_model_settings_and_details_for_every_token()
    {
        await using var engine = new EngineFactory();
        var backend = new FakeUsbBackend();
        Panel(backend);
        await using var factory = WithPanels(engine, backend);
        using var session = Client(factory);
        using var reader = Client(factory, await ReadToken(session));

        var device = await Until(reader, d => d.GetProperty("state").GetString() == "off");
        var list = await List(reader);
        Assert.True(list.GetProperty("supported").GetBoolean());
        Assert.Equal(JsonValueKind.Null, list.GetProperty("reason").ValueKind);
        Assert.True(list.GetProperty("version").GetInt64() >= 1);

        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", device.GetProperty("id").GetString());
        Assert.Equal("beadapanel", device.GetProperty("family").GetString());
        Assert.Equal("8", device.GetProperty("model").GetProperty("id").GetString());
        Assert.Equal(1920, device.GetProperty("model").GetProperty("width").GetInt32());
        Assert.Equal(480, device.GetProperty("model").GetProperty("height").GetInt32());
        Assert.True(device.GetProperty("connected").GetBoolean());
        Assert.Equal(JsonValueKind.Null, device.GetProperty("reason").ValueKind);
        var settings = device.GetProperty("settings");
        Assert.False(settings.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("profileId").ValueKind);
        Assert.Equal(0, settings.GetProperty("rotation").GetInt32());
        Assert.Equal(100, settings.GetProperty("brightness").GetInt32());
        Assert.Equal(["enabled", "profileId", "rotation", "brightness"], device.GetProperty("supports").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(JsonValueKind.Null, device.GetProperty("stats").ValueKind);
        var details = device.GetProperty("details");
        Assert.Equal("4e58:1001", details.GetProperty("usbId").GetString());
        Assert.Equal("0-1", details.GetProperty("port").GetString());
        Assert.Equal(Serial, details.GetProperty("serial").GetString());
        Assert.Equal(849, details.GetProperty("firmware").GetInt32());
    }

    [Fact]
    public async Task Only_the_session_token_changes_or_forgets_a_device()
    {
        await using var engine = new EngineFactory();
        var backend = new FakeUsbBackend();
        Panel(backend);
        await using var factory = WithPanels(engine, backend);
        using var session = Client(factory);
        using var integration = Client(factory, await ReadToken(session));
        var id = (await Until(integration, d => d.GetProperty("state").GetString() == "off")).GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(integration, HttpMethod.Patch, $"/api/devices/{id}", """{"settings":{"brightness":50}}""")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(integration, HttpMethod.Delete, $"/api/devices/{id}")).Status);
        Assert.Equal(100, (await Until(integration, _ => true)).GetProperty("settings").GetProperty("brightness").GetInt32());
    }

    [Fact]
    public async Task A_change_merges_its_settings_applies_them_live_and_saves_them_to_devices_json()
    {
        await using var engine = new EngineFactory();
        var backend = new FakeUsbBackend();
        var panel = Panel(backend);
        await using var factory = WithPanels(engine, backend);
        using var api = Client(factory);
        var id = (await Until(api, d => d.GetProperty("state").GetString() == "off")).GetProperty("id").GetString();
        var profile = await ProfileNamed(api, "Wide bar");
        var file = Path.Combine(engine.DataDir, "devices.json");

        // On, with a profile: answered with the entry, its other settings kept, then running with its stats.
        var (status, entry) = await Send(api, HttpMethod.Patch, $"/api/devices/{id}", $$$"""{"settings":{"enabled":true,"profileId":"{{{profile}}}"}}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(id, entry.GetProperty("id").GetString());
        Assert.True(entry.GetProperty("settings").GetProperty("enabled").GetBoolean());
        Assert.Equal(profile, entry.GetProperty("settings").GetProperty("profileId").GetString());
        Assert.Equal(100, entry.GetProperty("settings").GetProperty("brightness").GetInt32());
        var running = await Until(api, d => d.GetProperty("state").GetString() == "running");
        Assert.Equal(JsonValueKind.Object, running.GetProperty("stats").ValueKind);
        using (var saved = JsonDocument.Parse(File.ReadAllText(file)))
        {
            var device = saved.RootElement.GetProperty("devices")[0];
            Assert.True(device.GetProperty("enabled").GetBoolean());
            Assert.Equal(profile, device.GetProperty("profileId").GetString());
        }

        // Brightness, live: the panel is sent it between frames, the session kept.
        (status, entry) = await Send(api, HttpMethod.Patch, $"/api/devices/{id}", """{"settings":{"brightness":40,"rotation":90}}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(40, entry.GetProperty("settings").GetProperty("brightness").GetInt32());
        Assert.Equal(90, entry.GetProperty("settings").GetProperty("rotation").GetInt32());
        Assert.True(entry.GetProperty("settings").GetProperty("enabled").GetBoolean());
        var backlight = BeadaPanelProtocol.StatusLink(StatusLinkType.SetBacklight, [BeadaPanelProtocol.BrightnessByte(40, 2)]);
        await Eventually(() => panel.Transfers.Any(t => t.Data.AsSpan().SequenceEqual(backlight)), "the brightness reached the panel");
        using (var saved = JsonDocument.Parse(File.ReadAllText(file)))
        {
            var device = saved.RootElement.GetProperty("devices")[0];
            Assert.Equal(40, device.GetProperty("brightness").GetInt32());
            Assert.Equal(90, device.GetProperty("rotation").GetInt32());
        }

        // Another profile: the session is kept (the screen isn't turned off), its sink moved.
        var ends = panel.Transfers.Count(t => IsEnd(t.Data));
        var other = await AnotherProfile(api);
        (status, entry) = await Send(api, HttpMethod.Patch, $"/api/devices/{id}", $$$"""{"settings":{"profileId":"{{{other}}}"}}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(other, entry.GetProperty("settings").GetProperty("profileId").GetString());
        await Until(api, d => d.GetProperty("state").GetString() == "running" && d.GetProperty("settings").GetProperty("profileId").GetString() == other);
        Assert.Equal(ends, panel.Transfers.Count(t => IsEnd(t.Data)));

        // Off: the session stops with the screen off.
        (status, _) = await Send(api, HttpMethod.Patch, $"/api/devices/{id}", """{"settings":{"enabled":false}}""");
        Assert.Equal(HttpStatusCode.OK, status);
        await Until(api, d => d.GetProperty("state").GetString() == "off");
        await Eventually(() => panel.Transfers.Count(t => IsEnd(t.Data)) > ends, "the screen went off");
        using (var saved = JsonDocument.Parse(File.ReadAllText(file)))
            Assert.False(saved.RootElement.GetProperty("devices")[0].GetProperty("enabled").GetBoolean());

        // An empty change keeps everything.
        (status, entry) = await Send(api, HttpMethod.Patch, $"/api/devices/{id}", """{"settings":{}}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(40, entry.GetProperty("settings").GetProperty("brightness").GetInt32());
    }

    /// <summary>The stop's screen-off: the backlight set to 0 (every stop sends it first).</summary>
    private static bool IsEnd(byte[] data) => data.AsSpan().SequenceEqual(BeadaPanelProtocol.StatusLink(StatusLinkType.SetBacklight, [0]));

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"never: {what}");
            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public async Task A_value_out_of_range_or_a_setting_the_family_doesnt_take_is_422_and_changes_nothing()
    {
        await using var engine = new EngineFactory();
        Directory.CreateDirectory(engine.DataDir);
        const string other = "00000000-0000-4000-8000-0000000000f1";
        // A device of a family this engine doesn't drive (a later engine's): listed, not connected, taking no setting.
        File.WriteAllText(Path.Combine(engine.DataDir, "devices.json"),
            $$"""{"format": "infopanel.devices/1", "devices": [{"id": "{{other}}", "family": "later", "model": null, "serial": null, "port": null, "enabled": false, "profileId": null, "rotation": 0, "brightness": 100}]}""");
        var backend = new FakeUsbBackend();
        Panel(backend);
        await using var factory = WithPanels(engine, backend);
        using var api = Client(factory);
        var id = (await Until(api, d => d.GetProperty("state").GetString() == "off", count: 2)).GetProperty("id").GetString();

        foreach (var (body, path) in new[]
        {
            ("""{"settings":{"brightness":101}}""", "/settings/brightness"),
            ("""{"settings":{"brightness":-1}}""", "/settings/brightness"),
            ("""{"settings":{"rotation":45}}""", "/settings/rotation"),
            ("""{"settings":{"enabled":"yes"}}""", "/settings/enabled"),
            ("""{"settings":{"profileId":"Not-An-Id"}}""", "/settings/profileId"),
            ("""{"settings":{"name":"Desk"}}""", "/settings/name"),
            ("""{"brightness":50}""", ""),
        })
        {
            var (status, problem) = await Send(api, HttpMethod.Patch, $"/api/devices/{id}", body);
            Assert.True(status == HttpStatusCode.UnprocessableEntity, $"{body}: {status}");
            Assert.Contains(problem.GetProperty("errors").EnumerateArray(), e => e.GetProperty("path").GetString() == path);
        }

        var later = (await List(api)).GetProperty("devices")[0];
        Assert.Equal(other, later.GetProperty("id").GetString());
        Assert.Equal("later", later.GetProperty("family").GetString());
        Assert.Equal(JsonValueKind.Null, later.GetProperty("model").ValueKind);
        Assert.False(later.GetProperty("connected").GetBoolean());
        Assert.Equal("notConnected", later.GetProperty("state").GetString());
        Assert.Equal(0, later.GetProperty("supports").GetArrayLength());
        var (refused, why) = await Send(api, HttpMethod.Patch, $"/api/devices/{other}", """{"settings":{"brightness":50}}""");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused);
        var error = Assert.Single(why.GetProperty("errors").EnumerateArray());
        Assert.Equal("/settings/brightness", error.GetProperty("path").GetString());
        Assert.Equal("device.setting", error.GetProperty("code").GetString());

        Assert.Equal(100, (await Until(api, _ => true, count: 2)).GetProperty("settings").GetProperty("brightness").GetInt32());
        Assert.Contains("\"brightness\":100", File.ReadAllText(Path.Combine(engine.DataDir, "devices.json")).Replace(" ", ""));
    }

    [Fact]
    public async Task A_device_it_doesnt_have_is_404()
    {
        await using var engine = new EngineFactory();
        await using var factory = WithPanels(engine, new FakeUsbBackend());
        using var api = Client(factory);
        const string id = "00000000-0000-4000-8000-0000000000aa";

        Assert.True((await List(api)).GetProperty("supported").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Patch, $"/api/devices/{id}", """{"settings":{"brightness":50}}""")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Delete, $"/api/devices/{id}")).Status);
    }

    [Fact]
    public async Task Forgetting_a_running_panel_turns_its_screen_off_and_it_comes_back_as_new_and_off()
    {
        await using var engine = new EngineFactory();
        var backend = new FakeUsbBackend();
        var panel = Panel(backend);
        await using var factory = WithPanels(engine, backend);
        using var api = Client(factory);
        var id = (await Until(api, d => d.GetProperty("state").GetString() == "off")).GetProperty("id").GetString()!;
        var profile = await ProfileNamed(api, "Wide bar");
        Assert.Equal(HttpStatusCode.OK, (await Send(api, HttpMethod.Patch, $"/api/devices/{id}", $$$"""{"settings":{"enabled":true,"profileId":"{{{profile}}}","brightness":60}}""")).Status);
        await Until(api, d => d.GetProperty("state").GetString() == "running");

        var (status, _) = await Send(api, HttpMethod.Delete, $"/api/devices/{id}");
        Assert.Equal(HttpStatusCode.NoContent, status);
        await Eventually(() => panel.Transfers.Any(t => IsEnd(t.Data)), "the screen went off");
        Assert.DoesNotContain(id, File.ReadAllText(Path.Combine(engine.DataDir, "devices.json")));

        var again = await Until(api, d => d.GetProperty("id").GetString() != id && d.GetProperty("state").GetString() == "off");
        Assert.False(again.GetProperty("settings").GetProperty("enabled").GetBoolean());
        Assert.Equal(100, again.GetProperty("settings").GetProperty("brightness").GetInt32());
        Assert.Equal(JsonValueKind.Null, again.GetProperty("settings").GetProperty("profileId").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Delete, $"/api/devices/{id}")).Status);
    }

    // ---- the devices message ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_change_sends_every_socket_a_devices_message_with_the_lists_version()
    {
        await using var engine = new EngineFactory();
        var backend = new FakeUsbBackend();
        Panel(backend);
        await using var factory = WithPanels(engine, backend);
        using var session = Client(factory);
        var token = await ReadToken(session);
        var id = (await Until(session, d => d.GetProperty("state").GetString() == "off")).GetProperty("id").GetString();
        await using var sessionSocket = await PreviewClient.ConnectAsync(factory, Ct);
        await using var integrationSocket = await PreviewClient.ConnectAsync(factory, Ct, EngineRuntime.WebSocketProtocol, EngineRuntime.WebSocketAuthPrefix + token);
        await sessionSocket.ReceiveTextAsync("hello", Ct);
        await integrationSocket.ReceiveTextAsync("hello", Ct);

        Assert.Equal(HttpStatusCode.OK, (await Send(session, HttpMethod.Patch, $"/api/devices/{id}", """{"settings":{"brightness":30}}""")).Status);
        var version = (await List(session)).GetProperty("version").GetInt64();

        // Each socket was sent the list's version when it opened (the panel found before it), then the change's.
        foreach (var socket in new[] { sessionSocket, integrationSocket })
        {
            long seen;
            do
            {
                var message = await socket.ReceiveTextAsync("devices", Ct);
                Assert.Equal(["type", "version"], message.EnumerateObject().Select(m => m.Name));
                seen = message.GetProperty("version").GetInt64();
            }
            while (seen < version);
            Assert.Equal(version, seen);
        }
    }

    [Fact]
    public async Task A_socket_held_on_a_send_keeps_only_the_newest_devices_version_and_another_goes_on()
    {
        await using var engine = new EngineFactory();
        var backend = new FakeUsbBackend();
        Panel(backend);
        await using var factory = WithPanels(engine, backend);
        using var api = Client(factory);
        var id = (await Until(api, d => d.GetProperty("state").GetString() == "off")).GetProperty("id").GetString();
        var manager = factory.Services.GetRequiredService<PanelService>().Manager!;
        var (held, heldRun) = Open(factory, manager);
        var (other, otherRun) = Open(factory, manager);
        foreach (var socket in new[] { held, other })
        {
            Assert.True((await socket.NextAsync()).IsText("hello"));
            await socket.NextAsync(m => m.IsText("devices")); // the version when it opened
        }

        // The first socket's next devices message is held on the wire while 20 more changes are made.
        var blocked = held.PauseWhen(m => m.IsText("devices"));
        Assert.Equal(HttpStatusCode.OK, (await Send(api, HttpMethod.Patch, $"/api/devices/{id}", """{"settings":{"brightness":10}}""")).Status);
        var inFlight = Version(await blocked);
        for (var brightness = 11; brightness < 31; brightness++)
            Assert.Equal(HttpStatusCode.OK, (await Send(api, HttpMethod.Patch, $"/api/devices/{id}", $$$"""{"settings":{"brightness":{{{brightness}}}}}""")).Status);
        var newest = (await List(api)).GetProperty("version").GetInt64();
        Assert.True(newest > inFlight + 1);

        // The other socket isn't held up by it.
        long seen = 0;
        while (seen < newest)
            seen = Version(await other.NextAsync(m => m.IsText("devices")));
        Assert.Equal(newest, seen);

        // Resumed, the held socket sends the message in flight, then only the newest version.
        held.Resume();
        Assert.Equal(inFlight, Version(await held.NextAsync(m => m.IsText("devices"))));
        Assert.Equal(newest, Version(await held.NextAsync(m => m.IsText("devices"))));

        foreach (var (socket, run) in new[] { (held, heldRun), (other, otherRun) })
        {
            socket.ReceiveClose();
            await run.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        }
    }

    /// <summary>A preview session on a socket the test drives, over the engine's panels.</summary>
    private static (FakeWebSocket Socket, Task Run) Open(WebApplicationFactory<Program> factory, PanelManager manager)
    {
        var socket = new FakeWebSocket();
        var session = new PreviewSession(socket, factory.Services.GetRequiredService<Workspace>(), factory.Services.GetRequiredService<RenderService>(),
            NullLogger.Instance, panels: manager);
        return (socket, session.RunAsync(CancellationToken.None, CancellationToken.None));
    }

    private static long Version(FakeWebSocket.Sent message) => message.Json.GetProperty("version").GetInt64();

    [Fact]
    public async Task A_socket_opened_after_the_list_changed_is_sent_its_version()
    {
        await using var engine = new EngineFactory();
        var backend = new FakeUsbBackend();
        Panel(backend);
        await using var factory = WithPanels(engine, backend);
        using var api = Client(factory);
        await Until(api, d => d.GetProperty("state").GetString() == "off");
        var manager = factory.Services.GetRequiredService<PanelService>().Manager!;
        var version = manager.Version;
        Assert.True(version > 0);

        // A change before the socket listens for them (as one between its upgrade and its subscription) isn't missed.
        var (socket, run) = Open(factory, manager);
        Assert.True((await socket.NextAsync()).IsText("hello"));
        Assert.True(Version(await socket.NextAsync(m => m.IsText("devices"))) >= version);
        socket.ReceiveClose();
        await run.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task A_change_the_devices_file_cant_take_is_409_and_changes_nothing_live_or_saved()
    {
        await using var engine = new EngineFactory();
        var backend = new FakeUsbBackend();
        Panel(backend);
        await using var factory = WithPanels(engine, backend);
        using var api = Client(factory);
        var id = (await Until(api, d => d.GetProperty("state").GetString() == "off")).GetProperty("id").GetString();

        // From now on the file can't be written: a folder stands where it was.
        var file = Path.Combine(engine.DataDir, "devices.json");
        File.Delete(file);
        Directory.CreateDirectory(file);

        Assert.Equal(HttpStatusCode.Conflict, (await Send(api, HttpMethod.Patch, $"/api/devices/{id}", """{"settings":{"brightness":40}}""")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(api, HttpMethod.Delete, $"/api/devices/{id}")).Status);
        var device = Assert.Single((await List(api)).GetProperty("devices").EnumerateArray());
        Assert.Equal(id, device.GetProperty("id").GetString());
        Assert.Equal(100, device.GetProperty("settings").GetProperty("brightness").GetInt32());
    }

    [Fact]
    public async Task A_change_whose_folder_flush_fails_after_the_file_is_replaced_is_made_live_and_answered()
    {
        await using var engine = new EngineFactory();
        var backend = new FakeUsbBackend();
        var panel = Panel(backend);
        var fault = new FlushFault();
        await using var factory = WithPanels(engine, backend, storeWriter: fault.Write);
        using var api = Client(factory);
        var id = (await Until(api, d => d.GetProperty("state").GetString() == "off")).GetProperty("id").GetString();
        var profile = await ProfileNamed(api, "Wide bar");
        Assert.Equal(HttpStatusCode.OK, (await Send(api, HttpMethod.Patch, $"/api/devices/{id}", $$$"""{"settings":{"enabled":true,"profileId":"{{{profile}}}"}}""")).Status);
        await Until(api, d => d.GetProperty("state").GetString() == "running");

        // The file takes 40, then its folder's flush fails: the file holds the change, so it is made live and answered.
        fault.Arm();
        var (status, entry) = await Send(api, HttpMethod.Patch, $"/api/devices/{id}", """{"settings":{"brightness":40}}""");
        Assert.True(fault.Fired);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(40, entry.GetProperty("settings").GetProperty("brightness").GetInt32());
        using (var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(engine.DataDir, "devices.json"))))
            Assert.Equal(40, saved.RootElement.GetProperty("devices")[0].GetProperty("brightness").GetInt32());
        var device = Assert.Single((await List(api)).GetProperty("devices").EnumerateArray());
        Assert.Equal(40, device.GetProperty("settings").GetProperty("brightness").GetInt32());
        var backlight = BeadaPanelProtocol.StatusLink(StatusLinkType.SetBacklight, [BeadaPanelProtocol.BrightnessByte(40, 2)]);
        await Eventually(() => panel.Transfers.Any(t => t.Data.AsSpan().SequenceEqual(backlight)), "the brightness reached the panel");
    }

    [Fact]
    public async Task A_forget_whose_folder_flush_fails_after_the_file_is_replaced_is_made_live_and_answered()
    {
        await using var engine = new EngineFactory();
        var backend = new FakeUsbBackend();
        Panel(backend);
        var fault = new FlushFault();
        await using var factory = WithPanels(engine, backend, storeWriter: fault.Write);
        using var api = Client(factory);
        var id = (await Until(api, d => d.GetProperty("state").GetString() == "off")).GetProperty("id").GetString()!;

        // The file loses the panel, then its folder's flush fails: the file holds the change, so it is made live and answered.
        fault.Arm();
        var (status, _) = await Send(api, HttpMethod.Delete, $"/api/devices/{id}");
        Assert.True(fault.Fired);
        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.DoesNotContain(id, File.ReadAllText(Path.Combine(engine.DataDir, "devices.json")));
        Assert.DoesNotContain((await List(api)).GetProperty("devices").EnumerateArray(), d => d.GetProperty("id").GetString() == id);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Delete, $"/api/devices/{id}")).Status);
    }

    [Fact]
    public async Task An_integer_spelled_with_a_fraction_or_an_exponent_is_taken_and_a_real_fraction_is_422()
    {
        await using var engine = new EngineFactory();
        var backend = new FakeUsbBackend();
        Panel(backend);
        await using var factory = WithPanels(engine, backend);
        using var api = Client(factory);
        var id = (await Until(api, d => d.GetProperty("state").GetString() == "off")).GetProperty("id").GetString();

        var (status, entry) = await Send(api, HttpMethod.Patch, $"/api/devices/{id}", """{"settings":{"brightness":50.0}}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(50, entry.GetProperty("settings").GetProperty("brightness").GetInt32());
        (status, entry) = await Send(api, HttpMethod.Patch, $"/api/devices/{id}", """{"settings":{"rotation":9e1}}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(90, entry.GetProperty("settings").GetProperty("rotation").GetInt32());

        var (refused, problem) = await Send(api, HttpMethod.Patch, $"/api/devices/{id}", """{"settings":{"brightness":50.5}}""");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused);
        Assert.Contains(problem.GetProperty("errors").EnumerateArray(), e => e.GetProperty("path").GetString() == "/settings/brightness");
    }

    [Fact]
    public void A_change_keeps_a_null_profileId_apart_from_one_left_out()
    {
        foreach (var json in new[] { """{"settings":{"profileId":null}}""", """{"settings":{"brightness":5}}""", """{"settings":{}}""" })
        {
            using var read = JsonDocument.Parse(json);
            var change = SpecJson.Read<DeviceChangeRequest>(read.RootElement);
            Assert.Equal(json, JsonSerializer.Serialize(change, SpecJson.Options));
        }
    }

    [Fact]
    public async Task Stored_values_the_answer_cant_carry_are_answered_within_the_schema_and_kept()
    {
        await using var engine = new EngineFactory();
        Directory.CreateDirectory(engine.DataDir);
        var file = Path.Combine(engine.DataDir, "devices.json");
        var longText = new string('x', 300);
        const string bad = "00000000-0000-4000-8000-0000000000a1";
        File.WriteAllText(file, $$"""
            {"format": "infopanel.devices/1", "devices": [
              {"id": "{{bad}}", "family": "beadapanel", "model": "8", "serial": "{{longText}}", "port": "{{longText}}", "enabled": true, "profileId": "bad", "rotation": 0, "brightness": 100},
              {"id": "00000000-0000-4000-8000-0000000000a2", "family": "{{longText}}", "model": null, "serial": null, "port": null, "enabled": false, "profileId": null, "rotation": 0, "brightness": 100}]}
            """);
        await using var factory = WithPanels(engine, new FakeUsbBackend());
        using var api = Client(factory);

        var list = await List(api);
        var check = SpecSchemas.Strict.Validate(list, SpecSchemas.Api + "#/$defs/deviceList");
        Assert.True(check.IsValid, string.Join("; ", check.Errors));
        Assert.Equal(JsonValueKind.Null, list.GetProperty("devices")[0].GetProperty("settings").GetProperty("profileId").ValueKind);

        // Another setting changed: the stored value is kept as it was.
        var (status, entry) = await Send(api, HttpMethod.Patch, $"/api/devices/{bad}", """{"settings":{"brightness":50}}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(SpecSchemas.Strict.Validate(entry, SpecSchemas.Api + "#/$defs/deviceEntry").IsValid);
        Assert.Contains("\"profileId\":\"bad\"", File.ReadAllText(file).Replace(" ", ""));
    }

    [Fact]
    public async Task A_forgotten_jpeg_panel_found_again_and_turned_on_within_30_s_waits_for_it()
    {
        await using var engine = new EngineFactory();
        var backend = new FakeUsbBackend();
        Panel(backend);
        await using var factory = WithPanels(engine, backend);
        using var api = Client(factory);
        var id = (await Until(api, d => d.GetProperty("state").GetString() == "off")).GetProperty("id").GetString();
        var profile = await ProfileNamed(api, "Wide bar");
        var on = $$$"""{"settings":{"enabled":true,"profileId":"{{{profile}}}"}}""";
        Assert.Equal(HttpStatusCode.OK, (await Send(api, HttpMethod.Patch, $"/api/devices/{id}", on)).Status);
        await Until(api, d => d.GetProperty("state").GetString() == "running");

        // Forgotten while its JPEG stream runs, it is found again as a new device; turned on at once, it waits.
        Assert.Equal(HttpStatusCode.NoContent, (await Send(api, HttpMethod.Delete, $"/api/devices/{id}")).Status);
        var again = (await Until(api, d => d.GetProperty("id").GetString() != id && d.GetProperty("state").GetString() == "off")).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.OK, (await Send(api, HttpMethod.Patch, $"/api/devices/{again}", on)).Status);
        var waiting = await Until(api, d => d.GetProperty("state").GetString() is "starting" or "running");
        Assert.Equal("starting", waiting.GetProperty("state").GetString());
        Assert.Equal("settling", waiting.GetProperty("reason").GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_running_panels_stats_alone_count_at_most_once_a_second_and_a_settings_change_at_once()
    {
        await using var engine = new EngineFactory();
        await using var factory = engine.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton(new RenderServiceOptions(AutoTick: false))));
        using var started = factory.CreateClient();
        var workspace = factory.Services.GetRequiredService<Workspace>();
        var render = factory.Services.GetRequiredService<RenderService>();
        var profile = TestDocs.Create(workspace, TestDocs.Doc("Stats", 320, 80, TestDocs.Scene("shape", 0, 0, TestDocs.Rect())));

        // A manager of the test's own on the fake USB clock, which moves only with the panel's writes and the test.
        var backend = new FakeUsbBackend();
        var device = Panel(backend);
        var (logger, _) = PanelStoreTests.Logged();
        var store = PanelStore.Read(Path.Combine(engine.Root, "stats", PanelStore.FileName), logger);
        store.Save([new SavedPanel(Guid.NewGuid().ToString("D"), SavedPanel.BeadaPanelFamily, "8", Serial, "0-1", true, profile) { Encoding = PanelEncoding.Raw }]);
        var host = new PanelHost(_ => true, render.Attach, render.Detach, render.RequestChanged, render.Refresh, () => 60);
        await using var manager = new PanelManager(backend, store, host, backend.Clock, logger, new PanelRunnerOptions { KeepAlive = Timeout.InfiniteTimeSpan });
        manager.Start(passes: false);
        await manager.RunPassAsync();
        var id = (await Status(manager, s => s.State == "running")).Saved.Id;

        // A new frame written in another time: only its stats change. Every read of the list counts what changed.
        var x = 0;
        async Task<PanelStatus> Frame(int writeMs)
        {
            var before = (await manager.ListAsync())[0];
            device.WriteDelay = TimeSpan.FromMilliseconds(writeMs);
            TestDocs.Commit(workspace.Open(profile)!, TestDocs.Place("shape", ++x * 10, 0));
            render.Tick(profile);
            return await Status(manager, s => (s.Fps, s.FrameTimeMs) != (before.Fps, before.FrameTimeMs));
        }

        await Frame(20);
        var counted = manager.Version; // the first change of the stats alone counts

        await Frame(40);
        Assert.Equal(counted, manager.Version); // within a second of it: not counted

        await manager.UpdateAsync(id, s => s with { Brightness = 50 });
        Assert.Equal(counted + 1, manager.Version); // a settings change counts at once, within that second too

        await Frame(60);
        Assert.Equal(counted + 1, manager.Version); // still within the second

        backend.Clock.Advance(PanelManager.StatsInterval);
        await manager.ListAsync();
        Assert.Equal(counted + 2, manager.Version); // a second on: the stats' change counts
    }

    /// <summary>The first panel once <paramref name="wanted"/> holds for it (10 s at most); each read counts what changed.</summary>
    private static async Task<PanelStatus> Status(PanelManager manager, Func<PanelStatus, bool> wanted)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var list = await manager.ListAsync();
            if (list.Count > 0 && wanted(list[0]))
                return list[0];
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"never got there: {(list.Count > 0 ? list[0] : "none listed")}");
            await Task.Delay(10, Ct);
        }
    }
}
