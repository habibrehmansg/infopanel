using System;
using System.Collections.Generic;

namespace InfoPanel.SensorHelper;

/// <summary>
/// The sensors the helper serves (<see cref="HelperServer"/>): LibreHardwareMonitor's computer in the helper
/// (<c>InfoPanel.SensorHelper/LibreTree.cs</c>, Windows only), a fake one in the tests. It only reads: nothing here
/// sets a fan, a clock or any other hardware value. It is called from one thread at a time, and
/// <see cref="IDisposable.Dispose"/> only when no other call is inside it (<see cref="HelperServer.Retire"/>).
/// </summary>
public interface ISensorTree : IDisposable
{
    /// <summary>Whether the helper runs as administrator.</summary>
    bool Elevated { get; }

    /// <summary>The driver's state, as <c>hello</c> reports it (<c>unknown</c> until it is read).</summary>
    string Driver { get; }

    /// <summary>
    /// Opens the computer again with <see cref="HelperGroups.For"/>(<paramref name="storage"/>), storage read every
    /// <paramref name="storageInterval"/> seconds: 1.4.x restarted its monitor for either change. One that throws ends
    /// the helper (<see cref="HelperExit.TreeFailed"/>); it is never called again in that process.
    /// </summary>
    void Configure(bool storage, int storageInterval);

    /// <summary>Updates every piece of hardware and reads its sensors as they are now.</summary>
    TreeSnapshot Update();
}

/// <summary>What the tree read: its hardware and its sensors, in the tree's own order.</summary>
public sealed record TreeSnapshot(IReadOnlyList<TreeHardware> Hardware, IReadOnlyList<TreeSensor> Sensors);

/// <summary>
/// A piece of hardware: its identifier as LibreHardwareMonitor gives it, its name, its <c>HardwareType</c>'s name and
/// number (<paramref name="TypeOrder"/>, what 1.4.x ordered by), and the identifier of the hardware it is part of.
/// </summary>
public sealed record TreeHardware(string Identifier, string Name, string Type, int TypeOrder, string? Parent);

/// <summary>
/// A sensor: its identifier as LibreHardwareMonitor gives it, its name, its <c>SensorType</c>'s name, its index, its
/// hardware's identifier, and its value, minimum and maximum (null where LibreHardwareMonitor has none).
/// </summary>
public sealed record TreeSensor(string Identifier, string Name, string Type, int Index, string Hardware, float? Value, float? Min, float? Max);
