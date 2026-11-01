using InfoPanel.Enums;
using System;

namespace InfoPanel.Sensors;

/// <summary>
/// How a <see cref="PolledSensorSource"/>'s keys look: which keys it answers for and the key of each sensor a sampler
/// defines. A source handles every key in its space, so a key for a sensor this computer lacks reads
/// <see cref="SensorStatus.Unavailable"/> from it, never simulated.
/// </summary>
public abstract class SensorKeySpace
{
    /// <summary>Whether the source answers for <paramref name="key"/>.</summary>
    public abstract bool Handles(SensorKey key);

    /// <summary>The key of the sensor a sampler defines as <paramref name="definitionId"/>.</summary>
    public abstract SensorKey KeyOf(string definitionId);

    /// <summary>The default: <c>plugin:/{sourceId}/{definition id}</c>, every key under the prefix.</summary>
    public static SensorKeySpace Plugin(string sourceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        return new PluginSpace("/" + sourceId + "/");
    }

    /// <summary>Every key of one type (<c>hwinfo:</c>, <c>libre:</c>); a definition's id is the key's id as it is.</summary>
    public static SensorKeySpace Of(SensorType type)
    {
        if (type == SensorType.Plugin)
            throw new ArgumentException("A plugin key space has a prefix: use Plugin(sourceId).", nameof(type));
        return new TypeSpace(type);
    }

    private sealed class PluginSpace(string prefix) : SensorKeySpace
    {
        public override bool Handles(SensorKey key) =>
            key.Type == SensorType.Plugin && key.Id.StartsWith(prefix, StringComparison.Ordinal);

        public override SensorKey KeyOf(string definitionId) => SensorKey.Plugin(prefix + definitionId);
    }

    private sealed class TypeSpace(SensorType type) : SensorKeySpace
    {
        public override bool Handles(SensorKey key) => key.Type == type;

        public override SensorKey KeyOf(string definitionId)
        {
            ArgumentException.ThrowIfNullOrEmpty(definitionId);
            return new SensorKey(type, definitionId);
        }
    }
}
