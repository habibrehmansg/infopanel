using System.Net.Http.Json;
using System.Text.Json;
using InfoPanel.Core.Tests.Panels.Usb;
using InfoPanel.Hosting;
using InfoPanel.Panels;
using InfoPanel.Panels.BeadaPanel;
using InfoPanel.Panels.Usb;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests.Panels;

/// <summary>The panels in the engine: <c>--no-panels</c>, start-up without libusb or with a file it can't hold, and the quit's screen-off.</summary>
public sealed class PanelHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EngineOptions Options(params string[] args)
    {
        var parsed = EngineCommandLine.Parse(args);
        Assert.Null(parsed.Error);
        return EngineOptions.Load(new ConfigurationBuilder().AddInMemoryCollection(parsed.Settings).Build(), out _)!;
    }

    [Fact]
    public void No_panels_is_a_flag_off_by_default_and_in_the_help()
    {
        Assert.False(Options().NoPanels);
        Assert.True(Options("--no-panels").NoPanels);
        Assert.Contains("--no-panels", EngineCommandLine.Usage);
    }

    [Fact]
    public async Task Every_test_engine_runs_no_panels()
    {
        await using var engine = new EngineFactory();
        using var api = engine.CreateApiClient();
        Assert.True(engine.Services.GetRequiredService<EngineOptions>().NoPanels);
        Assert.Null(engine.Services.GetRequiredService<PanelService>().Manager);
    }

    [Fact]
    public async Task Without_libusb_the_engine_starts_and_logs_one_line()
    {
        await using var engine = new EngineFactory();
        var missing = Path.Combine(engine.Root, "no-libusb", "libusb-1.0.dylib");
        await using var factory = engine.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Engine:NoPanels", "false");
            builder.ConfigureTestServices(services => services.AddSingleton(new PanelServiceOptions { LibraryCandidates = () => [missing] }));
        });
        using var api = factory.CreateClient();

        Assert.Equal("ok", await api.GetStringAsync("/healthz", Ct));
        Assert.Null(factory.Services.GetRequiredService<PanelService>().Manager);
        // The panel service's own lines (its logger's source), not every line naming libusb: the host's content root path
        // can name it too (a checkout's folder).
        var logged = factory.Services.GetRequiredService<RecentLog>().After(0).Where(e => e.Source == typeof(PanelService).FullName);
        var line = Assert.Single(logged);
        Assert.Contains("USB panels are unsupported", line.Message);
        Assert.Contains(missing, line.Message);
    }

    [Fact]
    public async Task A_panels_file_it_cant_hold_leaves_panels_off_and_the_file_as_it_is()
    {
        await using var engine = new EngineFactory();
        Directory.CreateDirectory(engine.DataDir);
        var file = Path.Combine(engine.DataDir, "devices.json");
        File.WriteAllText(file, "{\"format\": \"infopanel.devices/9\", \"devices\": []}");
        var backend = new FakeUsbBackend();
        await using var factory = engine.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Engine:NoPanels", "false");
            builder.ConfigureTestServices(services => services.AddSingleton(new PanelServiceOptions { Backend = () => backend }));
        });
        using var api = factory.CreateClient();

        Assert.Equal("ok", await api.GetStringAsync("/healthz", Ct));
        Assert.Null(factory.Services.GetRequiredService<PanelService>().Manager);
        Assert.Contains(factory.Services.GetRequiredService<RecentLog>().After(0), e => e.Level == "error" && e.Message.Contains("USB panels are off this run", StringComparison.Ordinal));
        Assert.Equal("{\"format\": \"infopanel.devices/9\", \"devices\": []}", File.ReadAllText(file));
    }

    [Fact]
    public async Task A_found_panel_is_saved_off_and_a_running_one_goes_dark_when_the_engine_stops()
    {
        var backend = new FakeUsbBackend();
        var device = backend.Add(new FakeUsbDevice(new UsbDeviceId(BeadaPanelProtocol.VendorId, BeadaPanelProtocol.ProductId, 0, 4, "0-1"))
        {
            Reply = PanelReplies.Info(2, 5, 21, 1920, 480, "934072005c"),
        });
        var engine = new EngineFactory();
        var factory = engine.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Engine:NoPanels", "false");
            builder.ConfigureTestServices(services => services.AddSingleton(new PanelServiceOptions
            {
                Backend = () => backend,
                Runner = new PanelRunnerOptions { KeepAlive = Timeout.InfiniteTimeSpan },
            }));
        });
        try
        {
            using var api = factory.CreateClient();
            api.DefaultRequestHeaders.Authorization = new("Bearer", EngineFactory.Token);
            var profiles = await api.GetFromJsonAsync<JsonElement>("/api/profiles", Ct);
            var profile = profiles.EnumerateArray().First(p => p.GetProperty("name").GetString() == "Wide bar").GetProperty("id").GetString()!;
            var manager = factory.Services.GetRequiredService<PanelService>().Manager!;

            var found = await WaitFor(manager, s => s.State == "off");
            Assert.Contains(factory.Services.GetRequiredService<RecentLog>().After(0), e => e.Message.Contains("found", StringComparison.Ordinal) && e.Message.Contains("saved, off", StringComparison.Ordinal));
            Assert.Contains(found.Saved.Id, File.ReadAllText(Path.Combine(engine.DataDir, "devices.json")));

            await manager.UpdateAsync(found.Saved.Id, s => s with { Enabled = true, ProfileId = profile });
            await WaitFor(manager, s => s.State == "running");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            // A new panel is saved Auto, and this one is a T113 at PanelLink 2: JPEG frames.
            while (!device.Transfers.Any(t => Jpeg.Is(t.Data)))
            {
                Assert.True(DateTime.UtcNow < deadline, "no frame was written");
                await Task.Delay(10, Ct);
            }
        }
        finally
        {
            await factory.DisposeAsync();
            await engine.DisposeAsync();
        }

        var t = device.Transfers;
        Assert.Equal(BeadaPanelProtocol.StatusLink(StatusLinkType.SetBacklight, [0]), t[^3].Data);
        Assert.True(Jpeg.IsBlack(t[^2].Data), "the stop's black frame");
        Assert.Equal(BeadaPanelProtocol.EndStream(BeadaPanelInfo.Parse(device.Reply!, out _, out _)!), t[^1].Data);
        Assert.Equal(0, device.OpenHandles);
    }

    /// <summary>
    /// Quit while the computer is going to sleep and a panel's screen-off hangs: the source's disposal waits for the
    /// will-sleep under way, and the panels' stop gets only what is left of the one 3 s deadline.
    /// </summary>
    [Fact]
    public async Task A_quit_during_a_will_sleep_whose_screen_off_hangs_takes_3_s_in_all()
    {
        var backend = new FakeUsbBackend();
        var device = backend.Add(new FakeUsbDevice(new UsbDeviceId(BeadaPanelProtocol.VendorId, BeadaPanelProtocol.ProductId, 0, 4, "0-1"))
        {
            Reply = PanelReplies.Info(2, 5, 21, 1920, 480, "934072005c"),
        });
        var power = new QuitWaitsForWillSleep();
        using var release = new ManualResetEventSlim(false);
        using var hung = new ManualResetEventSlim(false);
        var backlightOff = BeadaPanelProtocol.StatusLink(StatusLinkType.SetBacklight, [0]);
        var engine = new EngineFactory();
        var factory = engine.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Engine:NoPanels", "false");
            builder.ConfigureTestServices(services => services.AddSingleton(new PanelServiceOptions
            {
                Backend = () => backend,
                Runner = new PanelRunnerOptions { KeepAlive = Timeout.InfiniteTimeSpan },
                Power = _ => power,
            }));
        });
        try
        {
            using var api = factory.CreateClient();
            api.DefaultRequestHeaders.Authorization = new("Bearer", EngineFactory.Token);
            var profiles = await api.GetFromJsonAsync<JsonElement>("/api/profiles", Ct);
            var profile = profiles.EnumerateArray().First(p => p.GetProperty("name").GetString() == "Wide bar").GetProperty("id").GetString()!;
            var service = factory.Services.GetRequiredService<PanelService>();
            var manager = service.Manager!;
            var found = await WaitFor(manager, s => s.State == "off");
            await manager.UpdateAsync(found.Saved.Id, s => s with { Enabled = true, ProfileId = profile });
            await WaitFor(manager, s => s.State == "running");
            device.OnWrite = write =>
            {
                if (write.Data.AsSpan().SequenceEqual(backlightOff))
                {
                    hung.Set();
                    release.Wait(TimeSpan.FromSeconds(20)); // the screen-off hangs
                }
                return null;
            };

            var sleep = power.SleepAsync();
            Assert.True(hung.Wait(TimeSpan.FromSeconds(10), Ct));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await service.StopAsync(Ct);
            var took = watch.Elapsed;
            Assert.False(await sleep);

            Assert.True(took < TimeSpan.FromSeconds(3.5), $"the quit took {took}"); // 3 s in all, written out
        }
        finally
        {
            release.Set();
            await factory.DisposeAsync();
            await engine.DisposeAsync();
        }
    }

    /// <summary>A power source like macOS's at quit: its disposal waits for a will-sleep under way, at most 3 s.</summary>
    private sealed class QuitWaitsForWillSleep : InfoPanel.Panels.Power.IPowerEvents
    {
        private readonly SemaphoreSlim _inWillSleep = new(1, 1);
        private InfoPanel.Panels.Power.PowerHandlers? _handlers;

        public string Name => "the test's power source";

        public bool Start(InfoPanel.Panels.Power.PowerHandlers handlers)
        {
            _handlers = handlers;
            return true;
        }

        /// <summary>Will-sleep on a thread of its own; true when the panels were off before the sleep was allowed.</summary>
        public Task<bool> SleepAsync()
        {
            _inWillSleep.Wait();
            return Task.Run(() =>
            {
                try
                {
                    return InfoPanel.Panels.Power.PowerEvents.WaitForSuspending(_handlers!.Suspending, TimeSpan.FromSeconds(3));
                }
                finally
                {
                    _inWillSleep.Release();
                }
            });
        }

        public void Dispose()
        {
            if (_inWillSleep.Wait(TimeSpan.FromSeconds(3)))
                _inWillSleep.Release();
        }
    }

    private static async Task<PanelStatus> WaitFor(PanelManager manager, Func<PanelStatus, bool> wanted)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var list = await manager.ListAsync();
            if (list.Count > 0 && wanted(list[0]))
                return list[0];
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"never got there: {(list.Count > 0 ? list[0] : "none")}");
            await Task.Delay(20, Ct);
        }
    }
}
