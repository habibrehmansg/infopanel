using System.Buffers;
using System.Text;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Sensors;
using InfoPanel.Tests.Desktop;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using static InfoPanel.Tests.Plugins.PluginPictures;

namespace InfoPanel.Tests.Plugins;

// The picture transport's bounds as the engine holds them: a picture comes as base64 text and is decoded only once the
// engine has admitted it, for a listed image entry of that host and within the rates; its size is known from the text's
// length before anything is decoded.
public sealed class PluginImageBoundsTests
{
    private const string Folder = "InfoPanel.Fixture";
    private const int MiB = 1024 * 1024;

    // The message as the engine reads it (its JSON-RPC formatter, then the callback's arguments bound as the dispatcher
    // binds them): a picture over 8 MiB, or one for an entry the host didn't list, costs no buffer of its size.
    [Theory]
    [InlineData("cover")]
    [InlineData("unlisted")]
    public void A_picture_over_8_MiB_or_for_an_unlisted_entry_is_refused_before_its_bytes_are_decoded(string entry)
    {
        var logger = new CountingLogger<PluginSensorSource>();
        var source = new PluginSensorSource(logger: logger);
        Assert.Empty(source.Load(Folder, [new PluginMetadataDto { Id = "p", Name = "P" }],
            new Dictionary<string, List<ContainerDto>> { ["p"] = [new ContainerDto { Id = "c", Name = "C", Entries = [new EntryDto { Id = "cover", Name = "Cover", Type = "image" }] }] }));
        source.SetState(Folder, PluginHostState.Running);
        var picture = Png(PluginImage.MaxBytes + MiB, 1);
        var request = Message(entry, Convert.ToBase64String(picture));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var updates = Arguments(request);
        source.Update(Folder, updates);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 0, MiB);
        Assert.Equal((SensorStatus.Unavailable, SensorKind.Image), (source.ReadState(Key("cover")).Status, source.ReadState(Key("cover")).Type));
        Assert.Equal(entry == "cover" ? 1 : 0, logger.Warnings);
    }

    // The engine admits a host's pictures within the host's rates and 5 s more burst (what a stall can hand over at
    // once): an entry 6 at once and one a second; a host 22 MiB of wire bytes at once and 2 MiB a second, 96 pictures at
    // once and 16 a second. Past them a picture is refused before it is decoded, as no picture, with one warning for the
    // host.
    [Fact]
    public void The_engine_admits_a_hosts_pictures_only_within_the_rates_and_refuses_the_rest_undecoded_with_one_warning()
    {
        var logger = new CountingLogger<PluginSensorSource>();
        var time = new ManualTime(Start);
        var source = Running(logger, time, [.. Enumerable.Range(0, 100).Select(i => $"e{i}")]);

        // An entry: six at once, then one a second.
        for (byte i = 1; i <= 6; i++)
            Send(source, "e0", Png(1000, i));
        Assert.Equal(ReferenceOf(Png(1000, 6)), source.ReadState(Key("e0")).Reading.ValueText);
        Assert.Equal(0, logger.Warnings);
        Send(source, "e0", Png(1000, 7));
        Assert.Equal(SensorStatus.Unavailable, source.ReadState(Key("e0")).Status);
        Assert.Equal(1, logger.Warnings);
        time.Advance(TimeSpan.FromSeconds(1));
        Send(source, "e0", Png(1000, 8));
        Assert.Equal(ReferenceOf(Png(1000, 8)), source.ReadState(Key("e0")).Reading.ValueText);

        // A host: 22 MiB of wire bytes at once: two 8 MiB pictures (2 × 11,188,908) leave no room for a third, which is
        // refused undecoded.
        time.Advance(TimeSpan.FromMinutes(1));
        Send(source, "e1", Png(PluginImage.MaxBytes, 1));
        Send(source, "e2", Png(PluginImage.MaxBytes, 2));
        Assert.Equal(SensorStatus.Live, source.ReadState(Key("e2")).Status);
        var request = Message("e3", Convert.ToBase64String(Png(PluginImage.MaxBytes, 3)));
        var before = GC.GetAllocatedBytesForCurrentThread();
        source.Update(Folder, Arguments(request));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, MiB);
        Assert.Equal(SensorStatus.Unavailable, source.ReadState(Key("e3")).Status);
        // 2 MiB a second: the third goes 5.4 s on.
        time.Advance(TimeSpan.FromSeconds(5.4));
        Send(source, "e3", Png(PluginImage.MaxBytes, 3));
        Assert.Equal(SensorStatus.Live, source.ReadState(Key("e3")).Status);

        // A host: 96 pictures at once, then 16 a second.
        time.Advance(TimeSpan.FromMinutes(1));
        for (var i = 4; i < 100; i++)
            Send(source, $"e{i}", Png(100, 1));
        Assert.Equal(SensorStatus.Live, source.ReadState(Key("e99")).Status);
        Send(source, "e0", Png(100, 2));
        Assert.Equal(SensorStatus.Unavailable, source.ReadState(Key("e0")).Status);
        time.Advance(TimeSpan.FromMilliseconds(70));
        Send(source, "e0", Png(100, 3));
        Assert.Equal(SensorStatus.Live, source.ReadState(Key("e0")).Status);
        Assert.Equal(1, logger.Warnings);
    }

    // A picture is committed only in the host generation and entry turn it was admitted in: a stop, a failure or a new
    // catalogue while it is checked (outside the lock) drops it.
    [Theory]
    [InlineData("stop")]
    [InlineData("fail")]
    [InlineData("reload")]
    public void A_picture_being_checked_when_its_host_stops_fails_or_reloads_is_dropped(string what)
    {
        var time = new ManualTime(Start);
        var source = Running(null, time, "cover");
        var picture = Png(1000, 1);
        source.BeforePictureCommit = () =>
        {
            source.BeforePictureCommit = null;
            switch (what)
            {
                case "stop":
                    source.SetState(Folder, PluginHostState.Stopped);
                    source.SetState(Folder, PluginHostState.Running);
                    break;
                case "fail":
                    source.SetState(Folder, PluginHostState.Failed);
                    break;
                default:
                    Load(source, "cover");
                    break;
            }
        };

        Send(source, "cover", picture);

        Assert.Equal((SensorStatus.Unavailable, SensorKind.Image), (source.ReadState(Key("cover")).Status, source.ReadState(Key("cover")).Type));
        Assert.False(source.TryGetImage(Key("cover"), ReferenceOf(picture), out _));
        // The next picture is taken.
        source.SetState(Folder, PluginHostState.Running);
        Send(source, "cover", picture);
        Assert.Equal(ReferenceOf(picture), source.ReadState(Key("cover")).Reading.ValueText);
    }

    // Of an entry's pictures admitted in turn, one checked later never lands after a later one or a later no picture.
    [Fact]
    public void A_picture_admitted_before_a_later_one_or_none_is_not_committed_after_it()
    {
        var time = new ManualTime(Start);
        var source = Running(null, time, "cover");
        var (a, b) = (Png(1000, 1), Png(1000, 2));
        source.BeforePictureCommit = () =>
        {
            source.BeforePictureCommit = null;
            Send(source, "cover", b);
        };
        Send(source, "cover", a);
        Assert.Equal(ReferenceOf(b), source.ReadState(Key("cover")).Reading.ValueText);

        source.BeforePictureCommit = () =>
        {
            source.BeforePictureCommit = null;
            Send(source, "cover", null);
        };
        Send(source, "cover", a);
        Assert.Equal(SensorStatus.Unavailable, source.ReadState(Key("cover")).Status);
        Assert.False(source.TryGetImage(Key("cover"), ReferenceOf(a), out _));
    }

    [Fact]
    public void A_pictures_text_gives_its_decoded_length_before_it_is_decoded()
    {
        Assert.Equal(new long[] { 0, 1, 2, 3, -1, -1 }, new[] { "", "QQ==", "QUI=", "QUJD", "QQ=", "QUJDR" }.Select(PictureText.DecodedLength).ToArray());
        Assert.Equal("ABC"u8.ToArray(), PictureText.Decode("QUJD", 3, out var tooLarge));
        Assert.False(tooLarge);
        Assert.Null(PictureText.Decode("QUJD", 2, out tooLarge));
        Assert.True(tooLarge);
        Assert.Null(PictureText.Decode("Q!JD", 3, out tooLarge));
        Assert.False(tooLarge);
        var largest = Convert.ToBase64String(new byte[PluginImage.MaxBytes]);
        Assert.Equal((11_184_812, PluginImage.MaxBytes), (largest.Length, PictureText.DecodedLength(largest)));
        Assert.Equal(11_184_812 + 4096, IpcLimits.ImageWireBytes(PluginImage.MaxBytes));
    }

    private static readonly DateTimeOffset Start = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private static PluginSensorSource Running(ILogger<PluginSensorSource>? logger, TimeProvider time, params string[] entries)
    {
        var source = new PluginSensorSource(logger: logger, time: time);
        Load(source, entries);
        source.SetState(Folder, PluginHostState.Running);
        return source;
    }

    private static void Load(PluginSensorSource source, params string[] entries) =>
        Assert.Empty(source.Load(Folder, [new PluginMetadataDto { Id = "p", Name = "P" }],
            new Dictionary<string, List<ContainerDto>>
            {
                ["p"] = [new ContainerDto { Id = "c", Name = "C", Entries = [.. entries.Select(e => new EntryDto { Id = e, Name = e, Type = "image" })] }],
            }));

    /// <summary>An update of the host's: the entry's picture as base64, or none (null).</summary>
    private static void Send(PluginSensorSource source, string entry, byte[]? bytes) =>
        source.Update(Folder,
        [
            new SensorUpdateBatchDto
            {
                PluginId = "p",
                Updates = [new EntryUpdateDto { ContainerId = "c", EntryId = entry, Type = "image", ImageValue = bytes == null ? null : Convert.ToBase64String(bytes) }],
            },
        ]);

    private static SensorKey Key(string entry) => SensorKey.Plugin($"/p/c/{entry}");

    /// <summary>An OnSensorUpdate notification as the host writes it, with one image update, read by the engine's formatter.</summary>
    private static JsonRpcRequest Message(string entry, string base64)
    {
        var json = "{\"jsonrpc\":\"2.0\",\"method\":\"OnSensorUpdate\",\"params\":[[{\"PluginId\":\"p\",\"Updates\":[{\"ContainerId\":\"c\",\"EntryId\":\""
            + entry + "\",\"Type\":\"image\",\"SensorValue\":null,\"TextValue\":null,\"TableValue\":null,\"ImageValue\":\"" + base64 + "\"}]}]]}";
        return (JsonRpcRequest)new JsonMessageFormatter().Deserialize(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>The callback's arguments, bound as the engine binds them (its handler's check, then the dispatcher).</summary>
    private static List<SensorUpdateBatchDto> Arguments(JsonRpcRequest request)
    {
        var parameters = typeof(IPluginClientCallback).GetMethod(nameof(IPluginClientCallback.OnSensorUpdate))!.GetParameters();
        var arguments = new object?[parameters.Length];
        Assert.Equal(JsonRpcRequest.ArgumentMatchResult.Success, request.TryGetTypedArguments(parameters, arguments));
        return (List<SensorUpdateBatchDto>)arguments[0]!;
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
