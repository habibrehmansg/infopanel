using InfoPanel.Plugins;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Scenes;
using InfoPanel.Sensors;
using InfoPanel.Tests.Desktop;
using Microsoft.Extensions.Logging;
using Xunit;
using static InfoPanel.Tests.Plugins.PluginPictures;

namespace InfoPanel.Tests.Plugins;

// A plugin's own picture in the engine: an image entry is an image sensor of the plugins source, its reading the picture's
// reference (16 hex digits of its SHA-256), its bytes answered through the hub (GET /api/sensors/image). A picture is
// inspected as media before it is kept; each entry keeps its current and its previous picture, a host at most 64 MiB of
// them; they go with the host's readings.
public class PluginImageSourceTests
{
    private const string Folder = "InfoPanel.Fixture";

    [Fact]
    public void An_image_entry_is_an_image_sensor_whose_reading_names_its_picture_and_whose_bytes_the_hub_answers()
    {
        var source = Running(null, Image("cover"));
        var hub = new SensorHub([source]);
        var key = Key("cover");
        Assert.Equal((SensorStatus.Unavailable, SensorKind.Image), (hub.ReadState(key).Status, hub.ReadState(key).Type));

        var png = Png(2000, 1);
        Send(source, "cover", png);

        var state = hub.ReadState(key);
        Assert.Equal((SensorStatus.Live, SensorKind.Image, ReferenceOf(png)), (state.Status, state.Type, state.Reading.ValueText));
        Assert.Equal(16, state.Reading.ValueText!.Length);
        Assert.True(hub.TryGetImage(key, ReferenceOf(png), out var bytes));
        Assert.Equal(png, bytes.ToArray());
        Assert.False(hub.TryGetImage(key, "0000000000000000", out _));
        Assert.False(hub.TryGetImage(Key("other"), ReferenceOf(png), out _));
        // A text input reads its reference, a number input nothing.
        var inputs = new SensorSceneInputs(hub, new SensorHistory(hub, TimeProvider.System));
        Assert.Equal(ReferenceOf(png), inputs.Read(key.ToString(), "text").Text);
        Assert.Equal(SceneInputStatus.Unavailable, inputs.Read(key.ToString(), "number").Status);
    }

    [Fact]
    public void Bytes_that_arent_a_PNG_JPEG_GIF_or_WebP_are_refused_as_media_with_one_warning()
    {
        var logger = new CountingLogger<PluginSensorSource>();
        var source = Running(logger, Image("cover"));
        var key = Key("cover");
        byte[][] refused =
        [
            "not a picture"u8.ToArray(),
            """<svg xmlns="http://www.w3.org/2000/svg" width="1" height="1"/>"""u8.ToArray(),
            Pixel[..20],
            [.. "RIFF"u8, 4, 0, 0, 0, .. "AVI "u8],
        ];

        foreach (var bytes in refused)
        {
            Send(source, "cover", bytes);
            Assert.Equal((SensorStatus.Unavailable, SensorKind.Image), (source.ReadState(key).Status, source.ReadState(key).Type));
            Assert.False(source.TryGetImage(key, ReferenceOf(bytes), out _));
        }
        Assert.Equal(1, logger.Warnings);

        Send(source, "cover", Pixel);
        Assert.Equal((SensorStatus.Live, ReferenceOf(Pixel)), (source.ReadState(key).Status, source.ReadState(key).Reading.ValueText));
    }

    [Fact]
    public void A_picture_over_8_MiB_is_refused_by_the_engine_too_with_one_warning()
    {
        var logger = new CountingLogger<PluginSensorSource>();
        var source = Running(logger, Image("cover"));
        var key = Key("cover");

        Send(source, "cover", Png(PluginImage.MaxBytes + 1, 1));
        Send(source, "cover", Png(PluginImage.MaxBytes + 2, 2));

        Assert.Equal(SensorStatus.Unavailable, source.ReadState(key).Status);
        Assert.Equal(1, logger.Warnings);
        var largest = Png(PluginImage.MaxBytes, 3);
        Send(source, "cover", largest);
        Assert.Equal(ReferenceOf(largest), source.ReadState(key).Reading.ValueText);
        Assert.True(source.TryGetImage(key, ReferenceOf(largest), out _));
    }

    [Fact]
    public void No_picture_makes_the_sensor_unavailable_and_keeps_the_one_before_for_a_hold()
    {
        var source = Running(null, Image("cover"));
        var key = Key("cover");
        var a = Png(1000, 1);
        Send(source, "cover", a);
        Assert.Equal(SensorStatus.Live, source.ReadState(key).Status);

        Send(source, "cover", null);

        Assert.Equal((SensorStatus.Unavailable, SensorKind.Image), (source.ReadState(key).Status, source.ReadState(key).Type));
        Assert.False(source.TryRead(key, out _));
        Assert.True(source.TryGetImage(key, ReferenceOf(a), out var held));
        Assert.Equal(a, held.ToArray());
    }

    [Fact]
    public void Each_entry_keeps_its_current_and_its_previous_picture()
    {
        var source = Running(null, Image("cover"), Image("icon"));
        var key = Key("cover");
        var (a, b, c) = (Png(1000, 1), Png(1000, 2), Png(1000, 3));
        var icon = Png(1000, 4);
        Send(source, "icon", icon);

        Send(source, "cover", a);
        Send(source, "cover", b);
        Send(source, "cover", c);

        Assert.Equal(ReferenceOf(c), source.ReadState(key).Reading.ValueText);
        Assert.False(source.TryGetImage(key, ReferenceOf(a), out _));
        Assert.True(source.TryGetImage(key, ReferenceOf(b), out _));
        Assert.True(source.TryGetImage(key, ReferenceOf(c), out _));
        // Another entry's picture is its own.
        Assert.False(source.TryGetImage(key, ReferenceOf(icon), out _));
        Assert.True(source.TryGetImage(Key("icon"), ReferenceOf(icon), out _));

        // Back to the one before: the two swap.
        Send(source, "cover", b);
        Assert.Equal(ReferenceOf(b), source.ReadState(key).Reading.ValueText);
        Assert.True(source.TryGetImage(key, ReferenceOf(c), out _));
    }

    [Fact]
    public void A_host_keeps_at_most_64_MiB_of_pictures_past_which_a_new_one_is_refused_with_one_warning()
    {
        var logger = new CountingLogger<PluginSensorSource>();
        var source = Running(logger, Image("e0"), Image("e1"), Image("e2"), Image("e3"), Image("e4"));
        byte seed = 0;
        // Four entries, each with a current and a previous picture of 8 MiB: 64 MiB.
        for (var e = 0; e < 4; e++)
        {
            Send(source, $"e{e}", Png(PluginImage.MaxBytes, ++seed));
            Send(source, $"e{e}", Png(PluginImage.MaxBytes, ++seed));
        }
        Assert.Equal(0, logger.Warnings);

        var refused = Png(PluginImage.MaxBytes, ++seed);
        Send(source, "e4", refused);
        Send(source, "e4", Png(PluginImage.MaxBytes, ++seed));

        Assert.Equal(SensorStatus.Unavailable, source.ReadState(Key("e4")).Status);
        Assert.False(source.TryGetImage(Key("e4"), ReferenceOf(refused), out _));
        Assert.Equal(1, logger.Warnings);

        // A new picture that lets go of its entry's previous one keeps the host at 64 MiB: taken.
        var replaced = Png(PluginImage.MaxBytes, ++seed);
        Send(source, "e0", replaced);
        Assert.Equal(ReferenceOf(replaced), source.ReadState(Key("e0")).Reading.ValueText);

        // No picture on e1 lets go of its previous one: room for e4's.
        Send(source, "e1", null);
        var taken = Png(PluginImage.MaxBytes, ++seed);
        Send(source, "e4", taken);
        Assert.Equal((SensorStatus.Live, ReferenceOf(taken)), (source.ReadState(Key("e4")).Status, source.ReadState(Key("e4")).Reading.ValueText));
        Assert.Equal(1, logger.Warnings);
    }

    [Theory]
    [InlineData(PluginHostState.Stopped)]
    [InlineData(PluginHostState.Failed)]
    public void A_stopped_or_failed_hosts_pictures_go_with_its_readings(PluginHostState state)
    {
        var source = Running(null, Image("cover"));
        var key = Key("cover");
        var (a, b) = (Png(1000, 1), Png(1000, 2));
        Send(source, "cover", a);
        Send(source, "cover", b);

        source.SetState(Folder, state);

        Assert.Equal((SensorStatus.Unavailable, SensorKind.Image), (source.ReadState(key).Status, source.ReadState(key).Type));
        Assert.False(source.TryGetImage(key, ReferenceOf(a), out _));
        Assert.False(source.TryGetImage(key, ReferenceOf(b), out _));
        // Started again, it has no picture until its host sends one.
        source.SetState(Folder, PluginHostState.Running);
        Assert.Equal(SensorStatus.Unavailable, source.ReadState(key).Status);
        Assert.False(source.TryGetImage(key, ReferenceOf(b), out _));
    }

    [Fact]
    public void A_restarting_hosts_pictures_stay_stale_until_its_new_host_lists_its_entries_and_sends_its_own()
    {
        var source = Running(null, Image("e0"), Image("e1"), Image("e2"), Image("e3"));
        byte seed = 0;
        for (var e = 0; e < 4; e++)
        {
            Send(source, $"e{e}", Png(PluginImage.MaxBytes, ++seed));
            Send(source, $"e{e}", Png(PluginImage.MaxBytes, ++seed));
        }
        var last = Png(PluginImage.MaxBytes, seed);

        source.SetState(Folder, PluginHostState.Restarting);
        var kept = source.ReadState(Key("e3"));
        Assert.Equal((SensorStatus.Stale, SensorKind.Image, ReferenceOf(last)), (kept.Status, kept.Type, kept.Reading.ValueText));
        Assert.True(source.TryGetImage(Key("e3"), ReferenceOf(last), out _));

        // The new host lists its entries: the old host's pictures go with its readings, and its 64 MiB count afresh.
        source.Load(Folder, [new PluginMetadataDto { Id = "p", Name = "P" }], Catalogue(Image("e0"), Image("e1"), Image("e2"), Image("e3")));
        source.SetState(Folder, PluginHostState.Running);
        Assert.Equal((SensorStatus.Unavailable, SensorKind.Image), (source.ReadState(Key("e3")).Status, source.ReadState(Key("e3")).Type));
        Assert.False(source.TryGetImage(Key("e3"), ReferenceOf(last), out _));

        Send(source, "e3", last);
        var again = Png(PluginImage.MaxBytes, ++seed);
        Send(source, "e0", again);
        Assert.Equal((SensorStatus.Live, ReferenceOf(last)), (source.ReadState(Key("e3")).Status, source.ReadState(Key("e3")).Reading.ValueText));
        Assert.Equal((SensorStatus.Live, ReferenceOf(again)), (source.ReadState(Key("e0")).Status, source.ReadState(Key("e0")).Reading.ValueText));
    }

    private static SensorKey Key(string entry) => SensorKey.Plugin($"/p/c/{entry}");

    private static EntryDto Image(string id) => new() { Id = id, Name = id, Type = "image" };

    private static Dictionary<string, List<ContainerDto>> Catalogue(params EntryDto[] entries) =>
        new() { ["p"] = [new ContainerDto { Id = "c", Name = "C", Entries = [.. entries] }] };

    // The engine's clock: each update comes a minute after the one before, so the picture rates (PluginImageBoundsTests)
    // never hold one back here.
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));

    private PluginSensorSource Running(ILogger<PluginSensorSource>? logger, params EntryDto[] entries)
    {
        var source = new PluginSensorSource(logger: logger, time: _time);
        Assert.Empty(source.Load(Folder, [new PluginMetadataDto { Id = "p", Name = "P" }], Catalogue(entries)));
        source.SetState(Folder, PluginHostState.Running);
        return source;
    }

    /// <summary>An update of the host's, a minute after the one before: the entry's picture (as base64, as it comes), or none (null).</summary>
    private void Send(PluginSensorSource source, string entry, byte[]? bytes)
    {
        _time.Advance(TimeSpan.FromMinutes(1));
        source.Update(Folder,
        [
            new SensorUpdateBatchDto
            {
                PluginId = "p",
                Updates = [new EntryUpdateDto { ContainerId = "c", EntryId = entry, Type = "image", ImageValue = bytes == null ? null : Convert.ToBase64String(bytes) }],
            },
        ]);
    }

    private sealed class CountingLogger<T> : ILogger<T>
    {
        private int _warnings;

        public int Warnings => _warnings;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Interlocked.Increment(ref _warnings);
        }
    }
}
