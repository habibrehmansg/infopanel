using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using InfoPanel.Spec;
using SkiaSharp;

namespace InfoPanel.Images.Video;

/// <summary>An RTSP stream that can't be opened or read on, with the status it reports.</summary>
internal sealed class RtspException(string code, string message) : Exception(message)
{
    public MediaStatus Status { get; } = new(code, message);

    /// <summary>The decoder was told no format for pictures larger than its room: the stream changed, or starts larger.</summary>
    public bool Changed { get; init; }

    /// <summary>When <see cref="Changed"/>: the coded size (macroblocks whole) of the pictures that didn't fit.</summary>
    public (int Width, int Height) Coded { get; init; }
}

/// <summary>
/// One camera's RTSP stream through FFmpeg, read by one thread: FFmpeg's RTSP demuxer, and nothing else, opens the
/// address it is given (the provider's loopback relay, <see cref="RtspRelay"/>) over TCP only, RTP interleaved on the
/// RTSP connection (no UDP port), its protocols limited to <c>rtsp</c> and <c>tcp</c>, and only its video track kept;
/// the track's H.264 decoder, and only it, decodes with low delay, its pictures held to the media limits (an H.265 track
/// is refused for now: its decoder sizes tables for a parameter set before it asks for a format). The
/// stream opens paused: the camera is asked to play (<see cref="Play"/>) only once the decoder is open, so it sends
/// nothing while the provider finds its decoder room. Every blocking step is bounded: FFmpeg checks an interrupt
/// callback while it waits, which stops it once the provider cancels or when a deadline passes (the stream's opening
/// and PLAY, then each next frame).
/// <para>
/// The decoder's memory is bounded before it decodes: it allocates no picture of more pixels than the provider gave it
/// room for (<see cref="OpenDecoder"/>, then <see cref="SetCeiling"/>), and decodes only 8-bit 4:2:0 and 4:2:2 pictures
/// (and grey), the formats that room is reckoned for: FFmpeg asks which format to decode in (its <c>get_format</c>),
/// with the pictures' coded size set, before it allocates the picture and the tables sized by it, and is told none for
/// any other format and for pictures larger than the room. It asks again when the stream's parameters change: pictures that still fit, in a budgeted format, go on; larger ones end the stream as changed,
/// with their size, so the provider can make room for them. The room the provider gives first comes from the size the
/// stream's description gives (an H.264 track's <c>sprop-parameter-sets</c>, <see cref="DescribedPixels"/>).
/// </para>
/// </summary>
internal sealed unsafe class RtspStream : IDisposable
{
    private readonly FFmpeg _f;
    private readonly CancellationToken _cancel;
    private readonly SpecLimits _limits;
    private GCHandle _self;
    private void* _format;
    private void* _codec;
    private void* _packet;
    private void* _frame;
    private void* _sws;
    private int _swsWidth, _swsHeight, _swsFormat = -1;
    private int _index;
    private long _deadline;
    private volatile bool _timedOut;
    private void* _parameters;
    private bool _changed; // asked again: the pictures changed
    private int _refusedFormat = int.MinValue; // offered only formats outside the budget
    private long _ceiling; // the most pixels a picture may have, as max_pixels says
    private (int Width, int Height) _coded; // the coded size of pictures larger than the room
    private TimeSpan _openTimeout;

    private RtspStream(FFmpeg f, SpecLimits limits, CancellationToken cancel)
    {
        _f = f;
        _limits = limits;
        _cancel = cancel;
    }

    /// <summary>The decoders a camera's track may use: H.264's alone, for now (<see cref="FindTrack"/>).</summary>
    public const string Codec = "h264";

    /// <summary>
    /// The pixels the decoder needs for the pictures the stream's description gives (its H.264 parameter sets, padded as
    /// <see cref="VideoDecoder.PictureArea"/>), or 0 when it gives none (no <c>sprop-parameter-sets</c>).
    /// </summary>
    public long DescribedPixels { get; private set; }

    /// <summary>The decoded frame's size.</summary>
    public (int Width, int Height) Size => (Field<int>(_frame, FFmpegAbi.FrameWidth), Field<int>(_frame, FFmpegAbi.FrameHeight));

    private static T Field<T>(void* p, int offset) where T : unmanaged => *(T*)((byte*)p + offset);

    private static void SetField<T>(void* p, int offset, T value) where T : unmanaged => *(T*)((byte*)p + offset) = value;

    /// <summary>
    /// Opens <paramref name="url"/> (an <c>rtsp://</c> address on loopback), paused, within <paramref name="openTimeout"/>
    /// for the camera's answers to DESCRIBE and SETUP, and finds its H.264 video track; <see cref="OpenDecoder"/>
    /// opens its decoder, and <see cref="Play"/> starts the camera.
    /// </summary>
    /// <exception cref="RtspException">FFmpeg couldn't open the stream, or it has no H.264 video track.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> was cancelled.</exception>
    public static RtspStream Open(FFmpeg f, string url, string userAgent, SpecLimits limits, TimeSpan openTimeout, CancellationToken cancel)
    {
        var stream = new RtspStream(f, limits, cancel);
        try
        {
            stream.OpenInput(url, userAgent, openTimeout);
            stream.FindTrack();
            return stream;
        }
        catch
        {
            stream.Dispose();
            cancel.ThrowIfCancellationRequested();
            throw;
        }
    }

    private void OpenInput(string url, string userAgent, TimeSpan openTimeout)
    {
        _openTimeout = openTimeout;
        _self = GCHandle.Alloc(this);
        _format = _f.avformat_alloc_context();
        if (_format is null) throw new RtspException("media.provider", "No memory for the stream.");
        // AVIOInterruptCB { callback, opaque }: FFmpeg asks it while it waits on the network.
        SetField(_format, FFmpegAbi.FormatContextInterruptCallback, (IntPtr)(delegate* unmanaged[Cdecl]<void*, int>)&Interrupt);
        SetField(_format, FFmpegAbi.FormatContextInterruptCallback + IntPtr.Size, GCHandle.ToIntPtr(_self));

        void* input;
        fixed (byte* name = Utf8("rtsp"))
            input = _f.av_find_input_format(name);
        if (input is null) throw new RtspException("media.provider", "This engine's FFmpeg has no RTSP client.");

        var options = IntPtr.Zero;
        try
        {
            // The RTSP demuxer only, over TCP only (RTP interleaved on the RTSP connection), and only its video; no PLAY
            // until the decoder is open.
            VideoDecoder.SetOption(_f, ref options, "format_whitelist", "rtsp");
            VideoDecoder.SetOption(_f, ref options, "protocol_whitelist", "rtsp,tcp");
            VideoDecoder.SetOption(_f, ref options, "rtsp_transport", "tcp");
            VideoDecoder.SetOption(_f, ref options, "allowed_media_types", "video");
            VideoDecoder.SetOption(_f, ref options, "initial_pause", "1");
            VideoDecoder.SetOption(_f, ref options, "user_agent", userAgent);
            Arm(openTimeout);
            var format = _format;
            var dict = (void*)options;
            int result;
            fixed (byte* u = Utf8(url))
                result = _f.avformat_open_input(&format, u, input, &dict);
            options = (IntPtr)dict;
            _format = format; // FFmpeg frees the context and nulls it when it fails
            _cancel.ThrowIfCancellationRequested();
            if (result < 0)
                throw _timedOut
                    ? new RtspException("media.provider", $"The camera didn't answer within {openTimeout.TotalSeconds:0} s.")
                    : new RtspException("media.provider", $"The camera's stream couldn't be opened: {_f.Error(result)}.");
            Rethrow(() => VideoDecoder.RequireConsumed(_f, options, "The RTSP client"));
        }
        finally
        {
            VideoDecoder.FreeOptions(_f, ref options);
        }
    }

    private void FindTrack()
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
            }
            else
            {
                SetField(stream, FFmpegAbi.StreamDiscard, FFmpegAbi.DiscardAll);
            }
        }
        if (parameters is null) throw new RtspException("media.format", "The camera's stream has no video.");
        var id = Field<int>(parameters, FFmpegAbi.CodecParId);
        // H.264 only for now. HEVC's decoder sizes tables for a parameter set's pictures before it asks get_format, so
        // the room it is given wouldn't bound it; HEVC cameras wait for their parameter sets to be checked first.
        if (id == FFmpegAbi.CodecId("hevc"))
            throw new RtspException(VideoProbe.CodecCode, "The camera sends H.265 (HEVC) video; the engine shows H.264 cameras only for now.");
        if (id != FFmpegAbi.CodecId("h264")) throw new RtspException(VideoProbe.CodecCode, "The camera's video isn't H.264.");
        _parameters = parameters;
        // The RTSP demuxer gives the description's sprop-parameter-sets as the track's extradata (Annex B).
        var extradata = (byte*)Field<IntPtr>(parameters, FFmpegAbi.CodecParExtradata);
        var size = Field<int>(parameters, FFmpegAbi.CodecParExtradataSize);
        if (extradata is not null && size is > 0 and <= 64 * 1024 && H264Size.Read(new ReadOnlySpan<byte>(extradata, size)) is var (w, h))
            DescribedPixels = Math.Min(_limits.MediaArea, VideoDecoder.PictureArea(new MediaInfo(MediaFormat.Mp4, w, h, 0, 0)));
    }

    /// <summary>
    /// Opens the track's decoder, which allocates no picture of more than <paramref name="maxPixels"/> pixels (nor over
    /// the media area) and decodes only the formats its memory is reckoned for.
    /// </summary>
    /// <exception cref="RtspException">The decoder couldn't be opened.</exception>
    public void OpenDecoder(long maxPixels)
    {
        var parameters = _parameters;
        void* decoder;
        fixed (byte* n = Utf8(Codec))
            decoder = _f.avcodec_find_decoder_by_name(n);
        if (decoder is null) throw new RtspException(VideoProbe.CodecCode, $"This engine's FFmpeg has no {Codec} decoder.");
        _codec = _f.avcodec_alloc_context3(decoder);
        if (_codec is null) throw new RtspException("media.provider", "No memory for the decoder.");
        var copied = _f.avcodec_parameters_to_context(_codec, parameters);
        if (copied < 0) throw new RtspException("media.malformed", $"The camera's video parameters were refused: {_f.Error(copied)}.");
        // FFmpeg asks which format to decode in before it allocates a picture, and again when the pictures change.
        SetField(_codec, FFmpegAbi.CodecContextOpaque, GCHandle.ToIntPtr(_self));
        SetField(_codec, FFmpegAbi.CodecContextGetFormat, (IntPtr)(delegate* unmanaged[Cdecl]<void*, int*, int>)&GetFormat);
        var options = IntPtr.Zero;
        try
        {
            VideoDecoder.SetOption(_f, ref options, "codec_whitelist", Codec);
            // No picture larger than the room the provider gave the decoder, nor over the media limits, whatever the
            // stream's headers say; one thread and low delay, so a frame shows as soon as it is decoded.
            var pixels = Math.Max(1, Math.Min(maxPixels, _limits.MediaArea));
            _ceiling = pixels;
            VideoDecoder.SetOption(_f, ref options, "max_pixels", pixels.ToString(CultureInfo.InvariantCulture));
            VideoDecoder.SetOption(_f, ref options, "threads", "1");
            VideoDecoder.SetOption(_f, ref options, "flags", "low_delay");
            var dict = (void*)options;
            var opened = _f.avcodec_open2(_codec, decoder, &dict);
            options = (IntPtr)dict;
            if (opened < 0) throw new RtspException("media.malformed", $"The decoder could not be opened: {_f.Error(opened)}.");
            Rethrow(() => VideoDecoder.RequireConsumed(_f, options, $"The {Codec} decoder"));
        }
        finally
        {
            VideoDecoder.FreeOptions(_f, ref options);
        }
        _packet = _f.av_packet_alloc();
        _frame = _f.av_frame_alloc();
        if (_packet is null || _frame is null) throw new RtspException("media.provider", "No memory for the decoder.");
    }

    /// <summary>The most pixels a picture the open decoder allocates may have from now on.</summary>
    public void SetCeiling(long pixels)
    {
        _ceiling = Math.Max(1, Math.Min(pixels, _limits.MediaArea));
        SetField(_codec, FFmpegAbi.CodecContextMaxPixels, _ceiling);
    }

    /// <summary>Asks the camera to play (the stream opened paused), within the open time limit.</summary>
    /// <exception cref="RtspException">The camera didn't play.</exception>
    /// <exception cref="OperationCanceledException">The provider stopped the stream.</exception>
    public void Play()
    {
        Arm(_openTimeout);
        var played = _f.av_read_play(_format);
        _cancel.ThrowIfCancellationRequested();
        if (played < 0)
            throw _timedOut
                ? new RtspException("media.provider", $"The camera didn't answer within {_openTimeout.TotalSeconds:0} s.")
                : new RtspException("media.provider", $"The camera's stream couldn't be played: {_f.Error(played)}.");
    }

    /// <summary>The formats the decoder's room is reckoned for (<see cref="RtspProvider"/>): at most 2 bytes a pixel.</summary>
    private static bool Budgeted(int format) => format is FFmpegAbi.PixFmtYuv420p or FFmpegAbi.PixFmtYuvj420p
        or FFmpegAbi.PixFmtYuv422p or FFmpegAbi.PixFmtYuvj422p or FFmpegAbi.PixFmtGray8;

    /// <summary>
    /// FFmpeg's <c>get_format</c>, first and on every change of the stream's parameters: the first budgeted format
    /// offered when the pictures' coded size fits the decoder's room (as <c>max_pixels</c> counts it); none when only
    /// other formats are offered, and none, the stream changed, for larger pictures. Called on the decoding thread,
    /// inside <see cref="Next"/>.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetFormat(void* context, int* formats)
    {
        try
        {
            var stream = (RtspStream)GCHandle.FromIntPtr(Field<IntPtr>(context, FFmpegAbi.CodecContextOpaque)).Target!;
            var chosen = FFmpegAbi.PixFmtNone;
            for (var p = formats; *p != FFmpegAbi.PixFmtNone; p++)
            {
                if (!Budgeted(*p)) continue;
                chosen = *p;
                break;
            }
            if (chosen == FFmpegAbi.PixFmtNone)
            {
                stream._refusedFormat = *formats;
                return FFmpegAbi.PixFmtNone;
            }
            var w = Field<int>(context, FFmpegAbi.CodecContextCodedWidth);
            var h = Field<int>(context, FFmpegAbi.CodecContextCodedHeight);
            if (w < 1 || h < 1 || (long)w * h > stream._ceiling)
            {
                stream._coded = (w, h);
                stream._changed = true;
                return FFmpegAbi.PixFmtNone;
            }
            return chosen;
        }
        catch
        {
            return FFmpegAbi.PixFmtNone;
        }
    }

    /// <summary>Ends the stream when the decoder was told no format: the pictures changed, or aren't in a budgeted format.</summary>
    private void CheckFormat()
    {
        if (_changed) throw new RtspException("media.provider", "The camera's pictures changed.") { Changed = true, Coded = _coded };
        if (_refusedFormat != int.MinValue)
            throw new RtspException(VideoProbe.CodecCode, $"The camera's pictures aren't 8-bit 4:2:0 or 4:2:2 (pixel format {_refusedFormat}), the formats its decoder's memory is reckoned for.");
    }

    /// <summary>A <see cref="VideoException"/> from the shared option helpers, as this stream's status.</summary>
    private static void Rethrow(Action action)
    {
        try
        {
            action();
        }
        catch (VideoException e)
        {
            throw new RtspException(e.Code, e.Message);
        }
    }

    /// <summary>The next deadline: FFmpeg's wait fails once it passes.</summary>
    private void Arm(TimeSpan within)
    {
        _timedOut = false;
        Volatile.Write(ref _deadline, Stopwatch.GetTimestamp() + (long)(within.TotalSeconds * Stopwatch.Frequency));
    }

    /// <summary>
    /// Decodes the next frame, within <paramref name="within"/>: the camera sends packets as it films, and a frame
    /// decoded resets the clock. A corrupt packet is skipped (a few in a row fail the stream).
    /// </summary>
    /// <exception cref="RtspException">The stream ended, broke, or sent no frame in time.</exception>
    /// <exception cref="OperationCanceledException">The provider stopped the stream.</exception>
    public void Next(TimeSpan within)
    {
        Arm(within);
        var errors = 0;
        while (true)
        {
            _cancel.ThrowIfCancellationRequested();
            CheckFormat();
            _f.av_frame_unref(_frame);
            var received = _f.avcodec_receive_frame(_codec, _frame);
            if (received == 0)
            {
                var (w, h) = Size;
                if (w < 1 || h < 1 || w > _limits.MediaSide || h > _limits.MediaSide || (long)w * h > _limits.MediaArea)
                    throw new RtspException("media.malformed", $"The camera sends {w} x {h} pictures, outside the media limits.");
                var format = Field<int>(_frame, FFmpegAbi.FrameFormat);
                if (!Budgeted(format))
                    throw new RtspException(VideoProbe.CodecCode, $"The camera's pictures aren't 8-bit 4:2:0 or 4:2:2 (pixel format {format}), the formats its decoder's memory is reckoned for.");
                return;
            }
            if (received != FFmpegAbi.ErrorAgain && ++errors > 32)
                throw new RtspException("media.malformed", $"The camera's video could not be decoded: {_f.Error(received)}.");

            var read = _f.av_read_frame(_format, _packet);
            _cancel.ThrowIfCancellationRequested();
            if (read < 0)
                throw _timedOut ? new RtspException("media.provider", $"The camera sent nothing for {within.TotalSeconds:0} s.")
                    : read == FFmpegAbi.ErrorEof ? new RtspException("media.provider", "The stream ended.")
                    : new RtspException("media.provider", $"The stream ended: {_f.Error(read)}.");
            if (Field<int>(_packet, FFmpegAbi.PacketStreamIndex) != _index)
            {
                _f.av_packet_unref(_packet);
                continue;
            }
            var sent = _f.avcodec_send_packet(_codec, _packet);
            _f.av_packet_unref(_packet);
            CheckFormat();
            if (sent < 0 && sent != FFmpegAbi.ErrorAgain && ++errors > 32)
                throw new RtspException("media.malformed", $"The camera's video could not be decoded: {_f.Error(sent)}.");
        }
    }

    /// <summary>The decoded frame at its own size, in the platform's colour type.</summary>
    /// <exception cref="RtspException">The frame can't be converted.</exception>
    public SKImage Convert()
    {
        var (w, h) = Size;
        var format = Field<int>(_frame, FFmpegAbi.FrameFormat);
        if (_sws is null || w != _swsWidth || h != _swsHeight || format != _swsFormat)
        {
            if (_sws is not null) _f.sws_freeContext(_sws);
            var target = SKImageInfo.PlatformColorType == SKColorType.Bgra8888 ? FFmpegAbi.PixFmtBgra : FFmpegAbi.PixFmtRgba;
            _sws = _f.sws_getContext(w, h, format, w, h, target, FFmpegAbi.SwsBilinear, null, null, null);
            (_swsWidth, _swsHeight, _swsFormat) = (w, h, format);
            if (_sws is null) throw new RtspException("media.malformed", $"The camera's pixel format ({format}) can't be converted.");
        }
        var info = new SKImageInfo(w, h, SKImageInfo.PlatformColorType, SKAlphaType.Unpremul);
        using var data = SKData.Create(info.BytesSize64) ?? throw new RtspException("media.provider", "No memory for the frame.");
        var dst = (byte*)data.Data;
        var dstStride = info.RowBytes;
        var rows = _f.sws_scale(_sws, (byte**)((byte*)_frame + FFmpegAbi.FrameData), (int*)((byte*)_frame + FFmpegAbi.FrameLinesize), 0, h, &dst, &dstStride);
        if (rows <= 0) throw new RtspException("media.malformed", "The frame could not be converted.");
        return SKImage.FromPixels(info, data, info.RowBytes) ?? throw new RtspException("media.provider", "No memory for the frame.");
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    /// <summary>Whether <paramref name="f"/> has the RTSP client (a build without it can't open camera streams).</summary>
    public static bool HasClient(FFmpeg f)
    {
        fixed (byte* name = Utf8("rtsp"))
            return f.av_find_input_format(name) is not null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Interrupt(void* opaque)
    {
        try
        {
            var stream = (RtspStream)GCHandle.FromIntPtr((IntPtr)opaque).Target!;
            if (stream._cancel.IsCancellationRequested) return 1;
            if (Stopwatch.GetTimestamp() <= Volatile.Read(ref stream._deadline)) return 0;
            stream._timedOut = true;
            return 1;
        }
        catch
        {
            return 1;
        }
    }

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
            // TEARDOWN goes out without waiting for an answer; a deadline already passed keeps it from waiting at all.
            Arm(TimeSpan.FromSeconds(1));
            var format = _format;
            _f.avformat_close_input(&format);
            _format = null;
        }
        if (_self.IsAllocated) _self.Free();
    }
}

/// <summary>
/// The coded size an H.264 sequence parameter set gives (H.264 7.3.2.1.1: whole macroblocks, before cropping), read from
/// parameter sets in Annex B form (start codes) as the RTSP demuxer gives a track's <c>sprop-parameter-sets</c>. Pure:
/// null for anything it can't read.
/// </summary>
internal static class H264Size
{
    /// <summary>The first SPS's coded width and height, or null.</summary>
    public static (int Width, int Height)? Read(ReadOnlySpan<byte> annexB)
    {
        for (var i = 0; i + 3 < annexB.Length; i++)
        {
            if (annexB[i] != 0 || annexB[i + 1] != 0 || annexB[i + 2] != 1) continue;
            var start = i + 3;
            var end = start;
            while (end + 2 < annexB.Length && !(annexB[end] == 0 && annexB[end + 1] == 0 && annexB[end + 2] is 0 or 1)) end++;
            if (end + 2 >= annexB.Length) end = annexB.Length;
            if (start < end && (annexB[start] & 0x1F) == 7) return Parse(Unescape(annexB[start..end]));
            i = end - 1;
        }
        return null;
    }

    /// <summary>The NAL unit without its emulation prevention bytes (00 00 03).</summary>
    private static byte[] Unescape(ReadOnlySpan<byte> nal)
    {
        var rbsp = new List<byte>(nal.Length);
        var zeros = 0;
        foreach (var b in nal)
        {
            if (zeros >= 2 && b == 3)
            {
                zeros = 0;
                continue;
            }
            rbsp.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return [.. rbsp];
    }

    private static (int Width, int Height)? Parse(byte[] rbsp)
    {
        try
        {
            var r = new Bits(rbsp);
            r.Skip(8); // NAL unit header
            var profile = r.Read(8);
            r.Skip(16); // constraint flags, level_idc
            r.Ue(); // seq_parameter_set_id
            if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                var chroma = r.Ue();
                if (chroma > 3) return null;
                if (chroma == 3) r.Skip(1); // separate_colour_plane_flag
                r.Ue(); // bit_depth_luma_minus8
                r.Ue(); // bit_depth_chroma_minus8
                r.Skip(1); // qpprime_y_zero_transform_bypass_flag
                if (r.Flag()) // seq_scaling_matrix_present_flag
                {
                    for (var i = 0; i < (chroma != 3 ? 8 : 12); i++)
                    {
                        if (!r.Flag()) continue;
                        int last = 8, next = 8;
                        for (var j = 0; j < (i < 6 ? 16 : 64); j++)
                        {
                            if (next != 0) next = (last + r.Se() + 256) % 256;
                            last = next == 0 ? last : next;
                        }
                    }
                }
            }
            r.Ue(); // log2_max_frame_num_minus4
            var poc = r.Ue();
            if (poc == 0)
            {
                r.Ue(); // log2_max_pic_order_cnt_lsb_minus4
            }
            else if (poc == 1)
            {
                r.Skip(1);
                r.Se();
                r.Se();
                var cycle = r.Ue();
                if (cycle > 255) return null;
                for (var i = 0u; i < cycle; i++) r.Se();
            }
            else if (poc != 2)
            {
                return null;
            }
            r.Ue(); // max_num_ref_frames
            r.Skip(1); // gaps_in_frame_num_value_allowed_flag
            var widthMbs = r.Ue() + 1L;
            var heightUnits = r.Ue() + 1L;
            var frameMbsOnly = r.Flag();
            var width = widthMbs * 16;
            var height = heightUnits * 16 * (frameMbsOnly ? 1 : 2);
            return width > 65536 || height > 65536 ? null : ((int)width, (int)height);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private ref struct Bits(byte[] data)
    {
        private readonly byte[] _data = data;
        private int _position;

        public bool Flag()
        {
            if (_position >= _data.Length * 8) throw new InvalidOperationException("the SPS ends early");
            var bit = (_data[_position >> 3] >> (7 - (_position & 7))) & 1;
            _position++;
            return bit != 0;
        }

        public uint Read(int count)
        {
            var value = 0u;
            for (var i = 0; i < count; i++) value = (value << 1) | (Flag() ? 1u : 0u);
            return value;
        }

        public void Skip(int count)
        {
            if (_position + count > _data.Length * 8) throw new InvalidOperationException("the SPS ends early");
            _position += count;
        }

        public uint Ue()
        {
            var zeros = 0;
            while (!Flag())
                if (++zeros > 31) throw new InvalidOperationException("an Exp-Golomb code over 32 bits");
            return (uint)((1UL << zeros) - 1 + Read(zeros));
        }

        public int Se()
        {
            var k = Ue();
            return (k & 1) != 0 ? (int)((k + 1) / 2) : -(int)(k / 2);
        }
    }
}
