using System;

namespace InfoPanel.Sensors.NowPlaying;

/// <summary>Whether the system's current media session plays: <see cref="Stopped"/> also when there is none.</summary>
public enum NowPlayingState : byte
{
    Stopped,
    Paused,
    Playing,
}

/// <summary>A media session's repeat mode.</summary>
public enum NowPlayingRepeat : byte
{
    Off,
    One,
    All,
}

/// <summary>
/// What the system's current media session says, as a platform's reader last read it. Null members are what the player
/// or the platform doesn't say; the source reads them as unavailable, never 0 or empty.
/// </summary>
public sealed record NowPlayingSnapshot
{
    public string? Title { get; init; }

    /// <summary>The artist, several joined with ", ".</summary>
    public string? Artist { get; init; }

    public string? Album { get; init; }

    /// <summary>The playing app's name for people ("Music"), else its id.</summary>
    public string? App { get; init; }

    public NowPlayingState State { get; init; }

    /// <summary>Seconds into the track at <see cref="PositionAt"/>.</summary>
    public double? Position { get; init; }

    /// <summary>When the system last gave <see cref="Position"/>: the position moves on from it while playing.</summary>
    public DateTimeOffset? PositionAt { get; init; }

    /// <summary>The playback rate (1 at normal speed); none is 1 while playing.</summary>
    public double? Rate { get; init; }

    /// <summary>The track's length in seconds.</summary>
    public double? Duration { get; init; }

    public int? TrackNumber { get; init; }

    /// <summary>The genre, several joined with ", ".</summary>
    public string? Genre { get; init; }

    public bool? Shuffle { get; init; }

    public NowPlayingRepeat? Repeat { get; init; }

    /// <summary>The cover art's bytes as the platform gives them, at most <see cref="NowPlayingSource.MaxArtBytes"/>; null without.</summary>
    public ReadOnlyMemory<byte>? Art { get; init; }

    /// <summary>The cover art's content type (<c>image/jpeg</c>), when the platform says it.</summary>
    public string? ArtType { get; init; }

    /// <summary>The cover art's address, for a platform that gives one in place of bytes.</summary>
    public string? ArtAddress { get; init; }
}

/// <summary>
/// The platform seam of <see cref="NowPlayingSource"/>: reads the system's current media session. Started when the
/// source comes into use and stopped when it goes out of use (<see cref="Start"/>, <see cref="Stop"/>, from the poll
/// thread); <see cref="TryRead"/> once a poll.
/// </summary>
public interface INowPlayingReader : IDisposable
{
    /// <summary>Starts reading (macOS: the media helper's process). Nothing when it reads already, or can't.</summary>
    void Start();

    /// <summary>Stops reading and lets go of what it holds (macOS: the helper's process ended). Nothing when stopped.</summary>
    void Stop();

    /// <summary>
    /// The latest snapshot: true with null when nothing plays, true with a snapshot when something does, false while it
    /// has nothing to say (not started, started but not yet answered, or failed).
    /// </summary>
    bool TryRead(out NowPlayingSnapshot? snapshot);

    /// <summary>Why it can't read (<see cref="NowPlayingSource.HelperMissing"/> and the rest), else null.</summary>
    SourceState? State { get; }
}
