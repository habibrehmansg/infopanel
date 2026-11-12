using System.Text.Json;
using InfoPanel.Core.Tests.Panels.Power;
using InfoPanel.Core.Tests.Panels.Usb;
using InfoPanel.Panels;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Usb;
using Xunit;

namespace InfoPanel.Core.Tests.Panels;

/// <summary>
/// A family's own settings and a panel's own frame rate in the engine: the settings a <c>panel.json</c> declares, read
/// and bounded; a panel's values checked against them; both kept in <c>devices.json</c>; a change of the family settings
/// closing the session and opening it again once, with them; a panel's frame rate in place of the engine's; and "Try it
/// as…": a model the family doesn't know saved by its code and run once the <c>model</c> setting names one it does.
/// </summary>
public sealed class PanelFamilySettingsTests : IAsyncDisposable
{
    private const ushort Vendor = 0x1d50;
    private const ushort Product = 0x61f2;

    private readonly FakeUsbBackend _backend = new();
    private readonly KeptLog _log = new();
    private readonly string _root = Directory.CreateTempSubdirectory("panel-family-settings-").FullName;
    private PanelManager? _manager;
    private volatile PanelSink? _sink;

    public async ValueTask DisposeAsync()
    {
        if (_manager != null)
            await _manager.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private const string Declared = """
        [{ "id": "flickerFix", "name": "Flicker fix", "type": "boolean", "default": false, "v1Field": "FlickerFix" },
         { "id": "mask", "name": "Display mask", "type": "enum", "values": [{ "id": "none", "name": "None" }, { "id": "roundedLeft", "name": "Rounded left" }], "default": "none", "v1Field": "DisplayMask" },
         { "id": "jpegQuality", "name": "JPEG quality", "type": "integer", "min": 50, "max": 100, "default": 95, "v1Field": "JpegQuality" },
         { "id": "label", "name": "Label", "type": "string", "default": "" },
         { "id": "model", "name": "Model", "type": "model", "default": null }]
        """;

    private static IReadOnlyList<PanelFamilySetting> Settings(string json = Declared)
    {
        using var document = JsonDocument.Parse(json);
        return PanelFamilySettings.Parse(document.RootElement, "families[0]", out var error) ?? throw new Xunit.Sdk.XunitException(error);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void A_familys_settings_are_read_with_their_types_defaults_values_ranges_and_1x_fields()
    {
        var settings = Settings();

        Assert.Equal(["flickerFix", "mask", "jpegQuality", "label", "model"], settings.Select(s => s.Id));
        Assert.Equal(["none", "roundedLeft"], settings[1].Values.Select(v => v.Id));
        Assert.Equal((50, 100, 95), (settings[2].Min, settings[2].Max, settings[2].Default.GetInt32()));
        Assert.Equal("FlickerFix", settings[0].V1Field);
        Assert.Equal(JsonValueKind.Null, settings[4].Default.ValueKind);
    }

    [Theory]
    [InlineData("""[{ "id": "Bad", "name": "x", "type": "boolean", "default": false }]""", "id")]
    [InlineData("""[{ "id": "a", "name": "x", "type": "colour", "default": 1 }]""", "type")]
    [InlineData("""[{ "id": "a", "name": "x", "type": "boolean", "default": 1 }]""", "default")]
    [InlineData("""[{ "id": "a", "name": "x", "type": "enum", "values": [{ "id": "p", "name": "P" }], "default": "q" }]""", "default")]
    [InlineData("""[{ "id": "a", "name": "x", "type": "integer", "min": 5, "max": 1, "default": 3 }]""", "min")]
    [InlineData("""[{ "id": "a", "name": "x", "type": "integer", "min": 1, "max": 5, "default": 9 }]""", "default")]
    [InlineData("""[{ "id": "other", "name": "x", "type": "model" }]""", "model")]
    [InlineData("""[{ "id": "a", "name": "x", "type": "boolean", "default": false }, { "id": "a", "name": "y", "type": "boolean", "default": true }]""", "twice")]
    public void A_setting_declared_wrong_refuses_the_manifest(string declared, string why)
    {
        using var document = JsonDocument.Parse(declared);

        Assert.Null(PanelFamilySettings.Parse(document.RootElement, "families[0]", out var error));
        Assert.Contains(why, error);
    }

    [Fact]
    public void A_manifests_family_keeps_its_settings()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "InfoPanel.Panels.Set")).FullName;
        File.WriteAllText(Path.Combine(dir, PanelPackages.ManifestName), $$"""
            { "format": "infopanel.panel-package/1", "sdk": "1.0",
              "families": [{ "id": "set", "name": "Set", "match": [{ "usb": "1d50:61f2" }], "models": [], "settings": {{Declared}} }] }
            """);

        var family = Assert.Single(PanelPackages.ReadManifest(dir, out _)!.Families);

        Assert.Equal(5, family.Settings.Count);
    }

    [Theory]
    [InlineData("""{"loud": true}""", "loud")]
    [InlineData("""{"flickerFix": 1}""", "flickerFix")]
    [InlineData("""{"mask": "roundedRight"}""", "mask")]
    [InlineData("""{"jpegQuality": 49}""", "jpegQuality")]
    [InlineData("""{"jpegQuality": 101}""", "jpegQuality")]
    [InlineData("""{"jpegQuality": 70.5}""", "jpegQuality")]
    [InlineData("""{"label": 5}""", "label")]
    [InlineData("""{"model": "pm-47"}""", "model")]
    public void A_value_its_setting_doesnt_take_or_a_setting_the_family_doesnt_declare_is_refused_and_nothing_changes(string change, string key)
    {
        var saved = """{"mask":"roundedLeft"}""";

        var after = PanelFamilySettings.Apply(saved, Json(change), Settings(), ["wonder-360"], out var refused);

        Assert.Equal(key, Assert.Single(refused).Key);
        Assert.Equal(saved, after);
    }

    [Fact]
    public void Values_its_settings_take_are_set_over_the_saved_ones_and_null_puts_one_back_to_its_default()
    {
        var after = PanelFamilySettings.Apply("""{"mask":"roundedLeft","label":"desk"}""",
            Json("""{"flickerFix": true, "jpegQuality": 50, "label": null, "model": "wonder-360"}"""), Settings(), ["wonder-360"], out var refused);

        Assert.Empty(refused);
        Assert.Equal("""{"flickerFix":true,"mask":"roundedLeft","jpegQuality":50,"model":"wonder-360"}""", after); // in the declared order
        Assert.Null(PanelFamilySettings.Apply(after, Json("""{"mask":null,"flickerFix":null,"jpegQuality":null,"model":null}"""), Settings(), ["wonder-360"], out _));
    }

    [Fact]
    public void A_panels_frame_rate_and_family_settings_are_kept_in_devices_json_and_read_back()
    {
        var path = Path.Combine(_root, PanelStore.FileName);
        var store = PanelStore.Read(path, _log.Logger);
        var panel = new SavedPanel("3f2b8c1e-6a4d-4e2f-9b1a-7c5d8e9f0a12", "set", "wonder-360", null, null) { FrameRate = 12, FamilySettings = """{"mask":"roundedLeft"}""" };

        store.Save([panel, new SavedPanel("3f2b8c1e-6a4d-4e2f-9b1a-7c5d8e9f0a13", "set", "wonder-360", null, null)]);
        var read = PanelStore.Read(path, _log.Logger).Devices;

        Assert.Equal((12, """{"mask":"roundedLeft"}"""), (read[0].FrameRate, read[0].FamilySettings));
        Assert.Equal(((int?)null, (string?)null), (read[1].FrameRate, read[1].FamilySettings));
        Assert.DoesNotContain("frameRate\":null", File.ReadAllText(path));
    }

    // The manager: a panel's own frame rate, a settings change reopening the session, and "Try it as…".

    private PanelManager Manager(IPanelDriver driver)
    {
        var host = new PanelHost(_ => true, sink =>
        {
            _sink = (PanelSink)sink;
            return true;
        }, _ => { }, _ => { }, _ => { }, () => 15);
        _manager = new PanelManager(_backend, PanelStore.InMemory(), host, _backend.Clock, _log.Logger,
            new PanelRunnerOptions { KeepAlive = Timeout.InfiniteTimeSpan }, drivers: [driver]);
        _manager.Start(passes: false);
        return _manager;
    }

    private async Task Until(Func<Task<bool>> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await done())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{what}\nThe log:\n{_log}");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private async Task<PanelStatus> Found(PanelManager manager)
    {
        _backend.Add(new FakeUsbDevice(new UsbDeviceId(Vendor, Product, 0, 7, "0-7")));
        await manager.RunPassAsync();
        return Assert.Single(await manager.ListAsync());
    }

    [Fact]
    public async Task A_panels_own_frame_rate_is_asked_in_place_of_the_engines_and_null_follows_the_engines_again()
    {
        var driver = new Driver(_backend.Clock);
        var manager = Manager(driver);
        var found = await Found(manager);

        await manager.UpdateAsync(found.Saved.Id, s => s with { Enabled = true, ProfileId = "p-1", FrameRate = 10 });
        await Until(async () => (await manager.ListAsync())[0].State == "running" && _sink != null, "the panel never ran");
        Assert.Equal(10, _sink!.Request.MaxFps);

        await manager.UpdateAsync(found.Saved.Id, s => s with { FrameRate = 25 });
        Assert.Equal(25, _sink.Request.MaxFps); // at once, the session kept
        await manager.UpdateAsync(found.Saved.Id, s => s with { FrameRate = null });
        Assert.Equal(15, _sink.Request.MaxFps); // the engine's
        Assert.Single(driver.Opened);
    }

    [Theory]
    [InlineData(10, 100)] // below the panel's ceiling (30): its own rate
    [InlineData(30, 34)] // at it
    [InlineData(60, 34)] // above it: never past the ceiling
    public async Task A_panels_own_frame_rate_paces_its_writes_never_past_its_ceiling(int frameRate, long intervalMs)
    {
        var driver = new Driver(_backend.Clock);
        var manager = Manager(driver);
        var found = await Found(manager);
        await manager.UpdateAsync(found.Saved.Id, s => s with { Enabled = true, ProfileId = "p-1", FrameRate = frameRate });
        await Until(async () => (await manager.ListAsync())[0].State == "running" && _sink != null, "the panel never ran");
        var session = driver.Sessions[0];

        Deliver(1);
        await Until(() => Task.FromResult(session.Writes.Count >= 1), "no frame was written");
        Deliver(2);
        await Until(() => Task.FromResult(session.Writes.Count >= 2), "the second frame wasn't written");

        Assert.Equal(intervalMs, session.Writes[1] - session.Writes[0]);
    }

    private void Deliver(long frameId)
    {
        using var pixels = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(8, 2, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul));
        pixels.Erase(SkiaSharp.SKColors.SteelBlue);
        using var image = SkiaSharp.SKImage.FromBitmap(pixels);
        var frame = new InfoPanel.Output.RenderedFrame(image)
        {
            ProfileId = "p-1",
            Rev = 1,
            FrameId = frameId,
            Tick = frameId,
            Scale = 1,
            PixelWidth = 8,
            PixelHeight = 2,
            ProfileWidth = 8,
            ProfileHeight = 2,
            Bounds = [],
            ContentHash = (ulong)frameId,
        };
        _sink!.OnFrame(frame);
        frame.Close();
    }

    [Fact]
    public async Task A_change_of_its_family_settings_closes_the_session_and_opens_it_again_once_with_them()
    {
        var driver = new Driver(_backend.Clock);
        var manager = Manager(driver);
        var found = await Found(manager);
        await manager.UpdateAsync(found.Saved.Id, s => s with { Enabled = true, ProfileId = "p-1", FamilySettings = """{"mask":"none"}""" });
        await Until(async () => (await manager.ListAsync())[0].State == "running", "the panel never ran");

        await manager.UpdateAsync(found.Saved.Id, s => s with { Brightness = 50 }); // not a family setting: the session stays
        await manager.UpdateAsync(found.Saved.Id, s => s with { FamilySettings = """{"mask":"roundedLeft"}""" });
        await Until(async () =>
        {
            await manager.RunPassAsync();
            return driver.Opened.Count == 2 && (await manager.ListAsync())[0].State == "running";
        }, "the session wasn't opened again");

        Assert.Equal(["""{"mask":"none"}""", """{"mask":"roundedLeft"}"""], driver.Opened.Select(o => o.FamilySettings.GetRawText()));
        Assert.True(driver.Sessions[0].Closed);
        await manager.RunPassAsync();
        Assert.Equal(2, driver.Opened.Count);
    }

    [Fact]
    public async Task A_jpeg_session_of_another_family_opens_again_after_a_settings_change_without_BeadaPanels_wait()
    {
        // BeadaPanel's T113 takes no new stream for a while after a JPEG one; another family's JPEG panel has no such wait.
        var driver = new Driver(_backend.Clock) { Format = PanelFrameFormat.Jpeg };
        var manager = Manager(driver);
        var found = await Found(manager);
        await manager.UpdateAsync(found.Saved.Id, s => s with { Enabled = true, ProfileId = "p-1", FamilySettings = """{"mask":"none"}""" });
        await Until(async () => (await manager.ListAsync())[0] is { State: "running", Jpeg: true }, "the panel never ran its JPEG session");

        await manager.UpdateAsync(found.Saved.Id, s => s with { FamilySettings = """{"mask":"roundedLeft"}""" });
        await Until(async () =>
        {
            await manager.RunPassAsync();
            return driver.Opened.Count == 2 && (await manager.ListAsync())[0].State == "running";
        }, "the session wasn't opened again at once");

        Assert.DoesNotContain(_log.Lines, l => l.Contains("once the panel takes a stream after its JPEG one"));
    }

    [Theory]
    [InlineData("5e1")]
    [InlineData("50.0")]
    [InlineData("50.000000000000001")]
    public void An_integer_spelled_any_way_the_format_reads_as_one_is_taken_and_kept_as_the_integer(string spelled)
    {
        var after = PanelFamilySettings.Apply(null, Json($$"""{"jpegQuality": {{spelled}}}"""), Settings(), ["wonder-360"], out var refused);

        Assert.Empty(refused);
        Assert.Equal("""{"jpegQuality":50}""", after);
        Assert.Equal(50, PanelFamilySettings.ElementOf(after).GetProperty("jpegQuality").GetInt32());
    }

    [Fact]
    public void A_change_keeps_the_stored_values_it_doesnt_touch_even_of_settings_the_package_no_longer_declares()
    {
        // The package dropped "retired": its stored value stays, after the declared ones, until something removes it.
        const string saved = """{"retired":true,"mask":"roundedLeft"}""";

        var after = PanelFamilySettings.Apply(saved, Json("""{"flickerFix": true}"""), Settings(), ["wonder-360"], out var refused);

        Assert.Empty(refused);
        Assert.Equal("""{"flickerFix":true,"mask":"roundedLeft","retired":true}""", after);
        Assert.Equal("""{"mask":"roundedLeft","retired":true}""", PanelFamilySettings.Apply(after, Json("""{"flickerFix": null}"""), Settings(), ["wonder-360"], out _));
        // Only null removes it; any other value of a setting the family doesn't declare is still refused.
        Assert.Equal("""{"mask":"roundedLeft"}""", PanelFamilySettings.Apply(saved, Json("""{"retired": null}"""), Settings(), ["wonder-360"], out refused));
        Assert.Empty(refused);
        Assert.Equal(saved, PanelFamilySettings.Apply(saved, Json("""{"retired": false}"""), Settings(), ["wonder-360"], out refused));
        Assert.Equal("retired", Assert.Single(refused).Key);
    }

    [Fact]
    public void A_change_whose_values_with_the_kept_ones_would_pass_32_is_refused_and_the_stored_values_stay()
    {
        // 32 values set under the package's old settings, all retired by an update that declares the five above.
        var saved = "{" + string.Join(",", Enumerable.Range(0, PanelFamilySettings.MaxSettings).Select(i => $"\"old{i}\":true")) + "}";

        var after = PanelFamilySettings.Apply(saved, Json("""{"flickerFix": true}"""), Settings(), ["wonder-360"], out var refused);

        Assert.Same(saved, after);
        Assert.Equal("flickerFix", Assert.Single(refused).Key);
        Assert.Contains("32", refused[0].Message);
        // Putting one back to its default first makes the room.
        var room = PanelFamilySettings.Apply(saved, Json("""{"old0": null, "flickerFix": true}"""), Settings(), ["wonder-360"], out refused);
        Assert.Empty(refused);
        Assert.Equal(PanelFamilySettings.MaxSettings, PanelFamilySettings.Read(room).Count);
    }

    [Fact]
    public void A_stored_value_a_setting_declared_again_with_another_type_doesnt_take_opens_with_its_default()
    {
        // "mask" was a boolean once; this version declares it an enum, and "jpegQuality" an integer from 50.
        const string saved = """{"mask":true,"jpegQuality":20,"flickerFix":true,"retired":1}""";

        var opened = PanelFamilySettings.ForOpen(saved, Settings(), ["wonder-360"], out var mismatched);

        Assert.Equal(["mask", "jpegQuality"], mismatched);
        Assert.Equal("none", opened.GetProperty("mask").GetString());
        Assert.Equal(95, opened.GetProperty("jpegQuality").GetInt32());
        Assert.True(opened.GetProperty("flickerFix").GetBoolean());
        Assert.Equal(1, opened.GetProperty("retired").GetInt32()); // not declared: as stored
        Assert.Equal(JsonValueKind.Object, PanelFamilySettings.ForOpen(null, Settings(), ["wonder-360"], out mismatched).ValueKind);
        Assert.Empty(mismatched);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"mask": "roundedLeft"}""")]
    [InlineData("""{"label": null}""")]
    public void A_change_that_changes_nothing_gives_the_stored_values_back_as_they_were(string change)
    {
        const string saved = """{"retired":true,"mask":"roundedLeft"}""";

        Assert.Same(saved, PanelFamilySettings.Apply(saved, Json(change), Settings(), ["wonder-360"], out var refused));
        Assert.Empty(refused);
    }

    [Fact]
    public async Task An_empty_family_settings_change_leaves_the_session_open()
    {
        var driver = new Driver(_backend.Clock);
        var manager = Manager(driver);
        var found = await Found(manager);
        const string saved = """{"retired":true,"mask":"none"}""";
        await manager.UpdateAsync(found.Saved.Id, s => s with { Enabled = true, ProfileId = "p-1", FamilySettings = saved });
        await Until(async () => (await manager.ListAsync())[0].State == "running", "the panel never ran");

        await manager.UpdateAsync(found.Saved.Id, s => s with { FamilySettings = PanelFamilySettings.Apply(s.FamilySettings, Json("{}"), Settings(), ["wonder-360"], out _) });
        await manager.RunPassAsync();
        await manager.RunPassAsync();

        Assert.Single(driver.Opened);
        Assert.False(driver.Sessions[0].Closed);
        Assert.Equal(saved, (await manager.ListAsync())[0].Saved.FamilySettings);
    }

    [Fact]
    public async Task A_model_the_family_doesnt_know_is_saved_by_its_code_and_runs_once_the_model_setting_names_one_it_does()
    {
        var driver = new Driver(_backend.Clock) { ProbedModel = "pm-47" };
        var manager = Manager(driver);
        var found = await Found(manager);
        Assert.Equal("pm-47", found.Saved.Model);

        await manager.UpdateAsync(found.Saved.Id, s => s with { Enabled = true, ProfileId = "p-1" });
        await Until(async () => (await manager.ListAsync())[0] is { State: "failed", ReasonCode: "notSupported" }, "the unknown model wasn't refused");

        await manager.UpdateAsync(found.Saved.Id, s => s with { FamilySettings = """{"model":"wonder-360"}""" });
        await Until(async () =>
        {
            await manager.RunPassAsync();
            return (await manager.ListAsync())[0].State == "running";
        }, "the panel never ran as the model it was tried as");
        Assert.Equal("wonder-360", driver.Opened[^1].FamilySettings.GetProperty("model").GetString());
    }

    /// <summary>
    /// A family whose probe names <see cref="ProbedModel"/>; its start refuses a model other than <c>wonder-360</c> unless
    /// the <c>model</c> setting names it (as a driver whose handshake picks the model does).
    /// </summary>
    private sealed class Driver(FakeUsbClock clock) : IPanelDriver
    {
        private readonly List<PanelOpenOptions> _opened = [];
        private readonly List<Session> _sessions = [];

        public string ProbedModel { get; init; } = "wonder-360";

        /// <summary>The one format its sessions take.</summary>
        public PanelFrameFormat Format { get; init; } = PanelFrameFormat.Rgb565Le;

        public string Family => "set";

        public IReadOnlyList<PanelMatch> Matches { get; } = [new(Vendor, Product)];

        public IReadOnlyList<PanelOpenOptions> Opened
        {
            get
            {
                lock (_opened)
                    return [.. _opened];
            }
        }

        public IReadOnlyList<Session> Sessions
        {
            get
            {
                lock (_opened)
                    return [.. _sessions];
            }
        }

        public PanelProbe Probe(PanelCandidate device, CancellationToken stop) =>
            new(true, new PanelIdentity(ProbedModel, 8, 2, "S1", "1"), PanelDeviceStatus.Ok);

        public PanelOpen Open(PanelCandidate device, PanelIdentity identity, PanelOpenOptions options, CancellationToken stop)
        {
            var model = options.FamilySettings.ValueKind == JsonValueKind.Object && options.FamilySettings.TryGetProperty("model", out var tried) ? tried.GetString() : identity.Model;
            lock (_opened)
            {
                _opened.Add(options);
                if (model != "wonder-360")
                    return new PanelOpen(null, PanelDeviceStatus.NotSupported, $"model {identity.Model} isn't one this family knows");
                var session = new Session(clock, Format);
                _sessions.Add(session);
                return new PanelOpen(session, PanelDeviceStatus.Ok, "");
            }
        }
    }

    private sealed class Session(FakeUsbClock clock, PanelFrameFormat format) : IPanelSession
    {
        private readonly List<long> _writes = [];

        public PanelCapabilities Capabilities { get; } = new(8, 2, [format], MaxFps: 30);

        public bool Closed { get; private set; }

        /// <summary>When each frame (not a keep-alive) was written, the fake clock's milliseconds.</summary>
        public IReadOnlyList<long> Writes
        {
            get
            {
                lock (_writes)
                    return [.. _writes];
            }
        }

        public PanelWrite WriteFrame(PanelFrameView frame, CancellationToken stop)
        {
            lock (_writes)
                _writes.Add(clock.NowMs);
            return new(PanelDeviceStatus.Ok, TimeSpan.FromMilliseconds(1));
        }

        public PanelDeviceStatus SetBrightness(int percent) => PanelDeviceStatus.Ok;

        public void Close(bool screenOff, TimeSpan budget) => Closed = true;

        public void Dispose()
        {
        }
    }
}
