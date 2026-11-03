using InfoPanel.Persistence;
using InfoPanel.Sensors.HwInfo;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace InfoPanel.Sensors.Replay;

/// <summary>
/// What a replay loads (<see cref="HwInfoReplay"/>), and so what a recorder writes at most (<see cref="HwInfoRecorder"/>):
/// rounds, frame files, mappings in one round, and decompressed bytes.
/// </summary>
public sealed record SensorRecordingLimits(int MaxRounds, int MaxFrames, int MaxMappingsPerRound, long MaxBytes)
{
    public static SensorRecordingLimits Default { get; } =
        new(SensorRecording.MaxRounds, SensorRecording.MaxFrames, SensorRecording.MaxMappingsPerRound, SensorRecording.MaxBytes);

    /// <summary>The entries a replay lists in the <c>hwinfo/</c> folder before it gives up: the frames and a few more.</summary>
    public int MaxFiles => MaxFrames + 4;
}

/// <summary>
/// A sensor recording (<c>--record-sensors</c>, <c>--replay-sensors</c>): a folder with <c>recording.json</c> and, per
/// source, the bytes it read each round. HWiNFO's are in <c>hwinfo/</c>, one gzip file per mapping per round,
/// <c>{round:000000}.local.bin.gz</c> or <c>{round:000000}.remote-{i}.bin.gz</c>: the copy the source read (only the used
/// bytes, or the 4-byte signature of a mapping that said <c>DEAD</c>). A round with no file for a mapping had none. The
/// manifest's <c>rounds</c> is the number of rounds recorded, those with no file at the end included.
/// </summary>
public static partial class SensorRecording
{
    public const string Format = "infopanel.sensor-recording/1";
    public const string ManifestName = "recording.json";
    public const string HwInfoFolder = "hwinfo";
    public const string LibreFolder = "libre";

    /// <summary>At most this many rounds, frames, mappings a round and decompressed bytes are replayed (<see cref="SensorRecordingLimits"/>).</summary>
    public const int MaxRounds = 100_000;
    public const int MaxFrames = 300_000;
    public const int MaxMappingsPerRound = 17;
    public const long MaxBytes = 1L << 30;

    /// <summary>The manifest is read up to this size.</summary>
    public const int MaxManifestBytes = 64 * 1024;

    public static string FileName(int round, int remote) =>
        string.Create(CultureInfo.InvariantCulture, $"{round:000000}.{(remote < 0 ? "local" : "remote-" + remote)}.bin.gz");

    [GeneratedRegex(@"^(\d{6})\.(local|remote-(\d{1,4}))\.bin\.gz$")]
    private static partial Regex FilePattern();

    /// <summary>
    /// The file a recorder claims its source's folder with (<c>hwinfo/</c>, <c>libre/</c>), created only when absent
    /// (<see cref="TryClaim"/>): a second recorder on the same folder records nothing and deletes nothing. A replay
    /// skips it.
    /// </summary>
    public const string ClaimName = "recorder.claim";

    /// <summary>
    /// Claims <paramref name="sourceFolder"/> (made when missing) for one recorder: creates <see cref="ClaimName"/> in
    /// it. False, and never a throw, when it can't: <paramref name="problem"/> is then null when the claim is there
    /// already (another recorder's), else why the folder or the claim couldn't be made (permission or any other I/O error):
    /// the exception's type, never its message, which names the path (a log must not).
    /// </summary>
    public static bool TryClaim(string sourceFolder, out string? problem)
    {
        problem = null;
        var claim = Path.Combine(sourceFolder, ClaimName);
        try
        {
            Directory.CreateDirectory(sourceFolder);
            using var _ = new FileStream(claim, FileMode.CreateNew, FileAccess.Write);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problem = File.Exists(claim) ? null : e.GetType().Name;
            return false;
        }
    }

    /// <summary>A Libre round's file while it is written (its name, then this); it takes its own name once whole.</summary>
    public const string LibreTempSuffix = ".partial";

    /// <summary>Libre's file of one round: the helper's answers that round, as its frames (<c>{round:000000}.frames.gz</c>).</summary>
    public static string LibreFileName(int round) => string.Create(CultureInfo.InvariantCulture, $"{round:000000}.frames.gz");

    [GeneratedRegex(@"^(\d{6})\.frames\.gz$")]
    private static partial Regex LibreFilePattern();

    /// <summary>The round a Libre file name holds, or false for any other name.</summary>
    public static bool TryParseLibreFileName(string name, out int round)
    {
        var match = LibreFilePattern().Match(name);
        round = match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        return match.Success;
    }

    /// <summary>The round and remote index (−1 local) a file name holds, or false for any other name.</summary>
    public static bool TryParseFileName(string name, out int round, out int remote)
    {
        round = 0;
        remote = -1;
        var match = FilePattern().Match(name);
        if (!match.Success)
            return false;
        round = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        if (match.Groups[3].Success)
            remote = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Why <paramref name="folder"/> can't be replayed, or null when it can: it holds HWiNFO's frames, Libre's, or both.</summary>
    public static string? Check(string folder)
    {
        if (!Directory.Exists(folder))
            return $"'{folder}' does not exist.";
        if (!HoldsFrames(folder))
            return $"'{folder}' holds no recorded sensors (no {HwInfoFolder}/*.bin.gz or {LibreFolder}/*.frames.gz).";
        return null;
    }

    /// <summary>Whether <paramref name="folder"/> holds a recorded frame of any source (a recorder never writes into one that does).</summary>
    public static bool HoldsFrames(string folder) => HoldsHwInfo(folder) || HoldsLibre(folder);

    /// <summary>Whether <paramref name="folder"/> holds HWiNFO frames.</summary>
    public static bool HoldsHwInfo(string folder)
    {
        var hwinfo = Path.Combine(folder, HwInfoFolder);
        return Directory.Exists(hwinfo)
            && Directory.EnumerateFiles(hwinfo).Take(SensorRecordingLimits.Default.MaxFiles).Any(f => TryParseFileName(Path.GetFileName(f), out _, out _));
    }

    /// <summary>Whether <paramref name="folder"/> holds the sensor helper's frames (Libre's).</summary>
    public static bool HoldsLibre(string folder)
    {
        var libre = Path.Combine(folder, LibreFolder);
        return Directory.Exists(libre)
            && Directory.EnumerateFiles(libre).Take(SensorRecordingLimits.Default.MaxFiles).Any(f => TryParseLibreFileName(Path.GetFileName(f), out _));
    }

    // Both recorders write the one manifest, each its own members, from their own poll threads.
    private static readonly System.Threading.Lock ManifestLock = new();

    /// <summary>
    /// Writes HWiNFO's part of the manifest: the format, when, the source's interval, whether names were scrubbed, and,
    /// once the recording ended, its rounds and when it ended. Libre's part (<see cref="WriteLibreManifest"/>) is kept.
    /// </summary>
    public static void WriteManifest(string folder, TimeSpan interval, bool scrubbed, DateTimeOffset started, int? rounds = null, DateTimeOffset? ended = null) =>
        UpdateManifest(folder, scrubbed, started, HwInfoFolder, manifest =>
        {
            manifest["intervalMs"] = (int)interval.TotalMilliseconds;
            manifest.Remove("rounds");
            manifest.Remove("ended");
            if (rounds is { } count)
                manifest["rounds"] = count;
            if (ended is { } end)
                manifest["ended"] = end.ToString("O", CultureInfo.InvariantCulture);
        });

    /// <summary>
    /// Writes Libre's part of the manifest, <c>libre</c>: its interval and, once its recording ended, its rounds and
    /// when it ended. HWiNFO's part is kept; a manifest Libre writes first has the format, when and scrubbed too.
    /// </summary>
    public static void WriteLibreManifest(string folder, TimeSpan interval, bool scrubbed, DateTimeOffset started, int? rounds = null, DateTimeOffset? ended = null) =>
        UpdateManifest(folder, scrubbed, started, LibreFolder, manifest =>
        {
            var libre = new JsonObject { ["intervalMs"] = (int)interval.TotalMilliseconds };
            if (rounds is { } count)
                libre["rounds"] = count;
            if (ended is { } end)
                libre["ended"] = end.ToString("O", CultureInfo.InvariantCulture);
            manifest[LibreFolder] = libre;
        });

    private static void UpdateManifest(string folder, bool scrubbed, DateTimeOffset started, string source, Action<JsonObject> update)
    {
        lock (ManifestLock)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, ManifestName);
            JsonObject manifest;
            try
            {
                manifest = File.Exists(path) && JsonNode.Parse(RegularFile.ReadAtMost(path, MaxManifestBytes)) is JsonObject read ? read : [];
            }
            catch (JsonException)
            {
                manifest = [];
            }

            manifest["format"] = Format;
            if (!manifest.ContainsKey("started"))
                manifest["started"] = started.ToString("O", CultureInfo.InvariantCulture);
            manifest["scrubbed"] = scrubbed;
            var sources = (manifest["sources"] as JsonArray)?.Select(n => n is JsonValue v && v.TryGetValue<string>(out var text) ? text : null).OfType<string>().ToList() ?? [];
            if (!sources.Contains(source))
                sources.Add(source);
            manifest["sources"] = new JsonArray([.. sources.Order(StringComparer.Ordinal).Select(s => (JsonNode?)s)]);
            update(manifest);
            File.WriteAllText(path, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
    }

    /// <summary>
    /// The manifest's <c>rounds</c>, or null when it has none (a recording that didn't end, or a hand-made one).
    /// Throws <see cref="InvalidDataException"/> for a manifest that can't be read.
    /// </summary>
    public static int? RoundsOf(string folder)
    {
        var path = Path.Combine(folder, ManifestName);
        if (!File.Exists(path))
            return null;
        try
        {
            using var manifest = JsonDocument.Parse(RegularFile.ReadAtMost(path, MaxManifestBytes));
            if (manifest.RootElement.ValueKind != JsonValueKind.Object || !manifest.RootElement.TryGetProperty("rounds", out var rounds))
                return null;
            return rounds.TryGetInt32(out var count) && count >= 1 ? count : throw new InvalidDataException($"{ManifestName}'s rounds is not a count.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"{ManifestName} can't be read: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The manifest's <c>libre.rounds</c>, or null when it has none. Throws <see cref="InvalidDataException"/> for a
    /// manifest that can't be read.
    /// </summary>
    public static int? LibreRoundsOf(string folder)
    {
        var path = Path.Combine(folder, ManifestName);
        if (!File.Exists(path))
            return null;
        try
        {
            using var manifest = JsonDocument.Parse(RegularFile.ReadAtMost(path, MaxManifestBytes));
            if (manifest.RootElement.ValueKind != JsonValueKind.Object || !manifest.RootElement.TryGetProperty(LibreFolder, out var libre)
                || libre.ValueKind != JsonValueKind.Object || !libre.TryGetProperty("rounds", out var rounds))
                return null;
            return rounds.TryGetInt32(out var count) && count >= 1 ? count : throw new InvalidDataException($"{ManifestName}'s libre.rounds is not a count.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"{ManifestName} can't be read: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Records what the HWiNFO source copies each round into a recording's <c>hwinfo/</c> folder, until its duration has
/// passed (from its first round), it has <c>maxRounds</c> rounds, the next round would take it past what a replay
/// loads (<see cref="SensorRecordingLimits"/>), writing fails, or the source stops (<see cref="Stopped"/>, the engine
/// shutting down); then it stops (the source reads on) and writes the rounds into the manifest. A round's frames are
/// kept until it ends and written together, so a recording never holds part of one. It never takes, or deletes, a file
/// it didn't write: it claims <c>hwinfo/</c> first (<see cref="SensorRecording.TryClaim"/>, as Libre's recorder claims
/// <c>libre/</c>), and a folder another recorder claimed is left alone (nothing recorded, the manifest untouched, an
/// error logged); a frame's name another file holds ends the recording, and only the round's files it created are taken
/// back. With <c>scrub</c>, each copy is scrubbed first (<see cref="HwInfoScrubber"/>), and a copy that can't be (it doesn't
/// read) isn't kept. Called on the poll thread.
/// </summary>
public sealed class HwInfoRecorder : HwInfoSampler.IObserver
{
    private readonly string _root;
    private readonly string _folder;
    private readonly HwInfoScrubber? _scrubber;
    private readonly TimeSpan _interval;
    private readonly DateTimeOffset _started;
    private readonly TimeSpan? _duration;
    private readonly TimeProvider _time;
    private readonly SensorRecordingLimits _limits;
    private readonly ILogger _log = Log.ForContext<HwInfoRecorder>();
    private readonly List<(int Remote, byte[] Compressed, int Length)> _pending = [];
    private byte[] _buffer = [];
    private long? _firstRound;
    private int _round;
    private int _frames;
    private long _bytes;
    private bool _finished;
    private readonly bool _claimed;

    /// <param name="folder">The recording (made when missing); its <c>hwinfo/</c> must hold no frames yet.</param>
    /// <param name="duration">How long it records, from its first round; until <paramref name="maxRounds"/> when null.</param>
    /// <param name="scrubber">The scrubber, with <paramref name="scrub"/>; one for this PC and user by default.</param>
    public HwInfoRecorder(string folder, int maxRounds, bool scrub, TimeSpan interval, DateTimeOffset started, TimeSpan? duration = null,
        TimeProvider? time = null, SensorRecordingLimits? limits = null, HwInfoScrubber? scrubber = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRounds, 1);
        _root = folder;
        _folder = Path.Combine(folder, SensorRecording.HwInfoFolder);
        _limits = limits ?? SensorRecordingLimits.Default;
        MaxRounds = Math.Min(maxRounds, _limits.MaxRounds);
        _scrubber = scrub ? scrubber ?? new HwInfoScrubber(Environment.MachineName, Environment.UserName) : null;
        _interval = interval;
        _started = started;
        _duration = duration;
        _time = time ?? TimeProvider.System;
        _claimed = SensorRecording.TryClaim(_folder, out var problem);
        if (!_claimed)
        {
            // Another recorder (another engine that passed the folder's check at the same time) has it, or the folder
            // can't be written: either way HWiNFO's sensors aren't recorded, and the engine starts and reads on.
            _finished = true;
            if (problem is null)
                _log.Error("Recording sensors: {Folder} is another recording's; HWiNFO's sensors aren't recorded", _folder);
            else
                _log.Error("Recording sensors: {Folder} can't be claimed ({Problem}); check its permissions; HWiNFO's sensors aren't recorded", _folder, problem);
            return;
        }

        try
        {
            SensorRecording.WriteManifest(folder, interval, scrub, started);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The recording's own folder refuses its manifest: nothing is recorded, and the engine starts and reads on.
            _finished = true;
            _log.Error("Recording sensors: {Folder}'s manifest can't be written ({Problem}); check its permissions; HWiNFO's sensors aren't recorded", folder, e.Message);
            return;
        }

        _log.Information("Recording sensors into {Folder} for {Duration}{Scrubbed}", folder,
            duration is { } d ? d.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s" : maxRounds + " rounds", scrub ? ", scrubbed" : "");
    }

    public int MaxRounds { get; }

    /// <summary>The rounds recorded so far.</summary>
    public int Rounds => _round;

    public bool Finished => _finished;

    public void Copied(int remote, ReadOnlySpan<byte> image)
    {
        if (_finished)
            return;

        if (_buffer.Length < image.Length)
            _buffer = new byte[image.Length];
        var copy = _buffer.AsSpan(0, image.Length);
        image.CopyTo(copy);
        if (_scrubber != null && !_scrubber.Scrub(copy))
        {
            // A torn or untrusted copy can't be scrubbed: it isn't kept (the round has no frame for this mapping).
            _log.Debug("Recording sensors: round {Round}'s copy of mapping {Remote} doesn't read, so it isn't kept", _round, remote);
            return;
        }

        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(copy);
        _pending.Add((remote, compressed.ToArray(), copy.Length));
    }

    public void RoundEnded()
    {
        if (_finished)
            return;

        var now = _time.GetTimestamp();
        _firstRound ??= now;
        try
        {
            var bytes = _pending.Sum(p => (long)p.Length);
            if (_pending.Count > _limits.MaxMappingsPerRound || _frames + _pending.Count > _limits.MaxFrames || _bytes + bytes > _limits.MaxBytes)
            {
                Finish($"the next round would pass what a replay loads ({_pending.Count} mappings, {_frames + _pending.Count} frames, {_bytes + bytes} bytes)");
                return;
            }

            var written = new List<string>(_pending.Count);
            try
            {
                foreach (var (remote, compressed, _) in _pending)
                {
                    var path = Path.Combine(_folder, SensorRecording.FileName(_round, remote));
                    // Never a name another file holds; and only once this recorder created the file is it its own to
                    // take back.
                    using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                    {
                        written.Add(path);
                        file.Write(compressed);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A round is whole or absent: take back what of it this recorder created, nothing else.
                foreach (var path in written)
                    TryDelete(path);
                _log.Error(ex, "Recording sensors failed at round {Round}", _round);
                Finish("writing failed");
                return;
            }

            _frames += _pending.Count;
            _bytes += bytes;
            _round++;
            if (_round >= MaxRounds)
                Finish($"{_round} rounds");
            else if (_duration is { } duration && _time.GetElapsedTime(_firstRound.Value, now) >= duration)
                Finish($"{duration.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s");
        }
        finally
        {
            _pending.Clear();
        }
    }

    /// <summary>
    /// The source stopped (the engine shuts down) before the recording ended: the rounds recorded so far, those with no
    /// frame at the end included, go into the manifest. Called on the poll thread, after its last round.
    /// </summary>
    public void Stopped()
    {
        if (_finished)
            return;
        _pending.Clear();
        Finish("the source stopped");
    }

    private void Finish(string why)
    {
        _finished = true;
        if (!_claimed)
            return;
        try
        {
            SensorRecording.WriteManifest(_root, _interval, _scrubber != null, _started, _round, _time.GetUtcNow());
            _log.Information("Sensor recording finished after {Why}: {Rounds} rounds in {Folder}", why, _round, _root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logged, never thrown: the source reads on.
            _log.Error(ex, "Sensor recording: writing its manifest failed; {Rounds} rounds in {Folder}", _round, _root);
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
/// Takes what could identify a PC, its user or its drives out of HWiNFO images before they are kept. An image is
/// rebuilt from its retained fields only: the header; each sensor's id, instance and original name; each reading's
/// type, sensor index, id, original label, units (both encodings) and values. Everything else is zero: the user's names
/// (always blank), fields an element is too short to hold whole, bytes past the known fields, gaps between sections.
/// Every retained string goes through <see cref="ScrubName"/>. An image that doesn't read isn't scrubbed
/// (<see cref="Scrub"/> answers false); a <c>DEAD</c> one keeps only its signature.
/// </summary>
public sealed partial class HwInfoScrubber(string? machineName = null, string? userName = null)
{
    private readonly Dictionary<string, string> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _serials = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _addresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _macs = new(StringComparer.Ordinal);

    [GeneratedRegex(@"^\[([^\]]+)\] ")]
    private static partial Regex HostPrefix();

    [GeneratedRegex(@"\b[0-9A-Fa-f]{2}([:-])(?:[0-9A-Fa-f]{2}\1){4}[0-9A-Fa-f]{2}\b")]
    private static partial Regex Mac();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"(?<![0-9A-Za-z])[0-9A-Fa-f]{0,4}(?::[0-9A-Fa-f]{0,4}){2,7}(?:%[0-9A-Za-z]+)?(?![0-9A-Za-z])")]
    private static partial Regex Ipv6();

    [GeneratedRegex(@"\(([A-Za-z0-9][A-Za-z0-9._-]{5,})\)")]
    private static partial Regex Parenthesised();

    /// <summary>A parenthesised run the string ends inside: a field cut it off before its closing parenthesis.</summary>
    [GeneratedRegex(@"\(([A-Za-z0-9][A-Za-z0-9._-]{5,})$")]
    private static partial Regex ParenthesisedToTheEnd();

    [GeneratedRegex(@"\b(S/?N|Serial(?:\s*(?:Number|No\.?))?)(\s*[:#]?\s*)([A-Za-z0-9][A-Za-z0-9._-]{5,})", RegexOptions.IgnoreCase)]
    private static partial Regex AfterSerial();

    [GeneratedRegex(@"^(?:SERIAL\d+|PC\d+|IP\d+|MAC\d+|THISPC|THISUSER)$")]
    private static partial Regex Alias();

    /// <summary>
    /// <paramref name="name"/> with a remote's <c>[HOST]</c> prefix (and that host anywhere else) replaced by
    /// <c>PC1</c>, <c>PC2</c> … (by first sight); serial-like tokens (one token of letters and digits, six or more,
    /// with a digit, in parentheses, in an opening parenthesis the string ends inside (a 16-byte unit or a 128-byte
    /// name cut it off), or after <c>SN</c>, <c>S/N</c> or <c>Serial</c>) by <c>SERIAL1</c> …; IPv4 and IPv6
    /// addresses by <c>IP1</c> …; MAC addresses by <c>MAC1</c> …; this computer's name by <c>THISPC</c> and the user's
    /// by <c>THISUSER</c>.
    /// </summary>
    public string ScrubName(string name)
    {
        name = HostPrefix().Replace(name, m => "[" + Alias(_hosts, m.Groups[1].Value, "PC") + "] ");
        foreach (var (host, alias) in _hosts)
            name = Whole(name, host, alias);
        name = Mac().Replace(name, m => Alias(_macs, string.Concat(m.Value.Where(Uri.IsHexDigit)).ToUpperInvariant(), "MAC"));
        name = Ipv4().Replace(name, m => Alias(_addresses, m.Value, "IP"));
        name = Ipv6().Replace(name, m => m.Value.Any(Uri.IsHexDigit) ? Alias(_addresses, m.Value, "IP") : m.Value);
        name = Parenthesised().Replace(name, m => IsSerial(m.Groups[1].Value) ? "(" + Alias(_serials, m.Groups[1].Value, "SERIAL") + ")" : m.Value);
        name = ParenthesisedToTheEnd().Replace(name, m => IsSerial(m.Groups[1].Value) ? "(" + Alias(_serials, m.Groups[1].Value, "SERIAL") : m.Value);
        name = AfterSerial().Replace(name, m => IsSerial(m.Groups[3].Value) ? m.Groups[1].Value + m.Groups[2].Value + Alias(_serials, m.Groups[3].Value, "SERIAL") : m.Value);
        if (!string.IsNullOrEmpty(machineName))
            name = Whole(name, machineName, "THISPC");
        if (!string.IsNullOrEmpty(userName))
            name = Whole(name, userName, "THISUSER");
        return name;
    }

    private static bool IsSerial(string token) => token.Any(char.IsAsciiDigit) && !Alias().IsMatch(token);

    private static string Whole(string text, string word, string replacement) =>
        Regex.Replace(text, @"(?<![0-9A-Za-z])" + Regex.Escape(word) + @"(?![0-9A-Za-z])", replacement.Replace("$", "$$", StringComparison.Ordinal),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Alias(Dictionary<string, string> map, string value, string prefix)
    {
        if (!map.TryGetValue(value, out var alias))
            map[value] = alias = prefix + (map.Count + 1).ToString(CultureInfo.InvariantCulture);
        return alias;
    }

    /// <summary>
    /// Scrubs <paramref name="image"/> in place: true when done; false, and the image left as it was, when it doesn't
    /// read (a torn copy, an unknown signature, sections out of bounds or overlapping), so it must not be kept.
    /// </summary>
    public bool Scrub(Span<byte> image)
    {
        var state = HwInfoLayout.ReadHeader(image, image.Length, out var h, out _);
        if (state == HwInfoImageState.Dead)
        {
            image[4..].Clear();
            return true;
        }

        if (state != HwInfoImageState.Ok || Overlap(h))
            return false;

        var output = new byte[image.Length];
        var hasPeriod = h.SensorOffset >= HwInfoLayout.HeaderSizeWithPeriod && h.ReadingOffset >= HwInfoLayout.HeaderSizeWithPeriod;
        var header = hasPeriod ? HwInfoLayout.HeaderSizeWithPeriod : HwInfoLayout.HeaderSize;
        image[..header].CopyTo(output);
        for (var i = 0; i < h.SensorCount; i++)
        {
            var at = (int)(h.SensorOffset + (long)h.SensorSize * i);
            var e = image.Slice(at, (int)h.SensorSize);
            var o = output.AsSpan(at, (int)h.SensorSize);
            e[..8].CopyTo(o);
            Write1252(o.Slice(8, HwInfoLayout.StringSize), ScrubName(HwInfoLayout.SensorNameOf(e)));
        }

        for (var i = 0; i < h.ReadingCount; i++)
        {
            var at = (int)(h.ReadingOffset + (long)h.ReadingSize * i);
            var e = image.Slice(at, (int)h.ReadingSize);
            var o = output.AsSpan(at, (int)h.ReadingSize);
            e[..12].CopyTo(o);
            Write1252(o.Slice(12, HwInfoLayout.StringSize), ScrubName(HwInfoLayout.ReadingNameOf(e)));
            Write1252(o.Slice(268, HwInfoLayout.UnitSize), ScrubName(HwInfoLayout.Windows1252(e.Slice(268, HwInfoLayout.UnitSize))));
            e.Slice(284, 32).CopyTo(o[284..]);
            if (e.Length >= HwInfoLayout.ReadingSizeWithUtf8)
                WriteUtf8(o.Slice(444, HwInfoLayout.UnitSize), ScrubName(HwInfoLayout.Utf8(e.Slice(444, HwInfoLayout.UnitSize))));
        }

        output.CopyTo(image);
        return true;
    }

    /// <summary>Whether the two sections share bytes (HWiNFO's never do: a scrub can't keep one's fields without the other's).</summary>
    private static bool Overlap(in HwInfoHeader h)
    {
        var sensorEnd = h.SensorOffset + (long)h.SensorSize * h.SensorCount;
        var readingEnd = h.ReadingOffset + (long)h.ReadingSize * h.ReadingCount;
        return h.SensorCount > 0 && h.ReadingCount > 0 && h.SensorOffset < readingEnd && h.ReadingOffset < sensorEnd;
    }

    private static void Write1252(Span<byte> field, string text)
    {
        field.Clear();
        for (var i = 0; i < text.Length && i < field.Length; i++)
            field[i] = Cp1252Of(text[i]);
    }

    private static byte Cp1252Of(char c)
    {
        if (c < 0x80 || c is >= (char)0xA0 and <= (char)0xFF)
            return (byte)c;
        for (var b = 0x80; b < 0xA0; b++)
        {
            if (HwInfoLayout.Windows1252([(byte)b]) is [var d] && d == c)
                return (byte)b;
        }

        return (byte)'?';
    }

    private static void WriteUtf8(Span<byte> field, string text)
    {
        field.Clear();
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        // Never cut a character: the longest whole prefix that fits.
        var length = Math.Min(bytes.Length, field.Length);
        while (length > 0 && length < bytes.Length && (bytes[length] & 0xC0) == 0x80)
            length--;
        bytes.AsSpan(0, length).CopyTo(field);
    }
}
