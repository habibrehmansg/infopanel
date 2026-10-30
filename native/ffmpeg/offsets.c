/*
 * Prints the FFmpeg struct offsets and constants the engine's P/Invoke binding pins (InfoPanel.Core/Images/Video/
 * FFmpegAbi.cs). Run against the headers of the FFmpeg major versions the engine accepts, and compare:
 *
 *   cc -I<ffmpeg>/include native/ffmpeg/offsets.c -o offsets && ./offsets
 *
 * Every field read through these offsets is public API whose position a major version keeps (FFmpeg adds fields at the
 * end of a struct and moves them only at a major bump), which is why the engine loads only the majors it was built for.
 */
#include <stddef.h>
#include <stdio.h>
#include <errno.h>
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavformat/avio.h>
#include <libavutil/frame.h>
#include <libavutil/pixfmt.h>
#include <libavutil/version.h>
#include <libswscale/swscale.h>

#define OFF(type, field) printf("%-40s %zu\n", #type "." #field, offsetof(type, field))
#define VAL(name) printf("%-40s %lld\n", #name, (long long)(name))

int main(void)
{
    VAL(LIBAVUTIL_VERSION_MAJOR);
    VAL(LIBAVCODEC_VERSION_MAJOR);
    VAL(LIBAVFORMAT_VERSION_MAJOR);
    VAL(LIBSWSCALE_VERSION_MAJOR);

    OFF(AVFormatContext, pb);
    OFF(AVFormatContext, flags);
    OFF(AVFormatContext, nb_streams);
    OFF(AVFormatContext, streams);

    OFF(AVStream, index);
    OFF(AVStream, codecpar);
    OFF(AVStream, time_base);
    OFF(AVStream, start_time);
    OFF(AVStream, discard);

    OFF(AVCodecParameters, codec_type);
    OFF(AVCodecParameters, codec_id);
    OFF(AVCodecParameters, width);
    OFF(AVCodecParameters, height);

    OFF(AVPacket, pts);
    OFF(AVPacket, dts);
    OFF(AVPacket, stream_index);

    OFF(AVFrame, data);
    OFF(AVFrame, linesize);
    OFF(AVFrame, width);
    OFF(AVFrame, height);
    OFF(AVFrame, format);
    OFF(AVFrame, pts);
    OFF(AVFrame, best_effort_timestamp);

    OFF(AVIOContext, buffer);

    VAL(sizeof(AVRational));
    VAL(AVMEDIA_TYPE_VIDEO);
    VAL(AVDISCARD_ALL);
    VAL(AV_PIX_FMT_BGRA);
    VAL(AV_PIX_FMT_RGBA);
    VAL(AV_CODEC_ID_H264);
    VAL(AV_CODEC_ID_HEVC);
    VAL(AV_CODEC_ID_VP8);
    VAL(AV_CODEC_ID_VP9);
    VAL(AV_CODEC_ID_AV1);
    VAL(AV_CODEC_ID_MPEG4);
    VAL(AV_CODEC_ID_MJPEG);
    VAL(AV_CODEC_ID_PRORES);
    VAL(AVSEEK_SIZE);
    VAL(AVSEEK_FLAG_BACKWARD);
    VAL(AVFMT_FLAG_CUSTOM_IO);
    VAL(AVERROR_EOF);
    VAL(AVERROR(EAGAIN));
    VAL(AV_NOPTS_VALUE);
    VAL(SWS_BILINEAR);
    VAL(SWS_AREA);
    return 0;
}
