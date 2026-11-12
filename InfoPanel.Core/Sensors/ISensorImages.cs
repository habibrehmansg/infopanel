using System;

namespace InfoPanel.Sensors;

/// <summary>
/// A source of <see cref="SensorKind.Image"/> sensors that holds their pictures' bytes (spec/docs/api.md, "Sensor
/// pictures"): <see cref="SensorHub.TryGetImage"/> routes a key to the source that handles it.
/// </summary>
public interface ISensorImages
{
    /// <summary>
    /// The bytes of the picture <paramref name="reference"/> names, which the source holds now for its image sensor
    /// <paramref name="key"/>, exactly as it holds them; false for any other key, a reference it doesn't hold, or a picture
    /// it holds no bytes of. Not a use of the sensor (it starts no reading), and it copies, fetches and logs nothing.
    /// </summary>
    bool TryGetImage(SensorKey key, string reference, out ReadOnlyMemory<byte> bytes);
}
