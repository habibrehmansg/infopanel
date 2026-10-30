using System.Data;
using InfoPanel.Enums;
using InfoPanel.Models;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Sensors;

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
/// (<see cref="PluginLimits"/>).
/// </summary>
public sealed class PluginSensorSource : ISensorSource, ISensorStatusSource
{
    public const string SourceId = "plugins";

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

    public PluginSensorSource(PluginLimits? limits = null, ILogger<PluginSensorSource>? logger = null)
    {
        _limits = limits ?? PluginLimits.Default;
        _logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public string Id => SourceId;

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
    }

    private sealed class Entry(SensorInfo info, SensorKind kind, string folder, string pluginId)
    {
        public SensorInfo Info { get; } = info;
        public SensorKind Kind { get; } = kind;
        public string Folder { get; } = folder;
        public string PluginId { get; } = pluginId;
        public SensorReading Reading { get; set; }
        public bool HasReading { get; set; }
    }

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

    /// <summary>The package's host state, for its sensors' statuses.</summary>
    public void SetState(string folder, PluginHostState state)
    {
        lock (_lock)
        {
            if (!_packages.TryGetValue(folder, out var package))
                _packages[folder] = package = new Package(folder, _order++);
            package.State = state;
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
    /// <see cref="ReservedPrefix"/>, or that another package (or this one, twice) already provides is refused and
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
                if (_owners.TryGetValue(id, out var owner))
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

    /// <summary>Applies changed values a package's host sent. Entries it didn't list at load (or that were left out) are ignored.</summary>
    public void Update(string folder, IReadOnlyList<SensorUpdateBatchDto> batches)
    {
        lock (_lock)
        {
            if (!_packages.TryGetValue(folder, out var package))
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
                    if (ReadingOf(package, entry.Info.Key, entry.Kind, update.SensorValue, update.TextValue, update.TableValue) is { } reading)
                    {
                        entry.Reading = reading;
                        entry.HasReading = true;
                    }
                }
            }
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
            _entries.Remove(key);
        foreach (var id in package.PluginIds)
            _owners.Remove(id);
        package.Keys.Clear();
        package.PluginIds.Clear();
        package.Routes.Clear();
    }

    private void Rebuild() =>
        _list = _packages.Values.OrderBy(p => p.Order).SelectMany(p => p.Keys).Select(k => _entries[k].Info).ToList();

    /// <summary>Why a plugin id can't be taken, or null.</summary>
    public string? IdProblem(string id) =>
        id.Length == 0 ? "its id is empty"
        : id.Contains('/') ? "its id contains '/'"
        : id.Length > _limits.IdChars ? $"its id is longer than {_limits.IdChars} characters"
        : id.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase) ? $"its id starts with '{ReservedPrefix}', which the engine's own sources use"
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
