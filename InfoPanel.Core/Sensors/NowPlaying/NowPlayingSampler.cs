using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.Sensors.NowPlaying;

/// <summary>
/// Reads <see cref="NowPlayingSource"/>'s entries from its reader once a poll. While the source is in use
/// (<see cref="NowPlayingSource.Demand"/>) the reader is started, and stopped once it isn't (every reading then fails:
/// stale, then unavailable). A reader with nothing to say yet fails the round too. Nothing playing reads
/// <c>Stopped</c> and every other entry unavailable. The position moves on from the system's last update by the
/// playback rate while playing (none is 1), clamped to 0…duration; progress is position ÷ duration × 100, unavailable
/// without a duration. What the player doesn't say (an empty text, a duration or track number of 0) is unavailable,
/// never 0 or empty. Texts are cut at <see cref="NowPlayingSource.MaxTextChars"/> characters.
/// <para>
/// Its state: the reader's when it has one (<see cref="INowPlayingReader.State"/>), else <c>starting</c> until a round
/// in use has read, then <c>running</c>. It logs starts, stops and states, never what plays.
/// </para>
/// </summary>
public sealed class NowPlayingSampler : ISensorSampler
{
    private enum Entry
    {
        Title,
        Artist,
        Album,
        App,
        State,
        Position,
        Duration,
        Progress,
        TrackNumber,
        Genre,
        Shuffle,
        Repeat,
    }

    private static readonly SensorDefinition[] Definitions = NowPlayingSource.Entries
        .Select(e => new SensorDefinition(e.Id, NowPlayingSource.Name, e.Name, e.Unit, e.Kind)).ToArray();

    private readonly INowPlayingReader _reader;
    private readonly NowPlayingSource.Demand _demand;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    private bool _started;
    private bool _read;
    private SourceState? _loggedState;

    // The last texts handed out, cut, so an unchanged text is handed back as the same string.
    private readonly string?[] _texts = new string?[Definitions.Length];

    internal NowPlayingSampler(INowPlayingReader reader, NowPlayingSource.Demand demand, TimeProvider time, ILogger? log = null)
    {
        _reader = reader;
        _demand = demand;
        _time = time;
        _log = (log ?? Log.Logger).ForContext<NowPlayingSampler>();
    }

    public IReadOnlyList<SensorDefinition> Discover() => Definitions;

    public SourceState? State
    {
        get
        {
            var state = _reader.State ?? (_read ? SourceState.Running : SourceState.Starting);
            if (state != _loggedState && state.Reason is { } reason)
                _log.Warning("Now Playing: {Code}: {Message}", reason.Code, reason.Message);
            _loggedState = state;
            return state;
        }
    }

    public void Sample(Span<SensorSample> samples, TimeSpan elapsed)
    {
        var inUse = _demand.InUse;
        if (inUse && !_started)
        {
            _started = true;
            _log.Information("Now Playing: in use; reading the media session");
            _reader.Start();
        }
        else if (!inUse && _started)
        {
            _started = false;
            _log.Information("Now Playing: not read for {Minutes} minutes; reading stopped", NowPlayingSource.UseWindow.TotalMinutes);
            _reader.Stop();
        }

        NowPlayingSnapshot? snapshot = null;
        if (!_started || !_reader.TryRead(out snapshot))
        {
            for (var i = 0; i < samples.Length; i++)
                samples[i].Outcome = SampleOutcome.Failed;
            return;
        }

        _read = true;
        for (var i = 0; i < samples.Length && i < Definitions.Length; i++)
            Fill(ref samples[i], (Entry)i, snapshot);
    }

    private void Fill(ref SensorSample sample, Entry entry, NowPlayingSnapshot? s)
    {
        if (entry == Entry.State)
        {
            sample.SetText(s?.State switch
            {
                NowPlayingState.Playing => "Playing",
                NowPlayingState.Paused => "Paused",
                _ => "Stopped",
            });
            return;
        }

        if (s is null)
        {
            sample.SetUnavailable();
            return;
        }

        switch (entry)
        {
            case Entry.Title:
                SetText(ref sample, entry, s.Title);
                break;
            case Entry.Artist:
                SetText(ref sample, entry, s.Artist);
                break;
            case Entry.Album:
                SetText(ref sample, entry, s.Album);
                break;
            case Entry.App:
                SetText(ref sample, entry, s.App);
                break;
            case Entry.Genre:
                SetText(ref sample, entry, s.Genre);
                break;
            case Entry.Shuffle:
                SetText(ref sample, entry, s.Shuffle switch { true => "On", false => "Off", null => null });
                break;
            case Entry.Repeat:
                SetText(ref sample, entry, s.Repeat switch
                {
                    NowPlayingRepeat.Off => "Off",
                    NowPlayingRepeat.One => "One",
                    NowPlayingRepeat.All => "All",
                    _ => null,
                });
                break;
            case Entry.Position:
                SetNumber(ref sample, Position(s));
                break;
            case Entry.Duration:
                SetNumber(ref sample, Duration(s));
                break;
            case Entry.Progress:
                SetNumber(ref sample, Position(s) is { } position && Duration(s) is { } duration ? position / duration * 100 : null);
                break;
            case Entry.TrackNumber:
                SetNumber(ref sample, s.TrackNumber is > 0 and var n ? n : null);
                break;
        }
    }

    private static double? Duration(NowPlayingSnapshot s) => s.Duration is { } d && double.IsFinite(d) && d > 0 ? d : null;

    /// <summary>The position now: moved on from the system's last update while playing, clamped to 0…duration.</summary>
    private double? Position(NowPlayingSnapshot s)
    {
        if (s.Position is not { } position || !double.IsFinite(position))
            return null;
        if (s.State == NowPlayingState.Playing && s.PositionAt is { } at)
        {
            var rate = s.Rate is { } r && double.IsFinite(r) && r != 0 ? r : 1;
            position += (_time.GetUtcNow() - at).TotalSeconds * rate;
        }

        position = Math.Max(0, position);
        return Duration(s) is { } duration ? Math.Min(position, duration) : position;
    }

    private static void SetNumber(ref SensorSample sample, double? value)
    {
        if (value is { } v)
            sample.Set(v);
        else
            sample.SetUnavailable();
    }

    private void SetText(ref SensorSample sample, Entry entry, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            sample.SetUnavailable();
            return;
        }

        var cut = Cut(text);
        ref var last = ref _texts[(int)entry];
        if (last != cut)
            last = cut;
        sample.SetText(last);
    }

    /// <summary>The text cut at <see cref="NowPlayingSource.MaxTextChars"/> characters, a surrogate pair kept whole.</summary>
    internal static string Cut(string text)
    {
        var max = NowPlayingSource.MaxTextChars;
        return text.Length <= max ? text : text[..(char.IsHighSurrogate(text[max - 1]) ? max - 1 : max)];
    }

    public void Dispose()
    {
        if (!_started)
            return;
        _started = false;
        _reader.Stop();
    }
}
