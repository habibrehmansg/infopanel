using InfoPanel.Sensors.HwInfo.Gadget;

namespace InfoPanel.Core.Tests.Sensors.HwInfo.Gadget;

/// <summary>One reading as HWiNFO writes it: <c>Sensor{n}</c>, <c>Label{n}</c>, <c>Value{n}</c> (none when null), <c>ValueRaw{n}</c>.</summary>
internal sealed record GadgetReading(string Sensor, string Label, string? Value, string Raw);

/// <summary>
/// The gadget key in both hives, as a test sets it. Every change stamps the key's last-write time from the clock's wall
/// time, as Windows does on every value write; <see cref="Key.Touch"/> rewrites a value with the same data.
/// </summary>
internal sealed class FakeGadgetRegistry(TimeProvider time) : IGadgetRegistry
{
    private readonly Dictionary<GadgetHive, Key> _keys = [];

    public int Reads { get; private set; }

    /// <summary>Thrown by the next reads while set.</summary>
    public Exception? Throw { get; set; }

    public sealed class Key(TimeProvider time)
    {
        public readonly List<GadgetValue> Values = [];

        public DateTime LastWriteUtc = time.GetUtcNow().UtcDateTime;

        private void Stamp() => LastWriteUtc = time.GetUtcNow().UtcDateTime;

        public void Set(string name, string text) => Set(name, GadgetValueKind.String, text);

        public void Set(string name, GadgetValueKind kind, string? text)
        {
            var value = new GadgetValue(name, kind, kind == GadgetValueKind.String ? text : null);
            var at = Values.FindIndex(v => v.Name == name);
            if (at >= 0)
                Values[at] = value;
            else
                Values.Add(value);
            Stamp();
        }

        public void Remove(string name)
        {
            Values.RemoveAll(v => v.Name == name);
            Stamp();
        }

        /// <summary>A value rewritten with the data it has: the last-write time moves, nothing else.</summary>
        public void Touch() => Stamp();

        public void Set(int index, GadgetReading reading)
        {
            Set("Sensor" + index, reading.Sensor);
            Set("Label" + index, reading.Label);
            if (reading.Value != null)
                Set("Value" + index, reading.Value);
            Set("ValueRaw" + index, reading.Raw);
        }

        /// <summary>
        /// HWiNFO's write of the readings ticked now, indexed 0, 1 … in order: every reading value it had goes first.
        /// They are added last-first, so the order values come in isn't the indices' order.
        /// </summary>
        public void Write(params GadgetReading[] readings)
        {
            Values.RemoveAll(v => v.Name.StartsWith("Sensor", StringComparison.Ordinal) || v.Name.StartsWith("Label", StringComparison.Ordinal)
                                  || v.Name.StartsWith("Value", StringComparison.Ordinal));
            for (var i = readings.Length - 1; i >= 0; i--)
                Set(i, readings[i]);
            Stamp();
        }
    }

    public Key Create(GadgetHive hive = GadgetHive.CurrentUser) => _keys[hive] = new Key(time);

    public Key this[GadgetHive hive] => _keys[hive];

    public void Delete(GadgetHive hive = GadgetHive.CurrentUser) => _keys.Remove(hive);

    public GadgetSnapshot? Read(GadgetHive hive)
    {
        Reads++;
        if (Throw != null)
            throw Throw;
        if (!_keys.TryGetValue(hive, out var key))
            return null;
        var values = key.Values.Count > HwInfoGadgetSampler.MaxValues ? [] : key.Values.ToArray();
        return new GadgetSnapshot(hive, key.LastWriteUtc, key.Values.Count, values);
    }

    /// <summary>
    /// The same keys as the registry's raw calls, for <see cref="GadgetRegistryReader"/>: each call is recorded in
    /// <see cref="Calls"/> (<c>info</c>, <c>enum {index}</c>, <c>query {name}</c>, <c>read {name}</c>) and, before it
    /// answers, passed to <see cref="OnCall"/>, which may change the key as HWiNFO would mid-read.
    /// </summary>
    public IGadgetKeyApi Api => new RawApi(this);

    public readonly List<string> Calls = [];

    public Action<string>? OnCall { get; set; }

    private void Call(string call)
    {
        Calls.Add(call);
        OnCall?.Invoke(call);
    }

    private sealed class RawApi(FakeGadgetRegistry registry) : IGadgetKeyApi
    {
        public IGadgetOpenKey? Open(GadgetHive hive)
        {
            registry.Reads++;
            if (registry.Throw != null)
                throw registry.Throw;
            return registry._keys.TryGetValue(hive, out var key) ? new OpenKey(registry, key) : null;
        }
    }

    private sealed class OpenKey(FakeGadgetRegistry registry, Key key) : IGadgetOpenKey
    {
        public (int ValueCount, DateTime LastWriteUtc) QueryInfo()
        {
            registry.Call("info");
            return (key.Values.Count, key.LastWriteUtc);
        }

        public GadgetEnumResult EnumName(int index, int maxLength, out string? name)
        {
            registry.Call("enum " + index);
            name = null;
            if (index >= key.Values.Count)
                return GadgetEnumResult.NoMore;
            if (key.Values[index].Name.Length > maxLength)
                return GadgetEnumResult.TooLong;
            name = key.Values[index].Name;
            return GadgetEnumResult.Found;
        }

        public (GadgetValueKind Kind, int Bytes)? QueryValue(string name)
        {
            registry.Call("query " + name);
            return key.Values.Find(v => v.Name == name) is { } value
                ? (value.Kind == GadgetValueKind.String ? GadgetValueKind.String : GadgetValueKind.Other, ((value.Text?.Length ?? 0) + 1) * sizeof(char))
                : null;
        }

        public string? ReadText(string name, int bytes)
        {
            registry.Call("read " + name);
            if (key.Values.Find(v => v.Name == name) is not { Kind: GadgetValueKind.String, Text: { } text })
                return null;
            return (text.Length + 1) * sizeof(char) > bytes ? null : text;
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// A test clock whose monotonic time moves only on <see cref="Advance"/>, and whose wall time moves with it and also on
/// its own (<see cref="ShiftWall"/>), as a user or the network setting the clock.
/// </summary>
internal sealed class GadgetClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks;
    private TimeSpan _shift;

    public override DateTimeOffset GetUtcNow() => start + TimeSpan.FromTicks(_ticks) + _shift;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan by) => _ticks += by.Ticks;

    public void ShiftWall(TimeSpan by) => _shift += by;
}
