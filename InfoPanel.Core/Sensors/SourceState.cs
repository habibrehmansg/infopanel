using System;
using System.Text.Json.Serialization;

namespace InfoPanel.Sensors;

/// <summary>
/// What a sensor source is doing (<c>GET /api/sensors/sources</c> <c>state</c>). Clients treat a state they don't know
/// as <see cref="Unavailable"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SourceStateKind>))]
public enum SourceStateKind : byte
{
    /// <summary>Finding its sensors: started, its first discovery not done yet.</summary>
    [JsonStringEnumMemberName("starting")]
    Starting,

    /// <summary>Reading its sensors.</summary>
    [JsonStringEnumMemberName("running")]
    Running,

    /// <summary>Reading, but fewer sensors than it could (its <see cref="SourceState.Reason"/> says why).</summary>
    [JsonStringEnumMemberName("limited")]
    Limited,

    /// <summary>It can't read now (its <see cref="SourceState.Reason"/> says why).</summary>
    [JsonStringEnumMemberName("unavailable")]
    Unavailable,

    /// <summary>Turned off, or not set up (its <see cref="SourceState.Reason"/> says which).</summary>
    [JsonStringEnumMemberName("off")]
    Off,
}

/// <summary>Why a source is in its state: an open <see cref="Code"/> for programs, and the same in English for people.</summary>
public sealed record SourceReason
{
    public SourceReason(string code, string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentException.ThrowIfNullOrEmpty(message);
        Code = code;
        Message = message;
    }

    public string Code { get; }

    public string Message { get; }
}

/// <summary>A sensor source's state now, and why when there is something to say (<c>GET /api/sensors/sources</c>).</summary>
public sealed record SourceState(SourceStateKind Kind, SourceReason? Reason = null)
{
    public static readonly SourceState Starting = new(SourceStateKind.Starting);

    public static readonly SourceState Running = new(SourceStateKind.Running);

    /// <summary>Unavailable, saying nothing more (a source whose discovery failed).</summary>
    public static readonly SourceState Unavailable = new(SourceStateKind.Unavailable);

    public static SourceState UnavailableBecause(SourceReason reason) => new(SourceStateKind.Unavailable, reason);

    public static SourceState OffBecause(SourceReason reason) => new(SourceStateKind.Off, reason);
}

/// <summary>One source's state in <see cref="SensorHub.SourceStates"/>: its id, its state and how many sensors it lists.</summary>
public sealed record SourceStateEntry(string Id, SourceState State, int Sensors);

/// <summary>A source that reports its state. One that doesn't is <see cref="SourceState.Running"/> while the engine runs it.</summary>
public interface ISensorSourceState
{
    /// <summary>The state now; read from any thread.</summary>
    SourceState State { get; }
}
