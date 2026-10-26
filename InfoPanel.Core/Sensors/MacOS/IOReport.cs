using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace InfoPanel.Sensors.MacOS;

/// <summary>
/// <c>/usr/lib/libIOReport.dylib</c> (private, undocumented; in the dyld shared cache, so the absolute path loads), the
/// interface <c>powermetrics</c> builds on. Resolved at run time: every entry is null when the library or a symbol is
/// missing, and <see cref="IsAvailable"/> is false.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class IOReportApi
{
    private const string Lib = "/usr/lib/libIOReport.dylib";

    public static readonly delegate* unmanaged<IntPtr, IntPtr, ulong, ulong, ulong, IntPtr> CopyChannelsInGroup;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> MergeChannels;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr*, ulong, IntPtr, IntPtr> CreateSubscription;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr> CreateSamples;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr> CreateSamplesDelta;
    public static readonly delegate* unmanaged<IntPtr, IntPtr> ChannelGetGroup;
    public static readonly delegate* unmanaged<IntPtr, IntPtr> ChannelGetSubGroup;
    public static readonly delegate* unmanaged<IntPtr, IntPtr> ChannelGetChannelName;
    public static readonly delegate* unmanaged<IntPtr, IntPtr> ChannelGetUnitLabel;
    public static readonly delegate* unmanaged<IntPtr, int, long> SimpleGetIntegerValue;
    public static readonly delegate* unmanaged<IntPtr, int> StateGetCount;
    public static readonly delegate* unmanaged<IntPtr, int, IntPtr> StateGetNameForIndex;
    public static readonly delegate* unmanaged<IntPtr, int, long> StateGetResidency;

    public static readonly bool IsAvailable;

    static IOReportApi()
    {
        if (!NativeLibrary.TryLoad(Lib, out var h))
            return;

        IntPtr Get(string name) => NativeLibrary.TryGetExport(h, name, out var p) ? p : IntPtr.Zero;

        var ptrs = new[]
        {
            Get("IOReportCopyChannelsInGroup"), Get("IOReportMergeChannels"), Get("IOReportCreateSubscription"),
            Get("IOReportCreateSamples"), Get("IOReportCreateSamplesDelta"), Get("IOReportChannelGetGroup"),
            Get("IOReportChannelGetSubGroup"), Get("IOReportChannelGetChannelName"), Get("IOReportChannelGetUnitLabel"),
            Get("IOReportSimpleGetIntegerValue"), Get("IOReportStateGetCount"), Get("IOReportStateGetNameForIndex"),
            Get("IOReportStateGetResidency"),
        };
        if (Array.IndexOf(ptrs, IntPtr.Zero) >= 0)
            return;

        CopyChannelsInGroup = (delegate* unmanaged<IntPtr, IntPtr, ulong, ulong, ulong, IntPtr>)ptrs[0];
        MergeChannels = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)ptrs[1];
        CreateSubscription = (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, ulong, IntPtr, IntPtr>)ptrs[2];
        CreateSamples = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr>)ptrs[3];
        CreateSamplesDelta = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr>)ptrs[4];
        ChannelGetGroup = (delegate* unmanaged<IntPtr, IntPtr>)ptrs[5];
        ChannelGetSubGroup = (delegate* unmanaged<IntPtr, IntPtr>)ptrs[6];
        ChannelGetChannelName = (delegate* unmanaged<IntPtr, IntPtr>)ptrs[7];
        ChannelGetUnitLabel = (delegate* unmanaged<IntPtr, IntPtr>)ptrs[8];
        SimpleGetIntegerValue = (delegate* unmanaged<IntPtr, int, long>)ptrs[9];
        StateGetCount = (delegate* unmanaged<IntPtr, int>)ptrs[10];
        StateGetNameForIndex = (delegate* unmanaged<IntPtr, int, IntPtr>)ptrs[11];
        StateGetResidency = (delegate* unmanaged<IntPtr, int, long>)ptrs[12];
        IsAvailable = true;
    }
}

/// <summary>
/// One long-lived IOReport subscription over a few channel groups. <see cref="Sample"/> takes a sample and, from the
/// second call on, hands every channel of the delta since the previous sample to the caller. Channel order in a
/// subscription is fixed, so callers map channels by index once (<see cref="Channels"/>) and read values without
/// comparing names.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class IOReportSubscription : IDisposable
{
    private static readonly IntPtr ChannelsKey = CF.Str("IOReportChannels");

    private IntPtr _subscription;
    private IntPtr _channels;
    private IntPtr _previous;
    private IntPtr _delta;

    private IOReportSubscription(IntPtr subscription, IntPtr channels)
    {
        _subscription = subscription;
        _channels = channels;
    }

    /// <summary>
    /// Subscribes to the given (group, subgroup or null, which channels to keep or null for all) entries; null when
    /// IOReport is missing or refuses. Keeping only the needed channels matters: the whole "Energy Model" group is about
    /// 300 channels and costs milliseconds per sample.
    /// </summary>
    public static IOReportSubscription? Create(IEnumerable<(string Group, string? SubGroup, Func<string, bool>? Keep)> groups)
    {
        if (!IOReportApi.IsAvailable)
            return null;

        IntPtr merged = IntPtr.Zero;
        foreach (var (group, subGroup, keep) in groups)
        {
            var g = CF.Str(group);
            var s = subGroup == null ? IntPtr.Zero : CF.Str(subGroup);
            var channels = IOReportApi.CopyChannelsInGroup(g, s, 0, 0, 0);
            CF.CFRelease(g);
            if (s != IntPtr.Zero)
                CF.CFRelease(s);
            if (channels == IntPtr.Zero)
                continue;

            if (keep != null)
                Filter(channels, keep);

            if (merged == IntPtr.Zero)
            {
                merged = channels;
            }
            else
            {
                IOReportApi.MergeChannels(merged, channels, IntPtr.Zero);
                CF.CFRelease(channels);
            }
        }

        if (merged == IntPtr.Zero)
            return null;

        IntPtr subscribed = IntPtr.Zero;
        var subscription = IOReportApi.CreateSubscription(IntPtr.Zero, merged, &subscribed, 0, IntPtr.Zero);
        CF.CFRelease(merged);
        if (subscription == IntPtr.Zero || subscribed == IntPtr.Zero)
        {
            if (subscription != IntPtr.Zero)
                CF.CFRelease(subscription);
            if (subscribed != IntPtr.Zero)
                CF.CFRelease(subscribed);
            return null;
        }

        return new IOReportSubscription(subscription, subscribed);
    }

    /// <summary>Removes the channels <paramref name="keep"/> rejects from a channel description (once, at subscription).</summary>
    private static void Filter(IntPtr description, Func<string, bool> keep)
    {
        var array = CF.CFDictionaryGetValue(description, ChannelsKey);
        if (!CF.IsArray(array))
            return;

        for (var i = CF.CFArrayGetCount(array) - 1; i >= 0; i--)
        {
            var name = CF.ToManaged(IOReportApi.ChannelGetChannelName(CF.CFArrayGetValueAtIndex(array, i)));
            if (name == null || !keep(name))
                CF.CFArrayRemoveValueAtIndex(array, i);
        }
    }

    public enum SampleResult
    {
        /// <summary>The first sample (or the first after a failure): a baseline, no delta yet.</summary>
        Primed,

        /// <summary>A delta since the previous sample.</summary>
        Delta,

        /// <summary>The sample or the delta failed; the next good sample primes again.</summary>
        Failed,
    }

    /// <summary>
    /// Takes a sample. On <see cref="SampleResult.Delta"/>, <paramref name="channels"/> is the delta's channel array,
    /// valid until the next call. A failure drops the baseline, so a delta never spans a missed sample.
    /// </summary>
    public SampleResult Sample(out IntPtr channels)
    {
        channels = IntPtr.Zero;
        if (_delta != IntPtr.Zero)
        {
            CF.CFRelease(_delta);
            _delta = IntPtr.Zero;
        }

        var current = _subscription == IntPtr.Zero ? IntPtr.Zero : IOReportApi.CreateSamples(_subscription, _channels, IntPtr.Zero);
        var previous = _previous;
        _previous = current;
        if (current == IntPtr.Zero)
        {
            if (previous != IntPtr.Zero)
                CF.CFRelease(previous);
            return SampleResult.Failed;
        }

        if (previous == IntPtr.Zero)
            return SampleResult.Primed;

        _delta = IOReportApi.CreateSamplesDelta(previous, current, IntPtr.Zero);
        CF.CFRelease(previous);
        var array = _delta == IntPtr.Zero ? IntPtr.Zero : CF.CFDictionaryGetValue(_delta, ChannelsKey);
        if (!CF.IsArray(array))
            return SampleResult.Failed;

        channels = array;
        return SampleResult.Delta;
    }

    /// <summary>The names of the subscribed channels (allocates; for discovery).</summary>
    public List<string> SubscribedChannelNames()
    {
        var names = new List<string>();
        var array = CF.CFDictionaryGetValue(_channels, ChannelsKey);
        if (!CF.IsArray(array))
            return names;

        var count = CF.CFArrayGetCount(array);
        for (nint i = 0; i < count; i++)
        {
            if (CF.ToManaged(IOReportApi.ChannelGetChannelName(CF.CFArrayGetValueAtIndex(array, i))) is { } name)
                names.Add(name);
        }

        return names;
    }

    /// <summary>The (group, subgroup, channel, unit) of every channel in a delta array (allocates; once per subscription).</summary>
    public static List<(string Group, string SubGroup, string Channel, string Unit)> Channels(IntPtr array)
    {
        var list = new List<(string, string, string, string)>();
        var count = CF.CFArrayGetCount(array);
        for (nint i = 0; i < count; i++)
        {
            var channel = CF.CFArrayGetValueAtIndex(array, i);
            list.Add((
                CF.ToManaged(IOReportApi.ChannelGetGroup(channel)) ?? "",
                CF.ToManaged(IOReportApi.ChannelGetSubGroup(channel)) ?? "",
                CF.ToManaged(IOReportApi.ChannelGetChannelName(channel)) ?? "",
                CF.ToManaged(IOReportApi.ChannelGetUnitLabel(channel)) ?? ""));
        }

        return list;
    }

    /// <summary>A state channel's state names (allocates; once per subscription).</summary>
    public static string[] StateNames(IntPtr channel)
    {
        var count = IOReportApi.StateGetCount(channel);
        var names = new string[Math.Max(0, count)];
        for (var i = 0; i < names.Length; i++)
            names[i] = CF.ToManaged(IOReportApi.StateGetNameForIndex(channel, i)) ?? "";
        return names;
    }

    public void Dispose()
    {
        foreach (var p in new[] { _delta, _previous, _channels, _subscription })
        {
            if (p != IntPtr.Zero)
                CF.CFRelease(p);
        }

        _delta = _previous = _channels = _subscription = IntPtr.Zero;
    }
}
