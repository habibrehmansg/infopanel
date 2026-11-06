using InfoPanel.Sensors.MacOS;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace InfoPanel.Sensors.Volume;

/// <summary>
/// macOS: the output devices through Core Audio's C API (<c>AudioObjectGetPropertyData</c>; no permission prompt, an
/// output's properties aren't the microphone): the system's devices (<c>kAudioHardwarePropertyDevices</c>) that are
/// alive and have output streams, and the default output device (<c>kAudioHardwarePropertyDefaultOutputDevice</c>). Each
/// device's id is its UID; its name (Core Audio has one a device) is its Name and Device Name both. Its volume is the
/// one the menu bar's slider shows (AudioToolbox's virtual main volume), else the output's main volume, else the average
/// of every output channel's own volume it can read; none of them, it has none (HDMI, DisplayPort and some USB devices).
/// Its mute is the output's main mute, else, when its output channels have mute controls, muted only when every one is;
/// none, it has none. Names and UIDs are read whole whatever their length (the source applies its text limit). Core
/// Audio's notifications run on a thread of its own (set once, at the first lister), so devices coming and going are
/// seen whether or not the main thread runs a run loop (desktop mode runs AppKit's; a headless engine runs none).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacAudioLister : IAudioLister
{
    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    private const uint SystemObject = 1;
    private const uint ScopeGlobal = 0x676C6F62; // 'glob'
    private const uint ScopeOutput = 0x6F757470; // 'outp'
    private const uint ElementMain = 0;

    private const uint Devices = 0x64657623; // 'dev#'
    private const uint DefaultOutputDevice = 0x644F7574; // 'dOut'
    private const uint RunLoop = 0x726E6C70; // 'rnlp'
    private const uint DeviceUid = 0x75696420; // 'uid '
    private const uint ObjectName = 0x6C6E616D; // 'lnam'
    private const uint DeviceIsAlive = 0x6C69766E; // 'livn'
    private const uint Streams = 0x73746D23; // 'stm#'
    private const uint StreamConfiguration = 0x736C6179; // 'slay'
    private const uint VolumeScalar = 0x766F6C6D; // 'volm'
    private const uint MuteProperty = 0x6D757465; // 'mute'
    private const uint VirtualMainVolume = 0x766D7663; // 'vmvc'

    /// <summary>At most this many output channels are read for a device without a main control.</summary>
    internal const int MaxChannels = 1024;

    private static int _runLoopSet;

    [StructLayout(LayoutKind.Sequential)]
    private struct CFRange(nint location, nint length)
    {
        public nint Location = location;
        public nint Length = length;
    }

    /// <summary>One buffer of an <c>AudioBufferList</c>: its channels, its size and its data.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AudioBuffer
    {
        public uint NumberChannels;
        public uint DataByteSize;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyAddress(uint selector, uint scope, uint element = ElementMain)
    {
        public uint Selector = selector;
        public uint Scope = scope;
        public uint Element = element;
    }

    public MacAudioLister()
    {
        if (Interlocked.Exchange(ref _runLoopSet, 1) == 0)
        {
            var address = new PropertyAddress(RunLoop, ScopeGlobal);
            IntPtr none = IntPtr.Zero;
            // Best effort: without it, devices that come and go may go unseen until Core Audio's next look.
            AudioObjectSetPropertyData(SystemObject, &address, 0, null, (uint)sizeof(IntPtr), &none);
        }
    }

    public IReadOnlyList<IAudioDevice> List()
    {
        var address = new PropertyAddress(Devices, ScopeGlobal);
        uint size;
        Check(AudioObjectGetPropertyDataSize(SystemObject, &address, 0, null, &size), "the devices' count");
        var ids = new uint[size / sizeof(uint)];
        if (ids.Length == 0)
            return [];
        fixed (uint* p = ids)
        {
            size = (uint)(ids.Length * sizeof(uint));
            Check(AudioObjectGetPropertyData(SystemObject, &address, 0, null, &size, p), "the devices");
        }

        var count = (int)(size / sizeof(uint));
        var devices = new List<IAudioDevice>(count);
        for (var i = 0; i < count; i++)
        {
            if (IsOutput(ids[i]))
                devices.Add(new MacAudioDevice(ids[i]));
        }

        return devices;
    }

    public IAudioDevice? Default()
    {
        var address = new PropertyAddress(DefaultOutputDevice, ScopeGlobal);
        var id = ReadUInt(SystemObject, address, "the default output device");
        return id == 0 ? null : new MacAudioDevice(id);
    }

    /// <summary>Whether a device is alive and has output streams; false when it can't be told (it went).</summary>
    private static bool IsOutput(uint device)
    {
        var streams = new PropertyAddress(Streams, ScopeOutput);
        uint size;
        if (AudioObjectGetPropertyDataSize(device, &streams, 0, null, &size) != 0 || size == 0)
            return false;
        var alive = new PropertyAddress(DeviceIsAlive, ScopeGlobal);
        uint value = 1;
        size = sizeof(uint);
        // A device that doesn't say it is alive is taken as alive.
        return AudioObjectGetPropertyData(device, &alive, 0, null, &size, &value) != 0 || value != 0;
    }

    private static void Check(int status, string what)
    {
        if (status != 0)
            throw new IOException($"Core Audio couldn't read {what} (OSStatus {status}).");
    }

    private static uint ReadUInt(uint device, PropertyAddress address, string what)
    {
        uint value = 0, size = sizeof(uint);
        Check(AudioObjectGetPropertyData(device, &address, 0, null, &size, &value), what);
        return value;
    }

    private static string ReadString(uint device, PropertyAddress address, string what)
    {
        IntPtr cf = IntPtr.Zero;
        var size = (uint)sizeof(IntPtr);
        Check(AudioObjectGetPropertyData(device, &address, 0, null, &size, &cf), what);
        try
        {
            return Text(cf) ?? throw new IOException($"Core Audio gave {what} as no readable text.");
        }
        finally
        {
            if (cf != IntPtr.Zero)
                CF.CFRelease(cf);
        }
    }

    /// <summary>
    /// A CFString whole, as UTF-16 (a fixed UTF-8 buffer would fail a long name and with it the device); null when it
    /// isn't a string.
    /// </summary>
    internal static string? Text(IntPtr cf)
    {
        if (!CF.IsString(cf))
            return null;
        var length = CFStringGetLength(cf);
        if (length <= 0)
            return "";
        return string.Create(checked((int)length), cf, static (chars, s) =>
        {
            fixed (char* p = chars)
                CFStringGetCharacters(s, new CFRange(0, chars.Length), p);
        });
    }

    /// <summary>How many output channels a device has (its output streams' channels), at most <see cref="MaxChannels"/>; 0 when it can't be told.</summary>
    private static int OutputChannels(uint device)
    {
        var address = new PropertyAddress(StreamConfiguration, ScopeOutput);
        uint size;
        if (AudioObjectGetPropertyDataSize(device, &address, 0, null, &size) != 0 || size < sizeof(uint) || size > 64 * 1024)
            return 0;
        var bytes = new byte[size];
        fixed (byte* buffer = bytes)
        {
            if (AudioObjectGetPropertyData(device, &address, 0, null, &size, buffer) != 0)
                return 0;

            // An AudioBufferList: its count, then its buffers, aligned as a pointer is.
            var buffers = *(uint*)buffer;
            var first = sizeof(IntPtr);
            long channels = 0;
            for (var i = 0; i < buffers && first + (long)(i + 1) * sizeof(AudioBuffer) <= size; i++)
                channels += ((AudioBuffer*)(buffer + first))[i].NumberChannels;
            return (int)Math.Min(channels, MaxChannels);
        }
    }

    /// <summary>A float property of an output's channel, or null when it has none or it can't be read.</summary>
    private static float? ReadChannelFloat(uint device, uint selector, uint channel)
    {
        var address = new PropertyAddress(selector, ScopeOutput, channel);
        if (AudioObjectHasProperty(device, &address) == 0)
            return null;
        float value = 0;
        uint size = sizeof(float);
        return AudioObjectGetPropertyData(device, &address, 0, null, &size, &value) == 0 && float.IsFinite(value)
            ? Math.Clamp(value, 0f, 1f)
            : null;
    }

    /// <summary>An output channel's mute, or null when it has no mute control or it can't be read.</summary>
    private static bool? ReadChannelMute(uint device, uint channel)
    {
        var address = new PropertyAddress(MuteProperty, ScopeOutput, channel);
        if (AudioObjectHasProperty(device, &address) == 0)
            return null;
        uint value = 0, size = sizeof(uint);
        return AudioObjectGetPropertyData(device, &address, 0, null, &size, &value) == 0 ? value != 0 : null;
    }

    /// <summary>A float property of an output, or null when the device has none.</summary>
    private static float? ReadFloat(uint device, uint selector, uint element, bool service)
    {
        var address = new PropertyAddress(selector, ScopeOutput, element);
        if ((service ? AudioHardwareServiceHasProperty(device, &address) : AudioObjectHasProperty(device, &address)) == 0)
            return null;
        float value = 0;
        uint size = sizeof(float);
        var status = service
            ? AudioHardwareServiceGetPropertyData(device, &address, 0, null, &size, &value)
            : AudioObjectGetPropertyData(device, &address, 0, null, &size, &value);
        Check(status, "the volume");
        return float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : null;
    }

    private sealed class MacAudioDevice(uint device) : IAudioDevice
    {
        public string Id => ReadString(device, new PropertyAddress(DeviceUid, ScopeGlobal), "a device's UID");

        public string Name => ReadString(device, new PropertyAddress(ObjectName, ScopeGlobal), "a device's name");

        public string DeviceName => Name;

        public float? Volume
        {
            get
            {
                if (ReadFloat(device, VirtualMainVolume, ElementMain, service: true) is { } main)
                    return main;
                if (ReadFloat(device, VolumeScalar, ElementMain, service: false) is { } scalar)
                    return scalar;

                // Else every output channel's own volume (channels count from 1), averaged.
                var channels = new float?[OutputChannels(device)];
                for (var i = 0; i < channels.Length; i++)
                    channels[i] = ReadChannelFloat(device, VolumeScalar, (uint)i + 1);
                return MacAudioRules.ChannelVolume(channels);
            }
        }

        public bool? Mute
        {
            get
            {
                var address = new PropertyAddress(MuteProperty, ScopeOutput);
                if (AudioObjectHasProperty(device, &address) != 0)
                    return ReadUInt(device, address, "the mute") != 0;

                // Else the output channels' own mutes.
                var channels = new bool?[OutputChannels(device)];
                for (var i = 0; i < channels.Length; i++)
                    channels[i] = ReadChannelMute(device, (uint)i + 1);
                return MacAudioRules.ChannelMute(channels);
            }
        }
    }

    [DllImport(CoreAudio)]
    private static extern byte AudioObjectHasProperty(uint objectId, PropertyAddress* address);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyDataSize(uint objectId, PropertyAddress* address, uint qualifierSize, void* qualifier, uint* dataSize);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyData(uint objectId, PropertyAddress* address, uint qualifierSize, void* qualifier, uint* dataSize, void* data);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectSetPropertyData(uint objectId, PropertyAddress* address, uint qualifierSize, void* qualifier, uint dataSize, void* data);

    [DllImport(CoreFoundation)]
    private static extern nint CFStringGetLength(IntPtr str);

    [DllImport(CoreFoundation)]
    private static extern void CFStringGetCharacters(IntPtr str, CFRange range, char* buffer);

    [DllImport(AudioToolbox)]
    private static extern byte AudioHardwareServiceHasProperty(uint objectId, PropertyAddress* address);

    [DllImport(AudioToolbox)]
    private static extern int AudioHardwareServiceGetPropertyData(uint objectId, PropertyAddress* address, uint qualifierSize, void* qualifier, uint* dataSize, void* data);
}

/// <summary>The macOS lister's rules for a device without a main control (pure: tested without hardware).</summary>
internal static class MacAudioRules
{
    /// <summary>The output channels' volumes averaged, those it can't read left out; none, null.</summary>
    internal static float? ChannelVolume(IReadOnlyList<float?> channels)
    {
        double sum = 0;
        var count = 0;
        foreach (var channel in channels)
        {
            if (channel is { } c)
            {
                sum += c;
                count++;
            }
        }

        return count == 0 ? null : (float)(sum / count);
    }

    /// <summary>
    /// The output channels' mutes: true only when every channel is muted (one without a mute control is heard, so false);
    /// null when none has a mute control.
    /// </summary>
    internal static bool? ChannelMute(IReadOnlyList<bool?> channels)
    {
        var any = false;
        var all = true;
        foreach (var channel in channels)
        {
            any |= channel is not null;
            all &= channel == true;
        }

        return any ? all : null;
    }
}
