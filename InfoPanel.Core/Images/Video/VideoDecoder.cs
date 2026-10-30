using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using InfoPanel.Spec;
using SkiaSharp;

namespace InfoPanel.Images.Video;

/// <summary>A video that can't be opened or decoded, with its media code (<c>media.malformed</c>, <c>media.codec</c>, ...).</summary>
internal sealed class VideoException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// One video file decoded in order through FFmpeg, its frames scaled to one size: used by one thread at a time. FFmpeg
/// reads the file only through this decoder's own I/O callbacks over the stream it is given (custom I/O: it opens no
/// file, address or protocol itself), with the demuxer the probe named, only that demuxer and decoder allowed, the
/// decoder's picture size held to the media limits, and every stream but the first video track discarded. The caller
/// probes the stream (<see cref="VideoProbe"/>) before handing it over: FFmpeg never sees a file the probe refused.
/// </summary>
internal sealed unsafe class VideoDecoder : IDisposable
{
    private const int IoBufferSize = 64 * 1024;

    /// <summary>Decode errors in a row that fail the video (a corrupt packet here and there is skipped).</summary>
    private const int MaxErrors = 32;

    private readonly FFmpeg _f;
    private readonly Stream _stream;
    private GCHandle _self;
    private void* _io;
    private void* _format;
    private void* _codec;
    private void* _packet;
    private void* _frame;
    private void* _held;
    private void* _sws;
    private int _swsWidth, _swsHeight, _swsFormat = -1;
    private int _index;
    private int _timeNum, _timeDen;
    private long _start;
    private bool _draining;
    private bool _decoded; // a frame has decoded since the decoder opened
    private int? _error; // the last error FFmpeg decoded with
    private double _lastSeconds = double.NaN;
    private SpecLimits _limits = SpecLimits.Default;
    private CancellationToken _cancel;

    /// <summary>The threads FFmpeg decodes one video with (each holds a picture of its own).</summary>
    internal const int Threads = 2;

    /// <summary>
    /// The most pixels a picture of a video may have, from the size its container states: each side 64 pixels longer
    /// (a picture's edges and row alignment), rounded up to 64, and at least 320 rows (MPEG-4's scratch buffers are 280
    /// rows of a picture's width, and FFmpeg holds them to the same cap). The decoder allocates no larger picture, and
    /// its memory is reserved for pictures of this size (<see cref="VideoPlayer.WorkingSet"/>).
    /// </summary>
    internal static long PictureArea(MediaInfo info) => Padded(info.Width) * Math.Max(Padded(info.Height), 320);

    private static long Padded(int side) => ((long)Math.Max(1, side) + 64 + 63) / 64 * 64;

    private VideoDecoder(FFmpeg f, Stream stream, MediaInfo probe, int width, int height)
    {
        _f = f;
        _stream = stream;
        Info = probe;
        Width = width;
        Height = height;
    }

    /// <summary>What the probe read of the file.</summary>
    public MediaInfo Info { get; }

    /// <summary>The size frames are scaled to.</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>The decoded frame's own size and pixel format, as FFmpeg reports them (tests read them).</summary>
    public (int Width, int Height, int Format) FrameShape =>
        (Field<int>(_frame, FFmpegAbi.FrameWidth), Field<int>(_frame, FFmpegAbi.FrameHeight), Field<int>(_frame, FFmpegAbi.FrameFormat));

    private static T Field<T>(void* p, int offset) where T : unmanaged => *(T*)((byte*)p + offset);

    private static void SetField<T>(void* p, int offset, T value) where T : unmanaged => *(T*)((byte*)p + offset) = value;

    /// <summary>
    /// Opens <paramref name="stream"/> (seekable; the decoder owns and disposes it), which the probe read as
    /// <paramref name="probe"/>, decoding to <paramref name="width"/> x <paramref name="height"/>.
    /// <paramref name="opening"/> is told the address FFmpeg is given, always none: it reads through the callbacks.
    /// Once <paramref name="cancel"/> is cancelled FFmpeg's next read fails and the decoder stops: opening or decoding
    /// throws <see cref="OperationCanceledException"/> rather than read on.
    /// </summary>
    /// <exception cref="VideoException">FFmpeg refused the file, or its first video track isn't what the probe read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> was cancelled.</exception>
    public static VideoDecoder Open(FFmpeg f, Stream stream, MediaInfo probe, int width, int height, SpecLimits limits, Action<string?>? opening = null,
        CancellationToken cancel = default)
    {
        if (probe.Codec is not { } codecName || !probe.IsVideo)
            throw new VideoException(MediaProbe.FormatCode, "Not a video.");
        var decoder = new VideoDecoder(f, stream, probe, Math.Max(1, width), Math.Max(1, height)) { _limits = limits, _cancel = cancel };
        try
        {
            decoder.OpenInput(opening);
            decoder.OpenCodec(codecName, limits);
            return decoder;
        }
        catch (Exception e) when (e is not OperationCanceledException && cancel.IsCancellationRequested)
        {
            // A read refused because the session was closed: the open stopped, it didn't fail.
            decoder.Dispose();
            throw new OperationCanceledException("The video was closed while it opened.", e, cancel);
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
    }

    private void OpenInput(Action<string?>? opening)
    {
        _self = GCHandle.Alloc(this);
        var buffer = (byte*)_f.av_malloc(IoBufferSize);
        if (buffer is null) throw new VideoException(MediaProbe.MalformedCode, "No memory for the video.");
        _io = _f.avio_alloc_context(buffer, IoBufferSize, 0, (void*)GCHandle.ToIntPtr(_self), &IoRead, null, &IoSeek);
        if (_io is null)
        {
            _f.av_free(buffer);
            throw new VideoException(MediaProbe.MalformedCode, "No memory for the video.");
        }

        _format = _f.avformat_alloc_context();
        if (_format is null) throw new VideoException(MediaProbe.MalformedCode, "No memory for the video.");
        SetField(_format, FFmpegAbi.FormatContextPb, (IntPtr)_io);
        SetField(_format, FFmpegAbi.FormatContextFlags, Field<int>(_format, FFmpegAbi.FormatContextFlags) | FFmpegAbi.FormatFlagCustomIo);

        var demuxer = FFmpegAbi.Demuxer(Info.Format);
        void* input;
        fixed (byte* name = Utf8(demuxer))
            input = _f.av_find_input_format(name);
        if (input is null) throw new VideoException(VideoProbe.CodecCode, $"This engine's FFmpeg has no {demuxer} demuxer.");

        void* options = null;
        try
        {
            // Only the probed container's demuxer, no protocol (the bytes come through the callbacks), no external references.
            Set(&options, "format_whitelist", demuxer);
            Set(&options, "protocol_whitelist", "none");
            // The mov demuxer's own option (the others have no external references, and would leave it unused).
            if (demuxer == "mov") Set(&options, "enable_drefs", "0");
            opening?.Invoke(null);
            var format = _format;
            var result = _f.avformat_open_input(&format, null, input, &options);
            _format = format; // FFmpeg frees the context and nulls it when it fails
            if (result < 0) throw new VideoException(MediaProbe.MalformedCode, $"The video could not be opened: {_f.Error(result)}.");
            RequireConsumed(_f, (IntPtr)options, $"The {demuxer} demuxer");
        }
        finally
        {
            _f.av_dict_free(&options);
        }
    }

    private void OpenCodec(string codecName, SpecLimits limits)
    {
        var count = Field<uint>(_format, FFmpegAbi.FormatContextNbStreams);
        var streams = Field<IntPtr>(_format, FFmpegAbi.FormatContextStreams);
        void* parameters = null;
        _index = -1;
        for (var i = 0; i < count; i++)
        {
            var stream = (void*)((IntPtr*)streams)[i];
            var par = (void*)Field<IntPtr>(stream, FFmpegAbi.StreamCodecPar);
            if (_index < 0 && Field<int>(par, FFmpegAbi.CodecParType) == FFmpegAbi.MediaTypeVideo)
            {
                _index = Field<int>(stream, FFmpegAbi.StreamIndex);
                parameters = par;
                _timeNum = Field<int>(stream, FFmpegAbi.StreamTimeBase);
                _timeDen = Field<int>(stream, FFmpegAbi.StreamTimeBase + 4);
                var start = Field<long>(stream, FFmpegAbi.StreamStartTime);
                _start = start == FFmpegAbi.NoPts ? 0 : start;
            }
            else
            {
                SetField(stream, FFmpegAbi.StreamDiscard, FFmpegAbi.DiscardAll);
            }
        }
        if (parameters is null) throw new VideoException(MediaProbe.FormatCode, "The decoder finds no video track.");
        if (_timeNum <= 0 || _timeDen <= 0) throw new VideoException(MediaProbe.MalformedCode, "The video track has no time base.");
        var id = Field<int>(parameters, FFmpegAbi.CodecParId);
        if (id != FFmpegAbi.CodecId(codecName))
            throw new VideoException(MediaProbe.MalformedCode, $"The decoder reads another codec ({id}) than the container's {codecName}.");

        void* decoder = null;
        string? found = null;
        foreach (var name in FFmpegAbi.Decoders(codecName))
        {
            fixed (byte* n = Utf8(name))
                decoder = _f.avcodec_find_decoder_by_name(n);
            if (decoder is not null)
            {
                found = name;
                break;
            }
        }
        if (decoder is null || found is null) throw new VideoException(VideoProbe.CodecCode, $"This engine's FFmpeg has no {codecName} decoder.");

        _codec = _f.avcodec_alloc_context3(decoder);
        if (_codec is null) throw new VideoException(MediaProbe.MalformedCode, "No memory for the decoder.");
        var copied = _f.avcodec_parameters_to_context(_codec, parameters);
        if (copied < 0) throw new VideoException(MediaProbe.MalformedCode, $"The video's parameters were refused: {_f.Error(copied)}.");
        void* options = null;
        try
        {
            Set(&options, "codec_whitelist", found);
            // FFmpeg allocates no picture larger than the decoder's memory was reserved for (the container's size), nor
            // over the media area, whatever the stream's own headers say: a larger stream fails to decode.
            var pixels = Math.Min(limits.MediaArea, PictureArea(Info));
            Set(&options, "max_pixels", pixels.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Set(&options, "threads", Threads.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var opened = _f.avcodec_open2(_codec, decoder, &options);
            if (opened < 0) throw new VideoException(MediaProbe.MalformedCode, $"The decoder could not be opened: {_f.Error(opened)}.");
            RequireConsumed(_f, (IntPtr)options, $"The {found} decoder");
        }
        finally
        {
            _f.av_dict_free(&options);
        }
        _packet = _f.av_packet_alloc();
        _frame = _f.av_frame_alloc();
        _held = _f.av_frame_alloc();
        if (_packet is null || _frame is null || _held is null) throw new VideoException(MediaProbe.MalformedCode, "No memory for the decoder.");
    }

    private void Set(void** options, string key, string value)
    {
        var dict = (IntPtr)(*options);
        SetOption(_f, ref dict, key, value);
        *options = (void*)dict;
    }

    /// <summary>
    /// Adds <paramref name="key"/> = <paramref name="value"/> to an options dictionary (<see cref="IntPtr.Zero"/>: a new
    /// one). These options are what keeps FFmpeg to the probed file, so one FFmpeg refuses fails the video.
    /// </summary>
    /// <exception cref="VideoException">FFmpeg refused the option.</exception>
    internal static void SetOption(FFmpeg f, ref IntPtr options, string? key, string value)
    {
        var dict = (void*)options;
        int result;
        fixed (byte* v = Utf8(value))
        {
            if (key is null)
            {
                result = f.av_dict_set(&dict, null, v, 0);
            }
            else
            {
                fixed (byte* k = Utf8(key))
                    result = f.av_dict_set(&dict, k, v, 0);
            }
        }
        options = (IntPtr)dict;
        if (result < 0)
            throw new VideoException(MediaProbe.MalformedCode, $"FFmpeg refused the option {key ?? "(none)"}: {f.Error(result)}.");
    }

    internal static void FreeOptions(FFmpeg f, ref IntPtr options)
    {
        var dict = (void*)options;
        f.av_dict_free(&dict);
        options = IntPtr.Zero;
    }

    /// <summary>
    /// After an open, the options FFmpeg didn't take are left in the dictionary: each one set is meant for what was
    /// opened (<paramref name="what"/>), so one left over is a safety option that didn't apply and fails the video.
    /// </summary>
    /// <exception cref="VideoException">An option was left over.</exception>
    internal static void RequireConsumed(FFmpeg f, IntPtr options, string what)
    {
        if (options == IntPtr.Zero)
            return;
        // av_dict_get with an empty key and AV_DICT_IGNORE_SUFFIX: the first entry; an AVDictionaryEntry starts with its key.
        void* entry;
        fixed (byte* any = Utf8(""))
            entry = f.av_dict_get((void*)options, any, null, FFmpegAbi.DictIgnoreSuffix);
        if (entry is null)
            return;
        var key = Marshal.PtrToStringUTF8(*(IntPtr*)entry) ?? "?";
        throw new VideoException(MediaProbe.MalformedCode, $"{what} didn't take the option {key}.");
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    /// <summary>
    /// Decodes the next frame: its time in seconds from the video's start, or null at the end. The frame stays in the
    /// decoder until the next call or <see cref="Seek"/>; <see cref="Convert"/> scales it.
    /// </summary>
    /// <exception cref="VideoException">The video can't be decoded.</exception>
    /// <exception cref="OperationCanceledException">The decoder was cancelled: it stops before reading on.</exception>
    public double? Next()
    {
        var errors = 0;
        while (true)
        {
            // Each packet read and each frame asked for: a cancelled decoder (its session closed) goes no further.
            _cancel.ThrowIfCancellationRequested();
            _f.av_frame_unref(_frame);
            var received = _f.avcodec_receive_frame(_codec, _frame);
            if (received == 0)
            {
                CheckFrame();
                _decoded = true;
                return _lastSeconds = SecondsOf(_frame);
            }
            if (received == FFmpegAbi.ErrorEof)
                return End();
            if (received != FFmpegAbi.ErrorAgain)
            {
                _error = received;
                if (++errors > MaxErrors) throw new VideoException(MediaProbe.MalformedCode, $"The video could not be decoded: {_f.Error(received)}.");
                continue;
            }
            if (_draining)
                return End();

            var read = _f.av_read_frame(_format, _packet);
            if (read < 0)
            {
                // The end (or an unreadable rest): drain the frames the decoder still holds.
                _draining = true;
                _f.avcodec_send_packet(_codec, null);
                continue;
            }
            if (Field<int>(_packet, FFmpegAbi.PacketStreamIndex) != _index)
            {
                _f.av_packet_unref(_packet);
                continue;
            }
            var sent = _f.avcodec_send_packet(_codec, _packet);
            _f.av_packet_unref(_packet);
            if (sent < 0 && sent != FFmpegAbi.ErrorAgain)
            {
                _error = sent;
                if (++errors > MaxErrors) throw new VideoException(MediaProbe.MalformedCode, $"The video could not be decoded: {_f.Error(sent)}.");
            }
        }
    }

    /// <summary>The stream's end: a stream that ends before any frame decoded, its packets refused, fails.</summary>
    private double? End()
    {
        if (!_decoded && _error is { } error)
            throw new VideoException(MediaProbe.MalformedCode, $"The video could not be decoded: {_f.Error(error)}.");
        return null;
    }

    /// <summary>A decoded picture over the media limits fails the video (FFmpeg's own cap is the area alone).</summary>
    private void CheckFrame()
    {
        var (w, h, _) = FrameShape;
        if (w < 1 || h < 1 || w > _limits.MediaSide || h > _limits.MediaSide || (long)w * h > _limits.MediaArea)
            throw new VideoException(MediaProbe.MalformedCode, $"The decoder reads a {w} x {h} picture, outside the media limits.");
    }

    private double SecondsOf(void* frame)
    {
        var ts = Field<long>(frame, FFmpegAbi.FrameBestEffortTimestamp);
        if (ts == FFmpegAbi.NoPts) ts = Field<long>(frame, FFmpegAbi.FramePts);
        if (ts == FFmpegAbi.NoPts)
            return double.IsNaN(_lastSeconds) ? 0 : _lastSeconds; // no timestamp: shown with the frame before it
        return Math.Max(0, (ts - _start) * (double)_timeNum / _timeDen);
    }

    /// <summary>Goes back to the key frame at or before <paramref name="seconds"/>; the next frames decoded follow from there.</summary>
    /// <exception cref="VideoException">The video can't be sought.</exception>
    public void Seek(double seconds)
    {
        var target = _start + (long)Math.Floor(Math.Max(0, seconds) * _timeDen / _timeNum);
        var result = _f.av_seek_frame(_format, _index, target, FFmpegAbi.SeekFlagBackward);
        // A container without an index can still go back to its start, by bytes.
        if (result < 0 && seconds <= 0)
            result = _f.av_seek_frame(_format, -1, 0, FFmpegAbi.SeekFlagBackward | 2);
        if (result < 0) throw new VideoException(MediaProbe.MalformedCode, $"The video could not be sought: {_f.Error(result)}.");
        _f.avcodec_flush_buffers(_codec);
        _f.av_frame_unref(_frame);
        _f.av_frame_unref(_held);
        _draining = false;
        _lastSeconds = double.NaN;
    }

    /// <summary>The bytes of a frame at the decode size.</summary>
    public long FrameBytes => (long)Width * Height * 4;

    /// <summary>
    /// Keeps the decoded frame aside (its time <paramref name="seconds"/>), replacing the one kept before, so decoding
    /// can go on: the frame to show is known only once the one after it is decoded.
    /// </summary>
    public void Hold(double seconds)
    {
        _f.av_frame_unref(_held);
        _f.av_frame_move_ref(_held, _frame);
        HeldSeconds = seconds;
    }

    /// <summary>The time of the frame <see cref="Hold"/> kept.</summary>
    public double HeldSeconds { get; private set; }

    /// <summary>The decoded frame, scaled to <see cref="Width"/> x <see cref="Height"/> in the platform's colour type.</summary>
    /// <exception cref="VideoException">The frame can't be converted.</exception>
    public SKImage Convert() => Convert(_frame);

    /// <summary>The frame <see cref="Hold"/> kept, scaled as <see cref="Convert()"/> scales.</summary>
    public SKImage ConvertHeld() => Convert(_held);

    private SKImage Convert(void* frame)
    {
        int w = Field<int>(frame, FFmpegAbi.FrameWidth), h = Field<int>(frame, FFmpegAbi.FrameHeight), format = Field<int>(frame, FFmpegAbi.FrameFormat);
        if (w < 1 || h < 1) throw new VideoException(MediaProbe.MalformedCode, "No frame to convert.");
        if (_sws is null || w != _swsWidth || h != _swsHeight || format != _swsFormat)
        {
            if (_sws is not null) _f.sws_freeContext(_sws);
            var flags = Width < w || Height < h ? FFmpegAbi.SwsArea : FFmpegAbi.SwsBilinear;
            _sws = _f.sws_getContext(w, h, format, Width, Height, Target, flags, null, null, null);
            (_swsWidth, _swsHeight, _swsFormat) = (w, h, format);
            if (_sws is null) throw new VideoException(MediaProbe.MalformedCode, $"The video's pixel format ({format}) can't be converted.");
        }
        var info = new SKImageInfo(Width, Height, SKImageInfo.PlatformColorType, SKAlphaType.Unpremul);
        var data = SKData.Create(info.BytesSize64);
        if (data is null) throw new VideoException(MediaProbe.MalformedCode, "No memory for the frame.");
        try
        {
            var dst = (byte*)data.Data;
            var dstStride = info.RowBytes;
            var srcData = (byte**)((byte*)frame + FFmpegAbi.FrameData);
            var srcStride = (int*)((byte*)frame + FFmpegAbi.FrameLinesize);
            var rows = _f.sws_scale(_sws, srcData, srcStride, 0, h, &dst, &dstStride);
            if (rows <= 0) throw new VideoException(MediaProbe.MalformedCode, "The frame could not be converted.");
            return SKImage.FromPixels(info, data, info.RowBytes) ?? throw new VideoException(MediaProbe.MalformedCode, "No memory for the frame.");
        }
        finally
        {
            data.Dispose(); // the image holds its own reference
        }
    }

    /// <summary>FFmpeg's pixel format matching Skia's platform colour type.</summary>
    private static int Target => SKImageInfo.PlatformColorType == SKColorType.Bgra8888 ? FFmpegAbi.PixFmtBgra : FFmpegAbi.PixFmtRgba;

    public void Dispose()
    {
        if (_sws is not null) _f.sws_freeContext(_sws);
        _sws = null;
        if (_frame is not null)
        {
            var frame = _frame;
            _f.av_frame_free(&frame);
            _frame = null;
        }
        if (_held is not null)
        {
            var held = _held;
            _f.av_frame_free(&held);
            _held = null;
        }
        if (_packet is not null)
        {
            var packet = _packet;
            _f.av_packet_free(&packet);
            _packet = null;
        }
        if (_codec is not null)
        {
            var codec = _codec;
            _f.avcodec_free_context(&codec);
            _codec = null;
        }
        if (_format is not null)
        {
            var format = _format;
            _f.avformat_close_input(&format); // a custom I/O context is left to its owner
            _format = null;
        }
        if (_io is not null)
        {
            // FFmpeg may have replaced the buffer; it is the context's to free, then the context.
            var buffer = Field<IntPtr>(_io, FFmpegAbi.IoContextBuffer);
            _f.av_free((void*)buffer);
            var io = _io;
            _f.avio_context_free(&io);
            _io = null;
        }
        if (_self.IsAllocated) _self.Free();
        _stream.Dispose();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int IoRead(void* opaque, byte* buffer, int size)
    {
        try
        {
            var decoder = (VideoDecoder)GCHandle.FromIntPtr((IntPtr)opaque).Target!;
            if (decoder._cancel.IsCancellationRequested)
                return FFmpegAbi.ErrorExit; // FFmpeg stops reading: the session was closed
            var n = decoder._stream.Read(new Span<byte>(buffer, Math.Max(0, size)));
            return n == 0 ? FFmpegAbi.ErrorEof : n;
        }
        catch
        {
            return FFmpegAbi.ErrorIo;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long IoSeek(void* opaque, long offset, int whence)
    {
        try
        {
            var decoder = (VideoDecoder)GCHandle.FromIntPtr((IntPtr)opaque).Target!;
            var stream = decoder._stream;
            if ((whence & FFmpegAbi.SeekSize) != 0) return stream.Length;
            var position = (whence & 0xFFFF) switch
            {
                0 => offset,
                1 => stream.Position + offset,
                2 => stream.Length + offset,
                _ => -1,
            };
            if (position < 0 || position > stream.Length) return -22; // EINVAL
            stream.Position = position;
            return position;
        }
        catch
        {
            return FFmpegAbi.ErrorIo;
        }
    }
}
