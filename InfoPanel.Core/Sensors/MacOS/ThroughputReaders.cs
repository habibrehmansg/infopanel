using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace InfoPanel.Sensors.MacOS;

/// <summary>
/// Disk throughput from every <c>IOBlockStorageDriver</c>'s <c>Statistics</c> (<c>Bytes (Read)</c>,
/// <c>Bytes (Write)</c>; public IOKit), as MB/s from per-driver deltas keyed by registry entry id, so a disk attached,
/// ejected or replaced between samples adds nothing for that round instead of a bogus jump. A failed round drops the
/// baselines, so a delta never spans a missed sample.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class DiskReader : MacReader
{
    private const string Group = "Disk";
    private const double MB = 1024 * 1024;

    private static readonly IntPtr StatisticsKey = CF.Str("Statistics");
    private static readonly IntPtr ReadKey = CF.Str("Bytes (Read)");
    private static readonly IntPtr WriteKey = CF.Str("Bytes (Write)");

    private sealed class Counters
    {
        public long Read;
        public long Write;
        public long Round;
    }

    private int _read, _write;
    private readonly Dictionary<ulong, Counters> _drivers = [];
    private readonly List<ulong> _gone = [];
    private long _round;
    private bool _primed;

    protected override void DiscoverCore(List<SensorDefinition> definitions)
    {
        if (!Deltas(out _, out _) || _drivers.Count == 0)
            return;

        _read = Add(definitions, "disk/read", Group, "Disk Read Rate", "MB/s");
        _write = Add(definitions, "disk/write", Group, "Disk Write Rate", "MB/s");
    }

    /// <summary>Bytes read and written since the previous call, over drivers present both times.</summary>
    private bool Deltas(out long read, out long write)
    {
        read = write = 0;
        uint iterator;
        fixed (byte* name = "IOBlockStorageDriver\0"u8)
            iterator = IOKit.Services(name);
        if (iterator == 0)
            return false;

        var round = ++_round;
        uint driver;
        while ((driver = IOKit.IOIteratorNext(iterator)) != 0)
        {
            var stats = IOKit.IORegistryEntryCreateCFProperty(driver, StatisticsKey, IntPtr.Zero, 0);
            if (stats != IntPtr.Zero)
            {
                if (IOKit.IORegistryEntryGetRegistryEntryID(driver, out var id) == IOKit.Success
                    && CF.IsDictionary(stats)
                    && CF.TryGetInt64(CF.CFDictionaryGetValue(stats, ReadKey), out var r)
                    && CF.TryGetInt64(CF.CFDictionaryGetValue(stats, WriteKey), out var w))
                {
                    if (_drivers.TryGetValue(id, out var last))
                    {
                        if (r >= last.Read && w >= last.Write)
                        {
                            read += r - last.Read;
                            write += w - last.Write;
                        }
                    }
                    else
                    {
                        last = new Counters();
                        _drivers[id] = last;
                    }

                    last.Read = r;
                    last.Write = w;
                    last.Round = round;
                }

                CF.CFRelease(stats);
            }

            IOKit.IOObjectRelease(driver);
        }

        IOKit.IOObjectRelease(iterator);

        foreach (var (id, counters) in _drivers)
        {
            if (counters.Round != round)
                _gone.Add(id);
        }

        foreach (var id in _gone)
            _drivers.Remove(id);
        _gone.Clear();
        return true;
    }

    protected override void SampleCore(Span<SensorSample> mine, TimeSpan elapsed)
    {
        if (!Deltas(out var read, out var write))
        {
            _drivers.Clear();
            _primed = false;
            mine[_read].Outcome = mine[_write].Outcome = SampleOutcome.Failed;
            return;
        }

        var seconds = elapsed.TotalSeconds;
        if (_primed && seconds > 0)
        {
            mine[_read].Set(read / MB / seconds);
            mine[_write].Set(write / MB / seconds);
        }

        _primed = true;
    }
}

/// <summary>
/// Network throughput from <c>sysctl {CTL_NET, PF_ROUTE, 0, 0, NET_RT_IFLIST2, 0}</c> (public): the byte counters in
/// each <c>if_msghdr2</c>, over interfaces that are up, running and Ethernet-type (<c>en*</c>, Wi-Fi included; loopback,
/// tunnels and bridges left out so traffic isn't counted twice), as KB/s from per-interface deltas. For an unprivileged
/// process macOS 27 truncates these counters to 32 bits and rounds them (to 4 KiB on Wi-Fi), so a 32-bit counter that
/// goes down is a wrap when the wrapped amount is no more than the interface could carry in the interval (its
/// <c>ifi_baudrate</c> × 1.5, or 2 GiB/s when it reports none); any other decrease (an interface reset) counts as zero
/// and re-primes. An interface absent or not counted in a round loses its baseline; a failed round drops all.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class NetworkReader : MacReader
{
    private const string Group = "Network";

    private const int CtlNet = 4;
    private const int PfRoute = 17;
    private const int NetRtIfList2 = 6;
    private const byte RtmIfInfo2 = 0x12;
    private const int IffUp = 0x1;
    private const int IffRunning = 0x40;
    private const byte IftEther = 0x6;
    private const int MaxInterfaces = 1024;

    // if_msghdr2 (net/if.h): msglen u16 @0, version u8 @2, type u8 @3, addrs @4, flags @8, index u16 @12,
    // snd_len @16, snd_maxlen @20, snd_drops @24, timer @28, if_data64 @32 (type u8 @+0, baudrate u64 @+16,
    // ibytes u64 @+64, obytes @+72).
    private const int FlagsOffset = 8;
    private const int IndexOffset = 12;
    private const int DataOffset = 32;
    private const int IfTypeOffset = DataOffset;
    private const int BaudRateOffset = DataOffset + 16;
    private const int InBytesOffset = DataOffset + 64;
    private const int OutBytesOffset = DataOffset + 72;

    private int _down, _up;
    private byte[] _buffer = new byte[64 * 1024];
    private readonly ulong[] _lastIn = new ulong[MaxInterfaces];
    private readonly ulong[] _lastOut = new ulong[MaxInterfaces];
    private readonly long[] _seenRound = new long[MaxInterfaces];
    private long _round;
    private bool _primed;

    protected override void DiscoverCore(List<SensorDefinition> definitions)
    {
        if (!Deltas(0, out _, out _))
            return;

        _down = Add(definitions, "network/download", Group, "Download Rate", "KB/s");
        _up = Add(definitions, "network/upload", Group, "Upload Rate", "KB/s");
    }

    /// <summary>The counter increase from <paramref name="previous"/> to <paramref name="current"/> (see the class summary).</summary>
    internal static ulong Delta(ulong previous, ulong current, double maxBytes)
    {
        if (current >= previous)
            return current - previous;
        if (previous <= uint.MaxValue && current <= uint.MaxValue)
        {
            var wrapped = current + (1UL << 32) - previous;
            if (wrapped <= maxBytes)
                return wrapped;
        }

        return 0;
    }

    /// <summary>The most bytes an interface of <paramref name="baudRate"/> bits/s can carry in <paramref name="seconds"/>, with headroom.</summary>
    internal static double MaxBytes(ulong baudRate, double seconds) =>
        (baudRate > 0 ? baudRate / 8.0 * 1.5 : 2.0 * 1024 * 1024 * 1024) * Math.Max(seconds, 0);

    /// <summary>Bytes received and sent since the previous call, over the counted interfaces.</summary>
    private bool Deltas(double seconds, out ulong received, out ulong sent)
    {
        received = sent = 0;
        var mib = stackalloc int[] { CtlNet, PfRoute, 0, 0, NetRtIfList2, 0 };
        nuint size = 0;
        if (LibSystem.sysctl(mib, 6, null, &size, null, 0) != 0)
            return false;
        if ((int)size > _buffer.Length)
            _buffer = new byte[(int)size + 4096];

        size = (nuint)_buffer.Length;
        fixed (byte* buffer = _buffer)
        {
            if (LibSystem.sysctl(mib, 6, buffer, &size, null, 0) != 0)
                return false;
        }

        var span = _buffer.AsSpan(0, (int)size);
        var round = ++_round;
        var offset = 0;
        while (offset + 4 <= span.Length)
        {
            var length = MemoryMarshal.Read<ushort>(span[offset..]);
            if (length == 0 || offset + length > span.Length)
                break;

            var message = span.Slice(offset, length);
            offset += length;
            if (message[3] != RtmIfInfo2 || length < OutBytesOffset + 8)
                continue;

            var flags = MemoryMarshal.Read<int>(message[FlagsOffset..]);
            var index = MemoryMarshal.Read<ushort>(message[IndexOffset..]);
            if ((flags & (IffUp | IffRunning)) != (IffUp | IffRunning) || message[IfTypeOffset] != IftEther || index >= MaxInterfaces)
                continue;

            var bytesIn = MemoryMarshal.Read<ulong>(message[InBytesOffset..]);
            var bytesOut = MemoryMarshal.Read<ulong>(message[OutBytesOffset..]);
            // Counted only when it was also counted last round; otherwise this round is its baseline.
            if (_seenRound[index] == round - 1 && round > 1)
            {
                var max = MaxBytes(MemoryMarshal.Read<ulong>(message[BaudRateOffset..]), seconds);
                received += Delta(_lastIn[index], bytesIn, max);
                sent += Delta(_lastOut[index], bytesOut, max);
            }

            _lastIn[index] = bytesIn;
            _lastOut[index] = bytesOut;
            _seenRound[index] = round;
        }

        return true;
    }

    protected override void SampleCore(Span<SensorSample> mine, TimeSpan elapsed)
    {
        if (!Deltas(elapsed.TotalSeconds, out var received, out var sent))
        {
            // The next round starts new baselines (no interface was seen in the round before it).
            _round++;
            _primed = false;
            mine[_down].Outcome = mine[_up].Outcome = SampleOutcome.Failed;
            return;
        }

        var seconds = elapsed.TotalSeconds;
        if (_primed && seconds > 0)
        {
            mine[_down].Set(received / 1024.0 / seconds);
            mine[_up].Set(sent / 1024.0 / seconds);
        }

        _primed = true;
    }
}
