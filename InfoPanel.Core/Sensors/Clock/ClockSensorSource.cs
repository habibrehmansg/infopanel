using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Models;

namespace InfoPanel.Sensors.Clock;

/// <summary>
/// The engine's clock as sensors, <c>infopanel.clock</c> ("Clock"): the day, month and year, the hour on a 12-hour clock
/// (12 at midnight and noon) and a 24-hour one, the minute, the second, and PM (1 from noon, else 0), numbers with no
/// unit under <c>plugin:/infopanel.clock/{entry}</c>, for gauges bound to a sensor and 1.x themes (the 1.x Clock plugin's
/// entries, which a 1.x import binds to these: <see cref="RetiredPlugins"/>). Each read is computed from the time
/// provider's local time, so nothing runs between reads: the engine's local zone, daylight saving by the zone's rules,
/// Gregorian whatever the culture. Every entry is always live, its minimum, maximum and average the value now. A key
/// under its prefix that it doesn't list is unavailable; it answers no other key.
/// </summary>
public sealed class ClockSensorSource : ISensorSource, ISensorStatusSource
{
    public const string SourceId = "infopanel.clock";

    /// <summary>The source's name, and its sensors' group.</summary>
    public const string Name = "Clock";

    private static readonly SensorKeySpace Space = SensorKeySpace.Plugin(SourceId);

    /// <summary>Its entries in the order it lists them (the 1.x plugin's): each one's id and name.</summary>
    public static IReadOnlyList<(string Id, string Name)> Entries { get; } =
    [
        ("day", "Day"),
        ("month", "Month"),
        ("year", "Year"),
        ("12-hour", "12 Hour"),
        ("24-hour", "24 Hour"),
        ("minute", "Minute"),
        ("second", "Second"),
        ("pm", "PM"),
    ];

    private readonly TimeProvider _time;
    private readonly IReadOnlyList<SensorInfo> _sensors;
    // Key → its index in Entries.
    private readonly Dictionary<SensorKey, int> _index = [];

    public ClockSensorSource(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        var sensors = new List<SensorInfo>(Entries.Count);
        for (var i = 0; i < Entries.Count; i++)
        {
            var key = KeyOf(Entries[i].Id);
            _index[key] = i;
            sensors.Add(new SensorInfo(key, SourceId, Name, Entries[i].Name, ""));
        }
        _sensors = sensors;
    }

    /// <summary>The key of the entry <paramref name="entryId"/>: <c>plugin:/infopanel.clock/{entryId}</c>.</summary>
    public static SensorKey KeyOf(string entryId) => Space.KeyOf(entryId);

    public string Id => SourceId;

    public bool Handles(SensorKey key) => Space.Handles(key);

    public IReadOnlyList<SensorInfo> GetSensors() => _sensors;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public bool TryRead(SensorKey key, out SensorReading reading)
    {
        var state = ReadState(key);
        reading = state.Reading;
        return state.HasReading;
    }

    public SensorStatus GetStatus(SensorKey key) => ReadState(key).Status;

    public SensorState ReadState(SensorKey key)
    {
        if (!_index.TryGetValue(key, out var index))
            return SensorState.Without(SensorStatus.Unavailable);
        var value = ValueOf(index, _time.GetLocalNow());
        return new SensorState(SensorStatus.Live, SensorKind.Number, new SensorReading(value, value, value, value, ""));
    }

    /// <summary>The entry at <paramref name="index"/> in <see cref="Entries"/> at the local time <paramref name="now"/>.</summary>
    private static double ValueOf(int index, DateTimeOffset now) => index switch
    {
        0 => now.Day,
        1 => now.Month,
        2 => now.Year,
        3 => now.Hour % 12 == 0 ? 12 : now.Hour % 12,
        4 => now.Hour,
        5 => now.Minute,
        6 => now.Second,
        _ => now.Hour >= 12 ? 1 : 0,
    };
}
