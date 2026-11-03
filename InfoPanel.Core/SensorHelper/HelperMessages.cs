using System.Collections.Generic;

namespace InfoPanel.SensorHelper;

/// <summary>A message the engine sends the helper.</summary>
public abstract record HelperRequest;

/// <summary><c>{"type":"hello","protocol":1}</c>: the first message, always; the helper refuses any other version.</summary>
public sealed record HelloRequest(int Protocol) : HelperRequest;

/// <summary><c>{"type":"catalogue"}</c>: every sensor the helper reads now.</summary>
public sealed record CatalogueRequest : HelperRequest;

/// <summary><c>{"type":"sample"}</c>: every sensor's value now.</summary>
public sealed record SampleRequest : HelperRequest;

/// <summary>
/// <c>{"type":"configure","storage":true,"storageInterval":30}</c>: reopen LibreHardwareMonitor's computer with storage
/// on or off and storage read every <see cref="StorageInterval"/> seconds (<see cref="HelperProtocol.MinStorageInterval"/>
/// to <see cref="HelperProtocol.MaxStorageInterval"/>).
/// </summary>
public sealed record ConfigureRequest(bool Storage, int StorageInterval) : HelperRequest;

/// <summary>A message the helper answers with.</summary>
public abstract record HelperAnswer;

/// <summary>
/// <c>{"type":"hello","protocol":1,"helper":"2.0.0","elevated":false,"driver":"missing"}</c>: the helper's protocol
/// and version, whether it runs as administrator, and the PawnIO driver's state as it read it at its start
/// (<see cref="PawnIoDriver"/>: <c>missing</c>, <c>outdated</c>, <c>installed</c>, <c>notLoaded</c>; <c>unknown</c> from a
/// helper that doesn't read it).
/// </summary>
public sealed record HelloAnswer(int Protocol, string Helper, bool Elevated, string Driver) : HelperAnswer;

/// <summary>
/// <c>{"type":"catalogue","version":1,"hardware":[…],"sensors":[…]}</c>: the sensors the helper reads, in 1.4.x's order
/// (by their hardware's type, then their index), under a version that changes whenever the list does and whenever the
/// computer is opened again (a successful <c>configure</c>), even with the same list.
/// </summary>
public sealed record CatalogueAnswer(long Version, IReadOnlyList<HelperHardware> Hardware, IReadOnlyList<HelperSensor> Sensors) : HelperAnswer;

/// <summary>
/// <c>{"id":"/lpc/nct6798d/0","name":"Nuvoton NCT6798D","type":"SuperIO","parent":"/motherboard"}</c>: a piece of
/// hardware, its identifier exactly as LibreHardwareMonitor gives it, and the hardware it is part of (null for none).
/// </summary>
public sealed record HelperHardware(string Id, string Name, string Type, string? Parent);

/// <summary>
/// <c>{"id":"/lpc/nct6798d/0/fan/1","name":"Fan #2","type":"Fan","index":1,"unit":"RPM","hw":"/lpc/nct6798d/0"}</c>:
/// a sensor, its identifier exactly as LibreHardwareMonitor gives it (the engine's <c>libre:{id}</c> key, as 1.4.x
/// stored it), its type's unit (<see cref="HelperUnits"/>) and its hardware's identifier.
/// </summary>
public sealed record HelperSensor(string Id, string Name, string Type, int Index, string Unit, string Hw);

/// <summary>
/// <c>{"type":"sample","version":1,"value":[…],"min":[…],"max":[…]}</c>: each sensor's value, minimum and maximum, in
/// the order of catalogue <see cref="Version"/>; null where LibreHardwareMonitor has none (a failed read, never 0).
/// A version other than the engine's catalogue's tells it to fetch the catalogue again.
/// </summary>
public sealed record SampleAnswer(long Version, IReadOnlyList<float?> Value, IReadOnlyList<float?> Min, IReadOnlyList<float?> Max) : HelperAnswer;

/// <summary><c>{"type":"configure","storage":true,"storageInterval":30}</c>: what the helper now reads with.</summary>
public sealed record ConfigureAnswer(bool Storage, int StorageInterval) : HelperAnswer;

/// <summary>
/// <c>{"type":"error","code":"protocol","message":"…"}</c>: a refusal. Two codes leave the pipe open: <c>treeFailed</c>
/// (LibreHardwareMonitor threw reading the sensors; the next request may work) and <c>tooLarge</c> (the answer would
/// pass the caps). Every other code is the helper's last message before it closes the pipe, and so is a
/// <c>treeFailed</c> for a computer that failed to open: the helper then ends, and the engine starts another.
/// </summary>
public sealed record ErrorAnswer(string Code, string Message) : HelperAnswer
{
    /// <summary>LibreHardwareMonitor failed this request: a read (the next may work), or the computer's open (the helper ends).</summary>
    public const string TreeFailed = "treeFailed";
}
