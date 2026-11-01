using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace InfoPanel.Sensors.HwInfo.Gadget;

/// <summary>What the gadget key showed at the latest round that read it, for the source's state.</summary>
public enum GadgetObservation : byte
{
    /// <summary>No key in either hive: HWiNFO isn't reporting to gadgets, or runs as another user.</summary>
    KeyMissing,

    /// <summary>The key holds no complete reading: nothing is ticked "Report value in Gadget".</summary>
    NoReadings,

    /// <summary>Readings, but the key hasn't changed within its window: HWiNFO stopped writing it.</summary>
    NotUpdating,

    /// <summary>Readings, fresh.</summary>
    Reading,
}

/// <summary>
/// Reads HWiNFO's gadget values (the readings a user ticks "Report value in Gadget" for) from the registry for
/// <see cref="PolledSensorSource"/>: <c>Software\HWiNFO64\VSB</c> in the user's hive, else the machine's, opened afresh
/// each round, never written. A reading is an index <c>n</c> with <c>Sensor{n}</c>, <c>Label{n}</c> and
/// <c>ValueRaw{n}</c> as text (<c>Value{n}</c>, optional, gives only the unit); its key is its names
/// (<see cref="GadgetKeys"/>), so readings HWiNFO renumbers keep theirs. <c>ValueRaw</c> is read without the culture,
/// <c>.</c> or <c>,</c> as the decimal point. The catalogue is compared with the last one every round
/// (<see cref="ISensorSampler.CatalogueVersion"/>).
/// <para>
/// Fresh while the key's last-write time changes, tracked per hive: a round counts when it changed within
/// 3 × the last interval between two changes (10–60 s; 10 s until two changes are seen) of the engine's monotonic
/// clock; when a hive is first seen or chosen again (the user's key gone, the machine's read instead), its write's
/// wall-clock age must be within that, so values HWiNFO left behind hours ago are never live. Windows doesn't move
/// that time when a value is written again with the same data, so readings that all hold still would look stopped:
/// outside the window, a round with readings also counts while a HWiNFO process runs in this session, unless that is
/// session 0 (asked at most every <see cref="ProcessCheckEvery"/>). Otherwise every reading fails; after
/// <see cref="WithdrawAfter"/> without a
/// fresh round the catalogue empties. Bounded: <see cref="MaxValues"/> values (more fails the round), indices up to
/// <see cref="MaxIndex"/>, texts up to <see cref="MaxTextLength"/> characters (<see cref="GadgetRegistryReader"/>
/// reads no more), names and keys as <see cref="GadgetKeys"/> says; a sensor and label that two readings share is
/// skipped. Each skip, failure and change of <see cref="Observation"/> is logged once.
/// </para>
/// </summary>
public sealed class HwInfoGadgetSampler : ISensorSampler
{
    /// <summary>The key under each hive.</summary>
    public const string KeyPath = @"Software\HWiNFO64\VSB";

    /// <summary>A key with more values than this fails the round (its values aren't read).</summary>
    public const int MaxValues = 8192;

    /// <summary>At most this many readings are listed.</summary>
    public const int MaxReadings = 1024;

    /// <summary>The highest index a value name may carry.</summary>
    public const int MaxIndex = 1023;

    /// <summary>A reading with a text longer than this is skipped.</summary>
    public const int MaxTextLength = 1024;

    public static readonly TimeSpan MinFreshWindow = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaxFreshWindow = TimeSpan.FromSeconds(60);

    /// <summary>The catalogue empties once no round has been fresh for this long.</summary>
    public static readonly TimeSpan WithdrawAfter = TimeSpan.FromSeconds(30);

    /// <summary>Whether HWiNFO runs is asked at most this often, and only while the key isn't changing.</summary>
    public static readonly TimeSpan ProcessCheckEvery = HwInfoProcessCheck.Every;

    private static readonly int KeyPrefixLength = SensorKey.Plugin(HwInfoGadgetSource.KeyPrefix + "x").ToString().Length - 1;

    private sealed record Reading(string Id, string Sensor, string Label, string Unit, string Raw);

    private enum Field : byte
    {
        Sensor,
        Label,
        Value,
        ValueRaw,
    }

    private readonly IGadgetRegistry _registry;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    private SensorDefinition[] _definitions = [];
    private string[] _handedOut = [];
    // This round's ValueRaw by definition id; null when the round isn't fresh (every reading fails).
    private Dictionary<string, string>? _rawOf;
    private long _version;
    private bool _rounded;

    // Freshness, per hive: its key's last-write time as last seen, and when (monotonic) it last changed.
    private sealed class HiveState
    {
        public DateTime LastWrite;
        public long ChangedAt;
        public bool ChangeSeen;
        public TimeSpan? Interval;
    }

    private readonly HiveState?[] _hives = new HiveState?[2];
    private GadgetHive? _hive;
    private bool _everFresh;
    private long _lastFresh;
    private readonly HwInfoProcessCheck _process;
    private SourceState _state = SourceState.OffBecause(HwInfoGadgetSource.NotSetUp);

    // What has been logged, so each is logged once while it stands.
    private HashSet<string> _skipped = [];
    private bool _readFailed;
    private bool _tooMany;
    private bool _observed;
    private volatile GadgetObservation _observation;
    private volatile bool _hadReadings;

    /// <param name="registry">The registry: <see cref="WinGadgetRegistry"/> on Windows, a test's elsewhere.</param>
    /// <param name="log">Where it logs; Serilog's global logger but in tests.</param>
    /// <param name="hwinfoRunning">
    /// Whether HWiNFO runs (<see cref="HwInfoGadgetSource.IsHwInfoRunning"/> on Windows); without it, only the key's
    /// changes count.
    /// </param>
    public HwInfoGadgetSampler(IGadgetRegistry registry, TimeProvider? time = null, ILogger? log = null, Func<bool>? hwinfoRunning = null)
    {
        _registry = registry;
        _time = time ?? TimeProvider.System;
        _log = (log ?? Log.Logger).ForContext<HwInfoGadgetSampler>();
        _process = new HwInfoProcessCheck(hwinfoRunning, _time, _log, "HWiNFO Gadget");
    }

    /// <summary>What the key showed at the latest round that read it (<see cref="GadgetObservation.KeyMissing"/> before any).</summary>
    public GadgetObservation Observation => _observation;

    /// <summary>Whether any round of this run read fresh readings.</summary>
    public bool HadReadings => _hadReadings;

    public long CatalogueVersion => _version;

    /// <summary>
    /// The source's state after the latest round that read the key: running while it reads; with no readings yet this
    /// run and no key or none in it, <see cref="SourceStateKind.Off"/> · <see cref="HwInfoGadgetSource.NotSetUp"/>;
    /// otherwise (after readings, or a key that never updated) <see cref="HwInfoGadgetSource.Stopped"/> while HWiNFO
    /// runs, else <see cref="HwInfoGadgetSource.NotRunning"/> (HWiNFO's process asked at most every
    /// <see cref="ProcessCheckEvery"/>, only then). A round whose read fails, or that refuses a key with more than
    /// <see cref="MaxValues"/> values, is <see cref="SourceState.Unavailable"/> with no reason, from the start or after
    /// readings; it leaves <see cref="Observation"/> as it was, and the next round that reads the key decides again.
    /// </summary>
    public SourceState? State => _state;

    public IReadOnlyList<SensorDefinition> Discover()
    {
        if (!_rounded)
            Refresh();
        _handedOut = _definitions.Select(d => d.Id).ToArray();
        return _definitions;
    }

    public void Sample(Span<SensorSample> samples, TimeSpan elapsed)
    {
        Refresh();
        var rawOf = _rawOf;
        for (var i = 0; i < samples.Length; i++)
        {
            if (rawOf != null && i < _handedOut.Length && rawOf.TryGetValue(_handedOut[i], out var raw) && TryParseRaw(raw, out var value))
                samples[i].Set(value);
            else
                samples[i].Outcome = SampleOutcome.Failed;
        }
    }

    public void Dispose()
    {
    }

    /// <summary>One round: the key read once, its freshness and catalogue updated.</summary>
    private void Refresh()
    {
        _rounded = true;
        var now = _time.GetTimestamp();
        List<Reading>? readings = null;
        GadgetObservation? observation = null;
        var fresh = false;
        try
        {
            var snapshot = _registry.Read(GadgetHive.CurrentUser) ?? _registry.Read(GadgetHive.LocalMachine);
            if (_readFailed)
                _log.Information("HWiNFO Gadget: the registry reads again");
            _readFailed = false;

            if (snapshot == null)
            {
                // A key that appears again is a hive chosen again.
                _hive = null;
                observation = GadgetObservation.KeyMissing;
            }
            else
            {
                Track(snapshot, now);
                if (snapshot.ValueCount > MaxValues)
                {
                    if (!_tooMany)
                        _log.Warning("HWiNFO Gadget: the key holds {Count} values, more than {Max}: not read", snapshot.ValueCount, MaxValues);
                    _tooMany = true;
                }
                else
                {
                    _tooMany = false;
                    readings = Parse(snapshot);
                    fresh = IsFresh(now) || (readings.Count > 0 && IsHwInfoRunning(now));
                    observation = readings.Count == 0 ? GadgetObservation.NoReadings
                        : fresh ? GadgetObservation.Reading
                        : GadgetObservation.NotUpdating;
                }
            }
        }
        catch (Exception ex)
        {
            // The round fails: every reading fails, the catalogue stands until it is withdrawn.
            if (!_readFailed)
                _log.Warning(ex, "HWiNFO Gadget: reading the registry failed: {Message}", ex.Message);
            _readFailed = true;
        }

        if (fresh && readings != null)
        {
            _everFresh = true;
            _lastFresh = now;
            Publish(readings);
            var rawOf = new Dictionary<string, string>(readings.Count, StringComparer.Ordinal);
            foreach (var r in readings)
                rawOf[r.Id] = r.Raw;
            _rawOf = rawOf;
        }
        else
        {
            _rawOf = null;
            if (!_everFresh || _time.GetElapsedTime(_lastFresh, now) > WithdrawAfter)
                Publish([]);
        }

        // A round that failed, or a key refused for its size, observed nothing: the observation stands, the source is
        // unavailable until a round reads the key again.
        if (observation is { } observed)
        {
            Observe(observed);
            _state = StateOf(observed, now);
        }
        else
            _state = SourceState.Unavailable;
    }

    /// <summary>The plan's table: the observation, whether this run had readings, and (only when it matters) whether HWiNFO runs.</summary>
    private SourceState StateOf(GadgetObservation observation, long now) => observation switch
    {
        GadgetObservation.Reading => SourceState.Running,
        GadgetObservation.KeyMissing or GadgetObservation.NoReadings when !_hadReadings => SourceState.OffBecause(HwInfoGadgetSource.NotSetUp),
        _ => SourceState.UnavailableBecause(IsHwInfoRunning(now) ? HwInfoGadgetSource.Stopped : HwInfoGadgetSource.NotRunning),
    };

    /// <summary>
    /// Notes a change of the key's last-write time, per hive. A hive seen for the first time, or chosen again after the
    /// other with a write it hadn't, is aged by its own last-write time: choosing a hive is never a write.
    /// </summary>
    private void Track(GadgetSnapshot snapshot, long now)
    {
        var chosen = _hive != snapshot.Hive;
        _hive = snapshot.Hive;
        var state = _hives[(int)snapshot.Hive];
        if (state == null || (chosen && snapshot.LastWriteUtc != state.LastWrite))
        {
            // The write's wall-clock age, the only use of the wall clock (a key written in the future is new).
            var age = _time.GetUtcNow().UtcDateTime - snapshot.LastWriteUtc;
            age = age < TimeSpan.Zero ? TimeSpan.Zero : age > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : age;
            _hives[(int)snapshot.Hive] = new HiveState
            {
                LastWrite = snapshot.LastWriteUtc,
                ChangedAt = now - (long)(age.TotalSeconds * _time.TimestampFrequency),
            };
            return;
        }

        if (snapshot.LastWriteUtc == state.LastWrite)
            return;

        if (state.ChangeSeen)
            state.Interval = _time.GetElapsedTime(state.ChangedAt, now);
        state.ChangeSeen = true;
        state.ChangedAt = now;
        state.LastWrite = snapshot.LastWriteUtc;
    }

    /// <summary>The chosen hive's key changed within its window: 3 × the last interval between changes, 10–60 s.</summary>
    private bool IsFresh(long now)
    {
        if (_hive is not { } hive || _hives[(int)hive] is not { } state)
            return false;
        var window = state.Interval is { } interval
            ? TimeSpan.FromTicks(Math.Clamp(interval.Ticks * 3, MinFreshWindow.Ticks, MaxFreshWindow.Ticks))
            : MinFreshWindow;
        return _time.GetElapsedTime(state.ChangedAt, now) <= window;
    }

    /// <summary>Whether HWiNFO runs, asked at most every <see cref="ProcessCheckEvery"/>; a check that throws counts as not.</summary>
    private bool IsHwInfoRunning(long now) => _process.IsRunning(now);

    private void Publish(IReadOnlyList<Reading> readings)
    {
        var definitions = readings.Select(r => new SensorDefinition(r.Id, r.Sensor, r.Label, r.Unit)).ToArray();
        if (definitions.SequenceEqual(_definitions))
            return;
        _definitions = definitions;
        _version++;
    }

    private void Observe(GadgetObservation observation)
    {
        if (observation == GadgetObservation.Reading)
            _hadReadings = true;
        if (_observed && observation == _observation)
            return;

        _observed = true;
        _observation = observation;
        switch (observation)
        {
            case GadgetObservation.KeyMissing:
                _log.Information("HWiNFO Gadget: no gadget key in the registry (HWiNFO isn't reporting to gadgets, or runs as another user)");
                break;
            case GadgetObservation.NoReadings:
                _log.Information("HWiNFO Gadget: the gadget key holds no reading (none is ticked Report value in Gadget)");
                break;
            case GadgetObservation.NotUpdating:
                _log.Information("HWiNFO Gadget: the gadget key isn't changing (HWiNFO stopped writing it): its readings are stale");
                break;
            case GadgetObservation.Reading:
                _log.Information("HWiNFO Gadget: reading {Count} readings from the {Hive} hive", _definitions.Length, _hive);
                break;
        }
    }

    /// <summary>
    /// The readings in index order, each skip logged once while it stands. A sensor and label that more than one
    /// reading has isn't offered at all, with one warning naming them: no ordinal tells such readings apart once
    /// HWiNFO renumbers, so offering either could move a binding to the other. Every complete reading whose sensor and
    /// label make a valid key counts toward that, before its other fields are checked: a duplicate skipped for its
    /// value still withholds the other.
    /// </summary>
    private List<Reading> Parse(GadgetSnapshot snapshot)
    {
        var parts = new SortedDictionary<int, string?[]>();
        var tooLong = new HashSet<(int Index, Field Field)>();
        foreach (var value in snapshot.Values)
        {
            if (value.Kind == GadgetValueKind.Other || !TryParseName(value.Name, out var field, out var index))
                continue;
            if (!parts.TryGetValue(index, out var texts))
                parts[index] = texts = new string?[4];
            // A text too long to read stands as an empty one, so its reading is still complete, and then skipped.
            texts[(int)field] = value.Kind == GadgetValueKind.TooLong ? "" : value.Text ?? "";
            if (value.Kind == GadgetValueKind.TooLong)
                tooLong.Add((index, field));
        }

        bool IsTooLong(int index, Field field, string? text) => tooLong.Contains((index, field)) || text?.Length > MaxTextLength;

        var tooLongWhy = $"a text longer than {MaxTextLength} characters";
        // Each reading with a valid key, and why its value is skipped (null when it isn't).
        var keyed = new List<(int Index, string SensorText, string LabelText, Reading Reading, string? Skip)>(Math.Min(parts.Count, MaxReadings));
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (index, texts) in parts)
        {
            if (texts[(int)Field.Sensor] is not { } sensorText || texts[(int)Field.Label] is not { } labelText || texts[(int)Field.ValueRaw] is not { } raw)
                continue;
            if (keyed.Count == MaxReadings)
            {
                Skip(skipped, index, sensorText, labelText, $"more than {MaxReadings} readings");
                break;
            }

            if (IsTooLong(index, Field.Sensor, sensorText) || IsTooLong(index, Field.Label, labelText))
            {
                Skip(skipped, index, sensorText, labelText, tooLongWhy);
                continue;
            }

            var sensor = sensorText.Trim();
            var label = labelText.Trim();
            if ((GadgetKeys.ProblemOf(sensor) ?? GadgetKeys.ProblemOf(label)) is { } problem)
            {
                Skip(skipped, index, sensorText, labelText, problem);
                continue;
            }

            var id = GadgetKeys.IdOf(sensor, label);
            if (KeyPrefixLength + id.Length > GadgetKeys.MaxKeyLength)
            {
                Skip(skipped, index, sensorText, labelText, $"a key longer than {GadgetKeys.MaxKeyLength} characters");
                continue;
            }

            var valueText = texts[(int)Field.Value];
            var why = IsTooLong(index, Field.ValueRaw, raw) || IsTooLong(index, Field.Value, valueText) ? tooLongWhy : null;
            keyed.Add((index, sensorText, labelText, new Reading(id, sensor, label, why == null ? UnitOf(valueText) : "", raw), why));
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var k in keyed)
            counts[k.Reading.Id] = counts.GetValueOrDefault(k.Reading.Id) + 1;

        var readings = new List<Reading>(keyed.Count);
        var warned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in keyed)
        {
            if (counts[k.Reading.Id] > 1)
            {
                if (warned.Add(k.Reading.Id))
                    Skip(skipped, k.Index, k.Reading.Sensor, k.Reading.Label, $"{counts[k.Reading.Id]} readings have the same sensor and label: neither is offered until one is renamed in HWiNFO");
            }
            else if (k.Skip != null)
                Skip(skipped, k.Index, k.SensorText, k.LabelText, k.Skip);
            else
                readings.Add(k.Reading);
        }

        _skipped = skipped;
        return readings;
    }

    private void Skip(HashSet<string> skipped, int index, string sensor, string label, string why)
    {
        var what = why + "\n" + sensor + "\n" + label;
        skipped.Add(what);
        if (!_skipped.Contains(what))
            _log.Warning("HWiNFO Gadget: skipped reading {Index} ({Sensor} › {Label}): {Why}", index, Shorten(sensor), Shorten(label), why);
    }

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..80] + "…";

    /// <summary>Whether <paramref name="name"/> is one of HWiNFO's value names (<see cref="TryParseName"/>).</summary>
    internal static bool IsValueName(string name) => TryParseName(name, out _, out _);

    /// <summary><c>Sensor</c>, <c>Label</c>, <c>Value</c> or <c>ValueRaw</c> then an index 0–1023 written without leading zeros.</summary>
    private static bool TryParseName(string name, out Field field, out int index)
    {
        index = -1;
        int at;
        if (name.StartsWith("ValueRaw", StringComparison.Ordinal))
            (field, at) = (Field.ValueRaw, 8);
        else if (name.StartsWith("Value", StringComparison.Ordinal))
            (field, at) = (Field.Value, 5);
        else if (name.StartsWith("Sensor", StringComparison.Ordinal))
            (field, at) = (Field.Sensor, 6);
        else if (name.StartsWith("Label", StringComparison.Ordinal))
            (field, at) = (Field.Label, 5);
        else
        {
            field = default;
            return false;
        }

        var digits = name.AsSpan(at);
        if (digits.Length is 0 or > 4 || (digits.Length > 1 && digits[0] == '0'))
            return false;
        var n = 0;
        foreach (var c in digits)
        {
            if (!char.IsAsciiDigit(c))
                return false;
            n = n * 10 + (c - '0');
        }

        if (n > MaxIndex)
            return false;
        index = n;
        return true;
    }

    /// <summary>
    /// <c>ValueRaw</c>, trimmed, as <c>[+-]digits[(.|,)digits]</c>: one separator can only be the decimal point (HWiNFO's
    /// raw values carry no group separators), so either is read as one, whatever the culture. Anything else fails.
    /// </summary>
    internal static bool TryParseRaw(string raw, out double value)
    {
        value = 0;
        var s = raw.AsSpan().Trim();
        var i = 0;
        if (i < s.Length && s[i] is '+' or '-')
            i++;
        var start = i;
        while (i < s.Length && char.IsAsciiDigit(s[i]))
            i++;
        if (i == start)
            return false;
        if (i < s.Length && s[i] is '.' or ',')
        {
            var point = ++i;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
                i++;
            if (i == point)
                return false;
        }

        if (i != s.Length)
            return false;

        Span<char> text = stackalloc char[s.Length];
        s.CopyTo(text);
        text.Replace(',', '.');
        return double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    /// <summary>
    /// The unit: <c>Value</c> trimmed, without its leading number (a sign, digits, <c>.</c>, <c>,</c> and spaces), trimmed:
    /// <c>4,198 MHz</c> → <c>MHz</c>. A <c>Value</c> that doesn't start with a number (<c>Yes</c>) has no unit.
    /// </summary>
    internal static string UnitOf(string? value)
    {
        if (value == null)
            return "";
        var v = value.AsSpan().Trim();
        var i = 0;
        if (i < v.Length && v[i] is '+' or '-')
            i++;
        if (i >= v.Length || !char.IsAsciiDigit(v[i]))
            return "";
        while (i < v.Length && (char.IsAsciiDigit(v[i]) || v[i] is '.' or ',' or ' ' or ' ' or ' '))
            i++;
        return v[i..].Trim().ToString();
    }
}
