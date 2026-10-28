using System.Text.Json.Serialization;
using InfoPanel.Models;

namespace InfoPanel.Sensors;

/// <summary>
/// The state of one sensor key, as the API reports it (<c>/api/sensors</c> <c>status</c>,
/// <c>/api/sensors/values</c> <c>status</c>, <c>ItemNode.meta.sensor.status</c>). Clients treat a value they don't
/// know as "no live value". Only <see cref="Live"/>, <see cref="Stale"/> and <see cref="Simulated"/> have a reading.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SensorStatus>))]
public enum SensorStatus
{
    /// <summary>A source handles the key and has a fresh reading.</summary>
    [JsonStringEnumMemberName("live")]
    Live,

    /// <summary>No source handles the key; the hub simulates it (mock mode only).</summary>
    [JsonStringEnumMemberName("simulated")]
    Simulated,

    /// <summary>Unbound or malformed: the binding names no sensor at all.</summary>
    [JsonStringEnumMemberName("missing")]
    Missing,

    /// <summary>
    /// The source has a reading, but no fresh sample came within the source's stale age (a slow or failing read);
    /// the last value is still returned.
    /// </summary>
    [JsonStringEnumMemberName("stale")]
    Stale,

    /// <summary>
    /// A well-formed key with no reading: no source handles it (outside mock mode), the sensor isn't present on this
    /// machine, its reads keep failing, or its last good sample is too old.
    /// </summary>
    [JsonStringEnumMemberName("unavailable")]
    Unavailable,

    /// <summary>
    /// The operating system refused the source access to the sensor (it needs root or extra privileges). Spelt
    /// <c>denied</c>, as a scene input's status is (spec/docs/bindings.md).
    /// </summary>
    [JsonStringEnumMemberName("denied")]
    PermissionDenied,
}

/// <summary>The type of value a sensor reads, as a scene input's type names it (spec/docs/bindings.md).</summary>
public enum SensorKind
{
    Number,
    Text,
    Table,
}

/// <summary>
/// One key's status, the type of value it reads and its reading, taken from one snapshot of its source, so the
/// reading is the one the status describes. <see cref="Type"/> is known without a reading (a refused sensor has one);
/// <see cref="Reading"/> is set exactly when <see cref="HasReading"/>.
/// </summary>
public readonly record struct SensorState(SensorStatus Status, SensorKind Type, SensorReading Reading)
{
    public bool HasReading => Status is SensorStatus.Live or SensorStatus.Stale or SensorStatus.Simulated;

    /// <summary>A key with no reading.</summary>
    public static SensorState Without(SensorStatus status, SensorKind type = SensorKind.Number) => new(status, type, default);

    /// <summary>The type of value <paramref name="reading"/> holds: a table, else text, else a number.</summary>
    public static SensorKind TypeOf(in SensorReading reading) =>
        reading.ValueTable is not null ? SensorKind.Table
        : reading.ValueText is not null ? SensorKind.Text
        : SensorKind.Number;
}

/// <summary>
/// A reader that also knows each key's <see cref="SensorStatus"/> (<see cref="SensorHub"/>), so a scene input can tell
/// <c>stale</c> and <c>denied</c> apart (<see cref="InfoPanel.Scenes.SensorSceneInputs"/>).
/// </summary>
public interface ISensorStatusReader : ISensorReader
{
    /// <summary>The key's status now, as <c>/api/sensors</c> reports it.</summary>
    SensorStatus GetStatus(SensorKey key);

    /// <summary>
    /// The key's status, type and reading from one snapshot (<see cref="SensorState"/>): what <see cref="GetStatus"/>
    /// and <see cref="ISensorReader.TryRead"/> answer, without a concurrent update landing between the two.
    /// </summary>
    SensorState ReadState(SensorKey key);
}

/// <summary>
/// A source that knows more about a key than "reading or not" (<see cref="SensorStatus.Stale"/>,
/// <see cref="SensorStatus.PermissionDenied"/>). <see cref="SensorHub.GetStatus"/> asks it for every key it handles;
/// sources without it are <see cref="SensorStatus.Live"/> when they have a reading, else
/// <see cref="SensorStatus.Unavailable"/>.
/// </summary>
public interface ISensorStatusSource
{
    /// <summary>
    /// The status of a key this source handles: <see cref="SensorStatus.Live"/> or <see cref="SensorStatus.Stale"/>
    /// exactly when <see cref="ISensorReader.TryRead"/> returns a reading, else <see cref="SensorStatus.Unavailable"/>
    /// or <see cref="SensorStatus.PermissionDenied"/>.
    /// </summary>
    SensorStatus GetStatus(SensorKey key);

    /// <summary>
    /// The key's status, as <see cref="GetStatus"/>, with its reading when that is live or stale, and the type of value
    /// the sensor reads (from the source's own list, so known while access is refused), taken under the lock that
    /// publishes readings: a sample published concurrently is either wholly in it or not at all.
    /// </summary>
    SensorState ReadState(SensorKey key);
}
