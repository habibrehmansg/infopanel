using System.Buffers;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Host;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Plugins.Loader;
using InfoPanel.Sensors;
using InfoPanel.Tests.Desktop;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using static InfoPanel.Tests.Plugins.PluginPictures;

namespace InfoPanel.Tests.Plugins;

// A host's whole traffic to the engine, readings and pictures together, against what the engine holds while it handles
// nothing (its pending budget, 32 MiB and 1,024 calls): the host's cycles on the test's clock, each message measured as
// the host's formatter writes it. And the two races of a picture's way: a changed picture waiting for its hash keeps its
// place, and an update of a host the engine has replaced never lands in its successor's catalogue.
public sealed class PluginTrafficTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Cycle = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(5);
    private const int MiB = 1024 * 1024;
    private readonly string _root = PluginFixtures.NewTempRoot("ip-traffic-");

    public void Dispose() => PluginFixtures.DeleteQuietly(_root);

    // Two 8 MiB pictures and 1,000 texts of 4,096 characters changed at 0, 2 and 4 s: every host limit kept, yet a 5 s
    // stall must never see more than the engine holds. Everything still arrives, the newest of each.
    [Fact]
    public async Task A_hosts_readings_and_pictures_together_fit_the_engines_pending_budget_over_any_5_s()
    {
        var time = new ManualTime(Start);
        var texts = Enumerable.Range(0, 1000).Select(i => new PluginText($"t{i}", $"T{i}", "")).ToArray();
        var (a, b) = (new PluginImage("a", "A"), new PluginImage("b", "B"));
        var (pictureA, pictureB) = (Png(PluginImage.MaxBytes, 1), Png(PluginImage.MaxBytes, 2));
        a.SetImage(pictureA);
        b.SetImage(pictureB);
        var transport = new RecordingTransport(time);
        var host = await Host(time, transport, [.. texts, a, b]);

        try
        {
            for (var cycle = 0; cycle < 200; cycle++)
            {
                if (cycle is 0 or 20 or 40)
                {
                    foreach (var text in texts)
                        text.Value = new string((char)('a' + cycle / 20), IpcLimits.TextChars);
                }
                await host.RunUpdateCycleAsync([], TestContext.Current.CancellationToken);
                time.Advance(Cycle);
            }
        }
        finally
        {
            await host.ShutdownAsync();
        }

        var messages = transport.Messages;
        foreach (var first in messages)
        {
            var window = messages.Where(m => m.At >= first.At && m.At - first.At <= Stall).ToList();
            Assert.InRange(window.Sum(m => m.Bytes), 0, PluginLimits.Default.PendingBytes);
            Assert.InRange(window.Count, 0, PluginLimits.Default.PendingMessages);
        }
        // Nothing is lost on the way: both pictures, and each text's newest value.
        Assert.Equal(pictureA, transport.Pictures["a"]);
        Assert.Equal(pictureB, transport.Pictures["b"]);
        Assert.All(texts, t => Assert.Equal(new string('c', IpcLimits.TextChars), transport.Texts[t.Id]));
    }

    // Readings past the host's rate wait, and those that waited go first: a plugin whose first entries change faster than
    // the rate, more than a message of them each time, can't keep its later ones from the engine.
    [Fact]
    public async Task Readings_held_back_by_the_rate_go_before_newer_changes()
    {
        var time = new ManualTime(Start);
        // 2,400 texts of 4,096 characters: the first 2,100 (over 8 MiB, a message's most) change every half second, the
        // last 300 once, at the start.
        var texts = Enumerable.Range(0, 2400).Select(i => new PluginText($"t{i}", $"T{i}", "")).ToArray();
        var late = new string('z', IpcLimits.TextChars);
        var transport = new RecordingTransport(time);
        var host = await Host(time, transport, texts);

        try
        {
            for (var cycle = 0; cycle < 100; cycle++)
            {
                if (cycle % 5 == 0)
                {
                    var value = new string((char)('a' + cycle / 5 % 20), IpcLimits.TextChars);
                    for (var i = 0; i < 2100; i++)
                        texts[i].Value = value;
                }
                if (cycle == 0)
                {
                    for (var i = 2100; i < texts.Length; i++)
                        texts[i].Value = late;
                }
                await host.RunUpdateCycleAsync([], TestContext.Current.CancellationToken);
                time.Advance(Cycle);
            }
        }
        finally
        {
            await host.ShutdownAsync();
        }

        Assert.All(texts.Skip(2100), t => Assert.Equal(late, transport.Texts.GetValueOrDefault(t.Id)));
    }

    // A picture B set again every cycle, as a new array, waits for its hash at most once a second; meanwhile 32 younger,
    // smaller pictures (256 KiB, a new one each second) must not take the wire it is waiting for. Alone, it would go once
    // the rates had refilled for it, in about 6 s; it is looked for over a minute.
    [Fact]
    public void A_changed_picture_waiting_for_its_hash_keeps_its_place_before_younger_smaller_pictures()
    {
        var time = new ManualTime(Start);
        var large = new PluginImage("large", "Large");
        var small = Enumerable.Range(0, 32).Select(i => new PluginImage($"s{i}", $"S{i}")).ToArray();
        var wrapper = Wrap("stub", [large, .. small]);
        var manager = new SensorSnapshotManager(time: time);
        large.SetImage(Png(PluginImage.MaxBytes, 1));
        Assert.Equal(["large"], PluginImageTransportTests.Deliver(manager, wrapper).Select(d => d.EntryId).ToArray());

        var b = Png(PluginImage.MaxBytes, 2);
        var delivered = TimeSpan.Zero;
        for (var cycle = 1; cycle <= 600 && delivered == TimeSpan.Zero; cycle++)
        {
            time.Advance(Cycle);
            large.SetImage(b);
            // A new small picture each second: each entry has one waiting all the time.
            if (cycle % 10 == 1)
            {
                var picture = Png(256 * 1024, (byte)(3 + cycle / 10));
                foreach (var image in small)
                    image.SetImage(picture);
            }
            if (PluginImageTransportTests.Deliver(manager, wrapper).Any(d => d.EntryId == "large"))
                delivered = Cycle * cycle;
        }
        Assert.InRange(delivered, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
    }

    // An update call of a host the engine has since replaced, paused once it was found current: the replacement loads its
    // catalogue and sends a newer picture; the old call, resumed, must not put its older picture back.
    [Fact]
    public async Task An_update_of_a_replaced_host_never_lands_in_its_successors_catalogue()
    {
        var time = new ManualTime(Start);
        var source = new PluginSensorSource(time: time);
        var key = SensorKey.Plugin("/fake/c/cover");
        var launcher = new FakeLauncher(() => new FakeHost { Entries = () => [new EntryDto { Id = "cover", Name = "Cover", Type = "image" }] });
        var package = new PluginPackage("InfoPanel.Fake", _root, Path.Combine(_root, "InfoPanel.Fake.dll"), Bundled: true);
        var settings = new PluginHostSettings { SettingsDirectory = Path.Combine(_root, "settings"), LogsDirectory = Path.Combine(_root, "logs") };
        await using var runner = new PluginHostRunner(package, settings, launcher, source, time, NullLogger.Instance);
        _ = runner.StartAsync();
        await Eventually.Until(() => runner.State == PluginHostState.Running, "the first host runs");

        var old = runner.Generation;
        using var paused = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var handedOff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.SourceUpdate = (generation, handOff) =>
        {
            if (generation != old)
            {
                handOff();
                return;
            }
            paused.Set();
            resume.Wait(Eventually.Limit);
            handOff();
            handedOff.TrySetResult();
        };
        var older = Png(1000, 1);
        await launcher.Started[0].Host.NotifyAsync(nameof(IPluginClientCallback.OnSensorUpdate), Picture(older));
        await Eventually.Until(() => paused.IsSet, "the old host's update is found current and paused");

        // Reloaded: the old host stopped, a new one started, its catalogue loaded and its newer picture taken.
        await runner.StopAsync(TimeSpan.FromSeconds(5));
        _ = runner.StartAsync();
        await Eventually.Until(() => runner.State == PluginHostState.Running && launcher.Started.Count == 2, "the second host runs");
        var newer = Png(1000, 2);
        await launcher.Started[1].Host.NotifyAsync(nameof(IPluginClientCallback.OnSensorUpdate), Picture(newer));
        await Eventually.Until(() => source.ReadState(key).Reading.ValueText == ReferenceOf(newer), "the new host's picture is taken");

        resume.Set();
        await handedOff.Task.WaitAsync(Eventually.Limit, TestContext.Current.CancellationToken);
        Assert.Equal(ReferenceOf(newer), source.ReadState(key).Reading.ValueText);
        Assert.True(source.TryGetImage(key, ReferenceOf(newer), out _));
        Assert.False(source.TryGetImage(key, ReferenceOf(older), out _));
    }

    private static List<SensorUpdateBatchDto> Picture(byte[] bytes) =>
        [new SensorUpdateBatchDto { PluginId = "fake", Updates = [new EntryUpdateDto { ContainerId = "c", EntryId = "cover", Type = "image", ImageValue = Convert.ToBase64String(bytes) }] }];

    private static async Task<HostService> Host(ManualTime time, RecordingTransport transport, IPluginData[] entries)
    {
        var wrapper = new PluginWrapper(new PluginDescriptor("plugin.dll", null), new StubPlugin("stub", entries));
        await wrapper.Initialize();
        var host = new HostService("plugin.dll", null, null, time);
        host.AddInitializedWrapper(wrapper);
        var jsonRpc = new JsonRpc(transport);
        jsonRpc.StartListening();
        host.SetJsonRpc(jsonRpc);
        return host;
    }

    private static PluginWrapper Wrap(string pluginId, IPluginData[] entries)
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

    /// <summary>
    /// A JsonRpc transport that records every notification the host sends: when (on the test's clock), its size as the
    /// host's formatter writes it, and the newest text and picture each entry was sent.
    /// </summary>
    private sealed class RecordingTransport(ManualTime time) : IJsonRpcMessageHandler
    {
        private readonly List<(DateTimeOffset At, long Bytes)> _messages = [];

        public bool CanRead => true;
        public bool CanWrite => true;
        public IJsonRpcMessageFormatter Formatter { get; } = new JsonMessageFormatter();
        public Dictionary<string, string?> Texts { get; } = [];
        public Dictionary<string, byte[]?> Pictures { get; } = [];

        public List<(DateTimeOffset At, long Bytes)> Messages
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
            if (message is not JsonRpcRequest request)
                return ValueTask.CompletedTask;
            lock (_messages)
            {
                // The arguments are read before serialising, which replaces them with JSON tokens.
                if (request.Method == nameof(IPluginClientCallback.OnSensorUpdate))
                {
                    foreach (var update in ((IEnumerable<SensorUpdateBatchDto>)request.ArgumentsList![0]!).SelectMany(b => b.Updates))
                    {
                        if (update.Type == "image")
                            Pictures[update.EntryId] = update.ImageValue == null ? null : Convert.FromBase64String(update.ImageValue);
                        else
                            Texts[update.EntryId] = update.TextValue;
                    }
                }
                var json = new ArrayBufferWriter<byte>();
                Formatter.Serialize(json, message);
                _messages.Add((time.GetUtcNow(), json.WrittenCount));
            }
            return ValueTask.CompletedTask;
        }
    }
}
