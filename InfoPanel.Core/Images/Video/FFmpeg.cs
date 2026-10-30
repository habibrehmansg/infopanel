using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Serilog;

namespace InfoPanel.Images.Video;

/// <summary>
/// FFmpeg's decoding libraries (libavutil, libavcodec, libavformat, libswscale), loaded by full path from one folder and
/// called through function pointers: the engine's own binding, no package. Only the major versions
/// <see cref="FFmpegAbi"/> pins are accepted, since it reads a few struct fields at their offsets in those versions; any
/// other version, a missing library or a missing function leaves video off (<see cref="Load"/> answers null and why).
/// The libraries are looked for beside the engine, then in <c>INFOPANEL_FFMPEG_DIR</c>; never by bare name, so the
/// system's FFmpeg is never picked up by the loader's search.
/// </summary>
public sealed unsafe class FFmpeg
{
    /// <summary>The variable that names a development folder of FFmpeg libraries (Homebrew's <c>/opt/homebrew/lib</c>, say).</summary>
    public const string FolderVariable = "INFOPANEL_FFMPEG_DIR";

    private static readonly ILogger Logger = Log.ForContext<FFmpeg>();

    private FFmpeg(string folder) => Folder = folder;

    /// <summary>The folder the libraries were loaded from.</summary>
    public string Folder { get; }

    /// <summary>The libraries' versions, as FFmpeg reports them (<c>major.minor.micro</c>).</summary>
    public string Versions { get; private set; } = "";

    // libavutil
    internal delegate* unmanaged[Cdecl]<uint> avutil_version;
    internal delegate* unmanaged[Cdecl]<void*> av_frame_alloc;
    internal delegate* unmanaged[Cdecl]<void**, void> av_frame_free;
    internal delegate* unmanaged[Cdecl]<void*, void> av_frame_unref;
    internal delegate* unmanaged[Cdecl]<void*, void*, void> av_frame_move_ref;
    internal delegate* unmanaged[Cdecl]<nuint, void*> av_malloc;
    internal delegate* unmanaged[Cdecl]<void*, void> av_free;
    internal delegate* unmanaged[Cdecl]<void**, byte*, byte*, int, int> av_dict_set;
    internal delegate* unmanaged[Cdecl]<void**, void> av_dict_free;
    internal delegate* unmanaged[Cdecl]<void*, byte*, void*, int, void*> av_dict_get;
    internal delegate* unmanaged[Cdecl]<int, void> av_log_set_level;
    internal delegate* unmanaged[Cdecl]<int, byte*, nuint, int> av_strerror;

    // libavcodec
    internal delegate* unmanaged[Cdecl]<uint> avcodec_version;
    internal delegate* unmanaged[Cdecl]<byte*, void*> avcodec_find_decoder_by_name;
    internal delegate* unmanaged[Cdecl]<void*, void*> avcodec_alloc_context3;
    internal delegate* unmanaged[Cdecl]<void**, void> avcodec_free_context;
    internal delegate* unmanaged[Cdecl]<void*, void*, int> avcodec_parameters_to_context;
    internal delegate* unmanaged[Cdecl]<void*, void*, void**, int> avcodec_open2;
    internal delegate* unmanaged[Cdecl]<void*, void*, int> avcodec_send_packet;
    internal delegate* unmanaged[Cdecl]<void*, void*, int> avcodec_receive_frame;
    internal delegate* unmanaged[Cdecl]<void*, void> avcodec_flush_buffers;
    internal delegate* unmanaged[Cdecl]<void*> av_packet_alloc;
    internal delegate* unmanaged[Cdecl]<void**, void> av_packet_free;
    internal delegate* unmanaged[Cdecl]<void*, void> av_packet_unref;

    // libavformat
    internal delegate* unmanaged[Cdecl]<uint> avformat_version;
    internal delegate* unmanaged[Cdecl]<void*> avformat_alloc_context;
    internal delegate* unmanaged[Cdecl]<void**, byte*, void*, void**, int> avformat_open_input;
    internal delegate* unmanaged[Cdecl]<void**, void> avformat_close_input;
    internal delegate* unmanaged[Cdecl]<byte*, void*> av_find_input_format;
    internal delegate* unmanaged[Cdecl]<void*, void*, int> av_read_frame;
    internal delegate* unmanaged[Cdecl]<void*, int, long, int, int> av_seek_frame;
    internal delegate* unmanaged[Cdecl]<byte*, int, int, void*, delegate* unmanaged[Cdecl]<void*, byte*, int, int>, void*, delegate* unmanaged[Cdecl]<void*, long, int, long>, void*> avio_alloc_context;
    internal delegate* unmanaged[Cdecl]<void**, void> avio_context_free;

    // libswscale
    internal delegate* unmanaged[Cdecl]<uint> swscale_version;
    internal delegate* unmanaged[Cdecl]<int, int, int, int, int, int, int, void*, void*, double*, void*> sws_getContext;
    internal delegate* unmanaged[Cdecl]<void*, byte**, int*, int, int, byte**, int*, int> sws_scale;
    internal delegate* unmanaged[Cdecl]<void*, void> sws_freeContext;

    /// <summary>The folders searched, in order: beside the engine, then <see cref="FolderVariable"/>'s.</summary>
    public static IReadOnlyList<string> DefaultFolders()
    {
        var folders = new List<string> { AppContext.BaseDirectory };
        if (Environment.GetEnvironmentVariable(FolderVariable) is { Length: > 0 } dev)
            folders.Add(dev);
        return folders;
    }

    /// <summary>
    /// The libraries from the first of <paramref name="folders"/> that holds all four, at the major versions this binding
    /// pins; null, with <paramref name="why"/>, when none does. A folder whose libraries are of other versions, or lack a
    /// function, is passed over with its reason.
    /// </summary>
    public static FFmpeg? Load(IEnumerable<string?> folders, out string why)
    {
        var reasons = new List<string>();
        if (!Environment.Is64BitProcess)
        {
            why = "Video needs a 64-bit engine.";
            return null;
        }
        foreach (var folder in folders)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                continue;
            var loaded = TryLoad(Path.GetFullPath(folder), out var reason);
            if (loaded is not null)
            {
                why = "";
                return loaded;
            }
            reasons.Add($"{folder}: {reason}");
        }
        why = reasons.Count == 0 ? "No FFmpeg libraries were found beside the engine or in " + FolderVariable + "." : string.Join("; ", reasons);
        return null;
    }

    /// <summary>A library's file name for a major version on this platform.</summary>
    internal static string FileName(string name, int major) =>
        OperatingSystem.IsWindows() ? $"{name}-{major}.dll"
        : OperatingSystem.IsMacOS() ? $"lib{name}.{major}.dylib"
        : $"lib{name}.so.{major}";

    private static FFmpeg? TryLoad(string folder, out string reason)
    {
        var handles = new List<IntPtr>();
        try
        {
            IntPtr Library(string name, int major)
            {
                var path = Path.Combine(folder, FileName(name, major));
                if (!File.Exists(path))
                    throw new FFmpegLoadException($"no {FileName(name, major)}");
                // By full path only: the loader's own search never runs, so no other FFmpeg can stand in.
                var handle = NativeLibrary.Load(path);
                handles.Add(handle);
                return handle;
            }

            // Dependencies first.
            var avutil = Library("avutil", FFmpegAbi.AvUtilMajor);
            var swscale = Library("swscale", FFmpegAbi.SwScaleMajor);
            var avcodec = Library("avcodec", FFmpegAbi.AvCodecMajor);
            var avformat = Library("avformat", FFmpegAbi.AvFormatMajor);

            var f = new FFmpeg(folder);
            f.avutil_version = (delegate* unmanaged[Cdecl]<uint>)Export(avutil, "avutil_version");
            f.avcodec_version = (delegate* unmanaged[Cdecl]<uint>)Export(avcodec, "avcodec_version");
            f.avformat_version = (delegate* unmanaged[Cdecl]<uint>)Export(avformat, "avformat_version");
            f.swscale_version = (delegate* unmanaged[Cdecl]<uint>)Export(swscale, "swscale_version");
            uint vu = f.avutil_version(), vc = f.avcodec_version(), vf = f.avformat_version(), vs = f.swscale_version();
            f.Versions = $"avutil {Version(vu)}, avcodec {Version(vc)}, avformat {Version(vf)}, swscale {Version(vs)}";
            if (vu >> 16 != FFmpegAbi.AvUtilMajor || vc >> 16 != FFmpegAbi.AvCodecMajor || vf >> 16 != FFmpegAbi.AvFormatMajor || vs >> 16 != FFmpegAbi.SwScaleMajor)
                throw new FFmpegLoadException($"the libraries are {f.Versions}; this engine reads avutil {FFmpegAbi.AvUtilMajor}, avcodec {FFmpegAbi.AvCodecMajor}, avformat {FFmpegAbi.AvFormatMajor} and swscale {FFmpegAbi.SwScaleMajor} only");

            f.av_frame_alloc = (delegate* unmanaged[Cdecl]<void*>)Export(avutil, "av_frame_alloc");
            f.av_frame_free = (delegate* unmanaged[Cdecl]<void**, void>)Export(avutil, "av_frame_free");
            f.av_frame_unref = (delegate* unmanaged[Cdecl]<void*, void>)Export(avutil, "av_frame_unref");
            f.av_frame_move_ref = (delegate* unmanaged[Cdecl]<void*, void*, void>)Export(avutil, "av_frame_move_ref");
            f.av_malloc = (delegate* unmanaged[Cdecl]<nuint, void*>)Export(avutil, "av_malloc");
            f.av_free = (delegate* unmanaged[Cdecl]<void*, void>)Export(avutil, "av_free");
            f.av_dict_set = (delegate* unmanaged[Cdecl]<void**, byte*, byte*, int, int>)Export(avutil, "av_dict_set");
            f.av_dict_free = (delegate* unmanaged[Cdecl]<void**, void>)Export(avutil, "av_dict_free");
            f.av_dict_get = (delegate* unmanaged[Cdecl]<void*, byte*, void*, int, void*>)Export(avutil, "av_dict_get");
            f.av_log_set_level = (delegate* unmanaged[Cdecl]<int, void>)Export(avutil, "av_log_set_level");
            f.av_strerror = (delegate* unmanaged[Cdecl]<int, byte*, nuint, int>)Export(avutil, "av_strerror");

            f.avcodec_find_decoder_by_name = (delegate* unmanaged[Cdecl]<byte*, void*>)Export(avcodec, "avcodec_find_decoder_by_name");
            f.avcodec_alloc_context3 = (delegate* unmanaged[Cdecl]<void*, void*>)Export(avcodec, "avcodec_alloc_context3");
            f.avcodec_free_context = (delegate* unmanaged[Cdecl]<void**, void>)Export(avcodec, "avcodec_free_context");
            f.avcodec_parameters_to_context = (delegate* unmanaged[Cdecl]<void*, void*, int>)Export(avcodec, "avcodec_parameters_to_context");
            f.avcodec_open2 = (delegate* unmanaged[Cdecl]<void*, void*, void**, int>)Export(avcodec, "avcodec_open2");
            f.avcodec_send_packet = (delegate* unmanaged[Cdecl]<void*, void*, int>)Export(avcodec, "avcodec_send_packet");
            f.avcodec_receive_frame = (delegate* unmanaged[Cdecl]<void*, void*, int>)Export(avcodec, "avcodec_receive_frame");
            f.avcodec_flush_buffers = (delegate* unmanaged[Cdecl]<void*, void>)Export(avcodec, "avcodec_flush_buffers");
            f.av_packet_alloc = (delegate* unmanaged[Cdecl]<void*>)Export(avcodec, "av_packet_alloc");
            f.av_packet_free = (delegate* unmanaged[Cdecl]<void**, void>)Export(avcodec, "av_packet_free");
            f.av_packet_unref = (delegate* unmanaged[Cdecl]<void*, void>)Export(avcodec, "av_packet_unref");

            f.avformat_alloc_context = (delegate* unmanaged[Cdecl]<void*>)Export(avformat, "avformat_alloc_context");
            f.avformat_open_input = (delegate* unmanaged[Cdecl]<void**, byte*, void*, void**, int>)Export(avformat, "avformat_open_input");
            f.avformat_close_input = (delegate* unmanaged[Cdecl]<void**, void>)Export(avformat, "avformat_close_input");
            f.av_find_input_format = (delegate* unmanaged[Cdecl]<byte*, void*>)Export(avformat, "av_find_input_format");
            f.av_read_frame = (delegate* unmanaged[Cdecl]<void*, void*, int>)Export(avformat, "av_read_frame");
            f.av_seek_frame = (delegate* unmanaged[Cdecl]<void*, int, long, int, int>)Export(avformat, "av_seek_frame");
            f.avio_alloc_context = (delegate* unmanaged[Cdecl]<byte*, int, int, void*, delegate* unmanaged[Cdecl]<void*, byte*, int, int>, void*, delegate* unmanaged[Cdecl]<void*, long, int, long>, void*>)Export(avformat, "avio_alloc_context");
            f.avio_context_free = (delegate* unmanaged[Cdecl]<void**, void>)Export(avformat, "avio_context_free");

            f.sws_getContext = (delegate* unmanaged[Cdecl]<int, int, int, int, int, int, int, void*, void*, double*, void*>)Export(swscale, "sws_getContext");
            f.sws_scale = (delegate* unmanaged[Cdecl]<void*, byte**, int*, int, int, byte**, int*, int>)Export(swscale, "sws_scale");
            f.sws_freeContext = (delegate* unmanaged[Cdecl]<void*, void>)Export(swscale, "sws_freeContext");

            // FFmpeg's own messages would go to the engine's stderr; the engine reports what fails itself.
            f.av_log_set_level(FFmpegAbi.LogQuiet);
            reason = "";
            return f;
        }
        catch (Exception e) when (e is FFmpegLoadException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            foreach (var handle in handles)
                NativeLibrary.Free(handle);
            reason = e.Message;
            return null;
        }
    }

    private static IntPtr Export(IntPtr library, string name) =>
        NativeLibrary.TryGetExport(library, name, out var address) ? address : throw new FFmpegLoadException($"no function {name}");

    private static string Version(uint v) => $"{v >> 16}.{(v >> 8) & 0xFF}.{v & 0xFF}";

    /// <summary>FFmpeg's text for an error code.</summary>
    internal string Error(int code)
    {
        var buffer = stackalloc byte[128];
        return av_strerror(code, buffer, 128) == 0 ? Marshal.PtrToStringUTF8((IntPtr)buffer) ?? $"error {code}" : $"error {code}";
    }

    /// <summary>Logs, once, why video is off.</summary>
    public static void LogUnavailable(string why) =>
        Logger.Information("Video playback is off: {Reason} (the media.video capability isn't listed)", why);

    private sealed class FFmpegLoadException(string message) : Exception(message);
}

/// <summary>
/// What the binding reads of FFmpeg's structs, at the major versions it accepts (from <c>native/ffmpeg/offsets.c</c> run
/// on FFmpeg 9.0's headers, 64-bit): every field is public API whose position a major version keeps.
/// </summary>
internal static class FFmpegAbi
{
    public const int AvUtilMajor = 61, AvCodecMajor = 63, AvFormatMajor = 63, SwScaleMajor = 10;

    public const int FormatContextPb = 32, FormatContextFlags = 128, FormatContextNbStreams = 44, FormatContextStreams = 48;
    public const int StreamIndex = 8, StreamCodecPar = 16, StreamTimeBase = 32, StreamStartTime = 40, StreamDiscard = 68;
    public const int CodecParType = 0, CodecParId = 4, CodecParWidth = 72, CodecParHeight = 76;
    public const int PacketStreamIndex = 36;
    public const int FrameData = 0, FrameLinesize = 64, FrameWidth = 104, FrameHeight = 108, FrameFormat = 116, FramePts = 136,
        FrameBestEffortTimestamp = 304;
    public const int IoContextBuffer = 8;

    public const int MediaTypeVideo = 0, DiscardAll = 48, PixFmtBgra = 28, PixFmtRgba = 26;
    public const int SeekSize = 0x10000, SeekFlagBackward = 1, FormatFlagCustomIo = 0x0080, DictIgnoreSuffix = 2;
    public const int ErrorEof = -541478725, ErrorExit = -1414092869; // AVERROR_EOF, AVERROR_EXIT
    public const long NoPts = long.MinValue;
    public const int SwsBilinear = 2, SwsArea = 32;
    public const int LogQuiet = -8;

    /// <summary>AVERROR(EAGAIN): EAGAIN is 35 on macOS and 11 on Linux and Windows.</summary>
    public static int ErrorAgain => OperatingSystem.IsMacOS() ? -35 : -11;

    public static int ErrorIo => -5;

    /// <summary>The codec ids of the spec's codecs.</summary>
    public static int CodecId(string codec) => codec switch
    {
        "h264" => 27,
        "hevc" => 172,
        "vp8" => 139,
        "vp9" => 166,
        "av1" => 222,
        "mpeg4" => 12,
        "mjpeg" => 7,
        "prores" => 147,
        _ => -1,
    };

    /// <summary>The decoders a spec codec may use, in order (AV1 only through dav1d: FFmpeg's own AV1 decoder needs hardware).</summary>
    public static string[] Decoders(string codec) => codec switch
    {
        "av1" => ["libdav1d"],
        _ => [codec],
    };

    /// <summary>The demuxer for a probed container.</summary>
    public static string Demuxer(Spec.MediaFormat format) => format switch
    {
        Spec.MediaFormat.Mp4 => "mov",
        Spec.MediaFormat.Matroska => "matroska",
        Spec.MediaFormat.Avi => "avi",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}
