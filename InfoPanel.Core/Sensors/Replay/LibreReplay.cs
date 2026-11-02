using InfoPanel.Persistence;
using InfoPanel.SensorHelper;
using InfoPanel.Sensors.Libre;
using InfoPanel.Settings;
using Serilog;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Sensors.Replay;

/// <summary>
/// Records the sensor helper's answers the Libre source accepts (<see cref="LibreSampler.IObserver"/>) into a
/// recording's <c>libre/</c> folder, one gzip file a round (<see cref="SensorRecording.LibreFileName"/>) holding that
/// round's answers as the pipe carried them (each a 4-byte little-endian length, then its JSON): the <c>hello</c> and
/// <c>catalogue</c> of a start, the <c>configure</c> of a settings change, the <c>sample</c>. A round with no answer (the
/// helper restarting, Libre off) has no file. A round is written under a temporary name and given its own only once it
/// is whole, so an engine stopped mid-write leaves its complete rounds as they were; the recorder never takes, or
/// deletes, a file it didn't write: it claims <c>libre/</c> first (<see cref="SensorRecording.LibreClaimName"/>, created
/// only if absent), and a folder another recorder claimed is left alone (nothing recorded). It stops, and writes its
/// rounds into the manifest's <c>libre</c>, as
/// <see cref="HwInfoRecorder"/> does: after its duration from its first round, at its last round, at what a replay
/// loads, when writing fails, or when the source stops. With <c>scrub</c>, every hardware and sensor name goes through
/// <see cref="HwInfoScrubber.ScrubName"/> (LibreHardwareMonitor's identifiers stay: they are the keys); an answer that
/// can't be read again isn't kept. Called on the poll thread.
/// </summary>
public sealed class LibreRecorder : LibreSampler.IObserver
{
    private readonly string _root;
    private readonly string _folder;
    private readonly HwInfoScrubber? _scrubber;
    private readonly TimeSpan _interval;
    private readonly DateTimeOffset _started;
    private readonly TimeSpan? _duration;
    private readonly TimeProvider _time;
    private readonly SensorRecordingLimits _limits;
    private readonly ILogger _log = Log.ForContext<LibreRecorder>();
    private readonly MemoryStream _pending = new();
    private int _pendingFrames;
    private long? _firstRound;
    private int _round;
    private int _files;
    private long _bytes;
    private bool _finished;
    private readonly bool _claimed;

    /// <param name="folder">The recording (made when missing); its <c>libre/</c> must hold no frames yet.</param>
    /// <param name="duration">How long it records, from its first round; until <paramref name="maxRounds"/> when null.</param>
    public LibreRecorder(string folder, int maxRounds, bool scrub, TimeSpan interval, DateTimeOffset started, TimeSpan? duration = null,
        TimeProvider? time = null, SensorRecordingLimits? limits = null, HwInfoScrubber? scrubber = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRounds, 1);
        _root = folder;
        _folder = Path.Combine(folder, SensorRecording.LibreFolder);
        _limits = limits ?? SensorRecordingLimits.Default;
        MaxRounds = Math.Min(maxRounds, _limits.MaxRounds);
        _scrubber = scrub ? scrubber ?? new HwInfoScrubber(Environment.MachineName, Environment.UserName) : null;
        _interval = interval;
        _started = started;
        _duration = duration;
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(_folder);
        _claimed = Claim();
        if (!_claimed)
        {
            // Another recorder (another engine that passed the folder's check at the same time) has it.
            _finished = true;
            _log.Error("Recording sensors: {Folder} is another recording's; Libre's sensors aren't recorded", _folder);
            return;
        }

        SensorRecording.WriteLibreManifest(folder, interval, scrub, started);
    }

    /// <summary>Creates the claim, which fails when it is there already.</summary>
    private bool Claim()
    {
        try
        {
            using var _ = new FileStream(Path.Combine(_folder, SensorRecording.LibreClaimName), FileMode.CreateNew, FileAccess.Write);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public int MaxRounds { get; }

    /// <summary>The rounds recorded so far.</summary>
    public int Rounds => _round;

    public bool Finished => _finished;

    public void Answered(ReadOnlySpan<byte> json)
    {
        if (_finished)
            return;

        var bytes = json.ToArray();
        if (_scrubber != null)
        {
            try
            {
                bytes = HelperCodec.EncodeAnswer(Scrub(HelperCodec.DecodeAnswer(bytes), _scrubber));
            }
            catch (HelperProtocolException)
            {
                _log.Debug("Recording sensors: an answer of the sensor helper doesn't read again once scrubbed, so it isn't kept");
                return;
            }
        }

        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)bytes.Length);
        _pending.Write(length);
        _pending.Write(bytes);
        _pendingFrames++;
    }

    private static HelperAnswer Scrub(HelperAnswer answer, HwInfoScrubber scrubber) => answer switch
    {
        CatalogueAnswer catalogue => catalogue with
        {
            Hardware = [.. catalogue.Hardware.Select(h => h with { Name = scrubber.ScrubName(h.Name) })],
            Sensors = [.. catalogue.Sensors.Select(s => s with { Name = scrubber.ScrubName(s.Name) })],
        },
        _ => answer,
    };

    public void RoundEnded()
    {
        if (_finished)
            return;

        var now = _time.GetTimestamp();
        _firstRound ??= now;
        try
        {
            if (_pendingFrames > 0)
            {
                if (_files + 1 > _limits.MaxFrames || _bytes + _pending.Length > _limits.MaxBytes)
                {
                    Finish($"the next round would pass what a replay loads ({_files + 1} files, {_bytes + _pending.Length} bytes)");
                    return;
                }

                var path = Path.Combine(_folder, SensorRecording.LibreFileName(_round));
                var temp = path + SensorRecording.LibreTempSuffix;
                var created = false;
                try
                {
                    using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                    {
                        created = true;
                        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
                        gzip.Write(_pending.GetBuffer(), 0, (int)_pending.Length);
                    }

                    // Whole and closed: it takes its name now, never one that is there already.
                    File.Move(temp, path, overwrite: false);
                    created = false;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (created)
                        TryDelete(temp);
                    _log.Error(ex, "Recording sensors failed at Libre's round {Round}", _round);
                    Finish("writing failed");
                    return;
                }

                _files++;
                _bytes += _pending.Length;
            }

            _round++;
            if (_round >= MaxRounds)
                Finish($"{_round} rounds");
            else if (_duration is { } duration && _time.GetElapsedTime(_firstRound.Value, now) >= duration)
                Finish($"{duration.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s");
        }
        finally
        {
            _pending.SetLength(0);
            _pendingFrames = 0;
        }
    }

    public void Stopped()
    {
        if (_finished)
            return;
        _pending.SetLength(0);
        _pendingFrames = 0;
        Finish("the source stopped");
    }

    private void Finish(string why)
    {
        _finished = true;
        if (!_claimed)
            return;
        try
        {
            SensorRecording.WriteLibreManifest(_root, _interval, _scrubber != null, _started, _round, _time.GetUtcNow());
            _log.Information("Libre's sensor recording finished after {Why}: {Rounds} rounds in {Folder}", why, _round, _root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Libre's sensor recording: writing its manifest failed; {Rounds} rounds in {Folder}", _round, _root);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "Recording sensors: {File} couldn't be taken back", path);
        }
    }
}

/// <summary>
/// The sensor helper as a recording heard it (<see cref="LibreRecorder"/>), for a replay (<c>--replay-sensors</c>): a
/// launcher whose helpers answer from it. Each sample plays the next recorded round (looping): its sample under the
/// catalogue then in effect, with the <c>hello</c> then in effect (the recording's first before any), or, for a round
/// the engine read nothing in (no file, or no sample: the helper restarting or unanswered, Libre off, the manifest's
/// trailing rounds too), a sample of failed reads; a round whose sample came under a catalogue not yet fetched (the
/// engine skipped it) isn't played. A catalogue is the one in effect at the sample to come, and a sample under a
/// catalogue the engine hasn't fetched waits for that fetch, so a change of catalogue plays as on the PC. A recording
/// whose manifest has no rounds (the engine stopped without finishing it) keeps its complete rounds when its last file
/// was cut short (its frames end part way through one), and only then: a last file that is corrupt or can't be read
/// fails the load as any other does. A round with no sample after a change of catalogue (a restart's) is played by the
/// fetch the change brings, so the next round's values come on time. <c>configure</c> works as the helper's does on the recorded hardware:
/// storage off leaves out the hardware of type <c>Storage</c> and what is part of it, under a catalogue version of its
/// own. Samples carry on from helper to helper (Libre turned off and on again). Loaded whole at start, only regular
/// files, within <see cref="SensorRecordingLimits"/>, every answer read by the protocol's own codec.
/// </summary>
public sealed class LibreReplay : ILibreHelperLauncher
{
    /// <summary>A round to play: its sample, or null for a round the engine read nothing in (played as failed reads).</summary>
    private sealed record Step(HelloAnswer Hello, int Catalogue, SampleAnswer? Sample);

    /// <summary>Marks an <see cref="InvalidDataException"/> for a file whose frames end part way through one: cut short.</summary>
    private const string CutShort = "cutShort";

    private sealed record Effective(CatalogueAnswer Catalogue, int[]? Kept);

    private static readonly HelloAnswer NoHello = new(HelperProtocol.Version, "replay", false, "unknown");

    private readonly Step[] _steps;
    private readonly Effective[][] _catalogues; // [catalogue][storage on 0, off 1]
    private readonly Lock _lock = new();
    private int _next;

    private LibreReplay(Step[] steps, Effective[][] catalogues)
    {
        _steps = steps;
        _catalogues = catalogues;
    }

    /// <summary>The samples it plays, in a loop.</summary>
    public int Samples => _steps.Length;

    /// <summary>Loads <paramref name="folder"/>'s Libre rounds. Throws <see cref="InvalidDataException"/> for one that can't be replayed.</summary>
    public static LibreReplay Load(string folder, SensorRecordingLimits? limits = null)
    {
        limits ??= SensorRecordingLimits.Default;
        if (!SensorRecording.HoldsLibre(folder))
            throw new InvalidDataException($"'{folder}' holds no recorded Libre sensors (no {SensorRecording.LibreFolder}/*.frames.gz).");

        var declared = SensorRecording.LibreRoundsOf(folder);
        if (declared > limits.MaxRounds)
            throw new InvalidDataException($"The recording has {declared} Libre rounds, over {limits.MaxRounds}.");

        var files = new List<(int Round, string Path)>();
        var listed = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(Path.Combine(folder, SensorRecording.LibreFolder)))
        {
            if (++listed > limits.MaxFiles)
                throw new InvalidDataException($"The recording's {SensorRecording.LibreFolder} folder holds over {limits.MaxFiles} files.");
            var name = Path.GetFileName(path);
            if (!SensorRecording.TryParseLibreFileName(name, out var round))
                continue;
            if (round >= (declared ?? limits.MaxRounds))
                throw new InvalidDataException(declared is { } d ? $"'{name}' is past the recording's {d} Libre rounds." : $"'{name}' is past {limits.MaxRounds} rounds.");
            if (files.Count >= limits.MaxFrames)
                throw new InvalidDataException($"The recording holds over {limits.MaxFrames} Libre files.");
            files.Add((round, path));
        }

        var byRound = files.ToDictionary(f => f.Round, f => f.Path);
        var last = files.Max(f => f.Round);
        var rounds = declared ?? last + 1;
        var steps = new List<(HelloAnswer? Hello, int Catalogue, SampleAnswer? Sample)>();
        var catalogues = new List<CatalogueAnswer>();
        HelloAnswer? hello = null;
        HelloAnswer? firstHello = null;
        long total = 0;
        for (var round = 0; round < rounds; round++)
        {
            if (!byRound.TryGetValue(round, out var path))
            {
                steps.Add((hello, catalogues.Count - 1, null)); // nothing answered this round
                continue;
            }

            List<HelperAnswer> answers;
            try
            {
                var bytes = Decompress(path, limits.MaxBytes - total);
                total += bytes.Length;
                answers = [.. Answers(bytes, Path.GetFileName(path))];
            }
            catch (InvalidDataException ex) when (declared is null && round == last && ex.Data.Contains(CutShort))
            {
                // The engine stopped while it wrote its last round: the rounds before it are whole. Only a file whose
                // frames end part way through one (its gzip cut short reads so) is taken for that; a whole frame the
                // protocol refuses, a frame no answer has, a gzip that isn't one and a file that can't be read are not.
                break;
            }

            var sampled = false;
            foreach (var answer in answers)
            {
                switch (answer)
                {
                    case HelloAnswer h:
                        hello = h;
                        firstHello ??= h;
                        break;
                    case CatalogueAnswer c:
                        catalogues.Add(c);
                        break;
                    case SampleAnswer s:
                        sampled = true;
                        if (catalogues.Count > 0 && s.Version == catalogues[^1].Version)
                            steps.Add((hello, catalogues.Count - 1, s));
                        break;
                }
            }

            if (!sampled)
                steps.Add((hello, catalogues.Count - 1, null)); // a start's round, or a sample the helper failed
        }

        if (!steps.Any(s => s.Sample is not null))
            throw new InvalidDataException("The recording holds no Libre sample under a catalogue it holds.");

        // Failed rounds before the first catalogue play under it.
        return new LibreReplay(
            [.. steps.Select(s => new Step(s.Hello ?? firstHello ?? NoHello, Math.Max(0, s.Catalogue), s.Sample))],
            [.. catalogues.Select((c, i) => new[] { new Effective(c with { Version = VersionOf(i, storage: true) }, null), WithoutStorage(c, VersionOf(i, storage: false)) })]);
    }

    private static long VersionOf(int catalogue, bool storage) => catalogue * 2L + (storage ? 1 : 2);

    /// <summary>The catalogue without the storage hardware and what is part of it, and which sensors it keeps.</summary>
    private static Effective WithoutStorage(CatalogueAnswer catalogue, long version)
    {
        var parents = catalogue.Hardware.GroupBy(h => h.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        bool IsStorage(string id)
        {
            for (var steps = 0; steps <= parents.Count && parents.TryGetValue(id, out var h); steps++)
            {
                if (h.Type == "Storage")
                    return true;
                if (h.Parent is not { } parent)
                    return false;
                id = parent;
            }

            return false;
        }

        var kept = Enumerable.Range(0, catalogue.Sensors.Count).Where(i => !IsStorage(catalogue.Sensors[i].Hw)).ToArray();
        return new Effective(catalogue with
        {
            Version = version,
            Hardware = [.. catalogue.Hardware.Where(h => !IsStorage(h.Id))],
            Sensors = [.. kept.Select(i => catalogue.Sensors[i])],
        }, kept);
    }

    private static IEnumerable<HelperAnswer> Answers(byte[] bytes, string name)
    {
        var at = 0;
        while (at < bytes.Length)
        {
            if (bytes.Length - at < 4)
                throw new InvalidDataException($"'{name}' ends inside a frame's length.") { Data = { [CutShort] = true } };
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
            if (length == 0 || length > HelperProtocol.MaxFrameBytes)
                throw new InvalidDataException($"'{name}' holds a frame of {length} bytes, which no answer has.");
            if (length > bytes.Length - at - 4)
                throw new InvalidDataException($"'{name}' ends inside a frame of {length} bytes.") { Data = { [CutShort] = true } };
            HelperAnswer answer;
            try
            {
                answer = HelperCodec.DecodeAnswer(bytes.AsMemory(at + 4, (int)length));
            }
            catch (HelperProtocolException ex)
            {
                throw new InvalidDataException($"'{name}' holds an answer the protocol refuses: {ex.Message}", ex);
            }

            at += 4 + (int)length;
            yield return answer;
        }
    }

    private static byte[] Decompress(string path, long room)
    {
        try
        {
            using var file = RegularFile.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = gzip.Read(buffer)) > 0)
            {
                memory.Write(buffer, 0, read);
                if (memory.Length > room)
                    throw new InvalidDataException($"'{Path.GetFileName(path)}' or the recording is larger than the replay takes.");
            }

            return memory.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not InvalidDataException)
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' can't be replayed: {ex.Message}", ex);
        }
    }

    public Task<ILibreHelper> StartAsync(SensorHelperClient.IObserver? observer, CancellationToken cancel) =>
        Task.FromResult<ILibreHelper>(new Session(this));

    /// <summary>The step the next sample plays.</summary>
    private Step Upcoming()
    {
        lock (_lock)
            return _steps[_next];
    }

    /// <summary>Moves to the next step, after <paramref name="step"/> was played.</summary>
    private void Played(Step step)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_steps[_next], step))
                _next = (_next + 1) % _steps.Length;
        }
    }

    private sealed class Session(LibreReplay replay) : ILibreHelper
    {
        private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _storage = HelperProtocol.DefaultStorage;
        private long _fetched;

        // The step whose sample came under a catalogue not yet fetched, until the fetch it brings.
        private Step? _awaitingFetch;

        public Task<int?> Exited => _exited.Task;

        private Effective EffectiveOf(Step step) => replay._catalogues[step.Catalogue][_storage ? 0 : 1];

        public Task<HelloAnswer> HelloAsync(CancellationToken cancel) => Task.FromResult(replay.Upcoming().Hello);

        public Task<CatalogueAnswer> CatalogueAsync(CancellationToken cancel)
        {
            var step = replay.Upcoming();
            var catalogue = EffectiveOf(step).Catalogue;
            _fetched = catalogue.Version;
            // A step with no sample (a restart's round: its hello and catalogue alone) is played by this fetch, as the
            // engine skips the round's readings either way; one with values waits to be sampled under it.
            if (_awaitingFetch is { Sample: null } waiting && ReferenceEquals(waiting, step))
                replay.Played(step);
            _awaitingFetch = null;
            return Task.FromResult(catalogue);
        }

        public Task<HelperSample> SampleAsync(CancellationToken cancel)
        {
            var step = replay.Upcoming();
            var effective = EffectiveOf(step);
            var version = effective.Catalogue.Version;
            var answer = step.Sample is not { } s
                ? new SampleAnswer(version, Failed(effective), Failed(effective), Failed(effective))
                : effective.Kept is { } kept
                    ? new SampleAnswer(version, [.. kept.Select(i => s.Value[i])], [.. kept.Select(i => s.Min[i])], [.. kept.Select(i => s.Max[i])])
                    : s with { Version = version };
            // Under a catalogue the engine hasn't fetched, the step waits: the engine fetches it next, then plays it.
            var changed = version != _fetched;
            if (!changed)
                replay.Played(step);
            _awaitingFetch = changed ? step : null;
            return Task.FromResult(new HelperSample(answer, changed));
        }

        private static float?[] Failed(Effective effective) => new float?[effective.Catalogue.Sensors.Count];

        public Task<ConfigureAnswer> ConfigureAsync(bool storage, int storageInterval, CancellationToken cancel)
        {
            _storage = storage;
            return Task.FromResult(new ConfigureAnswer(storage, storageInterval));
        }

        public ValueTask DisposeAsync()
        {
            _exited.TrySetResult(null);
            return ValueTask.CompletedTask;
        }
    }
}
