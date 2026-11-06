using System.Collections.Generic;

namespace InfoPanel.Sensors.Volume;

/// <summary>
/// One audio output device as the operating system has it, read when asked: each property may throw (a device that
/// went while it was read).
/// </summary>
public interface IAudioDevice
{
    /// <summary>The system's id: the device's UID on macOS (<c>BuiltInSpeakerDevice</c>), its endpoint id on Windows.</summary>
    string Id { get; }

    /// <summary>Its name (the 1.x plugin's Name): <c>Speakers (Realtek(R) Audio)</c> on Windows, <c>MacBook Pro Speakers</c> on macOS.</summary>
    string Name { get; }

    /// <summary>The name of the device it belongs to (the 1.x plugin's Device Name): <c>Realtek(R) Audio</c> on Windows; on macOS, which has one name a device, <see cref="Name"/>.</summary>
    string DeviceName { get; }

    /// <summary>Its output volume, 0 to 1; null when it has no volume the system can set (macOS greys its slider).</summary>
    float? Volume { get; }

    /// <summary>Whether its output is muted; null when it has no mute control.</summary>
    bool? Mute { get; }
}

/// <summary>Lists the audio output devices the operating system has now, and its default one.</summary>
public interface IAudioLister
{
    /// <summary>The active output devices, in the system's order.</summary>
    IReadOnlyList<IAudioDevice> List();

    /// <summary>The default output device; null when there is none.</summary>
    IAudioDevice? Default();
}
