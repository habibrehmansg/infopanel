using System.Text.Json.Serialization;

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

    /// <summary>The operating system refused the source access to the sensor (it needs root or extra privileges).</summary>
    [JsonStringEnumMemberName("permission-denied")]
    PermissionDenied,
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
}
