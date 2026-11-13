using System.Buffers;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Host;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Plugins.Loader;
using InfoPanel.Tests.Desktop;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using static InfoPanel.Tests.Plugins.PluginPictures;

namespace InfoPanel.Tests.Plugins;

// A plugin's own picture (PluginImage) on its way to the engine: the host reads it once a cycle and sends it, whole, in a
// notification of its own, only when its SHA-256 changed, within the bounds (IpcLimits): over 8 MiB left out with one
// warning, at most one new picture an entry a second, and a host's pictures within 2 MiB of wire bytes a second past a
// 12 MiB burst and 16 a second past a burst of 16, a picture waiting its turn replaced by a newer one. The host's clock is
// the test's.
public sealed class PluginImageTransportTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private const int MiB = 1024 * 1024;

    [Fact]
    public void SetImage_keeps_a_copy_and_Clear_or_no_bytes_leave_no_picture()
    {
        var bytes = Png(100, 1);
        var image = new PluginImage("cover", "Cover");
        Assert.True(image.Value.IsEmpty);

        image.SetImage(bytes);
        bytes[^1] ^= 0xFF;

        Assert.Equal(100, image.Value.Length);
        Assert.Equal(Png(100, 1), image.Value.ToArray());
        image.Clear();
        Assert.True(image.Value.IsEmpty);
        image.SetImage(Png(100, 2));
        image.SetImage([]);
        Assert.True(image.Value.IsEmpty);
        Assert.Equal(("cover-art", "Cover Art"), (new PluginImage("Cover Art").Id, new PluginImage("Cover Art").Name));
    }

    [Fact]
    public void A_picture_is_sent_whole_and_again_only_when_its_SHA256_changes()
    {
        var time = new ManualTime(Start);
        var image = new PluginImage("cover", "Cover");
        var wrapper = Wrap("stub", image);
        var manager = new SensorSnapshotManager(time: time);
        Assert.Empty(Deliver(manager, wrapper));

        var a = Png(1000, 1);
        image.SetImage(a);
        var sent = Assert.Single(Deliver(manager, wrapper));
        Assert.Equal(("pictures", "cover", "image"), (sent.ContainerId, sent.EntryId, sent.Type));
        Assert.Equal(a, sent.ImageValue);

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(Deliver(manager, wrapper));
        // The same bytes again, as another array: the same SHA-256, nothing to send.
        image.SetImage(a.ToArray());
        Assert.Empty(Deliver(manager, wrapper));

        // A picture is hashed at most once a second: a new one set just after waits for its hash.
        var b = Png(1000, 2);
        image.SetImage(b);
        Assert.Empty(Deliver(manager, wrapper));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(b, Assert.Single(Deliver(manager, wrapper)).ImageValue);
        Assert.Empty(Deliver(manager, wrapper));
    }

    [Fact]
    public void A_picture_over_8_MiB_is_left_out_as_no_picture_with_one_warning()
    {
        var time = new ManualTime(Start);
        var reported = new List<(string Plugin, string Message)>();
        var image = new PluginImage("cover", "Cover");
        var wrapper = Wrap("stub", image);
        var manager = new SensorSnapshotManager(reportCut: (plugin, message) => reported.Add((plugin, message)), time: time);
        image.SetImage(Png(1000, 1));
        Assert.Single(Deliver(manager, wrapper));

        time.Advance(TimeSpan.FromSeconds(2));
        image.SetImage(Png(PluginImage.MaxBytes + 1, 2));
        var none = Assert.Single(Deliver(manager, wrapper));
        Assert.Equal(("cover", "image", null), (none.EntryId, none.Type, none.ImageValue));

        time.Advance(TimeSpan.FromSeconds(2));
        image.SetImage(Png(PluginImage.MaxBytes + 2, 3));
        Assert.Empty(Deliver(manager, wrapper));
        var (plugin, message) = Assert.Single(reported);
        Assert.Equal("stub", plugin);
        Assert.Contains("over 8 MiB", message);

        // 8 MiB itself is within the bound.
        image.SetImage(Png(PluginImage.MaxBytes, 4));
        Assert.Equal(PluginImage.MaxBytes, Assert.Single(Deliver(manager, wrapper)).ImageValue!.Length);
        Assert.Single(reported);
    }

    [Fact]
    public void An_entry_sends_at_most_one_new_picture_a_second_and_the_newest_waiting_one_wins()
    {
        var time = new ManualTime(Start);
        var image = new PluginImage("cover", "Cover");
        var wrapper = Wrap("stub", image);
        var manager = new SensorSnapshotManager(time: time);
        image.SetImage(Png(1000, 1));
        Assert.Single(Deliver(manager, wrapper));

        time.Advance(TimeSpan.FromMilliseconds(100));
        image.SetImage(Png(1000, 2));
        Assert.Empty(Deliver(manager, wrapper));
        time.Advance(TimeSpan.FromMilliseconds(400));
        var newest = Png(1000, 3);
        image.SetImage(newest);
        Assert.Empty(Deliver(manager, wrapper));
        time.Advance(TimeSpan.FromMilliseconds(499));
        Assert.Empty(Deliver(manager, wrapper));

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(newest, Assert.Single(Deliver(manager, wrapper)).ImageValue);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(Deliver(manager, wrapper));
    }

    // A 4 MiB picture counts 5,596,504 wire bytes (its base64, 5,592,408, and 4 KiB for the message): two fit the 12 MiB
    // burst, leaving 1,389,904; a third needs 2.006 s more of the 2 MiB a second.
    [Fact]
    public void A_host_sends_at_most_2_MiB_of_picture_wire_bytes_a_second_past_a_12_MiB_burst_the_longest_waiting_first_with_its_newest_picture()
    {
        var time = new ManualTime(Start);
        var (one, two, three) = (new PluginImage("one", "One"), new PluginImage("two", "Two"), new PluginImage("three", "Three"));
        // Two plugins of one host: the rates are the host's.
        var first = Wrap("first", one, two);
        var second = Wrap("second", three);
        var manager = new SensorSnapshotManager(time: time);
        Assert.Equal(5_596_504, IpcLimits.ImageWireBytes(4 * MiB));
        one.SetImage(Png(4 * MiB, 1));
        two.SetImage(Png(4 * MiB, 2));
        three.SetImage(Png(4 * MiB, 3));

        Assert.Equal(["one", "two"], Deliver(manager, first, second).Select(u => u.EntryId).ToArray());

        time.Advance(TimeSpan.FromMilliseconds(500));
        var threeNewest = Png(4 * MiB, 4);
        three.SetImage(threeNewest);
        one.SetImage(Png(4 * MiB, 5));
        Assert.Empty(Deliver(manager, first, second));
        time.Advance(TimeSpan.FromMilliseconds(100));
        two.SetImage(Png(4 * MiB, 6));
        Assert.Empty(Deliver(manager, first, second));
        time.Advance(TimeSpan.FromMilliseconds(1400));
        Assert.Empty(Deliver(manager, first, second));

        // Three has waited longest and goes first, with its newest picture; one waits for the next 5.6 MB.
        time.Advance(TimeSpan.FromMilliseconds(10));
        var sent = Assert.Single(Deliver(manager, first, second));
        Assert.Equal("three", sent.EntryId);
        Assert.Equal(threeNewest, sent.ImageValue);
        time.Advance(TimeSpan.FromMilliseconds(2660));
        Assert.Empty(Deliver(manager, first, second));

        time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal("one", Assert.Single(Deliver(manager, first, second)).EntryId);
    }

    [Fact]
    public void A_host_sends_at_most_16_pictures_a_second_past_a_burst_of_16()
    {
        var time = new ManualTime(Start);
        var images = Enumerable.Range(0, 20).Select(i => new PluginImage($"e{i}", $"E{i}")).ToArray();
        var wrapper = Wrap("stub", images);
        var manager = new SensorSnapshotManager(time: time);
        for (var i = 0; i < images.Length; i++)
            images[i].SetImage(Png(100, (byte)i));

        Assert.Equal(16, Deliver(manager, wrapper).Count);
        time.Advance(TimeSpan.FromMilliseconds(120));
        Assert.Equal(["e16"], Deliver(manager, wrapper).Select(u => u.EntryId).ToArray());
        time.Advance(TimeSpan.FromMilliseconds(150));
        Assert.Equal(["e17", "e18", "e19"], Deliver(manager, wrapper).Select(u => u.EntryId).ToArray());
    }

    // The engine's pending budget (32 MiB and 1,024 calls) holds 5 s of what the host may send while the engine handles
    // nothing, beside the readings: the host's every-100-ms cycles, from full rates, with pictures that never stop changing.
    [Fact]
    public void Five_seconds_of_pictures_fit_the_engines_pending_budget_beside_the_readings()
    {
        var limits = PluginLimits.Default;
        var formatter = new JsonMessageFormatter();

        // Three entries with 8 MiB pictures, each new every half second: at most the 12 MiB burst and 5 × 2 MiB.
        var time = new ManualTime(Start);
        var large = Enumerable.Range(0, 3).Select(i => new PluginImage($"large{i}", $"Large {i}")).ToArray();
        var wrapper = Wrap("stub", large);
        var manager = new SensorSnapshotManager(time: time);
        long bytes = 0;
        var pictures = 0;
        byte seed = 0;
        for (var cycle = 0; cycle <= 50; cycle++)
        {
            if (cycle % 5 == 0)
            {
                foreach (var image in large)
                    image.SetImage(Png(PluginImage.MaxBytes, ++seed));
            }
            foreach (var (pluginId, update) in Sends(manager, wrapper))
            {
                bytes += Serialised(formatter, pluginId, update);
                pictures++;
            }
            time.Advance(TimeSpan.FromMilliseconds(100));
        }
        Assert.InRange(pictures, 2, int.MaxValue);
        Assert.InRange(bytes, 0, IpcLimits.ImageWireBurstBytes + IpcLimits.StallSeconds * IpcLimits.ImageWireBytesPerSecond);
        Assert.InRange(bytes + IpcLimits.HostMessageBytes, 0, limits.PendingBytes);

        // 2,000 entries with tiny pictures, each new every cycle: at most the burst of 16 and 5 × 16 calls, beside a
        // message of readings each cycle.
        time = new ManualTime(Start);
        var tiny = Enumerable.Range(0, 2000).Select(i => new PluginImage($"tiny{i}", $"Tiny {i}")).ToArray();
        wrapper = Wrap("stub", tiny);
        manager = new SensorSnapshotManager(time: time);
        var calls = 0;
        for (var cycle = 0; cycle <= 50; cycle++)
        {
            foreach (var image in tiny)
                image.SetImage(Png(100, (byte)cycle));
            calls += Sends(manager, wrapper).Count + 1;
            time.Advance(TimeSpan.FromMilliseconds(100));
        }
        Assert.InRange(calls - 51, 1, IpcLimits.ImagesBurst + IpcLimits.StallSeconds * IpcLimits.ImagesPerSecond);
        Assert.InRange(calls, 0, limits.PendingMessages);
    }

    // A picture is reserved alone, just before its send, and its reservation held through a send that stalls: the rates
    // fill again, and its entry's second runs, only from the send's end.
    [Fact]
    public void A_send_that_stalls_holds_its_pictures_reservation_until_it_ends()
    {
        var time = new ManualTime(Start);
        var (one, two, three) = (new PluginImage("one", "One"), new PluginImage("two", "Two"), new PluginImage("three", "Three"));
        var wrapper = Wrap("stub", one, two, three);
        var manager = new SensorSnapshotManager(time: time);
        one.SetImage(Png(4 * MiB, 1));
        two.SetImage(Png(4 * MiB, 2));

        var cycle = manager.GetChangedImages([wrapper]);
        Assert.Equal(["one", "two"], cycle.Select(s => s.Update.EntryId).ToArray());
        Assert.True(manager.TryReserve(cycle[0]));
        // Its send stalls for 3 s; two is reserved only as it ends, from the burst left: 1,389,904 bytes are left after it.
        time.Advance(TimeSpan.FromSeconds(3));
        manager.MarkSent("stub", [cycle[0].Update]);
        manager.Release(cycle[0]);
        Assert.True(manager.TryReserve(cycle[1]));
        manager.MarkSent("stub", [cycle[1].Update]);
        manager.Release(cycle[1]);

        // One's next picture waits a second from its send's end; three waits for the rate to refill from then too.
        one.SetImage(Png(100, 3));
        three.SetImage(Png(4 * MiB, 4));
        time.Advance(TimeSpan.FromMilliseconds(900));
        Assert.Empty(Deliver(manager, wrapper));
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(["one"], Deliver(manager, wrapper).Select(u => u.EntryId).ToArray());
        time.Advance(TimeSpan.FromMilliseconds(1000));
        Assert.Empty(Deliver(manager, wrapper));
        time.Advance(TimeSpan.FromMilliseconds(1100));
        Assert.Equal(["three"], Deliver(manager, wrapper).Select(u => u.EntryId).ToArray());
    }

    // A plugin that sets the same picture again and again, a new array each time, costs one hash a second; the newest
    // picture it sets still goes.
    [Fact]
    public void A_picture_is_hashed_at_most_once_a_second_the_newest_one_set_waiting()
    {
        var time = new ManualTime(Start);
        var image = new PluginImage("cover", "Cover");
        var wrapper = Wrap("stub", image);
        var manager = new SensorSnapshotManager(time: time);
        var a = Png(1000, 1);
        image.SetImage(a);
        Assert.Single(Deliver(manager, wrapper));
        Assert.Equal(1, manager.ImageHashes);

        for (var cycle = 0; cycle < 30; cycle++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            image.SetImage(a);
            Assert.Empty(Deliver(manager, wrapper));
        }
        Assert.Equal(1 + 3, manager.ImageHashes);

        var b = Png(1000, 2);
        image.SetImage(b);
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Empty(Deliver(manager, wrapper));
        var c = Png(1000, 3);
        image.SetImage(c);
        time.Advance(TimeSpan.FromMilliseconds(900));
        Assert.Equal(c, Assert.Single(Deliver(manager, wrapper)).ImageValue);
        Assert.Equal(1 + 3 + 1, manager.ImageHashes);
    }

    /// <summary>One cycle's pictures as the host sends them (<see cref="Deliver"/>), with each one's plugin and update.</summary>
    private static List<(string PluginId, EntryUpdateDto Update)> Sends(SensorSnapshotManager manager, PluginWrapper wrapper)
    {
        var sends = new List<(string, EntryUpdateDto)>();
        foreach (var send in manager.GetChangedImages([wrapper]))
        {
            if (!manager.TryReserve(send))
                break;
            manager.MarkSent(send.PluginId, [send.Update]);
            manager.Release(send);
            sends.Add((send.PluginId, send.Update));
        }
        return sends;
    }

    /// <summary>The bytes of the message a picture goes in, as the host's formatter writes it.</summary>
    private static long Serialised(JsonMessageFormatter formatter, string pluginId, EntryUpdateDto update)
    {
        var json = new ArrayBufferWriter<byte>();
        formatter.Serialize(json, new JsonRpcRequest
        {
            Method = nameof(IPluginClientCallback.OnSensorUpdate),
            ArgumentsList = [new List<SensorUpdateBatchDto> { new() { PluginId = pluginId, Updates = [update] } }],
        });
        return json.WrittenCount;
    }

    [Fact]
    public void Clearing_a_picture_sends_no_picture_at_once_and_the_next_picture_still_waits_its_second()
    {
        var time = new ManualTime(Start);
        var image = new PluginImage("cover", "Cover");
        var wrapper = Wrap("stub", image);
        var manager = new SensorSnapshotManager(time: time);
        var a = Png(1000, 1);
        image.SetImage(a);
        Assert.Single(Deliver(manager, wrapper));

        time.Advance(TimeSpan.FromMilliseconds(100));
        image.Clear();
        var none = Assert.Single(Deliver(manager, wrapper));
        Assert.Equal(("cover", "image", null), (none.EntryId, none.Type, none.ImageValue));
        Assert.Empty(Deliver(manager, wrapper));

        time.Advance(TimeSpan.FromMilliseconds(100));
        image.SetImage(a);
        Assert.Empty(Deliver(manager, wrapper));
        time.Advance(TimeSpan.FromMilliseconds(800));
        Assert.Equal(a, Assert.Single(Deliver(manager, wrapper)).ImageValue);
    }

    // The host's cycle: the plugin's readings in one message, then each picture in a message of its own (an 8 MiB picture
    // is about 10.7 MiB as base64), every one within the 16 MiB the engine reads.
    [Fact]
    public async Task Each_picture_goes_to_the_engine_in_a_notification_of_its_own_within_the_message_limit()
    {
        var a = new PluginImage("a", "A");
        var b = new PluginImage("b", "B");
        a.SetImage(Png(PluginImage.MaxBytes, 1));
        b.SetImage(Png(1000, 2));
        var plugin = new StubPlugin("stub", new PluginText("label", "Label", "hello"), a, b);
        var wrapper = new PluginWrapper(new PluginDescriptor("plugin.dll", null), plugin);
        await wrapper.Initialize();
        var host = new HostService("plugin.dll");
        host.AddInitializedWrapper(wrapper);
        var transport = new RecordingTransport();
        using var jsonRpc = new JsonRpc(transport);
        jsonRpc.StartListening();
        host.SetJsonRpc(jsonRpc);

        try
        {
            await host.RunUpdateCycleAsync([], TestContext.Current.CancellationToken);

            var messages = transport.Messages;
            Assert.Equal(3, messages.Count);
            Assert.Equal(["label"], messages[0].Entries);
            Assert.Equal(["a"], messages[1].Entries);
            Assert.Equal(["b"], messages[2].Entries);
            Assert.InRange(messages[1].Bytes, PluginImage.MaxBytes / 3 * 4, PluginLimits.Default.MessageBytes);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    /// <summary>An image update as the engine gets it: its picture decoded (null for none).</summary>
    internal sealed record Delivered(string ContainerId, string EntryId, string Type, byte[]? ImageValue);

    /// <summary>
    /// One host cycle's pictures, as the host sends them: read; each no picture delivered; then each picture reserved,
    /// delivered and released in turn, until one can't be reserved.
    /// </summary>
    internal static List<Delivered> Deliver(SensorSnapshotManager manager, params PluginWrapper[] wrappers)
    {
        var delivered = new List<Delivered>();
        foreach (var send in manager.GetChangedImages(wrappers))
        {
            if (!manager.TryReserve(send))
                break;
            manager.MarkSent(send.PluginId, [send.Update]);
            manager.Release(send);
            var update = send.Update;
            delivered.Add(new Delivered(update.ContainerId, update.EntryId, update.Type,
                update.ImageValue == null ? null : Convert.FromBase64String(update.ImageValue)));
        }
        return delivered;
    }

    private static PluginWrapper Wrap(string pluginId, params IPluginData[] entries)
    {
        var wrapper = new PluginWrapper(new PluginDescriptor("plugin.dll", null), new StubPlugin(pluginId));
        var container = new PluginContainer("pictures", "Pictures");
        container.Entries.AddRange(entries);
        wrapper.PluginContainers.Add(container);
        return wrapper;
    }

    /// <summary>A plugin without an interval whose Load lists <paramref name="entries"/> in one container, <c>pictures</c>.</summary>
    private sealed class StubPlugin(string id, params IPluginData[] entries) : IPlugin
    {
        public string Id => id;
        public string Name => "Stub";
        public string Description => "";
        public string? ConfigFilePath => null;
        public TimeSpan UpdateInterval => TimeSpan.Zero;
        public void Initialize() { }

        public void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("pictures", "Pictures");
            container.Entries.AddRange(entries);
            containers.Add(container);
        }

        public void Update() { }
        public Task UpdateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Close() { }
    }

    /// <summary>A JsonRpc transport that records each OnSensorUpdate: its entries' ids and its size, as the host's formatter writes it.</summary>
    private sealed class RecordingTransport : IJsonRpcMessageHandler
    {
        private readonly List<(List<string> Entries, long Bytes)> _messages = [];

        public bool CanRead => true;
        public bool CanWrite => true;
        public IJsonRpcMessageFormatter Formatter { get; } = new JsonMessageFormatter();

        public List<(List<string> Entries, long Bytes)> Messages
        {
            get { lock (_messages) return [.. _messages]; }
        }

        public async ValueTask<JsonRpcMessage?> ReadAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }

        public ValueTask WriteAsync(JsonRpcMessage message, CancellationToken cancellationToken)
        {
            if (message is JsonRpcRequest { Method: nameof(IPluginClientCallback.OnSensorUpdate) } request)
            {
                // The arguments are read before serialising, which replaces them with JSON tokens.
                var entries = ((IEnumerable<SensorUpdateBatchDto>)request.ArgumentsList![0]!).SelectMany(b => b.Updates).Select(u => u.EntryId).ToList();
                var json = new ArrayBufferWriter<byte>();
                Formatter.Serialize(json, message);
                lock (_messages)
                    _messages.Add((entries, json.WrittenCount));
            }
            return ValueTask.CompletedTask;
        }
    }
}
