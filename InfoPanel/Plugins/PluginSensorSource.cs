using System.Data;
using System.Security.Cryptography;
using InfoPanel.Enums;
using InfoPanel.Models;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Sensors;
using InfoPanel.Spec;

namespace InfoPanel.Plugins;

/// <summary>Where a package's host is, as its sensors' statuses follow it.</summary>
public enum PluginHostState
{
    /// <summary>Not started, turned off, or the engine stopping: its sensors are unavailable.</summary>
    Stopped,

    /// <summary>Its host is starting for the first time.</summary>
    Starting,

    /// <summary>Its host is connected: its sensors are live (plugins push changes only, so an unchanged value is live).</summary>
    Running,

    /// <summary>Its host ended unexpectedly and is being restarted: its sensors keep their last values, stale.</summary>
    Restarting,

    /// <summary>Its host was given up on after the restarts the policy allows: its sensors are unavailable.</summary>
    Failed,
}

/// <summary>
/// The <c>plugins</c> sensor source: every entry the plugin hosts list, under 1.x's keys,
/// <c>plugin:/{plugin id}/{container id}/{entry id}</c> (<c>plugin:/{plugin id}/{entry id}</c> for an ephemeral
/// container), so 1.x profiles bound to plugins read them. Its catalogue changes as hosts load and go (the hub's sources
/// are fixed, so it is always registered). A key under a plugin id a package provided this run is this source's
/// (unavailable when there is no such entry); any other key is left to the hub. Statuses follow the package's host:
/// live while it runs, stale while it restarts, unavailable once it is off or failed. What a host sends is bounded
/// (<see cref="PluginLimits"/>). Its state (<see cref="ISensorSourceState"/>) is running while the engine hosts plugins,
/// off with <see cref="Disabled"/> when it doesn't (<c>--no-plugins</c>). A plugin's picture (<see cref="PluginImage"/>)
/// is an image sensor: its reading the picture's reference, 16 hex digits of its SHA-256, its bytes held for
/// <see cref="TryGetImage"/> once inspected as media; a picture is admitted, before anything is decoded, only for a listed
/// image entry of its host and within the rates (<see cref="PluginLimits"/>); each entry keeps its current and its
/// previous picture, a host at most <see cref="ImageBytesPerHost"/>, and they go with its readings. The <c>plugin</c> media
/// provider draws them by reference alone (<see cref="IKeptPictures"/>).
/// </summary>
public sealed class PluginSensorSource : ISensorSource, ISensorStatusSource, ISensorSourceState, ISensorImages, InfoPanel.Images.IKeptPictures
{
    public const string SourceId = "plugins";

    /// <summary>
    /// The most bytes one host's pictures come to (64 MiB), each image entry's current and previous picture counted: past
    /// it a new picture is refused, with one warning.
    /// </summary>
    public const long ImageBytesPerHost = 64 * 1024 * 1024;

    /// <summary>The engine hosts no plugins (<c>--no-plugins</c>): the source is off.</summary>
    public static readonly SourceReason Disabled = new("disabled", "Plugins are turned off for this engine");

    /// <summary>The prefix of the engine's own sources' ids, which a plugin id may not take.</summary>
    public const string ReservedPrefix = "infopanel.";

    private readonly Lock _lock = new();
    private readonly PluginLimits _limits;
    private readonly ILogger _logger;
    private readonly Dictionary<string, Package> _packages = new(StringComparer.OrdinalIgnoreCase);
    // Plugin id → the package (folder) that provides it.
    private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);
    private readonly Dictionary<SensorKey, Entry> _entries = [];
    private IReadOnlyList<SensorInfo> _list = [];
    private int _order;
    // Every picture an image entry keeps (its current or its previous), by reference, with how many keep it: the plugin
    // media provider draws a picture by its reference alone.
    private readonly Dictionary<string, (byte[] Bytes, int Count)> _kept = new(StringComparer.Ordinal);

    private readonly TimeProvider _time;

    /// <param name="enabled">Whether the engine hosts plugins (<see cref="PluginOptions.Enabled"/>): off when it doesn't.</param>
    /// <param name="time">The clock the picture rates are kept by; the system's by default.</param>
    public PluginSensorSource(PluginLimits? limits = null, ILogger<PluginSensorSource>? logger = null, bool enabled = true, TimeProvider? time = null)
    {
        State = enabled ? SourceState.Running : SourceState.OffBecause(Disabled);
        _limits = limits ?? PluginLimits.Default;
        _logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Tests only: run between a picture's checks (outside the lock) and its commit.</summary>
    internal Action? BeforePictureCommit { get; set; }

    public string Id => SourceId;

    /// <summary>Its image sensors' pictures are drawn by the <c>plugin</c> media provider.</summary>
    public string Provider => InfoPanel.Images.PluginImageProvider.Name;

    /// <summary>Running while the engine hosts plugins (however many it found), off with <see cref="Disabled"/> when it doesn't.</summary>
    public SourceState State { get; }

    private sealed class Package(string folder, int order)
    {
        public string Folder { get; } = folder;
        public int Order { get; } = order;
        public PluginHostState State { get; set; } = PluginHostState.Stopped;
        public List<SensorKey> Keys { get; } = [];
        public HashSet<string> PluginIds { get; } = new(StringComparer.Ordinal);
        public HashSet<SensorKey> Warned { get; } = [];
        // (plugin id, container id, entry id), as updates name an entry → its key.
        public Dictionary<(string, string, string), SensorKey> Routes { get; } = [];
        // The bytes its image entries' pictures come to, and the warnings given about them: each entry's, by rule, and
        // the host's for passing ImageBytesPerHost.
        public long ImageBytes { get; set; }
        public HashSet<(SensorKey, string)> ImageWarned { get; } = [];
        public bool ImageBytesWarned { get; set; }
        // Its host's generation for pictures: a picture admitted in one is committed only in the same. A stop, a failure
        // or a new catalogue starts another.
        public long Epoch { get; set; }
        // The rates its host's pictures are admitted within (wire bytes, pictures), made at its catalogue, and whether
        // a refusal for them has been warned of.
        public RateBucket? ImageWire { get; set; }
        public RateBucket? ImageCount { get; set; }
        public bool ImageRateWarned { get; set; }
    }

    private sealed class Entry(SensorInfo info, SensorKind kind, string folder, string pluginId)
    {
        public SensorInfo Info { get; } = info;
        public SensorKind Kind { get; } = kind;
        public string Folder { get; } = folder;
        public string PluginId { get; } = pluginId;
        public SensorReading Reading { get; set; }
        public bool HasReading { get; set; }
        // An image entry's picture now and the one before it (an Image holds its last picture while a new one loads).
        public Picture? Current { get; set; }
        public Picture? Previous { get; set; }
        // An image entry's own rate, and its turn: each picture admitted, or no picture, takes the next, and a picture is
        // committed only while its turn is the last (so one admitted earlier never lands after a later one).
        public RateBucket? ImageRate { get; set; }
        public long Turn { get; set; }
    }

    /// <summary>A picture admitted under the lock, to check outside it and commit if its host's generation and its entry's turn hold.</summary>
    private sealed record Admitted(Entry Entry, long Turn, string Text);

    /// <summary>A kept picture: its reference (16 hex digits of its SHA-256) and its bytes, as the host sent them.</summary>
    private sealed record Picture(string Reference, byte[] Bytes);

    /// <summary>A picture as it came, checked outside the lock: its reference and bytes, or the rule it breaks and the warning's words.</summary>
    private readonly record struct PictureCheck(string? Reference, byte[]? Bytes, string? Rule, string? Problem);

    /// <summary>A plugin a host offered that wasn't taken, and why.</summary>
    public sealed record Refusal(string PluginId, string Reason);

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public IReadOnlyList<SensorInfo> GetSensors()
    {
        lock (_lock)
            return _list;
    }

    public bool Handles(SensorKey key)
    {
        if (key.Type != SensorType.Plugin || PluginIdOf(key) is not { } id)
            return false;
        lock (_lock)
            return _owners.ContainsKey(id);
    }

    public bool TryRead(SensorKey key, out SensorReading reading)
    {
        var state = ReadState(key);
        reading = state.Reading;
        return state.HasReading;
    }

    public SensorStatus GetStatus(SensorKey key) => ReadState(key).Status;

    public SensorState ReadState(SensorKey key)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry) || !_packages.TryGetValue(entry.Folder, out var package))
                return SensorState.Without(SensorStatus.Unavailable);

            var status = package.State switch
            {
                PluginHostState.Running => SensorStatus.Live,
                PluginHostState.Restarting or PluginHostState.Starting => SensorStatus.Stale,
                _ => SensorStatus.Unavailable,
            };
            return status != SensorStatus.Unavailable && entry.HasReading
                ? new SensorState(status, entry.Kind, entry.Reading)
                : SensorState.Without(SensorStatus.Unavailable, entry.Kind);
        }
    }

    /// <summary>
    /// The package's host state, for its sensors' statuses. A stopped or failed host's pictures go with its readings: its
    /// image sensors have none until a host of it sends them again.
    /// </summary>
    public void SetState(string folder, PluginHostState state)
    {
        lock (_lock)
        {
            if (!_packages.TryGetValue(folder, out var package))
                _packages[folder] = package = new Package(folder, _order++);
            package.State = state;
            if (state is PluginHostState.Stopped or PluginHostState.Failed)
            {
                // A picture of its host still being checked is not committed: a new generation.
                package.Epoch++;
                DropPictures(package);
            }
        }
    }

    public PluginHostState StateOf(string folder)
    {
        lock (_lock)
            return _packages.TryGetValue(folder, out var package) ? package.State : PluginHostState.Stopped;
    }

    /// <summary>
    /// Replaces the package's catalogue with what its host loaded: each plugin's containers and entries with their first
    /// values. A plugin id that is empty, holds <c>/</c>, is longer than <see cref="PluginLimits.IdChars"/>, starts with
    /// <see cref="ReservedPrefix"/>, is a retired plugin's whose sensors the engine reads itself now
    /// (<see cref="RetiredPlugins"/>: an old copy of a bundled package, whose keys must not come back half live), or that
    /// another package (or this one, twice) already provides is refused and
    /// returned, and so is a plugin whose catalogue has a container or entry id longer than
    /// <see cref="PluginLimits.IdPartChars"/> or would make a key longer than <see cref="PluginLimits.KeyChars"/> (checked
    /// before any key is made, so a host can't make the engine hold more than it sent); entries past
    /// <see cref="PluginLimits.EntriesPerHost"/> are left out with one warning.
    /// </summary>
    public IReadOnlyList<Refusal> Load(string folder, IReadOnlyList<PluginMetadataDto> plugins, IReadOnlyDictionary<string, List<ContainerDto>> containers)
    {
        var refused = new List<Refusal>();
        lock (_lock)
        {
            if (!_packages.TryGetValue(folder, out var package))
                _packages[folder] = package = new Package(folder, _order++);
            Clear(package);

            var count = 0;
            var capped = false;
            foreach (var plugin in plugins)
            {
                var id = plugin.Id ?? "";
                if (IdProblem(id) is { } problem)
                {
                    refused.Add(new Refusal(id, problem));
                    continue;
                }
                if (_owners.TryGetValue(id, out var owner) && !string.Equals(owner, folder, StringComparison.OrdinalIgnoreCase)
                    && _packages.TryGetValue(owner, out var other) && other.State is PluginHostState.Stopped or PluginHostState.Failed)
                {
                    // A package whose host is stopped or failed keeps its catalogue (its sensors unavailable) until another
                    // package's host takes one of its ids (the manager has decided that it may): it gives up the catalogue.
                    Clear(other);
                    owner = null;
                }
                if (owner != null)
                {
                    refused.Add(new Refusal(id, string.Equals(owner, folder, StringComparison.OrdinalIgnoreCase)
                        ? "its id is used by another plugin in the same package"
                        : $"its id is already used by the plugin package {owner}"));
                    continue;
                }

                if (!containers.TryGetValue(id, out var list))
                    list = null;
                if (list != null && CatalogueProblem(id, list) is { } bound)
                {
                    refused.Add(new Refusal(id, bound));
                    continue;
                }

                _owners[id] = folder;
                package.PluginIds.Add(id);
                var pluginName = Cut(plugin.Name ?? id, _limits.NameChars);
                if (list == null)
                    continue;

                foreach (var container in list)
                {
                    if (container == null)
                        continue;
                    var group = Cut($"{pluginName} › {container.Name}", _limits.NameChars);
                    foreach (var dto in container.Entries ?? [])
                    {
                        if (dto == null || string.IsNullOrEmpty(dto.Id))
                            continue;
                        if (count == _limits.EntriesPerHost)
                        {
                            capped = true;
                            continue;
                        }
                        if (!SensorKey.TryParse(KeyPrefix + KeyId(id, container, dto.Id), out var key) || _entries.ContainsKey(key))
                            continue;

                        count++;
                        var kind = KindOf(dto.Type);
                        var unit = kind == SensorKind.Number ? Cut(dto.SensorValue?.Unit ?? "", _limits.NameChars) : "";
                        var entry = new Entry(new SensorInfo(key, SourceId, group, Cut(dto.Name ?? dto.Id, _limits.NameChars), unit), kind, folder, id);
                        if (ReadingOf(package, key, kind, dto.SensorValue, dto.TextValue, dto.TableValue) is { } reading)
                        {
                            entry.Reading = reading;
                            entry.HasReading = true;
                        }
                        _entries[key] = entry;
                        package.Keys.Add(key);
                        package.Routes[(id, container.Id ?? "", dto.Id)] = key;
                    }
                }
            }

            if (capped)
                _logger.LogWarning("plugin {Folder}: its plugins list more than {Max} entries; the rest are left out", folder, _limits.EntriesPerHost);
            Rebuild();
        }

        return refused;
    }

    /// <summary>
    /// Applies changed values a package's host sent. Entries it didn't list at load (or that were left out) are ignored.
    /// An image entry's picture (base64 text) is admitted under the lock before anything is decoded (<see cref="Admit"/>:
    /// a listed image entry of a host that isn't stopped or failed, its size from the text's length, the rates); it is
    /// then decoded, inspected and hashed outside the lock, and committed (<see cref="TakePicture"/>) only if its host's
    /// generation and its entry's turn still hold, so a stop, a failure or a new catalogue meanwhile drops it.
    /// </summary>
    public void Update(string folder, IReadOnlyList<SensorUpdateBatchDto> batches) => Update(folder, EpochOf(folder), batches);

    /// <summary>
    /// The package's generation now: a stop, a failure and each catalogue start another. A host's caller reads it while it
    /// knows that host is the package's current one, and hands it to <see cref="Update(string, long, IReadOnlyList{SensorUpdateBatchDto})"/>.
    /// </summary>
    public long EpochOf(string folder)
    {
        lock (_lock)
            return _packages.TryGetValue(folder, out var package) ? package.Epoch : -1;
    }

    /// <summary>
    /// <see cref="Update(string, IReadOnlyList{SensorUpdateBatchDto})"/> for the host that was current in the package's
    /// generation <paramref name="epoch"/> (<see cref="EpochOf"/>, read while it was): nothing of it is taken once the
    /// generation has moved on (the host stopped, failed or was replaced, and another catalogue may hold the same
    /// entries), neither at its admission nor at a picture's commit.
    /// </summary>
    public void Update(string folder, long epoch, IReadOnlyList<SensorUpdateBatchDto> batches)
    {
        List<Admitted>? admitted = null;
        lock (_lock)
        {
            if (!_packages.TryGetValue(folder, out var package) || package.Epoch != epoch)
                return;
            foreach (var batch in batches)
            {
                if (batch?.PluginId is not { } pluginId || !package.PluginIds.Contains(pluginId))
                    continue;
                foreach (var update in batch.Updates ?? [])
                {
                    if (update == null
                        || !package.Routes.TryGetValue((pluginId, update.ContainerId ?? "", update.EntryId ?? ""), out var key)
                        || !_entries.TryGetValue(key, out var entry))
                        continue;
                    if (entry.Kind == SensorKind.Image)
                    {
                        // Each picture or none takes the entry's next turn: an earlier picture of this message is superseded.
                        entry.Turn++;
                        if (update.ImageValue is not { } text)
                            LetGo(package, entry);
                        else if (Admit(package, entry, text))
                            (admitted ??= []).Add(new Admitted(entry, entry.Turn, text));
                        continue;
                    }
                    if (ReadingOf(package, entry.Info.Key, entry.Kind, update.SensorValue, update.TextValue, update.TableValue) is { } reading)
                    {
                        entry.Reading = reading;
                        entry.HasReading = true;
                    }
                }
            }
        }

        if (admitted == null)
            return;
        var checks = admitted.Select(a => CheckPicture(a.Text)).ToList();
        BeforePictureCommit?.Invoke();
        lock (_lock)
        {
            if (!_packages.TryGetValue(folder, out var package) || package.Epoch != epoch)
                return;
            for (var i = 0; i < admitted.Count; i++)
            {
                var (entry, turn, _) = admitted[i];
                if (entry.Turn == turn && _entries.TryGetValue(entry.Info.Key, out var now) && ReferenceEquals(now, entry))
                    TakePicture(package, entry, checks[i]);
            }
        }
    }

    /// <summary>
    /// Whether the picture <paramref name="text"/> for <paramref name="entry"/> is admitted, under the lock and before
    /// anything is decoded: its host not stopped or failed (else ignored), at most <see cref="PluginImage.MaxBytes"/> by
    /// the text's length (else refused, as no picture, with one warning for the entry), and within the entry's and the
    /// host's rates (<see cref="PluginLimits"/>; else refused, as no picture, with one warning for the host). Admitted, it
    /// counts against the rates.
    /// </summary>
    private bool Admit(Package package, Entry entry, string text)
    {
        if (package.State is PluginHostState.Stopped or PluginHostState.Failed)
            return false;
        if (PictureText.DecodedLength(text) > PluginImage.MaxBytes)
        {
            Refuse(package, entry, "size", $"its picture is over {PluginImage.MaxBytes / (1024 * 1024)} MiB; it is refused, as no picture");
            return false;
        }

        var wire = text.Length + IpcLimits.ImageEnvelopeBytes;
        entry.ImageRate ??= new RateBucket(_limits.ImageEntryBurst, 1, _time);
        package.ImageWire ??= new RateBucket(_limits.ImageWireBurstBytes, _limits.ImageWireBytesPerSecond, _time);
        package.ImageCount ??= new RateBucket(_limits.ImagesBurst, _limits.ImagesPerSecond, _time);
        if (!entry.ImageRate.CanTake(1) || !package.ImageWire.CanTake(wire) || !package.ImageCount.CanTake(1))
        {
            if (!package.ImageRateWarned)
            {
                package.ImageRateWarned = true;
                _logger.LogWarning("plugin {Folder}: its host sends pictures faster than the engine takes them; a picture past the rate is refused, as no picture",
                    package.Folder);
            }
            LetGo(package, entry);
            return false;
        }
        entry.ImageRate.Take(1);
        package.ImageWire.Take(wire);
        package.ImageCount.Take(1);
        return true;
    }

    /// <summary>A picture refused by a rule of its own: the entry has none, with one warning for each entry and rule.</summary>
    private void Refuse(Package package, Entry entry, string rule, string problem)
    {
        if (package.ImageWarned.Add((entry.Info.Key, rule)))
            _logger.LogWarning("plugin {Folder}: {Key}: {What}", package.Folder, entry.Info.Key.ToString(), problem);
        LetGo(package, entry);
    }

    /// <summary>The plugin ids the package's catalogue holds now.</summary>
    public IReadOnlySet<string> PluginIdsOf(string folder)
    {
        lock (_lock)
            return _packages.TryGetValue(folder, out var package) ? package.PluginIds.ToHashSet(StringComparer.Ordinal) : new HashSet<string>();
    }

    /// <summary>How many sensors the package lists now (of <paramref name="pluginId"/> only, when given).</summary>
    public int CountOf(string folder, string? pluginId = null)
    {
        lock (_lock)
        {
            if (!_packages.TryGetValue(folder, out var package))
                return 0;
            return pluginId == null ? package.Keys.Count : package.Keys.Count(k => _entries[k].PluginId == pluginId);
        }
    }

    /// <summary>Forgets a package's catalogue and plugin ids (it was uninstalled, or its host reloads them).</summary>
    public void Remove(string folder)
    {
        lock (_lock)
        {
            if (_packages.Remove(folder, out var package))
            {
                Clear(package);
                Rebuild();
            }
        }
    }

    private const string KeyPrefix = "plugin:";

    private static string KeyId(string pluginId, ContainerDto container, string entryId) =>
        container.IsEphemeralPath ? $"/{pluginId}/{entryId}" : $"/{pluginId}/{container.Id}/{entryId}";

    /// <summary>
    /// Why a plugin's catalogue is refused, or null: a container or entry id over <see cref="PluginLimits.IdPartChars"/>,
    /// or a key over <see cref="PluginLimits.KeyChars"/>. Lengths only: nothing is allocated, so a catalogue of long ids
    /// costs no more than the message that carried it.
    /// </summary>
    private string? CatalogueProblem(string pluginId, List<ContainerDto> list)
    {
        foreach (var container in list)
        {
            if (container == null)
                continue;
            var containerId = container.Id?.Length ?? 0;
            if (containerId > _limits.IdPartChars)
                return $"its catalogue has a container id longer than {_limits.IdPartChars} characters";
            foreach (var dto in container.Entries ?? [])
            {
                var entryId = dto?.Id?.Length ?? 0;
                if (entryId > _limits.IdPartChars)
                    return $"its catalogue has an entry id longer than {_limits.IdPartChars} characters";
                // plugin:/{plugin id}/{container id}/{entry id}, or without the container for an ephemeral one.
                var key = KeyPrefix.Length + 1 + pluginId.Length + (container.IsEphemeralPath ? 0 : 1 + containerId) + 1 + entryId;
                if (key > _limits.KeyChars)
                    return $"its catalogue would make a key longer than {_limits.KeyChars} characters";
            }
        }

        return null;
    }

    private void Clear(Package package)
    {
        foreach (var key in package.Keys)
        {
            if (_entries.Remove(key, out var entry))
                (entry.Current, entry.Previous) = (Unkeep(entry.Current), Unkeep(entry.Previous));
        }
        foreach (var id in package.PluginIds)
            _owners.Remove(id);
        package.Keys.Clear();
        package.PluginIds.Clear();
        package.Routes.Clear();
        // Its pictures went with its entries; a picture still being checked isn't committed, and its next host's rates
        // start afresh.
        package.ImageBytes = 0;
        package.Epoch++;
        (package.ImageWire, package.ImageCount, package.ImageRateWarned) = (null, null, false);
    }

    /// <summary>
    /// The bytes of the picture <paramref name="reference"/> names that the image sensor <paramref name="key"/> keeps now,
    /// its current or its previous picture, as its host sent them (no copy); false for any other key or reference. Not a
    /// use, and nothing is logged.
    /// </summary>
    public bool TryGetImage(SensorKey key, string reference, out ReadOnlyMemory<byte> bytes)
    {
        bytes = default;
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.Kind != SensorKind.Image)
                return false;
            var picture = entry.Current?.Reference == reference ? entry.Current
                : entry.Previous?.Reference == reference ? entry.Previous
                : null;
            if (picture == null)
                return false;
            bytes = picture.Bytes;
            return true;
        }
    }

    /// <summary>
    /// An admitted picture as the engine takes it: base64 of at most <see cref="PluginImage.MaxBytes"/> (decoded only
    /// then), and a PNG, JPEG, GIF or WebP within the media limits, inspected as an asset is (its header and structure,
    /// nothing decoded; anything else, an SVG or a video, isn't inspected at all), its reference 16 hex digits of its
    /// SHA-256; else the rule it breaks.
    /// </summary>
    private static PictureCheck CheckPicture(string text)
    {
        var bytes = PictureText.Decode(text, PluginImage.MaxBytes, out var tooLarge);
        if (tooLarge)
            return new(null, null, "size", $"its picture is over {PluginImage.MaxBytes / (1024 * 1024)} MiB; it is refused, as no picture");
        const string NotMedia = "its picture isn't a PNG, JPEG, GIF or WebP within the media limits; it is refused, as no picture";
        if (bytes == null)
            return new(null, null, "media", NotMedia);
        ReadOnlySpan<byte> b = bytes;
        var looks = b.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47]) || b.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8])
            || b.StartsWith("GIF8"u8) || (b.Length >= 12 && b.StartsWith("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8));
        if (!looks || MediaValidator.Check(b) is not { Errors.Count: 0, Format: MediaFormat.Png or MediaFormat.Jpeg or MediaFormat.Gif or MediaFormat.WebP })
            return new(null, null, "media", NotMedia);
        return new(Convert.ToHexStringLower(SHA256.HashData(b))[..16], bytes, null, null);
    }

    /// <summary>
    /// An image entry's admitted picture, checked. A picture is kept as the entry's current one, its current one becoming
    /// the previous and the one before let go (a picture that was the previous one comes back by swapping the two); its
    /// reading is the reference. One refused (over 8 MiB, not a picture, or one that would take the host past
    /// <see cref="ImageBytesPerHost"/>) leaves the entry without one, unavailable, with one warning for each entry and
    /// rule (the host's bound: one for the host).
    /// </summary>
    private void TakePicture(Package package, Entry entry, PictureCheck check)
    {
        if (check is not { Reference: { } reference, Bytes: { } bytes })
        {
            Refuse(package, entry, check.Rule!, check.Problem!);
            return;
        }

        if (entry.Previous is { } previous && previous.Reference == reference)
        {
            (entry.Current, entry.Previous) = (previous, entry.Current);
        }
        else if (entry.Current?.Reference != reference)
        {
            var after = package.ImageBytes - (entry.Previous?.Bytes.Length ?? 0) + bytes.Length;
            if (after > ImageBytesPerHost)
            {
                if (!package.ImageBytesWarned)
                {
                    package.ImageBytesWarned = true;
                    _logger.LogWarning("plugin {Folder}: its pictures would pass {Max} MiB; a new picture is refused, as no picture, while they would",
                        package.Folder, ImageBytesPerHost / (1024 * 1024));
                }
                LetGo(package, entry);
                return;
            }
            package.ImageBytes = after;
            Unkeep(entry.Previous);
            (entry.Previous, entry.Current) = (entry.Current, Keep(new Picture(reference, bytes)));
        }

        entry.Reading = new SensorReading(reference);
        entry.HasReading = true;
    }

    /// <summary>The entry has no picture now: its current one is kept as the previous (an Image may still hold it), the one before let go.</summary>
    private void LetGo(Package package, Entry entry)
    {
        if (entry.Current is { } current)
        {
            package.ImageBytes -= entry.Previous?.Bytes.Length ?? 0;
            Unkeep(entry.Previous);
            (entry.Previous, entry.Current) = (current, null);
        }
        entry.Reading = default;
        entry.HasReading = false;
    }

    /// <summary>Lets go of every picture the package's image entries keep: they read unavailable until their host sends pictures again.</summary>
    private void DropPictures(Package package)
    {
        foreach (var key in package.Keys)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.Kind == SensorKind.Image)
            {
                (entry.Current, entry.Previous) = (Unkeep(entry.Current), Unkeep(entry.Previous));
                entry.Reading = default;
                entry.HasReading = false;
            }
        }
        package.ImageBytes = 0;
    }

    /// <summary><paramref name="picture"/> is kept by one more entry (as its current or previous); under the lock.</summary>
    private Picture Keep(Picture picture)
    {
        _kept[picture.Reference] = _kept.TryGetValue(picture.Reference, out var kept) ? (kept.Bytes, kept.Count + 1) : (picture.Bytes, 1);
        return picture;
    }

    /// <summary><paramref name="picture"/> is kept by one fewer entry, and by none it is forgotten; under the lock. Null, to clear what held it.</summary>
    private Picture? Unkeep(Picture? picture)
    {
        if (picture != null && _kept.TryGetValue(picture.Reference, out var kept))
        {
            if (kept.Count <= 1)
                _kept.Remove(picture.Reference);
            else
                _kept[picture.Reference] = (kept.Bytes, kept.Count - 1);
        }
        return null;
    }

    /// <summary>Whether an image entry keeps a picture under <paramref name="reference"/> now (a plugin's picture is never an address).</summary>
    public bool Keeps(string reference, out string? address)
    {
        address = null;
        lock (_lock)
            return _kept.ContainsKey(reference);
    }

    /// <summary>The bytes of the picture an image entry keeps under <paramref name="reference"/>, as its host sent them, named by the reference itself.</summary>
    public bool TryGet(string reference, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out byte[]? bytes, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? id)
    {
        lock (_lock)
        {
            if (_kept.TryGetValue(reference, out var kept))
            {
                (bytes, id) = (kept.Bytes, reference);
                return true;
            }
        }
        (bytes, id) = (null, null);
        return false;
    }

    private void Rebuild() =>
        _list = _packages.Values.OrderBy(p => p.Order).SelectMany(p => p.Keys).Select(k => _entries[k].Info).ToList();

    /// <summary>Why a plugin id can't be taken, or null.</summary>
    public string? IdProblem(string id) =>
        id.Length == 0 ? "its id is empty"
        : id.Contains('/') ? "its id contains '/'"
        : id.Length > _limits.IdChars ? $"its id is longer than {_limits.IdChars} characters"
        : id.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase) ? $"its id starts with '{ReservedPrefix}', which the engine's own sources use"
        : RetiredPlugins.SourceOf(id) is { } source ? $"its sensors are built into the engine now (the {source} source)"
        : null;

    /// <summary>The plugin id a key names: the first segment of <c>/{plugin id}/…</c>.</summary>
    private static string? PluginIdOf(SensorKey key)
    {
        var id = key.Id;
        if (id.Length < 2 || id[0] != '/')
            return null;
        var end = id.IndexOf('/', 1);
        return end < 0 ? id[1..] : id[1..end];
    }

    private static SensorKind KindOf(string? type) => type switch
    {
        "text" => SensorKind.Text,
        "table" => SensorKind.Table,
        "image" => SensorKind.Image,
        _ => SensorKind.Number,
    };

    private SensorReading? ReadingOf(Package package, SensorKey key, SensorKind kind, SensorValueDto? sensor, string? text, TableValueDto? table)
    {
        switch (kind)
        {
            case SensorKind.Number when sensor != null:
                return new SensorReading(Finite(sensor.ValueMin), Finite(sensor.ValueMax), Finite(sensor.ValueAvg), Finite(sensor.Value),
                    Cut(sensor.Unit ?? "", _limits.NameChars));
            case SensorKind.Text when text != null:
                if (text.Length > _limits.TextChars)
                    WarnOnce(package, key, $"its text is longer than {_limits.TextChars} characters; the rest is cut");
                return new SensorReading(Cut(text, _limits.TextChars));
            case SensorKind.Table when table != null:
                try
                {
                    return TableReading(package, key, table);
                }
                catch (Exception ex) when (ex is System.Data.DataException or ArgumentException or InvalidCastException)
                {
                    // A table the engine can't build is a diagnostic, not a failed callback (which would lose the rest).
                    WarnOnce(package, key, $"its table can't be read: {Cut(ex.Message, _limits.NameChars)}");
                    return null;
                }
            default:
                return null;
        }
    }

    private SensorReading TableReading(Package package, SensorKey key, TableValueDto dto)
    {
        var columns = dto.Columns ?? [];
        var rows = dto.Rows ?? [];
        var cut = columns.Count > _limits.TableColumns || rows.Count > _limits.TableRows;

        var data = new DataTable();
        var columnCount = Math.Min(columns.Count, _limits.TableColumns);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var c = 0; c < columnCount; c++)
        {
            // DataTable column names are unique ignoring case and non-empty: a clash gets its index, then the next number
            // until it is unique (another column may already be called that).
            var name = Cut(columns[c] ?? "", _limits.NameChars);
            if (name.Length == 0 || !names.Add(name))
            {
                var stem = name;
                for (var n = c; !names.Add(name = $"{stem}#{n}"); n++)
                {
                }
            }
            data.Columns.Add(name, typeof(object));
        }

        foreach (var row in rows.Take(_limits.TableRows))
        {
            var values = new object[columnCount];
            for (var c = 0; c < columnCount; c++)
            {
                var cell = row != null && c < row.Count ? row[c] : null;
                if (cell?.Type == "sensor" && cell.SensorValue is { } value)
                {
                    var unit = cell.SensorUnit ?? value.Unit ?? "";
                    cut |= unit.Length > _limits.CellChars || (cell.SensorName?.Length ?? 0) > _limits.CellChars;
                    values[c] = new PluginSensor("", Cut(cell.SensorName ?? "", _limits.CellChars), (float)Finite(value.Value), Cut(unit, _limits.CellChars));
                }
                else
                {
                    var cellText = cell?.TextValue ?? "";
                    cut |= cellText.Length > _limits.CellChars;
                    values[c] = new PluginText("", "", Cut(cellText, _limits.CellChars));
                }
            }
            data.Rows.Add(values);
        }

        if (cut)
            WarnOnce(package, key, $"its table is over {_limits.TableRows} rows, {_limits.TableColumns} columns or {_limits.CellChars} characters a cell; the rest is cut");

        var format = Cut(dto.DefaultFormat ?? "", _limits.CellChars);
        return new SensorReading(data, format, new PluginTable("", "", data, format).ToString());
    }

    private void WarnOnce(Package package, SensorKey key, string what)
    {
        if (package.Warned.Add(key))
            _logger.LogWarning("plugin {Folder}: {Key}: {What}", package.Folder, key.ToString(), what);
    }

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;

    private static string Cut(string value, int chars) => value.Length <= chars ? value : value[..chars];
}
