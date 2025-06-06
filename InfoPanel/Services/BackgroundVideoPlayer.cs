using FFmpeg.AutoGen;
using SkiaSharp;
using System;
using System.Buffers;
using System.IO;
using System.Threading;

namespace InfoPanel;

public unsafe class BackgroundVideoPlayer : IDisposable
{
    private readonly string _filePath;
    private readonly Thread _decodingThread;
    private readonly CancellationTokenSource _cancellationToken = new(); private volatile bool _disposed = false;
    // Immutable frame data with reference counting for thread-safe sharing
    private class ImmutableFrame
    {
        public readonly AVFrame* Frame; // Store a deep-copied AVFrame
        public readonly TimeSpan Time;
        public readonly long FrameIndex;
        public readonly int Width;
        public readonly int Height;
        private volatile int _refCount = 1;

        public ImmutableFrame(AVFrame* frame, TimeSpan time, long frameIndex, int width, int height)
        {
            Frame = frame;
            Time = time;
            FrameIndex = frameIndex;
            Width = width;
            Height = height;
        }

        public void AddRef() => Interlocked.Increment(ref _refCount);
        public bool Release()
        {
            if (Interlocked.Decrement(ref _refCount) == 0)
            {
                FreeFrame(Frame);
                return true;
            }
            return false;
        }
    }

    private static void FreeFrame(AVFrame* frame)
    {
        if (frame != null)
        {
            ffmpeg.av_frame_free(&frame);
        }
    }

    private volatile ImmutableFrame? _currentFrame;
    private readonly object _frameLock = new();

    // Video properties
    public int Width { get; private set; }
    public int Height { get; private set; }
    public TimeSpan Duration { get; private set; }
    public double FrameRate { get; private set; }
    public long TotalFrames { get; private set; }
    public bool IsOpen { get; private set; }

    // Performance tracking
    public long DecodedFrameCount { get; private set; }
    public int LoopCount { get; private set; }

    static BackgroundVideoPlayer()
    {
        InitializeFFmpeg();
    }

    private static void InitializeFFmpeg()
    {
        try
        {
            var currentDir = AppDomain.CurrentDomain.BaseDirectory;
            var ffmpegPath = currentDir;

            if (!File.Exists(Path.Combine(ffmpegPath, "avformat-61.dll")))
            {
                var projectRoot = Directory.GetParent(currentDir)?.Parent?.Parent?.Parent?.FullName;
                ffmpegPath = Path.Combine(projectRoot ?? currentDir, "bin", "x64");

                if (!Directory.Exists(ffmpegPath))
                {
                    ffmpegPath = Path.Combine(Directory.GetCurrentDirectory(), "bin", "x64");
                }
            }

            if (Directory.Exists(ffmpegPath) && File.Exists(Path.Combine(ffmpegPath, "avformat-61.dll")))
            {
                ffmpeg.RootPath = ffmpegPath;
            }

            ffmpeg.avformat_network_init();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to initialize FFmpeg: {ex.Message}", ex);
        }
    }

    public BackgroundVideoPlayer(string filePath)
    {
        _filePath = filePath;

        InitializeVideoProperties();

        _decodingThread = new Thread(DecodingLoop)
        {
            IsBackground = true,
            Name = "VideoDecoder"
        };

        // Start background decoding thread
        _decodingThread.Start();
        IsOpen = true;
    }

    private void InitializeVideoProperties()
    {
        AVFormatContext* formatContext = null;
        AVCodecContext* codecContext = null;

        try
        {
            // Allocate format context
            formatContext = ffmpeg.avformat_alloc_context();
            if (formatContext == null)
                throw new Exception("Failed to allocate format context");

            // Open input file
            if (ffmpeg.avformat_open_input(&formatContext, _filePath, null, null) < 0)
                throw new Exception($"Failed to open input file: {_filePath}");

            // Find stream info
            if (ffmpeg.avformat_find_stream_info(formatContext, null) < 0)
                throw new Exception("Failed to find stream info");

            // Find video stream
            int videoStreamIndex = -1;
            for (int i = 0; i < formatContext->nb_streams; i++)
            {
                if (formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    videoStreamIndex = i;
                    break;
                }
            }

            if (videoStreamIndex == -1)
                throw new Exception("No video stream found");

            var videoStream = formatContext->streams[videoStreamIndex];
            var codecParameters = videoStream->codecpar;

            // Find decoder
            var codec = ffmpeg.avcodec_find_decoder(codecParameters->codec_id);
            if (codec == null)
                throw new Exception("Decoder not found");

            // Allocate codec context
            codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (codecContext == null)
                throw new Exception("Failed to allocate codec context");

            // Copy codec parameters to context
            if (ffmpeg.avcodec_parameters_to_context(codecContext, codecParameters) < 0)
                throw new Exception("Failed to copy codec parameters");

            // Set properties
            Width = codecContext->width;
            Height = codecContext->height;

            // Calculate frame rate
            var frameRate = ffmpeg.av_q2d(videoStream->avg_frame_rate);
            if (frameRate <= 0)
            {
                frameRate = ffmpeg.av_q2d(videoStream->r_frame_rate);
            }
            FrameRate = frameRate > 0 ? frameRate : 30.0;

            if (formatContext->duration == ffmpeg.AV_NOPTS_VALUE)
            {
                Duration = TimeSpan.MaxValue;
                TotalFrames = long.MaxValue;
            }
            else
            {
                Duration = TimeSpan.FromSeconds(formatContext->duration / (double)ffmpeg.AV_TIME_BASE);
                TotalFrames = (long)(Duration.TotalSeconds * FrameRate);
            }

        }
        finally
        {
            // Clean up temporary resources
            if (codecContext != null)
            {
                ffmpeg.avcodec_free_context(&codecContext);
            }

            if (formatContext != null)
            {
                ffmpeg.avformat_close_input(&formatContext);
            }
        }
    }

    private bool IsValidPointer(IntPtr ptr)
    {
        return ptr != IntPtr.Zero && ptr != new IntPtr(-1);
    }

    private void DecodingLoop()
    {
        while (!_cancellationToken.Token.IsCancellationRequested)
        {
            try
            {
                // On restart, keep current frame to prevent flickering
                // No need to clear anything - current frame will be naturally updated

                DecodeVideoLoop();

                // Video finished, increment loop count and restart
                LoopCount++;
                Thread.Sleep(10); // Brief pause before restarting
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Decoding error: {ex.Message}");
                Thread.Sleep(1000); // Wait before retrying
            }
        }

        _cancellationToken.Dispose();
    }

    private void DecodeVideoLoop()
    {
        AVFormatContext* formatContext = null;
        AVCodecContext* codecContext = null;
        AVFrame* frame = null;
        AVPacket* packet = null;
        try
        {
            // Initialize FFmpeg components
            formatContext = ffmpeg.avformat_alloc_context();
            if (formatContext == null)
                throw new Exception("Failed to allocate format context");

            var formatContextPtr = formatContext;
            if (ffmpeg.avformat_open_input(&formatContextPtr, _filePath, null, null) < 0)
                throw new Exception($"Failed to open input file: {_filePath}");
            formatContext = formatContextPtr;

            if (ffmpeg.avformat_find_stream_info(formatContext, null) < 0)
                throw new Exception("Failed to find stream info");

            // Find video stream
            int videoStreamIndex = -1;
            for (int i = 0; i < formatContext->nb_streams; i++)
            {
                if (formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    videoStreamIndex = i;
                    break;
                }
            }

            var videoStream = formatContext->streams[videoStreamIndex];
            var codecParameters = videoStream->codecpar;
            var timeBase = ffmpeg.av_q2d(videoStream->time_base);

            // Setup decoder
            var codec = ffmpeg.avcodec_find_decoder(codecParameters->codec_id);
            codecContext = ffmpeg.avcodec_alloc_context3(codec);

            if (ffmpeg.avcodec_parameters_to_context(codecContext, codecParameters) < 0)
                throw new Exception("Failed to copy codec parameters");

            // Disable threading to avoid conflicts
            codecContext->thread_count = 1;
            codecContext->thread_type = 0;

            if (ffmpeg.avcodec_open2(codecContext, codec, null) < 0)
                throw new Exception("Failed to open codec");

            // Allocate frames and packet
            frame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();
            long frameIndex = 0;
            while (!_cancellationToken.Token.IsCancellationRequested)
            {
                ffmpeg.av_frame_unref(frame);
                int error;
                do
                {
                    try
                    {
                        do
                        {
                            ffmpeg.av_packet_unref(packet);
                            error = ffmpeg.av_read_frame(formatContext, packet);
                            if (error == ffmpeg.AVERROR_EOF)
                                return;
                            if (error < 0)
                                return;
                        } while (packet->stream_index != videoStreamIndex);
                        error = ffmpeg.avcodec_send_packet(codecContext, packet);
                        if (error < 0)
                            continue;
                    }
                    finally
                    {
                        ffmpeg.av_packet_unref(packet);
                    }
                    error = ffmpeg.avcodec_receive_frame(codecContext, frame);
                } while (error == ffmpeg.AVERROR(ffmpeg.EAGAIN));
                if (error < 0)
                    continue;
                // Deep copy the AVFrame
                AVFrame* frameCopy = ffmpeg.av_frame_clone(frame);
                var currentTime = TimeSpan.Zero;
                if (frame->pts != ffmpeg.AV_NOPTS_VALUE)
                {
                    currentTime = TimeSpan.FromSeconds(frame->pts * timeBase);
                }
                var newFrame = new ImmutableFrame(frameCopy, currentTime, frameIndex, Width, Height);
                var oldFrame = Interlocked.Exchange(ref _currentFrame, newFrame);
                if (oldFrame != null && oldFrame.Release()) { }
                DecodedFrameCount++;
                frameIndex++;
                if (FrameRate > 0)
                {
                    var targetFrameTime = (int)(1000.0 / FrameRate);
                    Thread.Sleep(Math.Max(1, targetFrameTime));
                }
            }
        }
        finally
        {
            // Clean up resources
            if (frame != null)
            {
                var framePtr = frame;
                ffmpeg.av_frame_free(&framePtr);
            }

            if (packet != null)
            {
                var packetPtr = packet;
                ffmpeg.av_packet_free(&packetPtr);
            }

            if (codecContext != null)
            {
                var codecPtr = codecContext;
                ffmpeg.avcodec_free_context(&codecPtr);
            }

            if (formatContext != null)
            {
                var formatPtr = formatContext;
                ffmpeg.avformat_close_input(&formatPtr);
            }
        }
    }

    /// <summary>
    /// Gets the latest decoded frame, optionally scaled to the specified width and height. Returns null if no frame is available yet.
    /// </summary>
    public SKImage? GetLatestFrame(int? width = null, int? height = null)
    {
        if (_disposed || !IsOpen)
            return null;
        var frame = _currentFrame;
        if (frame == null)
            return null;
        frame.AddRef();
        byte[]? rentedBuffer = null;
        try
        {
            int targetWidth = width ?? frame.Width;
            int targetHeight = height ?? frame.Height;
            var imageInfo = new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
            int bgraStride = targetWidth * 4;
            int bgraSize = targetWidth * targetHeight * 4;
            rentedBuffer = ArrayPool<byte>.Shared.Rent(bgraSize);
            fixed (byte* dstPtr = rentedBuffer)
            {
                byte*[] dstData = new byte*[4];
                int[] dstLinesize = new int[4];
                dstData[0] = dstPtr;
                dstLinesize[0] = bgraStride;
                SwsContext* swsContext = ffmpeg.sws_getContext(
                    frame.Width, frame.Height, (AVPixelFormat)frame.Frame->format,
                    targetWidth, targetHeight, AVPixelFormat.AV_PIX_FMT_BGRA,
                    ffmpeg.SWS_FAST_BILINEAR, null, null, null);
                ffmpeg.sws_scale(swsContext, frame.Frame->data, frame.Frame->linesize, 0, frame.Height, dstData, dstLinesize);
                ffmpeg.sws_freeContext(swsContext);
            }
            var image = SKImage.FromPixelCopy(imageInfo, rentedBuffer, bgraStride);
            return image;
        }
        finally
        {
            if (rentedBuffer != null)
                ArrayPool<byte>.Shared.Return(rentedBuffer);
            frame.Release();
        }
    }

    /// <summary>
    /// Gets the current playback time of the latest frame
    /// </summary>
    public TimeSpan GetCurrentTime()
    {
        var frame = _currentFrame;
        return frame?.Time ?? TimeSpan.Zero;
    }

    /// <summary>
    /// Gets the current frame index
    /// </summary>
    public long GetCurrentFrameIndex()
    {
        var frame = _currentFrame;
        return frame?.FrameIndex ?? 0;
    }

    /// <summary>
    /// Gets playback progress as a percentage (0.0 to 1.0)
    /// </summary>
    public double GetProgress()
    {
        if (TotalFrames == 0)
            return 0.0;

        var frame = _currentFrame;
        var currentIndex = frame?.FrameIndex ?? 0;
        return Math.Min(1.0, currentIndex / (double)TotalFrames);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        IsOpen = false;

        // Signal cancellation
        _cancellationToken.Cancel();

        // Wait for decoding thread to finish
        try
        {
            _decodingThread?.Join(2000); // Wait up to 2 seconds
        }
        catch { }        // Clean up current frame
        var frame = Interlocked.Exchange(ref _currentFrame, null);
        frame?.Release();

        GC.SuppressFinalize(this);
    }
}