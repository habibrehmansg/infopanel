using System.Net;
using System.Text;
using System.Text.Json;
using InfoPanel.Core.Tests.Panels.Usb;
using InfoPanel.Panels;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests.Panels;

/// <summary>
/// A panel's own frame rate and its family's settings through the devices API, with the virtual panel package bundled
/// (its panel.json declares a boolean, an enum and a model setting): the families list gives each family's settings and
/// each model's other 1.x names; a save and a change set them, each value checked against its family's declaration (a
/// setting it doesn't declare, a value of the wrong type, out of its range or values: 422 <c>device.setting</c>, nothing
/// changed); they are answered in the device's entry and written to <c>devices.json</c>.
/// </summary>
public sealed class DeviceFamilySettingsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebApplicationFactory<Program> WithVirtualPackage(EngineFactory engine, FakeUsbBackend backend)
    {
        var bundled = Path.Combine(engine.Root, "bundled");
        VirtualPackage.CopyTo(bundled);
        return engine.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Engine:NoPanels", "false");
            builder.UseSetting("Engine:NoPlugins", "false");
            builder.UseSetting("Engine:BundledPlugins", bundled);
            builder.UseSetting("Engine:RunDir", Path.Combine(engine.Root, "run"));
            builder.ConfigureTestServices(services => services.AddSingleton(new PanelServiceOptions
            {
                Backend = () => backend,
                Runner = new PanelRunnerOptions { KeepAlive = Timeout.InfiniteTimeSpan },
            }));
        });
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", EngineFactory.Token);
        return client;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpClient client, HttpMethod method, string path, string? json = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = json == null ? null : new StringContent(json, Encoding.UTF8, "application/json") };
        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    private static async Task<string> SaveVirtual(HttpClient api, string extra = "")
    {
        var (status, body) = await Send(api, HttpMethod.Post, "/api/devices", $$"""{"family":"virtual","model":"480x320","settings":{}{{extra}}}""");
        Assert.True(status == HttpStatusCode.Created, body.ToString());
        return body.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task The_families_list_gives_a_familys_settings_as_its_panel_json_declares_them()
    {
        await using var engine = new EngineFactory();
        using var factory = WithVirtualPackage(engine, new FakeUsbBackend());
        using var api = Client(factory);

        var (_, list) = await Send(api, HttpMethod.Get, "/api/devices");

        var family = list.GetProperty("families").EnumerateArray().Single(f => f.GetProperty("id").GetString() == "virtual");
        var settings = family.GetProperty("settings").EnumerateArray().ToList();
        Assert.Equal(["mirror", "look", "model"], settings.Select(s => s.GetProperty("id").GetString()));
        Assert.Equal(["boolean", "enum", "model"], settings.Select(s => s.GetProperty("type").GetString()));
        Assert.False(settings[0].GetProperty("default").GetBoolean());
        Assert.Equal(["plain", "framed"], settings[1].GetProperty("values").EnumerateArray().Select(v => v.GetProperty("id").GetString()));
        Assert.Equal("Look", settings[1].GetProperty("v1Field").GetString());
        Assert.Equal(JsonValueKind.Null, settings[2].GetProperty("default").ValueKind);
        Assert.Contains("frameRate", family.GetProperty("supports").EnumerateArray().Select(s => s.GetString()));
        // The built-in family declares none.
        Assert.False(list.GetProperty("families")[0].TryGetProperty("settings", out _));
    }

    [Fact]
    public async Task A_change_sets_family_settings_and_a_frame_rate_answers_and_writes_them_and_null_puts_them_back()
    {
        await using var engine = new EngineFactory();
        using var factory = WithVirtualPackage(engine, new FakeUsbBackend());
        using var api = Client(factory);
        var id = await SaveVirtual(api, """, "familySettings": {"look": "framed"}""");

        var (status, entry) = await Send(api, HttpMethod.Patch, $"/api/devices/{id}", """{"settings":{"frameRate":10},"familySettings":{"mirror":true,"model":"480x320"}}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(10, entry.GetProperty("settings").GetProperty("frameRate").GetInt32());
        var set = entry.GetProperty("familySettings");
        Assert.Equal(("framed", true, "480x320"), (set.GetProperty("look").GetString(), set.GetProperty("mirror").GetBoolean(), set.GetProperty("model").GetString()));
        var file = await File.ReadAllTextAsync(Path.Combine(engine.DataDir, PanelStore.FileName), Ct);
        Assert.Contains("\"frameRate\":10", file);
        Assert.Contains("\"familySettings\":{\"mirror\":true,\"look\":\"framed\",\"model\":\"480x320\"}", file); // in the declared order

        (status, entry) = await Send(api, HttpMethod.Patch, $"/api/devices/{id}", """{"settings":{"frameRate":null},"familySettings":{"look":null,"mirror":null,"model":null}}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(entry.GetProperty("settings").TryGetProperty("frameRate", out _));
        Assert.False(entry.TryGetProperty("familySettings", out _));
        file = await File.ReadAllTextAsync(Path.Combine(engine.DataDir, PanelStore.FileName), Ct);
        Assert.DoesNotContain("frameRate", file);
        Assert.DoesNotContain("familySettings", file);
    }

    [Theory]
    [InlineData("""{"settings":{},"familySettings":{"volume":3}}""", "/familySettings/volume")]
    [InlineData("""{"settings":{},"familySettings":{"mirror":"yes"}}""", "/familySettings/mirror")]
    [InlineData("""{"settings":{},"familySettings":{"look":"shiny"}}""", "/familySettings/look")]
    [InlineData("""{"settings":{},"familySettings":{"model":"pm-47"}}""", "/familySettings/model")]
    public async Task A_family_setting_the_family_doesnt_declare_or_a_value_it_doesnt_take_is_422_and_changes_nothing(string change, string path)
    {
        await using var engine = new EngineFactory();
        using var factory = WithVirtualPackage(engine, new FakeUsbBackend());
        using var api = Client(factory);
        var id = await SaveVirtual(api);
        var before = await File.ReadAllTextAsync(Path.Combine(engine.DataDir, PanelStore.FileName), Ct);

        var (status, body) = await Send(api, HttpMethod.Patch, $"/api/devices/{id}", change);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        var error = Assert.Single(body.GetProperty("errors").EnumerateArray());
        Assert.Equal(("device.setting", path), (error.GetProperty("code").GetString(), error.GetProperty("path").GetString()));
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(engine.DataDir, PanelStore.FileName), Ct));
    }

    [Theory]
    [InlineData("""{"settings":{"frameRate":0}}""", "/settings/frameRate")]
    [InlineData("""{"settings":{"frameRate":61}}""", "/settings/frameRate")]
    [InlineData("""{"settings":{},"familySettings":{"mirror":{"on":true}}}""", "/familySettings/mirror")]
    public async Task A_frame_rate_outside_1_to_60_or_a_family_setting_that_isnt_a_plain_value_is_refused_by_the_schema(string change, string path)
    {
        await using var engine = new EngineFactory();
        using var factory = WithVirtualPackage(engine, new FakeUsbBackend());
        using var api = Client(factory);
        var id = await SaveVirtual(api);

        var (status, body) = await Send(api, HttpMethod.Patch, $"/api/devices/{id}", change);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Contains(body.GetProperty("errors").EnumerateArray(), e => e.GetProperty("path").GetString() == path);
    }

    [Theory]
    [InlineData("PATCH", """{"settings":{},"familySettings":{"mirror":{"on":true}}}""", "/familySettings/mirror")]
    [InlineData("PATCH", """{"settings":{},"familySettings":{"look":[1,2]}}""", "/familySettings/look")]
    [InlineData("POST", """{"family":"virtual","model":"480x320","settings":{},"familySettings":{"mirror":{}}}""", "/familySettings/mirror")]
    public async Task A_family_setting_of_a_type_no_setting_takes_is_422_device_setting_at_its_own_path(string method, string body, string path)
    {
        await using var engine = new EngineFactory();
        using var factory = WithVirtualPackage(engine, new FakeUsbBackend());
        using var api = Client(factory);
        var target = "/api/devices";
        if (method == "PATCH")
            target += "/" + await SaveVirtual(api);

        var (status, answer) = await Send(api, new HttpMethod(method), target, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        var error = Assert.Single(answer.GetProperty("errors").EnumerateArray());
        Assert.Equal(("device.setting", path), (error.GetProperty("code").GetString(), error.GetProperty("path").GetString()));
    }

    [Fact]
    public async Task A_save_with_a_family_setting_its_family_doesnt_take_is_422_and_saves_nothing()
    {
        await using var engine = new EngineFactory();
        using var factory = WithVirtualPackage(engine, new FakeUsbBackend());
        using var api = Client(factory);

        var (status, body) = await Send(api, HttpMethod.Post, "/api/devices", """{"family":"virtual","model":"480x320","settings":{},"familySettings":{"look":"shiny"}}""");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("/familySettings/look", Assert.Single(body.GetProperty("errors").EnumerateArray()).GetProperty("path").GetString());
        var (_, list) = await Send(api, HttpMethod.Get, "/api/devices");
        Assert.Equal(0, list.GetProperty("devices").GetArrayLength());
    }
}
