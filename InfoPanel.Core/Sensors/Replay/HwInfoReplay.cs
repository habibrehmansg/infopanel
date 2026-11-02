using InfoPanel.Persistence;
using InfoPanel.Sensors.HwInfo;
using InfoPanel.Sensors.HwInfo.Gadget;
using InfoPanel.Sensors.Libre;
using InfoPanel.Settings;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace InfoPanel.Sensors.Replay;

/// <summary>
/// HWiNFO's mappings as a recording saw them (<see cref="SensorRecording"/>), round after round, looping: the
/// mappings a <see cref="HwInfoSampler"/> opens through it read the recorded round's bytes, and each round the
/// sampler ends (<see cref="RoundEnded"/>) moves to the next. A mapping the round has no file for isn't there (an open
/// view of it reads <c>DEAD</c>, as HWiNFO's own does when it ends); the rounds are the manifest's, so rounds with no
/// file at the end are played too; a recording with none plays as HWiNFO not running. Loaded whole at start, only regular files, within <see cref="SensorRecordingLimits"/>
/// (files listed, frames, mappings a round and rounds counted before anything is read).
/// </summary>
public sealed class HwInfoReplay : IHwInfoMemoryOpener, HwInfoSampler.IObserver
{
    private static readonly byte[] Dead = BitConverter.GetBytes(HwInfoLayout.SignatureDead);

    private readonly Dictionary<int, byte[]>?[] _rounds;
    private readonly string _localName;
    private readonly string _remotePrefix;
    private int _round;

    private HwInfoReplay(Dictionary<int, byte[]>?[] rounds, string localName, string remotePrefix)
    {
        _rounds = rounds;
        _localName = localName;
        _remotePrefix = remotePrefix;
    }

    /// <summary>The recorded rounds, in a loop.</summary>
    public int Rounds => _rounds.Length;

    /// <summary>The round the mappings read now (0 to <see cref="Rounds"/> − 1).</summary>
    public int Round => _round;

    /// <summary>Loads <paramref name="folder"/>'s HWiNFO rounds. Throws <see cref="InvalidDataException"/> for one that can't be replayed.</summary>
    public static HwInfoReplay Load(string folder, string localName = HwInfoSampler.LocalName, string remotePrefix = HwInfoSampler.RemotePrefix, SensorRecordingLimits? limits = null)
    {
        limits ??= SensorRecordingLimits.Default;
        if (SensorRecording.Check(folder) is { } problem)
            throw new InvalidDataException(problem);

        var declared = SensorRecording.RoundsOf(folder);
        if (declared > limits.MaxRounds)
            throw new InvalidDataException($"The recording has {declared} rounds, over {limits.MaxRounds}.");

        var files = new List<(int Round, int Remote, string Path)>();
        var mappings = new Dictionary<int, int>();
        var listed = 0;
        // A recording of a PC where HWiNFO didn't run (Libre's alone) has no frames: every round reads as no HWiNFO.
        var hwinfo = Path.Combine(folder, SensorRecording.HwInfoFolder);
        foreach (var path in Directory.Exists(hwinfo) ? Directory.EnumerateFileSystemEntries(hwinfo) : [])
        {
            if (++listed > limits.MaxFiles)
                throw new InvalidDataException($"The recording's {SensorRecording.HwInfoFolder} folder holds over {limits.MaxFiles} files.");
            var name = Path.GetFileName(path);
            if (!SensorRecording.TryParseFileName(name, out var round, out var remote))
                continue;
            if (round >= (declared ?? limits.MaxRounds))
                throw new InvalidDataException(declared is { } d ? $"'{name}' is past the recording's {d} rounds." : $"'{name}' is past {limits.MaxRounds} rounds.");
            if (files.Count >= limits.MaxFrames)
                throw new InvalidDataException($"The recording holds over {limits.MaxFrames} frames.");
            var inRound = mappings[round] = mappings.GetValueOrDefault(round) + 1;
            if (inRound > limits.MaxMappingsPerRound)
                throw new InvalidDataException($"Round {round} of the recording holds over {limits.MaxMappingsPerRound} mappings.");
            files.Add((round, remote, path));
        }

        var rounds = new Dictionary<int, byte[]>?[declared ?? (files.Count == 0 ? 1 : files.Max(f => f.Round) + 1)];
        long total = 0;
        foreach (var (round, remote, path) in files)
        {
            var bytes = Decompress(path, Math.Min(limits.MaxBytes - total, HwInfoLayout.MaxImageBytes));
            total += bytes.Length;
            (rounds[round] ??= [])[remote] = bytes;
        }

        return new HwInfoReplay(rounds, localName, remotePrefix);
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

    public IHwInfoMemory? TryOpen(string name)
    {
        var remote = RemoteOf(name);
        return remote is { } r && _rounds[_round]?.ContainsKey(r) == true ? new View(this, r) : null;
    }

    private int? RemoteOf(string name)
    {
        if (name == _localName)
            return -1;
        if (name.StartsWith(_remotePrefix, StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(_remotePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i > 0)
            return i;
        return null;
    }

    public void Copied(int remote, ReadOnlySpan<byte> image)
    {
    }

    public void RoundEnded() => _round = (_round + 1) % _rounds.Length;

    private byte[] Bytes(int remote) => _rounds[_round]?.TryGetValue(remote, out var bytes) == true ? bytes : Dead;

    private sealed class View(HwInfoReplay replay, int remote) : IHwInfoMemory
    {
        public long Length => replay.Bytes(remote).Length;

        public void Read(long offset, Span<byte> destination)
        {
            var bytes = replay.Bytes(remote);
            if (offset < 0 || offset > bytes.Length - destination.Length)
                throw new ArgumentOutOfRangeException(nameof(offset), "The read ends past the recorded image.");
            bytes.AsSpan((int)offset, destination.Length).CopyTo(destination);
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// The sources a recording replays (<c>--replay-sensors</c>), under their ids and keys: the sources of the Windows PC
/// it was recorded on, in its order, HWiNFO's from the recording, HWiNFO Gadget's with no key (a recording doesn't
/// hold the gadget key, so it reads as on a PC where gadget reporting isn't set up), and Libre's from the recording when
/// it holds Libre's. No HWiNFO process runs in a
/// replay, so where HWiNFO's shared memory isn't recorded it isn't running.
/// </summary>
public static class SensorReplay
{
    /// <summary>The HWiNFO source over the recording's rounds, one a poll, looping.</summary>
    public static PolledSensorSource CreateHwInfo(string folder, TimeProvider? time = null)
    {
        var replay = HwInfoReplay.Load(folder);
        return HwInfoSource.Create(replay, time, observer: replay);
    }

    /// <summary>
    /// The Libre source over the recording's sensor helper (<see cref="LibreReplay"/>), its settings applied as on the
    /// PC (<paramref name="settings"/>: off, storage). Only for a recording that holds Libre's frames
    /// (<see cref="SensorRecording.HoldsLibre"/>): one made before the engine read Libre replays without a Libre source.
    /// </summary>
    public static LibreSource CreateLibre(string folder, Func<LibreSettings> settings, TimeProvider? time = null) =>
        new(LibreReplay.Load(folder), settings, time);

    /// <summary>The HWiNFO Gadget source over a registry with no gadget key.</summary>
    public static PolledSensorSource CreateGadget(TimeProvider? time = null) => HwInfoGadgetSource.Create(new NoGadgetKey(), time);

    private sealed class NoGadgetKey : IGadgetRegistry
    {
        public GadgetSnapshot? Read(GadgetHive hive) => null;
    }
}
